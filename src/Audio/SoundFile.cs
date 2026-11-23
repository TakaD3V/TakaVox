using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace TakaVox.Audio
{
    /// <summary>
    /// Arquivos do sound pad: le qualquer formato que o Windows entende (MP3, WAV, M4A/AAC, WMA, FLAC, ALAC...),
    /// converte para mono 48 kHz com reamostragem de alta qualidade (sinc com janela de Kaiser) e mede o volume
    /// percebido (LUFS, ITU-R BS.1770) para todos os sons tocarem no mesmo volume.
    /// </summary>
    public static class SoundFile
    {
        public const int Rate = 48000;
        /// <summary>Maior som aceito (10 min): acima disso so o comeco entra.</summary>
        public const int MaxSeconds = 600;

        [SuppressUnmanagedCodeSecurity]
        static class Native
        {
            [DllImport("tvhost.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            public static extern int tvh_audio_decode(string path, out IntPtr data, out long frames, out int channels, out int rate, int maxSeconds, StringBuilder err, int errCap);
            [DllImport("tvhost.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern void tvh_audio_free(IntPtr data);
            [DllImport("tvhost.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            public static extern int tvh_audio_encode(string path, float[] mono, long frames, int rate, int kbps, StringBuilder err, int errCap);
        }

        /// <summary>Le o arquivo e devolve mono 48 kHz (null + erro se nao der).</summary>
        public static float[] LoadMono48k(string path, out string error, out int srcRate, out int srcChannels)
        {
            error = null; srcRate = 0; srcChannels = 0;
            var err = new StringBuilder(512);
            IntPtr p;
            long frames;
            int ch, rate;
            try
            {
                if (Native.tvh_audio_decode(path, out p, out frames, out ch, out rate, MaxSeconds, err, err.Capacity) != 1)
                {
                    error = err.Length > 0 ? err.ToString() : "não deu para ler o arquivo";
                    return null;
                }
            }
            catch (DllNotFoundException) { error = "tvhost.dll não encontrado (reinstale o TakaVox)"; return null; }
            catch (EntryPointNotFoundException) { error = "tvhost.dll antigo (reinstale o TakaVox)"; return null; }
            srcRate = rate; srcChannels = ch;
            float[] mono;
            try
            {
                // mistura os canais (media) direto da memoria nativa
                mono = new float[frames];
                var tmp = new float[Math.Min(frames * ch, 1 << 20)];
                long done = 0;
                while (done < frames)
                {
                    long take = Math.Min(frames - done, tmp.Length / ch);
                    Marshal.Copy(new IntPtr(p.ToInt64() + done * ch * 4), tmp, 0, (int)(take * ch));
                    for (long i = 0; i < take; i++)
                    {
                        float s = 0;
                        for (int c = 0; c < ch; c++) s += tmp[i * ch + c];
                        mono[done + i] = s / ch;
                    }
                    done += take;
                }
            }
            finally { Native.tvh_audio_free(p); }
            return rate == Rate ? mono : Resample(mono, rate, Rate);
        }

        /// <summary>Grava mono em .mp3 ou .m4a (codificador do Windows).</summary>
        public static bool Encode(string path, float[] mono, int rate, int kbps, out string error)
        {
            error = null;
            var err = new StringBuilder(512);
            try
            {
                if (Native.tvh_audio_encode(path, mono, mono.Length, rate, kbps, err, err.Capacity) == 1) return true;
                error = err.ToString();
            }
            catch (Exception ex) { error = ex.Message; }
            return false;
        }

        // ---------------------------------------------------------------- reamostragem
        static double BesselI0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 60; k++)
            {
                term *= q / (k * k);
                sum += term;
                if (term < sum * 1e-12) break;
            }
            return sum;
        }

        /// <summary>
        /// Reamostragem de alta qualidade (sinc com janela de Kaiser, 24 cruzamentos de zero, ~-90 dB de rejeicao).
        /// Lenta o bastante para so rodar na importacao, nunca na thread de audio.
        /// </summary>
        public static float[] Resample(float[] x, int src, int dst)
        {
            if (src == dst || x.Length == 0) return (float[])x.Clone();
            double ratio = (double)dst / src;
            long outLen = (long)Math.Floor(x.Length * ratio);
            var y = new float[outLen];
            double cutoff = Math.Min(1.0, ratio) * 0.96; // fracao da Nyquist da entrada
            const int Zeros = 24;
            int half = (int)Math.Ceiling(Zeros / cutoff);
            const int Phases = 4096; // tabela fina: o erro de interpolacao fica abaixo de -110 dB
            int tlen = half * Phases + 2;
            var table = new float[tlen];
            const double beta = 9.0;
            double i0b = BesselI0(beta);
            for (int k = 0; k < tlen; k++)
            {
                double t = (double)k / Phases;
                double a = Math.PI * cutoff * t;
                double s = t == 0 ? 1 : Math.Sin(a) / a;
                double r = t / half;
                double w = r >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - r * r)) / i0b;
                table[k] = (float)(cutoff * s * w);
            }
            int chunks = 1; // um nucleo so: importar sons nunca pesa no jogo
            long per = (outLen + chunks - 1) / chunks;
            Parallel.For(0, chunks, ci =>
            {
                long a0 = ci * per, a1 = Math.Min(outLen, a0 + per);
                for (long n = a0; n < a1; n++)
                {
                    double t = n / ratio;
                    long c = (long)Math.Floor(t);
                    double frac = t - c;
                    double acc = 0;
                    long k0 = Math.Max(-half + 1, -c), k1 = Math.Min(half, x.Length - 1 - c);
                    for (long k = k0; k <= k1; k++)
                    {
                        double tp = Math.Abs(k - frac) * Phases;
                        int ti = (int)tp;
                        if (ti >= tlen - 1) continue;
                        float h = table[ti] + (table[ti + 1] - table[ti]) * (float)(tp - ti);
                        acc += x[c + k] * h;
                    }
                    y[n] = (float)acc;
                }
            });
            return y;
        }

        // ---------------------------------------------------------------- medidas
        /// <summary>Volume percebido integrado (LUFS, ITU-R BS.1770 com portas absoluta e relativa) de um sinal mono 48 kHz.</summary>
        public static float LoudnessLufs(float[] x)
        {
            if (x.Length == 0) return -100f;
            // filtro K: prateleira de agudos (+4 dB) e passa-altas RLB, coeficientes oficiais para 48 kHz
            double b0 = 1.53512485958697, b1 = -2.69169618940638, b2 = 1.19839281085285, a1 = -1.69065929318241, a2 = 0.73248077421585;
            double c0 = 1.0, c1 = -2.0, c2 = 1.0, d1 = -1.99004745483398, d2 = 0.99007225036621;
            var k = new double[x.Length];
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0, u1 = 0, u2 = 0, z1 = 0, z2 = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double v = x[i];
                double y = b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = v; y2 = y1; y1 = y;
                double z = c0 * y + c1 * u1 + c2 * u2 - d1 * z1 - d2 * z2;
                u2 = u1; u1 = y; z2 = z1; z1 = z;
                k[i] = z * z;
            }
            const int Block = 19200, Hop = 4800; // 400 ms com 75% de sobreposicao
            if (x.Length < Block)
            {
                double ms = 0;
                foreach (var v in k) ms += v;
                ms /= k.Length;
                return ms > 0 ? (float)(-0.691 + 10 * Math.Log10(ms)) : -100f;
            }
            var pre = new double[k.Length + 1];
            for (int i = 0; i < k.Length; i++) pre[i + 1] = pre[i] + k[i];
            int nb = (k.Length - Block) / Hop + 1;
            var blocks = new double[nb];
            for (int b = 0; b < nb; b++) blocks[b] = (pre[b * Hop + Block] - pre[b * Hop]) / Block;
            double Lufs(double ms) => -0.691 + 10 * Math.Log10(ms);
            double sum = 0; int cnt = 0;
            foreach (var m in blocks) if (m > 0 && Lufs(m) > -70) { sum += m; cnt++; }
            if (cnt == 0) return -100f;
            double rel = Lufs(sum / cnt) - 10;
            double sum2 = 0; int cnt2 = 0;
            foreach (var m in blocks) if (m > 0 && Lufs(m) > -70 && Lufs(m) > rel) { sum2 += m; cnt2++; }
            return cnt2 == 0 ? (float)Lufs(sum / cnt) : (float)Lufs(sum2 / cnt2);
        }

        /// <summary>Pico (dBFS).</summary>
        public static float PeakDb(float[] x)
        {
            float p = 0;
            foreach (var v in x) { float a = Math.Abs(v); if (a > p) p = a; }
            return p > 0 ? (float)(20 * Math.Log10(p)) : -120f;
        }

        /// <summary>Forma de onda resumida: maior pico em cada um dos 'buckets' pedacos (0..1).</summary>
        public static float[] Peaks(float[] x, int buckets)
        {
            var r = new float[buckets];
            if (x.Length == 0) return r;
            for (int b = 0; b < buckets; b++)
            {
                long a0 = (long)x.Length * b / buckets, a1 = Math.Max(a0 + 1, (long)x.Length * (b + 1) / buckets);
                float p = 0;
                for (long i = a0; i < a1 && i < x.Length; i++) { float a = Math.Abs(x[i]); if (a > p) p = a; }
                r[b] = Math.Min(1f, p);
            }
            return r;
        }

        /// <summary>float -> 16 bits com dither TPDF (sem distorcao de quantizacao nos sons baixos).</summary>
        public static short[] ToPcm16(float[] x)
        {
            var r = new short[x.Length];
            uint s = 0x12345678;
            for (int i = 0; i < x.Length; i++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                float d1 = (s & 0xFFFF) / 65536f;
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                float d2 = (s & 0xFFFF) / 65536f;
                float v = x[i] * 32767f + (d1 - d2);
                r[i] = (short)Math.Max(-32768, Math.Min(32767, Math.Round(v)));
            }
            return r;
        }
    }
}
