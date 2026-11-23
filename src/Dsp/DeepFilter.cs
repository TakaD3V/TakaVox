using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TakaVox.Dsp
{
    /// <summary>Acesso a takavox_df.dll (IA DeepFilterNet3 compilada com os modelos embutidos).</summary>
    internal static unsafe class DeepFilterLib
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateFn(int lowLatency, float attenLim);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntFn(IntPtr st);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float ProcessFn(IntPtr st, float* input, float* output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetFn(IntPtr st, float v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Set3Fn(IntPtr st, float a, float b, float c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeFn(IntPtr st);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)] static extern IntPtr GetProcAddress(IntPtr module, string name);

        static CreateFn create; static IntFn frameLen, latency; static ProcessFn process;
        static SetFn setAtten, setPf; static Set3Fn setThr; static FreeFn free;
        static readonly object sync = new object();
        static bool initialized;
        public static bool Available { get; private set; }
        public static string Error { get; private set; } = "";

        public static bool Init()
        {
            lock (sync)
            {
                if (initialized) return Available;
                initialized = true;
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "takavox_df.dll");
                    if (!File.Exists(path)) { Error = "takavox_df.dll não encontrada"; return false; }
                    IntPtr h = LoadLibraryW(path);
                    if (h == IntPtr.Zero) { Error = "falha ao carregar takavox_df.dll (" + Marshal.GetLastWin32Error() + ")"; return false; }
                    T Get<T>(string n) where T : class
                    {
                        IntPtr p = GetProcAddress(h, n);
                        if (p == IntPtr.Zero) throw new EntryPointNotFoundException(n);
                        return Marshal.GetDelegateForFunctionPointer<T>(p);
                    }
                    create = Get<CreateFn>("tvdf_create");
                    frameLen = Get<IntFn>("tvdf_frame_length");
                    latency = Get<IntFn>("tvdf_latency");
                    process = Get<ProcessFn>("tvdf_process");
                    setAtten = Get<SetFn>("tvdf_set_atten_lim");
                    setPf = Get<SetFn>("tvdf_set_post_filter");
                    setThr = Get<Set3Fn>("tvdf_set_thresholds");
                    free = Get<FreeFn>("tvdf_free");
                    Available = true;
                    return true;
                }
                catch (Exception ex) { Error = ex.Message; return false; }
            }
        }

        public static IntPtr Create(bool lowLatency, float atten) => create(lowLatency ? 1 : 0, atten);
        public static int FrameLength(IntPtr st) => frameLen(st);
        public static int Latency(IntPtr st) => latency(st);
        public static float Process(IntPtr st, float* i, float* o) => process(st, i, o);
        public static void SetAtten(IntPtr st, float db) => setAtten(st, db);
        public static void SetPostFilter(IntPtr st, float beta) => setPf(st, beta);
        public static void SetThresholds(IntPtr st, float a, float b, float c) => setThr(st, a, b, c);
        public static void Free(IntPtr st) => free(st);
    }

    /// <summary>
    /// Reducao de ruido "pro" com DeepFilterNet3: remove teclado, mouse, ventilador, vozes ao fundo...
    /// Processa quadros de 480 amostras e devolve o SNR local (usado como detector de voz).
    /// Latencia fixa e conhecida (o caminho sem IA e atrasado igual para o A/B ficar alinhado).
    /// </summary>
    public sealed unsafe class DeepFilterDenoiser : IDisposable
    {
        IntPtr st;
        public bool LowLatency { get; }
        public int Latency { get; }
        public bool Ok => st != IntPtr.Zero;
        public float LastSnrDb { get; private set; } = -20f;
        readonly float[] inBuf = new float[VoiceChain.Block], outBuf = new float[VoiceChain.Block];
        float lastAtten = -1, lastPf = -1;
        int errors;

        public DeepFilterDenoiser(bool lowLatency)
        {
            LowLatency = lowLatency;
            if (!DeepFilterLib.Init()) return;
            try
            {
                st = DeepFilterLib.Create(lowLatency, 100f);
                if (st == IntPtr.Zero) return;
                if (DeepFilterLib.FrameLength(st) != VoiceChain.Block) { DeepFilterLib.Free(st); st = IntPtr.Zero; return; }
                Latency = DeepFilterLib.Latency(st);
            }
            catch { st = IntPtr.Zero; }
        }

        /// <summary>Processa 480 amostras no lugar. atten = limite de atenuacao (dB), pf = pos-filtro.</summary>
        public float Process(float[] block, float atten, float pf)
        {
            if (st == IntPtr.Zero) return float.NaN;
            // Com limite ~0 dB a DeepFilterNet pula o processamento E o atraso (e diz "SNR 35").
            // 0,5 dB e praticamente transparente e mantem a latencia constante (sem estalo ao ligar/desligar).
            if (atten < 0.5f) atten = 0.5f;
            if (Math.Abs(atten - lastAtten) > 0.01f) { DeepFilterLib.SetAtten(st, atten); lastAtten = atten; }
            if (Math.Abs(pf - lastPf) > 1e-5f) { DeepFilterLib.SetPostFilter(st, pf); lastPf = pf; }
            for (int i = 0; i < VoiceChain.Block; i++)
            {
                float v = block[i];
                inBuf[i] = v > 1.5f ? 1.5f : (v < -1.5f ? -1.5f : v);
            }
            float snr;
            fixed (float* pi = inBuf, po = outBuf) snr = DeepFilterLib.Process(st, pi, po);
            if (float.IsNaN(snr))
            {
                // erro interno: devolve o audio original (atrasado igual) em vez de silenciar
                if (++errors == 1) Log.Write("DeepFilterNet: erro ao processar um quadro (seguindo sem travar)");
                return float.NaN;
            }
            for (int i = 0; i < VoiceChain.Block; i++)
            {
                float o = outBuf[i];
                block[i] = float.IsNaN(o) || float.IsInfinity(o) ? 0f : o;
            }
            LastSnrDb = snr;
            return snr;
        }

        public void Dispose()
        {
            if (st != IntPtr.Zero) { try { DeepFilterLib.Free(st); } catch { } st = IntPtr.Zero; }
        }
    }
}
