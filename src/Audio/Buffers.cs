using System;
using System.Threading;

namespace TakaVox.Audio
{
    /// <summary>Fonte de amostras mono para uma saida de audio.</summary>
    public interface ISampleSource
    {
        /// <summary>Produz ate n amostras; retorna quantas produziu (pode ser menos se faltar audio).</summary>
        int Read(float[] dst, int n);
    }

    /// <summary>
    /// Fila elastica (1 produtor / 1 consumidor) entre o relogio do microfone e o do dispositivo de saida.
    /// Corrige a deriva entre os relogios com reamostragem adaptativa (controle PI + interpolacao cubica),
    /// sem cliques e sem atraso acumulando.
    /// </summary>
    public sealed class ElasticBuffer : ISampleSource
    {
        readonly float[] ring;
        readonly int mask;
        long writeIdx;           // escrito so pelo produtor
        long readIdx;            // so consumidor
        double frac, fillAvg, integ;
        bool primed;

        public int TargetFill { get; set; } = 960;  // 20 ms
        public int Underruns;   // fila ficou vazia de verdade (re-sincroniza)
        public int ShortReads;  // pediram mais do que tinha (a saida segura com o proprio buffer)
        public int Resyncs;
        public double Ratio { get; private set; } = 1.0;
        public double FillMs => fillAvg / 48.0;

        public ElasticBuffer(int capacityPow2 = 65536)
        {
            ring = new float[capacityPow2];
            mask = capacityPow2 - 1;
        }

        long lastWriteTicks;
        int lastWriteCount;
        static readonly double TicksToSamples = 48000.0 / System.Diagnostics.Stopwatch.Frequency;

        public void Write(float[] src, int n)
        {
            long w = writeIdx;
            for (int i = 0; i < n; i++) ring[(w + i) & mask] = src[i];
            Volatile.Write(ref lastWriteCount, n);
            Volatile.Write(ref lastWriteTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref writeIdx, w + n);
        }

        /// <summary>
        /// Nivel "suave" da fila: o produtor escreve em rajadas de 10 ms, entao descontamos a parte da
        /// ultima rajada que, em fluxo continuo, ainda nao teria chegado. Assim a medida nao depende da fase.
        /// </summary>
        double SmoothFill(long avail)
        {
            double since = (System.Diagnostics.Stopwatch.GetTimestamp() - Volatile.Read(ref lastWriteTicks)) * TicksToSamples;
            int burst = Volatile.Read(ref lastWriteCount);
            double ahead = Math.Max(0, burst - Math.Max(0, since));
            return avail - frac - ahead;
        }

        public void Reset()
        {
            primed = false;
        }

        public int Read(float[] dst, int n)
        {
            long w = Volatile.Read(ref writeIdx);
            long avail = w - readIdx;
            if (avail < 0 || avail > ring.Length - 8192)
            {
                // consumidor ficou parado: pula para perto do fim
                readIdx = w - Math.Min(avail < 0 ? 0 : avail, TargetFill);
                frac = 0; avail = w - readIdx; Interlocked.Increment(ref Resyncs);
                primed = false;
            }
            if (!primed)
            {
                // comeca com o nivel suave ja no alvo (compensa a rajada mais recente)
                int startFill = TargetFill + Volatile.Read(ref lastWriteCount) / 2;
                if (avail < startFill + 3) return 0;
                readIdx = w - startFill;
                frac = 0; integ = 0; primed = true;
                avail = startFill;
                fillAvg = SmoothFill(avail);
            }

            // controle de nivel da fila: proporcional, bem amortecido (sem oscilar). Erro em regime
            // para 100 ppm de deriva fica em ~20 amostras (0,4 ms) – irrelevante.
            double fill = SmoothFill(avail);
            fillAvg += (fill - fillAvg) * 0.02;
            double err = fillAvg - TargetFill;
            integ = Math.Max(-0.0005, Math.Min(0.0005, integ * 0.9999 + err * 1e-10));
            double adj = Math.Max(-0.003, Math.Min(0.003, err * 5e-6 + integ));
            Ratio = 1.0 + adj;
            if (fillAvg > TargetFill + 2400)
            {
                // atraso demais (ex.: travada do sistema): descarta o excesso de uma vez
                readIdx = w - TargetFill; frac = 0; fillAvg = TargetFill; integ = 0;
                Interlocked.Increment(ref Resyncs);
            }

            int produced = 0;
            for (int i = 0; i < n; i++)
            {
                if (w - readIdx < 3) break;
                float y0 = ring[(readIdx - 1) & mask], y1 = ring[readIdx & mask];
                float y2 = ring[(readIdx + 1) & mask], y3 = ring[(readIdx + 2) & mask];
                float t = (float)frac;
                dst[i] = y1 + 0.5f * t * (y2 - y0 + t * (2f * y0 - 5f * y1 + 4f * y2 - y3 + t * (3f * (y1 - y2) + y3 - y0)));
                frac += Ratio;
                while (frac >= 1.0) { frac -= 1.0; readIdx++; }
                produced++;
            }
            if (produced < n)
            {
                Interlocked.Increment(ref ShortReads);
                if (produced == 0) { primed = false; Interlocked.Increment(ref Underruns); }
            }
            return produced;
        }
    }

    /// <summary>Toca um trecho gravado (para comparar antes/depois).</summary>
    public sealed class ArraySource : ISampleSource
    {
        readonly float[] data;
        readonly int length;
        int pos;
        public volatile bool Finished;
        public double Progress => length == 0 ? 1 : (double)pos / length;

        public ArraySource(float[] samples, int len) { data = samples; length = Math.Min(len, samples.Length); }

        public int Read(float[] dst, int n)
        {
            int k = Math.Max(0, Math.Min(n, length - pos));
            if (k > 0) Array.Copy(data, pos, dst, 0, k);
            if (k < n) Array.Clear(dst, k, n - k);
            pos += k;
            if (pos >= length) Finished = true;
            return n;
        }
    }

    /// <summary>Guarda as ultimas amostras para o analisador de espectro.</summary>
    public sealed class SampleTap
    {
        readonly float[] ring;
        readonly int mask;
        int pos;

        public SampleTap(int sizePow2 = 8192) { ring = new float[sizePow2]; mask = sizePow2 - 1; }

        public void Write(float[] x, int n)
        {
            int p = pos;
            for (int i = 0; i < n; i++) ring[(p + i) & mask] = x[i];
            Volatile.Write(ref pos, p + n);
        }

        public void Snapshot(float[] dst)
        {
            int p = Volatile.Read(ref pos);
            int len = dst.Length;
            for (int i = 0; i < len; i++) dst[i] = ring[(p - len + i) & mask];
        }
    }

    /// <summary>Gravador em memoria do sinal ORIGINAL do microfone (para o teste A/B).</summary>
    public sealed class Recorder
    {
        public readonly float[] Buffer;
        int length;
        public volatile bool Recording;
        public int Length => Volatile.Read(ref length);
        public double Seconds => Length / 48000.0;
        public double MaxSeconds => Buffer.Length / 48000.0;

        public Recorder(int maxSamples) { Buffer = new float[maxSamples]; }

        public void Start() { Volatile.Write(ref length, 0); Recording = true; }
        public void Stop() { Recording = false; }

        public void Feed(float[] x, int n)
        {
            if (!Recording) return;
            int len = length;
            int k = Math.Min(n, Buffer.Length - len);
            if (k > 0) Array.Copy(x, 0, Buffer, len, k);
            Volatile.Write(ref length, len + k);
            if (len + k >= Buffer.Length) Recording = false;
        }
    }
}
