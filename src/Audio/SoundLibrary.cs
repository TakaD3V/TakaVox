using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TakaVox.Audio
{
    /// <summary>Um som do sound pad (o que aparece no botao e como ele toca).</summary>
    public sealed class SoundItem
    {
        public int Key;                   // numero do som (atalhos e o que esta tocando)
        public string Id = "";            // nome base dos arquivos (copia do original e cache)
        public string Name = "", FileName = "", Emoji = "", Color = "", Category = "", Hotkey = "";
        public float VolumeDb;            // ajuste do usuario
        public bool Normalize = true;     // iguala o volume percebido com os outros sons
        public double StartSec, EndSec;   // trecho (EndSec 0 = ate o fim)
        public int FadeInMs = 4, FadeOutMs = 30;
        public bool Loop;
        public int Mode;                  // 0 reiniciar, 1 liga/desliga, 2 sobrepor
        public float Lufs = -100f, PeakDb = -120f;
        public double Duration;
        public byte[] Peaks = new byte[0];
        public int Order;
        public string Problem;            // arquivo sumiu/estragou
        public string HotkeyProblem;      // atalho ocupado por outro programa

        public double EndOrFull => EndSec > 0 ? Math.Min(EndSec, Duration) : Duration;
        public double PlayLength => Math.Max(0, EndOrFull - StartSec);

        public SoundItem Clone() => (SoundItem)MemberwiseClone();
    }

    /// <summary>
    /// Biblioteca do sound pad em %APPDATA%\TakaVox\sons: copia do arquivo original (arquivos\), audio ja convertido
    /// para mono 48 kHz 16 bits (cache\, toca na hora, sem decodificar nada durante o jogo) e o indice (sons.ini).
    /// So a thread da interface mexe aqui (a importacao pesada roda em segundo plano e devolve o item pronto).
    /// </summary>
    public sealed class SoundLibrary
    {
        public static string Dir => Path.Combine(Log.Dir, "sons");
        static string FilesDir => Path.Combine(Dir, "arquivos");
        static string CacheDir => Path.Combine(Dir, "cache");
        static string IndexPath => Path.Combine(Dir, "sons.ini");

        /// <summary>Volume percebido de referencia: a voz tratada fica perto disso.</summary>
        public const float TargetLufs = -20f;
        public const int PeakBuckets = 240;
        /// <summary>Memoria maxima para sons carregados (os mais usados ficam; o resto e lido do disco na hora, ~1 ms).</summary>
        const long MaxCacheBytes = 96L << 20;

        public static readonly string[] Extensions =
            { ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac", ".ogg", ".opus", ".mp4", ".aif", ".aiff", ".caf", ".3gp", ".amr", ".webm", ".mka", ".alac" };

        public static readonly string[] Palette =
            { "#7C5CFF", "#E05A9C", "#2FA8E0", "#2BB673", "#F2A33A", "#E0533C", "#8E9AAF", "#C77DFF" };

        public readonly List<SoundItem> Items = new List<SoundItem>();
        public readonly List<string> Categories = new List<string>();

        readonly Dictionary<string, SoundClip> clips = new Dictionary<string, SoundClip>();
        readonly LinkedList<string> lru = new LinkedList<string>();
        long cachedBytes;

        public static bool IsAudioFile(string path)
        {
            string e = Path.GetExtension(path ?? "").ToLowerInvariant();
            return Extensions.Contains(e);
        }

        // ---------------------------------------------------------------- indice
        static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
        {
            var res = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return res;
            Dictionary<string, string> cur = null;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    res[line.Substring(1, line.Length - 2).Trim()] = cur;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || cur == null) continue;
                cur[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return res;
        }

        static string S(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string Clean(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        public void Load()
        {
            Items.Clear();
            Categories.Clear();
            try
            {
                var ini = ReadIni(IndexPath);
                if (ini.TryGetValue("pad", out var pad) && pad.TryGetValue("categorias", out var cats))
                    foreach (var c in cats.Split('|')) if (c.Trim().Length > 0 && !Categories.Contains(c.Trim())) Categories.Add(c.Trim());
                foreach (var kv in ini)
                {
                    if (!kv.Key.StartsWith("som", StringComparison.OrdinalIgnoreCase)) continue;
                    var d = kv.Value;
                    string G(string k, string def = "") => d.TryGetValue(k, out var v) ? v : def;
                    double D(string k, double def = 0) => double.TryParse(G(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : def;
                    var it = new SoundItem
                    {
                        Key = (int)D("chave"), Id = G("id"), Name = G("nome"), FileName = G("arquivo"), Emoji = G("emoji"), Color = G("cor"),
                        Category = G("categoria"), Hotkey = G("atalho"), VolumeDb = (float)D("volume"), Normalize = G("normalizar", "1") == "1",
                        StartSec = D("inicio"), EndSec = D("fim"), FadeInMs = (int)D("fadein", 4), FadeOutMs = (int)D("fadeout", 30),
                        Loop = G("loop") == "1", Mode = (int)D("modo"), Lufs = (float)D("lufs", -100), PeakDb = (float)D("pico", -120),
                        Duration = D("duracao"), Order = (int)D("ordem"),
                    };
                    try { it.Peaks = Convert.FromBase64String(G("picos")); } catch { it.Peaks = new byte[0]; }
                    if (it.Id.Length == 0 || it.FileName.Length == 0) continue;
                    if (!File.Exists(Path.Combine(FilesDir, it.FileName))) it.Problem = "o arquivo sumiu da pasta do sound pad";
                    Items.Add(it);
                }
                // chaves unicas e positivas
                var used = new HashSet<int>();
                foreach (var it in Items) if (it.Key <= 0 || !used.Add(it.Key)) it.Key = 0;
                int next = used.Count > 0 ? used.Max() + 1 : 1;
                foreach (var it in Items) if (it.Key == 0) { it.Key = next++; used.Add(it.Key); }
                Items.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Key.CompareTo(b.Key));
                foreach (var it in Items) if (it.Category.Length > 0 && !Categories.Contains(it.Category)) Categories.Add(it.Category);
            }
            catch (Exception ex) { Log.Write("Sound pad: falha ao ler a lista de sons: " + ex.Message); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("; TakaVox - sound pad (pode editar com o app fechado)");
                sb.AppendLine();
                sb.AppendLine("[pad]");
                sb.AppendLine("categorias=" + string.Join("|", Categories.Select(Clean)));
                for (int i = 0; i < Items.Count; i++)
                {
                    var it = Items[i];
                    it.Order = i + 1;
                    sb.AppendLine();
                    sb.AppendLine("[som" + it.Key + "]");
                    sb.AppendLine("chave=" + it.Key);
                    sb.AppendLine("id=" + it.Id);
                    sb.AppendLine("nome=" + Clean(it.Name));
                    sb.AppendLine("arquivo=" + it.FileName);
                    sb.AppendLine("emoji=" + Clean(it.Emoji));
                    sb.AppendLine("cor=" + Clean(it.Color));
                    sb.AppendLine("categoria=" + Clean(it.Category));
                    sb.AppendLine("atalho=" + Clean(it.Hotkey));
                    sb.AppendLine("volume=" + S(it.VolumeDb));
                    sb.AppendLine("normalizar=" + (it.Normalize ? "1" : "0"));
                    sb.AppendLine("inicio=" + S(it.StartSec));
                    sb.AppendLine("fim=" + S(it.EndSec));
                    sb.AppendLine("fadein=" + it.FadeInMs);
                    sb.AppendLine("fadeout=" + it.FadeOutMs);
                    sb.AppendLine("loop=" + (it.Loop ? "1" : "0"));
                    sb.AppendLine("modo=" + it.Mode);
                    sb.AppendLine("lufs=" + S(it.Lufs));
                    sb.AppendLine("pico=" + S(it.PeakDb));
                    sb.AppendLine("duracao=" + S(it.Duration));
                    sb.AppendLine("ordem=" + it.Order);
                    sb.AppendLine("picos=" + Convert.ToBase64String(it.Peaks ?? new byte[0]));
                }
                string tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(IndexPath)) File.Replace(tmp, IndexPath, null);
                else File.Move(tmp, IndexPath);
            }
            catch (Exception ex) { Log.Write("Sound pad: falha ao salvar a lista de sons: " + ex.Message); }
        }

        public int NextKey() => Items.Count == 0 ? 1 : Items.Max(i => i.Key) + 1;

        // ---------------------------------------------------------------- importar (pesado: rode fora da thread da interface)
        /// <summary>
        /// Converte um arquivo para o sound pad: copia o original, gera o audio pronto (mono 48 kHz), mede o volume e
        /// tira o silencio do comeco e do fim (sem apagar: da para mudar o trecho depois). Nao adiciona na lista.
        /// </summary>
        public static SoundItem ImportFile(string path, string category, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) { error = "arquivo não encontrado"; return null; }
                var x = SoundFile.LoadMono48k(path, out error, out _, out _);
                if (x == null) return null;
                if (x.Length < 480) { error = "som curto demais"; return null; }
                Directory.CreateDirectory(FilesDir);
                Directory.CreateDirectory(CacheDir);
                string id;
                do id = Guid.NewGuid().ToString("N").Substring(0, 10); while (File.Exists(Path.Combine(CacheDir, id + ".s16")));
                string ext = Path.GetExtension(path).ToLowerInvariant();
                string fileName = id + (ext.Length > 0 && ext.Length <= 6 ? ext : ".audio");
                File.Copy(path, Path.Combine(FilesDir, fileName), true);
                var it = new SoundItem { Id = id, FileName = fileName, Category = category ?? "" };
                string name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ').Trim();
                while (name.Contains("  ")) name = name.Replace("  ", " ");
                it.Name = name.Length > 40 ? name.Substring(0, 40).Trim() : name;
                Analyze(it, x);
                AutoTrim(it, x);
                it.Color = Palette[Math.Abs(it.Name.GetHashCode()) % Palette.Length];
                WriteCache(id, x);
                return it;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        /// <summary>Volume percebido, pico, duracao e forma de onda (do trecho que toca).</summary>
        static void Analyze(SoundItem it, float[] x)
        {
            it.Duration = x.Length / (double)SoundFile.Rate;
            var p = SoundFile.Peaks(x, PeakBuckets);
            it.Peaks = p.Select(v => (byte)Math.Round(Math.Min(1f, v) * 255)).ToArray();
            UpdateLoudness(it, x);
        }

        static void UpdateLoudness(SoundItem it, float[] x)
        {
            int a = (int)(it.StartSec * SoundFile.Rate), b = it.EndSec > 0 ? (int)(it.EndSec * SoundFile.Rate) : x.Length;
            a = Math.Max(0, Math.Min(a, x.Length)); b = Math.Max(a, Math.Min(b, x.Length));
            var seg = new float[b - a];
            Array.Copy(x, a, seg, 0, seg.Length);
            it.Lufs = SoundFile.LoudnessLufs(seg);
            it.PeakDb = SoundFile.PeakDb(seg);
        }

        /// <summary>Silencio no comeco (tocar na hora que apertar) e no fim: so marca o trecho.</summary>
        static void AutoTrim(SoundItem it, float[] x)
        {
            float thStart = 0.003f; // -50 dBFS
            float thEnd = 0.001f;   // -60 dBFS
            int first = 0;
            while (first < x.Length && Math.Abs(x[first]) < thStart) first++;
            int last = x.Length - 1;
            while (last > first && Math.Abs(x[last]) < thEnd) last--;
            if (first >= x.Length) return; // tudo silencio: deixa como esta
            double s = Math.Max(0, first - 0.005 * SoundFile.Rate) / SoundFile.Rate;
            double e = Math.Min(x.Length, last + 0.05 * SoundFile.Rate) / SoundFile.Rate;
            if (s > 0.02 && s < it.Duration - 0.05) it.StartSec = Math.Round(s, 3);
            if (e < it.Duration - 0.1 && e > it.StartSec + 0.05) it.EndSec = Math.Round(e, 3);
            UpdateLoudness(it, x);
        }

        static void WriteCache(string id, float[] x)
        {
            var pcm = SoundFile.ToPcm16(x);
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(CacheDir, id + ".s16"), bytes);
        }

        /// <summary>Depois de mudar o trecho no editor: mede de novo o volume percebido do trecho.</summary>
        public void Reanalyze(SoundItem it)
        {
            var c = GetClip(it, out _);
            if (c == null) return;
            var x = new float[c.Length];
            for (int i = 0; i < x.Length; i++) x[i] = c.Data[i] / 32768f;
            UpdateLoudness(it, x);
        }

        // ---------------------------------------------------------------- tocar
        /// <summary>O som pronto na memoria (le do cache; se o cache sumiu, gera de novo a partir do original).</summary>
        public SoundClip GetClip(SoundItem it, out string error)
        {
            error = null;
            if (clips.TryGetValue(it.Id, out var c))
            {
                lru.Remove(it.Id);
                lru.AddFirst(it.Id);
                return c;
            }
            try
            {
                string cache = Path.Combine(CacheDir, it.Id + ".s16");
                if (!File.Exists(cache))
                {
                    string orig = Path.Combine(FilesDir, it.FileName);
                    if (!File.Exists(orig)) { error = it.Problem = "o arquivo sumiu da pasta do sound pad"; return null; }
                    var x = SoundFile.LoadMono48k(orig, out error, out _, out _);
                    if (x == null) { it.Problem = error; return null; }
                    Directory.CreateDirectory(CacheDir);
                    WriteCache(it.Id, x);
                }
                var bytes = File.ReadAllBytes(cache);
                var pcm = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
                c = new SoundClip(pcm);
                clips[it.Id] = c;
                lru.AddFirst(it.Id);
                cachedBytes += bytes.Length;
                while (cachedBytes > MaxCacheBytes && lru.Count > 1)
                {
                    string old = lru.Last.Value;
                    lru.RemoveLast();
                    if (clips.TryGetValue(old, out var oc)) { cachedBytes -= oc.Length * 2L; clips.Remove(old); }
                }
                it.Problem = null;
                return c;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        /// <summary>Monta o pedido de tocar (volume, normalizacao com teto de pico, trecho, fades).</summary>
        public static SoundPlay MakePlay(SoundItem it, SoundClip clip, bool preview)
        {
            const int r = SoundFile.Rate;
            float norm = 0f;
            if (it.Normalize && it.Lufs > -70f)
            {
                norm = TargetLufs - it.Lufs;
                norm = Math.Max(-24f, Math.Min(18f, norm));
                norm = Math.Min(norm, -1f - it.PeakDb); // a normalizacao nunca faz o pico passar de -1 dBFS
            }
            float gainDb = norm + it.VolumeDb;
            int start = (int)Math.Round(it.StartSec * r), end = it.EndSec > 0 ? (int)Math.Round(it.EndSec * r) : clip.Length;
            return new SoundPlay
            {
                Key = it.Key, Clip = clip, Gain = (float)Math.Pow(10, gainDb / 20.0),
                Start = Math.Max(0, Math.Min(start, clip.Length)), End = Math.Max(0, Math.Min(end, clip.Length)),
                FadeIn = Math.Max(0, it.FadeInMs) * (r / 1000), FadeOut = Math.Max(0, it.FadeOutMs) * (r / 1000),
                Loop = it.Loop, Mode = it.Mode, PreviewOnly = preview,
            };
        }

        /// <summary>Apaga o som e os arquivos dele.</summary>
        public void Remove(SoundItem it)
        {
            Items.Remove(it);
            if (clips.TryGetValue(it.Id, out var c)) { cachedBytes -= c.Length * 2L; clips.Remove(it.Id); lru.Remove(it.Id); }
            try { File.Delete(Path.Combine(CacheDir, it.Id + ".s16")); } catch { }
            try { File.Delete(Path.Combine(FilesDir, it.FileName)); } catch { }
        }

        /// <summary>Libera a memoria dos sons carregados (os arquivos ficam no disco).</summary>
        public void TrimMemory()
        {
            clips.Clear();
            lru.Clear();
            cachedBytes = 0;
        }

        public long CachedBytes => cachedBytes;
    }
}
