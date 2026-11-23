using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TakaVox.Plugins
{
    /// <summary>Um efeito disponivel na biblioteca (resultado do escaneamento das pastas).</summary>
    public sealed class PluginEntry
    {
        public string Path = "";
        public int ClassIndex;
        public string Name = "", Vendor = "", Category = "";
        public int Format;            // 3 = VST3, 2 = VST2
        public bool IsInstrument;
        public string Error;          // != null: arquivo com problema (32 bits, travou...)
        public bool Hidden;           // DLL que nao e plugin (fica no cache para nao ler de novo)
        public long Stamp;

        public string FormatName => Format == 3 ? "VST3" : Format == 2 ? "VST2" : "VST";
        public bool Usable => Error == null && !IsInstrument && !Hidden;
        public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    }

    /// <summary>Uma posicao da cadeia: o plugin aberto ou, se nao abriu, o motivo (com o estado guardado para nao perder).</summary>
    public sealed class PluginSlot
    {
        public string Path = "";
        public int ClassIndex;
        public int Format;
        public string Name = "", Vendor = "";
        public PluginInstance Inst;
        public string Error;
        public byte[] SavedState;
        public bool CrashReported;
        bool enabled = true;

        public bool Enabled
        {
            get => Inst != null ? Inst.Enabled : enabled;
            set { enabled = value; if (Inst != null) Inst.Enabled = value; }
        }
        public bool Loaded => Inst != null && Inst.Alive;
        public bool Crashed => Inst != null && Inst.Crashed;
        public string Key => Path.TrimEnd('\\', '/').ToLowerInvariant() + "|" + ClassIndex;
        public string FormatName => Format == 3 ? "VST3" : "VST2";
    }

    /// <summary>
    /// Biblioteca, pastas e cadeia de plugins (tudo na thread da interface; o audio so ve a <see cref="PluginChain"/>).
    /// Protecoes: escaneamento num processo separado, marcador de "abrindo plugin" (se o app fechar no meio,
    /// na proxima vez esse plugin nao abre sozinho) e marcador de sessao (fechou sem querer -> plugins comecam desligados).
    /// </summary>
    public sealed class PluginManager
    {
        static string DirPath => Log.Dir;
        static string LibraryFile => System.IO.Path.Combine(DirPath, "plugins_lista.txt");
        static string ChainFile => System.IO.Path.Combine(DirPath, "plugins_cadeia.txt");
        static string FoldersFile => System.IO.Path.Combine(DirPath, "plugins_pastas.txt");
        static string OpeningFile => System.IO.Path.Combine(DirPath, "plugins_abrindo.txt");
        static string SessionFile => System.IO.Path.Combine(DirPath, "plugins_sessao.txt");

        public readonly PluginChain Chain = new PluginChain();
        readonly List<PluginSlot> slots = new List<PluginSlot>();
        public IReadOnlyList<PluginSlot> Slots => slots;
        public List<PluginEntry> Library { get; private set; } = new List<PluginEntry>();
        public int LibraryVersion { get; private set; }
        public readonly List<string> UserFolders = new List<string>();

        public bool Available { get; private set; }
        public string UnavailableReason { get; private set; } = "";
        public bool Started { get; private set; }
        public bool Scanning { get; private set; }
        public bool ScannedThisSession { get; private set; }
        public string ScanStatus { get; private set; } = "";

        /// <summary>A cadeia ou a biblioteca mudou (a interface redesenha).</summary>
        public event Action Changed;
        /// <summary>Aviso importante para o usuario (plugin travou etc.).</summary>
        public event Action<string> Notice;
        /// <summary>O plugin mudou os proprios parametros (o editor generico atualiza).</summary>
        public event Action<PluginSlot> ParamsChanged;

        string lastSavedChain;
        DateTime lastAutoSave = DateTime.Now;

        // ================================================================== pastas

        /// <summary>Efeitos gratuitos que vem com o TakaVox (pasta "plugins" do app).</summary>
        public static string BundledFolder => System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");

        public static bool IsBundled(string path) =>
            path.StartsWith(BundledFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        /// <summary>Do pacote incluso, os que nao fazem sentido na voz (geradores de tom, so-estereo, medidor sem tela).</summary>
        static readonly HashSet<string> BundledHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mda TestTone", "mda SpecMeter", "mda Image", "mda Stereo Simulator", "mda Round Panner",
            "mda Splitter", "mda Dither", "mda BeatBox", "mda Shepard", "mda TalkBox",
        };

        static readonly Dictionary<string, string> BundledInfo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mda Ambience"] = "reverb de sala pequena",
            ["mda Bandisto"] = "distorção por faixas de frequência",
            ["mda Combo"] = "amplificador / rádio antigo",
            ["mda DeEsser"] = "tira o chiado do S",
            ["mda Degrade"] = "lo-fi: rádio, telefone, voz de máquina",
            ["mda Delay"] = "eco",
            ["mda Detune"] = "dobra a voz (efeito coro)",
            ["mda DubDelay"] = "eco longo com repetições (estilo dub)",
            ["mda Dynamics"] = "compressor e portão completos",
            ["mda Leslie"] = "caixa rotativa (som ondulado)",
            ["mda Limiter"] = "limitador",
            ["mda Loudness"] = "realce de graves e agudos",
            ["mda MultiBand"] = "compressor de 3 faixas",
            ["mda Overdrive"] = "saturação suave",
            ["mda RePsycho!"] = "efeitos malucos de tom",
            ["mda RezFilter"] = "filtro ressonante (wah)",
            ["mda RingMod"] = "voz de robô",
            ["mda Sub-Bass Synthesizer"] = "voz mais grave (gera sub-grave)",
            ["mda Thru-Zero Flanger"] = "flanger (som de avião a jato)",
            ["mda Tracker"] = "transforma a voz em sintetizador",
        };

        /// <summary>Descricao curta em portugues para os efeitos que vem com o app (ou "").</summary>
        public static string Describe(PluginEntry e) => Describe(e.Path, e.Name);
        public static string Describe(string path, string name) => IsBundled(path) && BundledInfo.TryGetValue(name, out var d) ? d : "";

        internal static void ApplyBundledRules(PluginEntry e)
        {
            if (IsBundled(e.Path) && BundledHidden.Contains(e.Name)) e.Hidden = true;
        }

        public static List<string> DefaultFolders()
        {
            var l = new List<string> { BundledFolder };
            string cpf = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            l.Add(System.IO.Path.Combine(cpf, "VST3"));
            l.Add(System.IO.Path.Combine(la, "Programs", "Common", "VST3"));
            l.Add(System.IO.Path.Combine(cpf, "VST2"));
            l.Add(System.IO.Path.Combine(cpf, "Steinberg", "VST2"));
            l.Add(System.IO.Path.Combine(pf, "VSTPlugins"));
            l.Add(System.IO.Path.Combine(pf, "Steinberg", "VSTPlugins"));
            try
            {
                foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                    using (var k = hive.OpenSubKey(@"SOFTWARE\VST"))
                        if (k?.GetValue("VSTPluginsPath") is string v && !string.IsNullOrWhiteSpace(v)) l.Add(v.Trim().TrimEnd('\\'));
            }
            catch { }
            return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public List<string> AllFolders() => DefaultFolders().Concat(UserFolders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public void AddFolder(string dir)
        {
            dir = dir.TrimEnd('\\');
            if (AllFolders().Any(f => string.Equals(f, dir, StringComparison.OrdinalIgnoreCase))) return;
            UserFolders.Add(dir);
            SaveFolders();
            Log.Change("Plugins: pasta adicionada → " + dir);
            Changed?.Invoke();
        }

        public void RemoveFolder(string dir)
        {
            if (UserFolders.RemoveAll(f => string.Equals(f, dir, StringComparison.OrdinalIgnoreCase)) == 0) return;
            SaveFolders();
            Changed?.Invoke();
        }

        void LoadFolders()
        {
            UserFolders.Clear();
            try
            {
                if (File.Exists(FoldersFile))
                    foreach (var l in File.ReadAllLines(FoldersFile, Encoding.UTF8))
                        if (l.Trim().Length > 0 && !l.StartsWith("#")) UserFolders.Add(l.Trim());
            }
            catch (Exception ex) { Log.Write("Plugins: pastas: " + ex.Message); }
        }

        void SaveFolders()
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                File.WriteAllLines(FoldersFile, new[] { "# TakaVox - pastas extras de plugins (uma por linha)" }.Concat(UserFolders), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("Plugins: salvar pastas: " + ex.Message); }
        }

        // ================================================================== inicio / fim

        /// <summary>Le pastas, biblioteca e a cadeia salva e abre os plugins dela (thread da interface).</summary>
        public void Start()
        {
            if (Started) return;
            Started = true;
            Available = Tvh.Init(out string err);
            if (!Available)
            {
                UnavailableReason = err ?? "hospedeiro de plugins indisponível";
                Log.Write("Plugins: " + UnavailableReason);
            }
            LoadFolders();
            LoadLibrary();
            LoadChainFile();
            if (!Available)
            {
                foreach (var s in slots) s.Error = UnavailableReason;
                Changed?.Invoke();
                return;
            }

            string crashedKey = null;
            try
            {
                if (File.Exists(OpeningFile))
                {
                    crashedKey = File.ReadAllText(OpeningFile).Trim();
                    File.Delete(OpeningFile);
                }
            }
            catch { }
            bool unclean = File.Exists(SessionFile) && slots.Count > 0;

            foreach (var s in slots)
            {
                if (crashedKey != null && s.Key == crashedKey)
                {
                    s.Enabled = false;
                    s.Error = "fechou o TakaVox ao abrir da última vez (ficou desligado por segurança)";
                    Log.Change($"Plugin '{s.Name}' fechou o TakaVox ao abrir: ficou desativado. Na página Plugins dá para tentar de novo ou remover.");
                    Notice?.Invoke($"O plugin '{s.Name}' fechou o TakaVox ao abrir da última vez, então ficou desativado. Você pode tentar abrir de novo ou removê-lo na página Plugins.");
                    continue;
                }
                LoadSlot(s);
            }
            if (unclean && slots.Any(x => x.Loaded) && !Chain.Bypass)
            {
                Chain.Bypass = true;
                Log.Change("O TakaVox fechou de forma inesperada da última vez: os plugins começaram desligados por segurança.");
                Notice?.Invoke("O TakaVox fechou de forma inesperada da última vez. Por segurança, os plugins começaram desligados — ligue de novo na página Plugins.");
            }
            Publish();
            lastSavedChain = ChainText();
            MarkSession();
            Changed?.Invoke();
        }

        /// <summary>Saida normal do app: salva o estado dos plugins e fecha as janelas deles.</summary>
        public void Shutdown()
        {
            try { SaveChain(); } catch { }
            foreach (var s in slots) try { s.Inst?.CloseEditor(); } catch { }
            try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { }
        }

        void MarkSession()
        {
            try
            {
                if (slots.Any(s => s.Loaded)) File.WriteAllText(SessionFile, DateTime.Now.ToString("s"));
                else if (File.Exists(SessionFile)) File.Delete(SessionFile);
            }
            catch { }
        }

        static void WriteOpening(string key)
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                using (var fs = new FileStream(OpeningFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var b = Encoding.UTF8.GetBytes(key);
                    fs.Write(b, 0, b.Length);
                }
            }
            catch { }
        }

        static void ClearOpening()
        {
            try { if (File.Exists(OpeningFile)) File.Delete(OpeningFile); } catch { }
        }

        // ================================================================== cadeia

        bool LoadSlot(PluginSlot s)
        {
            s.Error = null;
            s.CrashReported = false;
            bool en = s.Enabled;
            WriteOpening(s.Key);
            PluginInstance inst;
            string err;
            try { inst = PluginInstance.Load(s.Path, s.ClassIndex, s.Vendor, s.SavedState, out err); }
            catch (Exception ex) { inst = null; err = ex.Message; }
            ClearOpening();
            if (inst == null)
            {
                s.Error = err ?? "não abriu";
                Log.Write($"Plugin '{s.Name}' não abriu: {s.Error}");
                return false;
            }
            s.Inst = inst;
            inst.Enabled = en;
            if (string.IsNullOrEmpty(s.Name)) s.Name = inst.Name;
            s.Format = inst.Format;
            return true;
        }

        bool Publish()
        {
            bool ok = Chain.Set(slots.Where(x => x.Loaded).Select(x => x.Inst).ToArray());
            if (!ok) Log.Write("Plugins: a thread de áudio não respondeu a tempo durante a troca da cadeia.");
            return ok;
        }

        public PluginSlot Add(PluginEntry e, out string error)
        {
            error = null;
            if (!Available) { error = UnavailableReason; return null; }
            if (e.IsInstrument) { error = "é um instrumento (sintetizador): não processa a sua voz"; return null; }
            if (e.Error != null || e.Hidden) { error = e.Error ?? "não é um plugin de efeito"; return null; } // ja deu problema ao ser lido: nao abre aqui dentro
            var s = new PluginSlot { Path = e.Path, ClassIndex = e.ClassIndex, Format = e.Format, Name = e.Name, Vendor = e.Vendor };
            if (!LoadSlot(s)) { error = s.Error; return null; }
            slots.Add(s);
            Publish();
            double ms = s.Inst.Latency * 1000.0 / Tvh.SampleRate;
            Log.Change($"Plugin adicionado: {s.Name} ({s.Inst.FormatName}{(s.Inst.Latency > 0 ? $", atraso de {ms:0.#} ms" : "")})");
            SaveChain();
            MarkSession();
            Changed?.Invoke();
            return s;
        }

        public void Remove(PluginSlot s)
        {
            int i = slots.IndexOf(s);
            if (i < 0) return;
            slots.RemoveAt(i);
            var inst = s.Inst;
            s.Inst = null;
            bool safe = Publish();
            if (inst != null)
            {
                try { inst.CloseEditor(); } catch { }
                if (safe) inst.Free();
            }
            Log.Change("Plugin removido: " + s.Name);
            SaveChain();
            MarkSession();
            Changed?.Invoke();
        }

        public void Move(PluginSlot s, int delta)
        {
            int i = slots.IndexOf(s), j = i + delta;
            if (i < 0 || j < 0 || j >= slots.Count) return;
            slots.RemoveAt(i);
            slots.Insert(j, s);
            Publish();
            SaveChain();
            Changed?.Invoke();
        }

        public void SetEnabled(PluginSlot s, bool on)
        {
            if (s.Enabled == on) return;
            s.Enabled = on;
            Chain.UpdateLatency();
            Log.Write($"Plugin '{s.Name}' {(on ? "ligado" : "desligado")}");
            SaveChain();
            Changed?.Invoke();
        }

        public void SetAllOn(bool on)
        {
            if (Chain.Bypass == !on) return;
            Chain.Bypass = !on;
            Log.Change(on ? "Plugins VST ligados." : "Plugins VST desligados (todos).");
            SaveChain();
            Changed?.Invoke();
        }

        /// <summary>Abre de novo um plugin que travou ou nao abriu (usa o ultimo estado salvo).</summary>
        public bool Reload(PluginSlot s, out string error)
        {
            error = null;
            if (!Available) { error = UnavailableReason; return false; }
            var old = s.Inst;
            if (old != null)
            {
                if (!old.Crashed)
                {
                    var st = old.GetState();
                    if (st != null) s.SavedState = st;
                }
                s.Inst = null;
                bool safe = Publish();
                try { old.CloseEditor(); } catch { }
                if (safe) old.Free();
            }
            if (!s.Enabled) s.Enabled = true;
            bool ok = LoadSlot(s);
            if (!ok) error = s.Error;
            Publish();
            SaveChain();
            MarkSession();
            Changed?.Invoke();
            return ok;
        }

        /// <summary>Chamar ~20-30x por segundo pela interface (mesmo com a janela escondida).</summary>
        public void Tick()
        {
            if (!Available) return;
            foreach (var s in slots.ToArray())
            {
                var inst = s.Inst;
                if (inst == null || !inst.Alive || inst.Crashed) continue;
                inst.Idle();
                int f = inst.Poll();
                if ((f & Tvh.PendLatency) != 0) ApplyNewLatency(s);
                if ((f & Tvh.PendParams) != 0) ParamsChanged?.Invoke(s);
            }
            if (Chain.CrashPending)
            {
                Chain.CrashPending = false;
                Chain.UpdateLatency();
                foreach (var s in slots)
                    if (s.Crashed && !s.CrashReported)
                    {
                        s.CrashReported = true;
                        try { s.Inst.CloseEditor(); } catch { }
                        Log.Change($"O plugin '{s.Name}' travou e foi desligado automaticamente (o resto continua funcionando).");
                        Notice?.Invoke($"O plugin '{s.Name}' travou e foi desligado. Sua voz continua passando pelo resto da cadeia. Dá para abrir de novo na página Plugins.");
                    }
                Changed?.Invoke();
            }
            if ((DateTime.Now - lastAutoSave).TotalSeconds > 20)
            {
                lastAutoSave = DateTime.Now;
                SaveChain();
            }
        }

        void ApplyNewLatency(PluginSlot s)
        {
            // tira da cadeia, reinicia e devolve: o audio nunca ve o plugin no meio da troca
            var without = slots.Where(x => x.Loaded && x != s).Select(x => x.Inst).ToArray();
            if (!Chain.Set(without)) { Publish(); return; }
            s.Inst.Reactivate();
            Publish();
            Log.Write($"Plugin '{s.Name}': latência nova = {s.Inst.Latency} amostras");
            Changed?.Invoke();
        }

        // ================================================================== arquivo da cadeia

        public void SaveChain()
        {
            try
            {
                string text = ChainText();
                if (text == lastSavedChain) return;
                Directory.CreateDirectory(DirPath);
                string tmp = ChainFile + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(ChainFile)) File.Replace(tmp, ChainFile, null);
                else File.Move(tmp, ChainFile);
                lastSavedChain = text;
            }
            catch (Exception ex) { Log.Write("Plugins: falha ao salvar a cadeia: " + ex.Message); }
        }

        string ChainText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# TakaVox - cadeia de plugins (gerado pelo app). ligado, formato, efeito, arquivo, nome, fabricante, estado (base64)");
            sb.AppendLine("ligados=" + (Chain.Bypass ? "0" : "1"));
            foreach (var s in slots)
            {
                byte[] st = s.SavedState;
                if (s.Loaded && !s.Crashed)
                {
                    var now = s.Inst.GetState();
                    if (now != null) st = s.SavedState = now;
                }
                sb.Append(s.Enabled ? '1' : '0').Append('\t').Append(s.Format).Append('\t').Append(s.ClassIndex).Append('\t')
                  .Append(Clean(s.Path)).Append('\t').Append(Clean(s.Name)).Append('\t').Append(Clean(s.Vendor)).Append('\t')
                  .Append(st != null ? Convert.ToBase64String(st) : "").AppendLine();
            }
            return sb.ToString();
        }

        void LoadChainFile()
        {
            slots.Clear();
            try
            {
                if (!File.Exists(ChainFile)) return;
                foreach (var line in File.ReadAllLines(ChainFile, Encoding.UTF8))
                {
                    if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                    if (line.StartsWith("ligados=")) { Chain.Bypass = line.Substring(8).Trim() == "0"; continue; }
                    var f = line.Split('\t');
                    if (f.Length < 7 || f[3].Length == 0) continue;
                    var s = new PluginSlot { Format = Int(f[1]), ClassIndex = Int(f[2]), Path = f[3], Name = f[4], Vendor = f[5] };
                    s.Enabled = f[0] != "0";
                    if (f[6].Length > 0) try { s.SavedState = Convert.FromBase64String(f[6]); } catch { }
                    slots.Add(s);
                }
            }
            catch (Exception ex) { Log.Write("Plugins: falha ao ler a cadeia: " + ex.Message); }
        }

        // ================================================================== biblioteca

        void LoadLibrary()
        {
            var list = new List<PluginEntry>();
            try
            {
                if (File.Exists(LibraryFile))
                    foreach (var line in File.ReadAllLines(LibraryFile, Encoding.UTF8))
                    {
                        if (line.StartsWith("#")) continue;
                        var f = line.Split('\t');
                        if (f.Length < 10) continue;
                        list.Add(new PluginEntry
                        {
                            Stamp = long.TryParse(f[0], out long st) ? st : 0, Format = Int(f[1]), ClassIndex = Int(f[2]),
                            IsInstrument = f[3] == "1", Hidden = f[4] == "1", Path = f[5], Name = f[6], Vendor = f[7], Category = f[8],
                            Error = f[9].Length > 0 ? f[9] : null,
                        });
                    }
            }
            catch (Exception ex) { Log.Write("Plugins: lista: " + ex.Message); }
            Library = list;
            LibraryVersion++;
        }

        void SaveLibrary()
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                var sb = new StringBuilder();
                sb.AppendLine("# TakaVox - plugins encontrados (cache do escaneamento; pode apagar para procurar tudo de novo)");
                foreach (var e in Library)
                    sb.Append(e.Stamp).Append('\t').Append(e.Format).Append('\t').Append(e.ClassIndex).Append('\t')
                      .Append(e.IsInstrument ? '1' : '0').Append('\t').Append(e.Hidden ? '1' : '0').Append('\t')
                      .Append(Clean(e.Path)).Append('\t').Append(Clean(e.Name)).Append('\t').Append(Clean(e.Vendor)).Append('\t')
                      .Append(Clean(e.Category)).Append('\t').Append(Clean(e.Error ?? "")).AppendLine();
                File.WriteAllText(LibraryFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("Plugins: salvar lista: " + ex.Message); }
        }

        /// <summary>Procura plugins nas pastas (em segundo plano; cada arquivo novo e lido num processo separado).</summary>
        public void ScanAsync(bool everything)
        {
            if (Scanning || !Available) return;
            Scanning = true;
            ScannedThisSession = true;
            ScanStatus = "Procurando plugins nas pastas…";
            Changed?.Invoke();
            var ctx = SynchronizationContext.Current;
            var folders = AllFolders();
            var cache = everything ? new List<PluginEntry>() : Library.ToList();
            Task.Run(() =>
            {
                List<PluginEntry> result = null;
                try
                {
                    result = PluginScanner.Scan(folders, cache, msg => Post(ctx, () => { ScanStatus = msg; Changed?.Invoke(); }));
                }
                catch (Exception ex) { Log.Write("Plugins: erro ao procurar: " + ex); }
                Post(ctx, () =>
                {
                    Scanning = false;
                    if (result != null)
                    {
                        int before = Library.Count(x => x.Usable);
                        Library = result;
                        LibraryVersion++;
                        SaveLibrary();
                        int ok = Library.Count(x => x.Usable), bad = Library.Count(x => x.Error != null && !x.Hidden);
                        ScanStatus = (ok == 0 ? "Nenhum efeito encontrado ainda" : ok == 1 ? "1 efeito encontrado" : ok + " efeitos encontrados")
                                     + (bad > 0 ? $" · {bad} arquivo(s) com problema" : "") + ".";
                        if (ok != before) Log.Write($"Plugins: {ok} efeito(s) disponíveis ({bad} com problema).");
                    }
                    else ScanStatus = "Não deu para procurar os plugins (detalhes no log).";
                    Changed?.Invoke();
                });
            });
        }

        static void Post(SynchronizationContext ctx, Action a)
        {
            if (ctx != null) ctx.Post(_ => { try { a(); } catch (Exception ex) { Log.Write("Plugins: " + ex.Message); } }, null);
            else a();
        }

        internal static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        internal static int Int(string s) => int.TryParse(s, out int v) ? v : 0;
    }
}
