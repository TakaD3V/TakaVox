using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TakaVox.Dsp
{
    /// <summary>Carrega a DLL do RNNoise 0.2 (AVX2 quando o processador suporta, senao SSE2).</summary>
    internal static unsafe class RnnoiseLib
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateFn(IntPtr model);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyFn(IntPtr st);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float ProcessFn(IntPtr st, float* output, float* input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FrameSizeFn();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")]
        static extern bool IsProcessorFeaturePresent(int feature);

        static CreateFn create;
        static DestroyFn destroy;
        static ProcessFn process;
        static readonly object sync = new object();
        static bool initialized;

        public static bool Available { get; private set; }
        public static string Variant { get; private set; } = "";
        public static string Error { get; private set; } = "";

        public static bool Init()
        {
            lock (sync)
            {
                if (initialized) return Available;
                initialized = true;
                try
                {
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    bool avx2 = false;
                    try { avx2 = IsProcessorFeaturePresent(40); } catch { }
                    string[] names = avx2 ? new[] { "rnnoise_avx2.dll", "rnnoise_sse2.dll" } : new[] { "rnnoise_sse2.dll" };
                    foreach (var name in names)
                    {
                        string path = Path.Combine(dir, name);
                        if (!File.Exists(path)) continue;
                        IntPtr h = LoadLibraryW(path);
                        if (h == IntPtr.Zero) continue;
                        IntPtr pc = GetProcAddress(h, "rnnoise_create");
                        IntPtr pd = GetProcAddress(h, "rnnoise_destroy");
                        IntPtr pp = GetProcAddress(h, "rnnoise_process_frame");
                        IntPtr pf = GetProcAddress(h, "rnnoise_get_frame_size");
                        if (pc == IntPtr.Zero || pd == IntPtr.Zero || pp == IntPtr.Zero || pf == IntPtr.Zero) continue;
                        var frameSize = Marshal.GetDelegateForFunctionPointer<FrameSizeFn>(pf);
                        if (frameSize() != VoiceChain.Block) continue;
                        create = Marshal.GetDelegateForFunctionPointer<CreateFn>(pc);
                        destroy = Marshal.GetDelegateForFunctionPointer<DestroyFn>(pd);
                        process = Marshal.GetDelegateForFunctionPointer<ProcessFn>(pp);
                        Variant = name.Contains("avx2") ? "AVX2" : "SSE2";
                        Available = true;
                        return true;
                    }
                    Error = "rnnoise_*.dll não encontrada na pasta do app";
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                }
                return false;
            }
        }

        public static IntPtr Create() => create(IntPtr.Zero);
        public static void Destroy(IntPtr st) => destroy(st);
        public static float Process(IntPtr st, float* output, float* input) => process(st, output, input);
    }

    /// <summary>
    /// Reducao de ruido por rede neural (RNNoise). Processa blocos de 480 amostras (10 ms a 48 kHz).
    /// O RNNoise 0.2 tem latencia de 2 quadros (960 amostras: janela + 1 quadro de antecipacao),
    /// entao o caminho "seco" e atrasado igual para a mistura nao gerar efeito pente.
    /// </summary>
    public sealed unsafe class RnnoiseDenoiser : IDisposable
    {
        public const int Latency = 2 * VoiceChain.Block;
        const float InScale = 16384f; // 32768 * 0.5 (6 dB de folga)
        IntPtr st;
        readonly float[] inBuf = new float[VoiceChain.Block];
        readonly float[] outBuf = new float[VoiceChain.Block];
        readonly float[] dryDelay = new float[Latency];
        int dpos;
        float mixPrev = 1f;

        public RnnoiseDenoiser()
        {
            if (RnnoiseLib.Init()) st = RnnoiseLib.Create();
        }

        public bool Ok => st != IntPtr.Zero;

        /// <summary>Processa 480 amostras no lugar; retorna a probabilidade de voz (0..1).</summary>
        public float Process(float[] block, float mix)
        {
            if (st == IntPtr.Zero)
            {
                // Sem IA: apenas mantem o mesmo atraso (latencia constante).
                for (int i = 0; i < VoiceChain.Block; i++)
                {
                    float d = dryDelay[dpos]; dryDelay[dpos] = block[i]; block[i] = d;
                    if (++dpos == Latency) dpos = 0;
                }
                return 1f;
            }
            for (int i = 0; i < VoiceChain.Block; i++)
            {
                // A IA trabalha na faixa de 16 bits. Entramos 6 dB mais baixo (folga): picos de ate
                // +6 dBFS passam sem distorcer; a saida volta ao nivel original logo abaixo.
                float v = block[i] * InScale;
                if (v > 32767f) v = 32767f; else if (v < -32768f) v = -32768f;
                inBuf[i] = v;
            }
            float vad;
            fixed (float* pi = inBuf, po = outBuf)
                vad = RnnoiseLib.Process(st, po, pi);
            if (float.IsNaN(vad)) vad = 0f;

            float m0 = mixPrev, dm = (mix - mixPrev) / VoiceChain.Block;
            const float inv = 1f / InScale;
            for (int i = 0; i < VoiceChain.Block; i++)
            {
                float wet = outBuf[i] * inv;
                if (float.IsNaN(wet) || float.IsInfinity(wet)) wet = 0f;
                float dry = dryDelay[dpos];
                dryDelay[dpos] = block[i];
                if (++dpos == Latency) dpos = 0;
                float m = m0 + dm * i;
                block[i] = dry + (wet - dry) * m;
            }
            mixPrev = mix;
            return vad;
        }

        public void Dispose()
        {
            if (st != IntPtr.Zero)
            {
                RnnoiseLib.Destroy(st);
                st = IntPtr.Zero;
            }
        }
    }
}
