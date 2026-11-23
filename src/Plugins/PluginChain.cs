using System;
using System.Diagnostics;
using System.Threading;

namespace TakaVox.Plugins
{
    /// <summary>
    /// Plugins VST que a thread de audio processa, em ordem (depois do compressor do TakaVox e antes do volume
    /// final e do limitador, que continua protegendo contra estouro).
    /// A interface troca a lista inteira de uma vez - o audio nunca espera trava - e, antes de liberar um plugin
    /// removido, espera o bloco que estiver em andamento terminar.
    /// </summary>
    public sealed class PluginChain
    {
        PluginInstance[] slots = new PluginInstance[0];
        long seq;              // impar = thread de audio dentro do Process
        volatile int latency;
        volatile bool bypass;

        /// <summary>Algum plugin travou no processamento (a interface avisa e atualiza a lista).</summary>
        public volatile bool CrashPending;
        /// <summary>Maior tempo gasto pelos plugins num bloco de 10 ms (a interface zera depois de ler).</summary>
        public double MaxMs;
        public long NanBlocks;

        public PluginInstance[] Items => Volatile.Read(ref slots);
        public int LatencySamples => latency;

        /// <summary>Desliga todos os plugins de uma vez (a lista continua carregada).</summary>
        public bool Bypass
        {
            get => bypass;
            set { bypass = value; UpdateLatency(); }
        }

        /// <summary>Thread de audio: processa n amostras mono no lugar.</summary>
        public unsafe void Process(float[] x, int n)
        {
            Interlocked.Increment(ref seq); // entrando (antes de ler a lista: a interface depende dessa ordem)
            try
            {
                var arr = Volatile.Read(ref slots);
                if (arr.Length == 0 || bypass) return;
                long t0 = Stopwatch.GetTimestamp();
                bool any = false;
                fixed (float* p = x)
                {
                    for (int k = 0; k < arr.Length; k++)
                    {
                        var s = arr[k];
                        if (!s.Enabled || s.Crashed) continue;
                        any = true;
                        if (Tvh.tvh_process(s.Handle, p, n) == -2)
                        {
                            s.Crashed = true;
                            CrashPending = true;
                        }
                    }
                }
                if (!any) return;
                // plugin com defeito pode soltar NaN/infinito: zera o bloco em vez de estragar o resto da cadeia
                for (int i = 0; i < n; i++)
                {
                    float v = x[i];
                    if (float.IsNaN(v) || float.IsInfinity(v))
                    {
                        Array.Clear(x, 0, n);
                        NanBlocks++;
                        break;
                    }
                }
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (ms > MaxMs) MaxMs = ms;
            }
            finally { Interlocked.Increment(ref seq); }
        }

        /// <summary>
        /// Interface: troca a lista. Retorna falso se o audio nao terminou o bloco a tempo
        /// (ai NAO e seguro liberar plugins que sairam da lista).
        /// </summary>
        public bool Set(PluginInstance[] arr)
        {
            Volatile.Write(ref slots, arr ?? new PluginInstance[0]);
            Interlocked.MemoryBarrier();
            UpdateLatency();
            long s = Interlocked.Read(ref seq);
            if ((s & 1) == 0) return true; // o audio nao estava dentro: o proximo bloco ja usa a lista nova
            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref seq) == s)
            {
                if (sw.ElapsedMilliseconds > 1500) return false;
                Thread.Sleep(1);
            }
            return true;
        }

        /// <summary>Interface: recalcula a latencia somada (ligar/desligar plugin, latencia nova).</summary>
        public void UpdateLatency()
        {
            int sum = 0;
            if (!bypass)
                foreach (var s in Volatile.Read(ref slots))
                    if (s.Enabled && !s.Crashed) sum += s.Latency;
            latency = sum;
        }
    }
}
