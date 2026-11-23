using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace TakaVox.Plugins
{
    /// <summary>Um efeito encontrado num arquivo de plugin (o mesmo arquivo pode ter varios).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TvhInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Category;
        public int Format;       // 3 = VST3, 2 = VST2
        public int ClassIndex;
        public int IsInstrument;
    }

    /// <summary>Funcoes do tvhost.dll (hospedeiro nativo de plugins VST3/VST2).</summary>
    [SuppressUnmanagedCodeSecurity]
    public static unsafe class Tvh
    {
        const string D = "tvhost.dll";
        const CallingConvention C = CallingConvention.Cdecl;
        const CharSet U = CharSet.Unicode;

        [DllImport(D, CallingConvention = C)] public static extern int tvh_init();
        [DllImport(D, CallingConvention = C, CharSet = U)] public static extern int tvh_scan(string path, [Out] TvhInfo[] info, int max, StringBuilder err, int errCap);
        [DllImport(D, CallingConvention = C, CharSet = U)] public static extern IntPtr tvh_load(string path, int classIndex, double sampleRate, int maxBlock, byte[] state, int stateLen, StringBuilder err, int errCap);
        [DllImport(D, CallingConvention = C, CharSet = U)] public static extern int tvh_get_name(IntPtr h, StringBuilder name, int cap);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_latency(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_failed(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_channels(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_poll(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_process(IntPtr h, float* io, int n);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_reactivate(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern void tvh_idle(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_param_count(IntPtr h);
        [DllImport(D, CallingConvention = C, CharSet = U)] public static extern int tvh_param_info(IntPtr h, int i, StringBuilder name, int ncap, StringBuilder display, int dcap, out double value, out int steps, out double def);
        [DllImport(D, CallingConvention = C)] public static extern void tvh_param_set(IntPtr h, int i, double v);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_param_id(IntPtr h, int i);
        [DllImport(D, CallingConvention = C)] public static extern double tvh_param_to_norm(IntPtr h, int id, double plain);
        [DllImport(D, CallingConvention = C)] public static extern double tvh_param_to_plain(IntPtr h, int id, double norm);
        [DllImport(D, CallingConvention = C)] public static extern double tvh_param_get_id(IntPtr h, int id);
        [DllImport(D, CallingConvention = C)] public static extern void tvh_param_set_id(IntPtr h, int id, double v);
        [DllImport(D, CallingConvention = C, CharSet = U)] public static extern int tvh_param_text(IntPtr h, int id, double norm, StringBuilder text, int cap);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_get_state(IntPtr h, byte[] buf, int cap);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_set_state(IntPtr h, byte[] buf, int len);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_has_editor(IntPtr h);
        [DllImport(D, CallingConvention = C, CharSet = U)] public static extern IntPtr tvh_open_editor(IntPtr h, IntPtr owner, string title);
        [DllImport(D, CallingConvention = C)] public static extern void tvh_close_editor(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_editor_open(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern IntPtr tvh_editor_create(IntPtr h, IntPtr parent, out int w, out int hgt);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_editor_resizable(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_program_count(IntPtr h);
        [DllImport(D, CallingConvention = C, CharSet = U)] public static extern int tvh_program_name(IntPtr h, int i, StringBuilder name, int cap);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_get_program(IntPtr h);
        [DllImport(D, CallingConvention = C)] public static extern int tvh_set_program(IntPtr h, int i);
        [DllImport(D, CallingConvention = C)] public static extern void tvh_free(IntPtr h);

        public const int SampleRate = 48000, MaxBlock = 480;
        public const int PendLatency = 1, PendParams = 2, PendRestart = 4;

        static int inited;
        static string initError;

        /// <summary>Inicia o hospedeiro (thread da interface). Falso se o tvhost.dll nao estiver disponivel.</summary>
        public static bool Init(out string error)
        {
            if (inited == 0)
            {
                try { tvh_init(); inited = 1; }
                catch (Exception ex) { inited = -1; initError = ex is DllNotFoundException ? "tvhost.dll não encontrado (reinstale o TakaVox)" : ex.Message; }
            }
            error = initError;
            return inited == 1;
        }

        /// <summary>Le os efeitos de um arquivo de plugin (use num processo separado: plugin ruim pode travar).</summary>
        public static TvhInfo[] Scan(string path, out string error)
        {
            var info = new TvhInfo[64];
            var err = new StringBuilder(512);
            int n = tvh_scan(path, info, info.Length, err, err.Capacity);
            error = n < 0 ? err.ToString() : null;
            if (n <= 0) return new TvhInfo[0];
            if (n > info.Length) n = info.Length;
            Array.Resize(ref info, n);
            return info;
        }
    }

    /// <summary>
    /// Um plugin carregado e ativo. Tudo aqui e da thread da interface, menos o processamento,
    /// que e feito pela <see cref="PluginChain"/> na thread de audio.
    /// </summary>
    public sealed class PluginInstance
    {
        public readonly string Path;
        public readonly int ClassIndex;
        public readonly int Format;
        public readonly string Vendor;
        public string Name { get; private set; }
        public IntPtr Handle { get; private set; }
        public int Latency { get; private set; }
        public int Channels { get; private set; }

        /// <summary>Ligado na cadeia (lido pela thread de audio).</summary>
        public volatile bool Enabled = true;
        /// <summary>Travou no processamento: fica desligado ate ser removido/recarregado.</summary>
        public volatile bool Crashed;

        int hasEditor = -1;

        PluginInstance(string path, int classIndex, int format, string vendor, IntPtr h)
        {
            Path = path; ClassIndex = classIndex; Format = format; Vendor = vendor ?? ""; Handle = h;
            var sb = new StringBuilder(256);
            Tvh.tvh_get_name(h, sb, sb.Capacity);
            Name = sb.Length > 0 ? sb.ToString() : System.IO.Path.GetFileNameWithoutExtension(path);
            Latency = Math.Max(0, Tvh.tvh_latency(h));
            Channels = Tvh.tvh_channels(h);
        }

        public static PluginInstance Load(string path, int classIndex, string vendor, byte[] state, out string error)
        {
            error = null;
            if (!Tvh.Init(out error)) return null;
            var err = new StringBuilder(512);
            IntPtr h = Tvh.tvh_load(path, classIndex, Tvh.SampleRate, Tvh.MaxBlock, state, state?.Length ?? 0, err, err.Capacity);
            if (h == IntPtr.Zero) { error = err.Length > 0 ? err.ToString() : "não abriu"; return null; }
            int format = path.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase) || path.TrimEnd('\\', '/').EndsWith(".vst3", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
            return new PluginInstance(path, classIndex, format, vendor, h);
        }

        public bool Alive => Handle != IntPtr.Zero;
        public string FormatName => Format == 3 ? "VST3" : "VST2";

        public bool HasEditor
        {
            get
            {
                if (!Alive) return false;
                if (hasEditor < 0) hasEditor = Tvh.tvh_has_editor(Handle);
                return hasEditor == 1;
            }
        }

        public bool EditorOpen => Alive && Tvh.tvh_editor_open(Handle) == 1;

        /// <summary>Abre (ou traz para frente) a janela original do plugin. Retorna o HWND ou zero.</summary>
        public IntPtr OpenEditor(IntPtr owner, string title)
        {
            if (!Alive || Crashed) return IntPtr.Zero;
            return Tvh.tvh_open_editor(Handle, owner, title);
        }

        public void CloseEditor() { if (Alive) Tvh.tvh_close_editor(Handle); }

        /// <summary>Abre a janela original do plugin DENTRO de 'parent' (janela filha). HWND (tamanho em pixels) ou zero.</summary>
        public IntPtr CreateEditorIn(IntPtr parent, out int w, out int h)
        {
            w = h = 0;
            if (!Alive || Crashed) return IntPtr.Zero;
            return Tvh.tvh_editor_create(Handle, parent, out w, out h);
        }

        /// <summary>A janela original aceita mudar de tamanho?</summary>
        public bool EditorResizable => Alive && Tvh.tvh_editor_resizable(Handle) == 1;

        // ---------------------------------------------------------------- programas (presets de fabrica)
        public int ProgramCount => Alive && !Crashed ? Math.Max(0, Tvh.tvh_program_count(Handle)) : 0;

        public string ProgramName(int i)
        {
            if (!Alive || Crashed) return "";
            var sb = new StringBuilder(256);
            return Tvh.tvh_program_name(Handle, i, sb, sb.Capacity) == 1 ? sb.ToString() : "";
        }

        public int CurrentProgram => Alive && !Crashed ? Tvh.tvh_get_program(Handle) : -1;

        public bool SetProgram(int i) => Alive && !Crashed && Tvh.tvh_set_program(Handle, i) == 1;

        /// <summary>Avisos do plugin desde a ultima chamada (Tvh.PendLatency etc.).</summary>
        public int Poll() => Alive ? Tvh.tvh_poll(Handle) : 0;

        public void Idle() { if (Alive && !Crashed) Tvh.tvh_idle(Handle); }

        /// <summary>Reinicia o processamento para aplicar uma nova latencia. So FORA da cadeia de audio.</summary>
        public void Reactivate()
        {
            if (!Alive || Crashed) return;
            Tvh.tvh_reactivate(Handle);
            Latency = Math.Max(0, Tvh.tvh_latency(Handle));
        }

        public bool CheckCrashed()
        {
            if (!Crashed && Alive && Tvh.tvh_failed(Handle) != 0) Crashed = true;
            return Crashed;
        }

        public byte[] GetState()
        {
            if (!Alive || Crashed) return null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int n = Tvh.tvh_get_state(Handle, null, 0);
                if (n <= 0) return null;
                var b = new byte[n];
                int m = Tvh.tvh_get_state(Handle, b, n);
                if (m == n) return b;
            }
            return null;
        }

        public bool SetState(byte[] s) => Alive && !Crashed && s != null && Tvh.tvh_set_state(Handle, s, s.Length) == 1;

        // ---------------------------------------------------------------- parametros (editor generico)
        public int ParamCount => Alive ? Tvh.tvh_param_count(Handle) : 0;

        public sealed class ParamInfo
        {
            public int Index;
            public string Name, Display;
            public double Value, Default;
            public int Steps;
            public bool IsBypass;
        }

        /// <summary>Le o parametro i. Null se ele e escondido/somente leitura.</summary>
        public ParamInfo GetParam(int i)
        {
            if (!Alive) return null;
            var nm = new StringBuilder(256);
            var ds = new StringBuilder(256);
            int r = Tvh.tvh_param_info(Handle, i, nm, nm.Capacity, ds, ds.Capacity, out double v, out int steps, out double def);
            if (r == 0) return null;
            return new ParamInfo { Index = i, Name = nm.ToString(), Display = ds.ToString(), Value = v, Steps = steps, Default = def, IsBypass = r == 2 };
        }

        public void SetParam(int i, double v) { if (Alive && !Crashed) Tvh.tvh_param_set(Handle, i, v); }

        /// <summary>Libera o plugin. So depois de tirado da cadeia de audio.</summary>
        public void Free()
        {
            if (!Alive) return;
            IntPtr h = Handle;
            Handle = IntPtr.Zero;
            Tvh.tvh_free(h);
        }
    }
}
