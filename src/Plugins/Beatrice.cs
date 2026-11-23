using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TakaVox.Plugins
{
    /// <summary>
    /// Voz com IA: o plugin Beatrice 2 (VST3 gratuito do Project Beatrice, codigo MIT; o motor de inferencia e fechado)
    /// e os modelos de voz dele. O plugin roda na thread de audio do TakaVox como um VST3 comum (tvhost), em 1 nucleo,
    /// sem placa de video, com ~50 ms de atraso.
    /// Formato do estado e IDs dos parametros: src/common/parameter_schema.* e parameter_state.cc do beatrice-vst.
    /// Os arquivos do plugin e dos modelos NAO vao no instalador (os termos nao permitem redistribuir): ficam em
    /// %APPDATA%\TakaVox\ia, baixados do site oficial pelo proprio usuario.
    /// </summary>
    public static class Beatrice
    {
        // IDs dos parametros (enum ParameterID do beatrice-vst)
        public const int PModel = 1;          // texto (so no estado; nao e parametro VST)
        public const int PVoice = 2;          // lista: voz 0..255 do modelo (256 = mistura de vozes)
        public const int PFormant = 3;        // timbre: -2..+2 st, passos de 0,5
        public const int PPitch = 4;          // tom: -24..+24 st
        public const int PAvgSrcPitch = 5;    // tom medio de quem fala (so a janela do plugin usa)
        public const int PLock = 6;
        public const int PInGain = 7;         // dB
        public const int POutGain = 8;        // dB
        public const int PIntonation = 9;     // 1 = natural; -1..3
        public const int PPitchCorrection = 10;
        public const int PPitchCorrectionType = 11;
        public const int PMinSrcPitch = 12;   // faixa em que o plugin procura o tom de quem fala (nota MIDI)
        public const int PMaxSrcPitch = 13;
        public const int PVqNeighbors = 14;   // 0..8

        public static string Root => Path.Combine(Log.Dir, "ia");
        public static string PluginDir => Path.Combine(Root, "beatrice");
        public static string ModelsDir => Path.Combine(Root, "modelos");

        public const string SiteUrl = "https://prj-beatrice.com/";

        /// <summary>O plugin instalado (pasta .vst3 da versao mais nova) ou null.</summary>
        public static string FindPlugin()
        {
            try
            {
                if (!Directory.Exists(PluginDir)) return null;
                return Directory.GetDirectories(PluginDir, "*.vst3", SearchOption.AllDirectories)
                    .Where(d => File.Exists(Path.Combine(d, "Contents", "x86_64-win", Path.GetFileName(d))))
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Versao do plugin pelo nome da pasta (ex.: "2.0.0-rc.3").</summary>
        public static string PluginVersion(string vst3)
        {
            string n = Path.GetFileNameWithoutExtension(vst3 ?? "");
            return n.StartsWith("beatrice_", StringComparison.OrdinalIgnoreCase) ? n.Substring(9) : n;
        }

        // ---------------------------------------------------------------- estado do plugin
        // tvhost: "TVS1" + formato(1) + u32 + estado do componente + u32 + estado do controlador.
        // Componente do Beatrice: int32 tamanho + registros {id int16, tipo int32 (0 int, 1 double, 2 texto UTF-8), valor}.
        sealed class Rec { public int Type; public byte[] Value; }

        static SortedDictionary<int, Rec> ParseComponent(byte[] comp)
        {
            var recs = new SortedDictionary<int, Rec>();
            if (comp == null || comp.Length < 4) return recs;
            int n = BitConverter.ToInt32(comp, 0);
            int p = 4, end = Math.Min(comp.Length, 4 + Math.Max(0, n));
            while (p + 6 <= end)
            {
                int id = BitConverter.ToInt16(comp, p);
                int type = BitConverter.ToInt32(comp, p + 2);
                p += 6;
                int len = type == 0 ? 4 : type == 1 ? 8 : type == 2 && p + 4 <= end ? 4 + BitConverter.ToInt32(comp, p) : -1;
                if (len < 0 || p + len > end) break;
                var v = new byte[len];
                Buffer.BlockCopy(comp, p, v, 0, len);
                recs[id] = new Rec { Type = type, Value = v };
                p += len;
            }
            return recs;
        }

        static byte[] BuildComponent(SortedDictionary<int, Rec> recs)
        {
            var body = new MemoryStream();
            var w = new BinaryWriter(body);
            foreach (var kv in recs)
            {
                w.Write((short)kv.Key);
                w.Write(kv.Value.Type);
                w.Write(kv.Value.Value);
            }
            w.Flush();
            var outS = new MemoryStream();
            var w2 = new BinaryWriter(outS);
            w2.Write((int)body.Length);
            w2.Write(body.ToArray());
            w2.Flush();
            return outS.ToArray();
        }

        static bool SplitTvs1(byte[] s, out byte[] comp, out byte[] ctrl)
        {
            comp = ctrl = null;
            if (s == null || s.Length < 9 || s[0] != 'T' || s[1] != 'V' || s[2] != 'S' || s[3] != '1' || s[4] != 3) return false;
            int la = BitConverter.ToInt32(s, 5);
            if (la < 0 || 9 + la > s.Length) return false;
            comp = new byte[la];
            Buffer.BlockCopy(s, 9, comp, 0, la);
            int p = 9 + la;
            if (p + 4 <= s.Length)
            {
                int lb = BitConverter.ToInt32(s, p);
                if (lb > 0 && p + 4 + lb <= s.Length) { ctrl = new byte[lb]; Buffer.BlockCopy(s, p + 4, ctrl, 0, lb); }
            }
            return true;
        }

        static byte[] JoinTvs1(byte[] comp, byte[] ctrl)
        {
            var m = new MemoryStream();
            m.Write(new byte[] { (byte)'T', (byte)'V', (byte)'S', (byte)'1', 3 }, 0, 5);
            var w = new BinaryWriter(m);
            w.Write(comp.Length); w.Write(comp);
            w.Write(ctrl?.Length ?? 0); if (ctrl != null) w.Write(ctrl);
            w.Flush();
            return m.ToArray();
        }

        /// <summary>O mesmo estado, com outro modelo (arquivo .toml). Null se o estado nao e do Beatrice.</summary>
        public static byte[] WithModel(byte[] tvs1, string toml)
        {
            if (!SplitTvs1(tvs1, out var comp, out var ctrl)) return null;
            var recs = ParseComponent(comp);
            var txt = Encoding.UTF8.GetBytes(toml ?? "");
            var v = new byte[4 + txt.Length];
            BitConverter.GetBytes(txt.Length).CopyTo(v, 0);
            txt.CopyTo(v, 4);
            recs[PModel] = new Rec { Type = 2, Value = v };
            return JoinTvs1(BuildComponent(recs), ctrl);
        }

        /// <summary>Caminho do modelo guardado no estado ("" se nenhum).</summary>
        public static string ModelOf(byte[] tvs1)
        {
            if (!SplitTvs1(tvs1, out var comp, out _)) return "";
            var recs = ParseComponent(comp);
            if (!recs.TryGetValue(PModel, out var r) || r.Type != 2 || r.Value.Length < 4) return "";
            return Encoding.UTF8.GetString(r.Value, 4, r.Value.Length - 4);
        }

        /// <summary>Valores numericos do estado (id -> valor), para diagnostico.</summary>
        public static Dictionary<int, double> NumbersOf(byte[] tvs1)
        {
            var d = new Dictionary<int, double>();
            if (!SplitTvs1(tvs1, out var comp, out _)) return d;
            foreach (var kv in ParseComponent(comp))
            {
                if (kv.Value.Type == 0) d[kv.Key] = BitConverter.ToInt32(kv.Value.Value, 0);
                else if (kv.Value.Type == 1) d[kv.Key] = BitConverter.ToDouble(kv.Value.Value, 0);
            }
            return d;
        }

        /// <summary>Estado so com o modelo (o plugin poe o padrao em todo o resto). Serve para carregar ja com a voz.</summary>
        public static byte[] StateForModel(string toml)
        {
            var recs = new SortedDictionary<int, Rec>();
            var txt = Encoding.UTF8.GetBytes(toml ?? "");
            var v = new byte[4 + txt.Length];
            BitConverter.GetBytes(txt.Length).CopyTo(v, 0);
            txt.CopyTo(v, 4);
            recs[PModel] = new Rec { Type = 2, Value = v };
            return JoinTvs1(BuildComponent(recs), null);
        }

        /// <summary>Nota MIDI (pode ser fracionaria) -> Hz, e o contrario.</summary>
        public static double MidiToHz(double m) => 440.0 * Math.Pow(2, (m - 69) / 12.0);
        public static double HzToMidi(double hz) => hz > 0 ? 69 + 12 * Math.Log(hz / 440.0, 2) : 0;

        // ---------------------------------------------------------------- instalacao (arquivos baixados pelo usuario)
        /// <summary>O plugin e pelo menos um modelo estao instalados?</summary>
        public static bool Installed => FindPlugin() != null && BeatriceModel.LoadAll(ModelsDir).Count > 0;

        /// <summary>Pasta Downloads do usuario.</summary>
        public static string DownloadsDir
        {
            get
            {
                try
                {
                    if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0 && Directory.Exists(p)) return p;
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string path);

        /// <summary>Zips do Beatrice na pasta Downloads (plugin e modelos), do mais novo para o mais velho.</summary>
        public static List<string> FindDownloads()
        {
            try
            {
                string d = DownloadsDir;
                if (!Directory.Exists(d)) return new List<string>();
                return Directory.GetFiles(d, "beatrice*.zip").OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>
        /// Instala o que houver nos zips: o plugin (pasta .vst3) vai para ia\beatrice e cada modelo (pasta com .toml e os .bin)
        /// para ia\modelos. Retorna o resumo para mostrar ao usuario.
        /// </summary>
        public static string Import(IEnumerable<string> zips, out int plugins, out int models)
        {
            plugins = models = 0;
            var msgs = new List<string>();
            foreach (var zip in zips)
            {
                try
                {
                    using (var za = System.IO.Compression.ZipFile.OpenRead(zip))
                    {
                        var names = za.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.FullName.Replace('\\', '/')).ToList();
                        // plugin: ".../<nome>.vst3/Contents/x86_64-win/<nome>.vst3"
                        var bin = names.FirstOrDefault(n => n.IndexOf(".vst3/Contents/x86_64-win/", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (bin != null)
                        {
                            int k = bin.IndexOf(".vst3/Contents/x86_64-win/", StringComparison.OrdinalIgnoreCase);
                            string bundle = bin.Substring(0, k + 5);
                            string bundleName = bundle.Substring(bundle.LastIndexOf('/') + 1);
                            string dest = Path.Combine(PluginDir, bundleName);
                            Extract(za, bundle + "/", dest, true);
                            plugins++;
                            msgs.Add("plugin " + Path.GetFileNameWithoutExtension(bundleName));
                        }
                        // modelos: pasta com um .toml e o phone_extractor.bin
                        foreach (var toml in names.Where(n => n.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)))
                        {
                            int slash = toml.LastIndexOf('/');
                            string dir = slash >= 0 ? toml.Substring(0, slash + 1) : "";
                            if (!names.Any(n => n.Equals(dir + "phone_extractor.bin", StringComparison.OrdinalIgnoreCase))) continue;
                            string folder = dir.Length > 0 ? dir.TrimEnd('/') : Path.GetFileNameWithoutExtension(zip);
                            folder = folder.Substring(folder.LastIndexOf('/') + 1);
                            foreach (char c in Path.GetInvalidFileNameChars()) folder = folder.Replace(c, '_');
                            Extract(za, dir, Path.Combine(ModelsDir, folder), false);
                            var m = BeatriceModel.Load(Path.Combine(ModelsDir, folder, toml.Substring(slash + 1)), out var err);
                            if (m == null) { msgs.Add(folder + ": " + err); continue; }
                            models++;
                            msgs.Add("modelo " + m.Name + " (" + m.Voices.Count + (m.Voices.Count == 1 ? " voz)" : " vozes)"));
                        }
                    }
                }
                catch (Exception ex) { msgs.Add(Path.GetFileName(zip) + ": " + ex.Message); }
            }
            return string.Join("; ", msgs);
        }

        /// <summary>Extrai as entradas que comecam com prefix para dest (com protecao contra caminhos maliciosos).</summary>
        static void Extract(System.IO.Compression.ZipArchive za, string prefix, string dest, bool recursive)
        {
            string root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
            foreach (var e in za.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;
                string full = e.FullName.Replace('\\', '/');
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = full.Substring(prefix.Length);
                if (!recursive && rel.Contains("/")) continue;
                string target = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', '\\')));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var fi = new FileInfo(target);
                if (fi.Exists && fi.Length == e.Length) continue; // ja esta igual (e pode estar em uso)
                System.IO.Compression.ZipFileExtensions.ExtractToFile(e, target, true);
            }
        }
    }

    /// <summary>Um modelo de voz do Beatrice (pasta com o .toml e os .bin).</summary>
    public sealed class BeatriceModel
    {
        public string Dir, Toml, Name = "", Description = "", Version = "";
        public readonly List<BeatriceVoice> Voices = new List<BeatriceVoice>();
        /// <summary>Chave estavel do modelo (nome da pasta).</summary>
        public string Key => Path.GetFileName(Dir);

        /// <summary>Codigo numerico do modelo (1..65535, CRC-16 do nome da pasta): e o que fica salvo nos ajustes (VcAiModel).</summary>
        public int Code
        {
            get
            {
                ushort crc = 0xFFFF;
                foreach (byte b in Encoding.UTF8.GetBytes(Key.ToLowerInvariant()))
                {
                    crc ^= (ushort)(b << 8);
                    for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
                }
                return crc == 0 ? 1 : crc;
            }
        }

        /// <summary>Modelo com esse codigo (0 ou nao achado = o primeiro da lista).</summary>
        public static BeatriceModel Find(IList<BeatriceModel> models, float code)
        {
            if (models == null || models.Count == 0) return null;
            int c = (int)Math.Round(code);
            foreach (var m in models) if (m.Code == c) return m;
            return models[0];
        }
        /// <summary>Modelo do corpus JVS (100 vozes de pessoas reais; uso nao comercial).</summary>
        public bool IsJvs => Key.IndexOf("jvs", StringComparison.OrdinalIgnoreCase) >= 0 || Name.IndexOf("JVS", StringComparison.Ordinal) >= 0;

        static readonly string[] Bins = { "phone_extractor.bin", "pitch_estimator.bin", "waveform_generator.bin", "embedding_setter.bin", "speaker_embeddings.bin" };

        /// <summary>Le todos os modelos instalados em <paramref name="dir"/> (cada um numa subpasta).</summary>
        public static List<BeatriceModel> LoadAll(string dir)
        {
            var list = new List<BeatriceModel>();
            try
            {
                if (!Directory.Exists(dir)) return list;
                foreach (var toml in Directory.GetFiles(dir, "*.toml", SearchOption.AllDirectories))
                {
                    var m = Load(toml, out _);
                    if (m != null) list.Add(m);
                }
            }
            catch { }
            // oficiais primeiro, JVS por ultimo
            return list.OrderBy(m => m.IsJvs ? 1 : 0).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static BeatriceModel Load(string toml, out string error)
        {
            error = null;
            try
            {
                string dir = Path.GetDirectoryName(toml);
                foreach (var b in Bins)
                    if (!File.Exists(Path.Combine(dir, b))) { error = "falta o arquivo " + b; return null; }
                var t = MiniToml.Parse(File.ReadAllText(toml, Encoding.UTF8));
                if (!t.TryGetValue("model", out var model) || !model.TryGetValue("version", out var ver)) { error = "não é um modelo do Beatrice"; return null; }
                if (!ver.StartsWith("2.0.0", StringComparison.Ordinal)) { error = "versão de modelo não suportada (" + ver + ")"; return null; }
                var m = new BeatriceModel { Dir = dir, Toml = toml, Version = ver };
                model.TryGetValue("name", out m.Name);
                model.TryGetValue("description", out m.Description);
                m.Name = (m.Name ?? "").Trim();
                m.Description = (m.Description ?? "").Trim();
                for (int i = 0; i < 256; i++)
                {
                    if (!t.TryGetValue("voice." + i, out var v)) break;
                    var bv = new BeatriceVoice { Model = m, Index = i };
                    v.TryGetValue("name", out bv.Name);
                    v.TryGetValue("description", out bv.Description);
                    bv.Name = (bv.Name ?? ("Voz " + i)).Trim();
                    bv.Description = (bv.Description ?? "").Trim();
                    if (v.TryGetValue("average_pitch", out var ap) && double.TryParse(ap, NumberStyles.Float, CultureInfo.InvariantCulture, out var apd)) bv.AveragePitch = apd;
                    else bv.AveragePitch = 60;
                    if (t.TryGetValue("voice." + i + ".portrait", out var po) && po.TryGetValue("path", out var pp) && !string.IsNullOrWhiteSpace(pp))
                    {
                        string full = Path.Combine(dir, pp);
                        if (File.Exists(full) && !pp.Equals("noimage.png", StringComparison.OrdinalIgnoreCase)) bv.Portrait = full;
                    }
                    m.Voices.Add(bv);
                }
                if (m.Voices.Count == 0) { error = "o modelo não tem vozes"; return null; }
                if (m.Name.Length == 0) m.Name = m.Key;
                return m;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }
    }

    /// <summary>Uma voz de um modelo do Beatrice.</summary>
    public sealed class BeatriceVoice
    {
        public BeatriceModel Model;
        public int Index;
        public string Name = "", Description = "", Portrait;
        /// <summary>Tom medio da voz alvo (nota MIDI).</summary>
        public double AveragePitch;
        public double AverageHz => Beatrice.MidiToHz(AveragePitch);
        /// <summary>Chave estavel: pasta do modelo + "#" + indice.</summary>
        public string Key => Model.Key + "#" + Index;
        public bool Feminine => AverageHz >= 175;
    }

    /// <summary>Leitor minimo de TOML (o que os modelos do Beatrice usam: secoes, textos e numeros).</summary>
    public static class MiniToml
    {
        public static Dictionary<string, Dictionary<string, string>> Parse(string text)
        {
            var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var cur = new Dictionary<string, string>(StringComparer.Ordinal);
            res[""] = cur;
            int i = 0, n = text.Length;
            while (i < n)
            {
                // inicio de linha
                while (i < n && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n')) i++;
                if (i >= n) break;
                char c = text[i];
                if (c == '#') { SkipLine(text, ref i); continue; }
                if (c == '[')
                {
                    int e = text.IndexOf(']', i);
                    if (e < 0) break;
                    string name = text.Substring(i + 1, e - i - 1).Trim().Trim('[', ']').Trim();
                    if (!res.TryGetValue(name, out cur)) { cur = new Dictionary<string, string>(StringComparer.Ordinal); res[name] = cur; }
                    i = e + 1;
                    SkipLine(text, ref i);
                    continue;
                }
                int eq = text.IndexOf('=', i);
                int nl = text.IndexOf('\n', i);
                if (eq < 0 || (nl >= 0 && nl < eq)) { SkipLine(text, ref i); continue; }
                string key = text.Substring(i, eq - i).Trim().Trim('"');
                i = eq + 1;
                while (i < n && (text[i] == ' ' || text[i] == '\t')) i++;
                cur[key] = ReadValue(text, ref i);
                SkipLine(text, ref i);
            }
            return res;
        }

        static void SkipLine(string t, ref int i)
        {
            int nl = t.IndexOf('\n', i);
            i = nl < 0 ? t.Length : nl + 1;
        }

        static string ReadValue(string t, ref int i)
        {
            int n = t.Length;
            if (i >= n) return "";
            if (string.CompareOrdinal(t, i, "\"\"\"", 0, 3) == 0 || string.CompareOrdinal(t, i, "'''", 0, 3) == 0)
            {
                string q = t.Substring(i, 3);
                bool literal = q[0] == '\'';
                i += 3;
                if (i < n && t[i] == '\r') i++;
                if (i < n && t[i] == '\n') i++; // a quebra logo depois das aspas nao conta
                int e = t.IndexOf(q, i, StringComparison.Ordinal);
                if (e < 0) e = n;
                string raw = t.Substring(i, e - i);
                i = Math.Min(n, e + 3);
                return (literal ? raw : Unescape(raw, true)).Replace("\r\n", "\n");
            }
            if (t[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < n && t[i] != '"' && t[i] != '\n')
                {
                    if (t[i] == '\\' && i + 1 < n) { sb.Append(t[i]).Append(t[i + 1]); i += 2; continue; }
                    sb.Append(t[i++]);
                }
                if (i < n && t[i] == '"') i++;
                return Unescape(sb.ToString(), false);
            }
            if (t[i] == '\'')
            {
                int e = t.IndexOf('\'', i + 1);
                if (e < 0) e = n;
                string s = t.Substring(i + 1, e - i - 1);
                i = Math.Min(n, e + 1);
                return s;
            }
            int end = i;
            while (end < n && t[end] != '\n' && t[end] != '#') end++;
            string v = t.Substring(i, end - i).Trim();
            i = end;
            return v;
        }

        static string Unescape(string s, bool multiline)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char d = s[++i];
                switch (d)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u':
                    case 'U':
                        int len = d == 'u' ? 4 : 8;
                        if (i + len < s.Length + 0 && int.TryParse(s.Substring(i + 1, Math.Min(len, s.Length - i - 1)), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                        {
                            sb.Append(char.ConvertFromUtf32(cp));
                            i += len;
                        }
                        break;
                    case '\r':
                    case '\n':
                        if (multiline) { while (i + 1 < s.Length && char.IsWhiteSpace(s[i + 1])) i++; }
                        break;
                    default: sb.Append('\\').Append(d); break;
                }
            }
            return sb.ToString();
        }
    }
}
