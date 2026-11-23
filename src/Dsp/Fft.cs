using System;

namespace TakaVox.Dsp
{
    /// <summary>FFT radix-2 complexa simples (usada so para o analisador de espectro da interface).</summary>
    public sealed class Fft
    {
        readonly int n, logN;
        readonly double[] cos, sin;
        readonly int[] rev;
        readonly double[] re, im;
        readonly double[] window;

        public int Size => n;

        public Fft(int size)
        {
            n = size;
            logN = (int)Math.Round(Math.Log(size, 2));
            if ((1 << logN) != size) throw new ArgumentException("tamanho precisa ser potencia de 2");
            cos = new double[n / 2]; sin = new double[n / 2];
            for (int i = 0; i < n / 2; i++) { cos[i] = Math.Cos(-2 * Math.PI * i / n); sin[i] = Math.Sin(-2 * Math.PI * i / n); }
            rev = new int[n];
            for (int i = 0; i < n; i++)
            {
                int r = 0;
                for (int b = 0; b < logN; b++) if ((i & (1 << b)) != 0) r |= 1 << (logN - 1 - b);
                rev[i] = r;
            }
            re = new double[n]; im = new double[n];
            window = new double[n];
            for (int i = 0; i < n; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n); // Hann
        }

        /// <summary>Calcula o espectro de potencia em dBFS (n/2+1 bins) de um sinal real com janela Hann.</summary>
        public void PowerDb(float[] input, float[] outDb)
        {
            Transform(input);
            // normalizacao: seno de amplitude 1 => 0 dB (janela Hann tem ganho coerente 0.5)
            double norm = 2.0 / (n * 0.5);
            for (int k = 0; k <= n / 2; k++)
            {
                double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * norm;
                outDb[k] = mag < 1e-9 ? -180f : (float)(20 * Math.Log10(mag));
            }
        }

        /// <summary>Mesmo espectro, em potencia linear (10*log10 da = PowerDb), sem log: mais barato para medias.</summary>
        public void Power(float[] input, double[] outPow)
        {
            Transform(input);
            double norm = 2.0 / (n * 0.5), n2 = norm * norm;
            for (int k = 0; k <= n / 2; k++) outPow[k] = (re[k] * re[k] + im[k] * im[k]) * n2;
        }

        void Transform(float[] input)
        {
            for (int i = 0; i < n; i++) { re[rev[i]] = input[i] * window[i]; im[rev[i]] = 0; }
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1, step = n / size;
                for (int start = 0; start < n; start += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        int a = start + k, b = a + half;
                        double wr = cos[k * step], wi = sin[k * step];
                        double tr = re[b] * wr - im[b] * wi;
                        double ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                    }
                }
            }
        }
    }
}
