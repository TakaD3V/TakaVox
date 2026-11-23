using System;

namespace TakaVox.Dsp
{
    public enum FilterType { Peak = 0, LowShelf = 1, HighShelf = 2, HighPass = 3, LowPass = 4, Notch = 5 }

    /// <summary>Filtro biquad (RBJ Audio EQ Cookbook), forma direta transposta II em double.</summary>
    public sealed class Biquad
    {
        double b0 = 1, b1, b2, a1, a2;
        double z1, z2;
        const double DenormGuard = 1e-30;

        public void Set(FilterType type, double fs, double freq, double q, double gainDb)
        {
            freq = Math.Max(10.0, Math.Min(freq, fs * 0.45));
            q = Math.Max(0.1, q);
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / fs;
            double cw = Math.Cos(w0), sw = Math.Sin(w0);
            double alpha = sw / (2.0 * q);
            double nb0, nb1, nb2, na0, na1, na2;
            switch (type)
            {
                case FilterType.HighPass:
                    nb0 = (1 + cw) / 2; nb1 = -(1 + cw); nb2 = (1 + cw) / 2;
                    na0 = 1 + alpha; na1 = -2 * cw; na2 = 1 - alpha;
                    break;
                case FilterType.LowPass:
                    nb0 = (1 - cw) / 2; nb1 = 1 - cw; nb2 = (1 - cw) / 2;
                    na0 = 1 + alpha; na1 = -2 * cw; na2 = 1 - alpha;
                    break;
                case FilterType.Notch: // corte total em freq; largura de -3 dB = freq/q (ganho ignorado)
                    nb0 = 1; nb1 = -2 * cw; nb2 = 1;
                    na0 = 1 + alpha; na1 = -2 * cw; na2 = 1 - alpha;
                    break;
                case FilterType.LowShelf:
                {
                    double sa = 2 * Math.Sqrt(A) * alpha;
                    nb0 = A * ((A + 1) - (A - 1) * cw + sa);
                    nb1 = 2 * A * ((A - 1) - (A + 1) * cw);
                    nb2 = A * ((A + 1) - (A - 1) * cw - sa);
                    na0 = (A + 1) + (A - 1) * cw + sa;
                    na1 = -2 * ((A - 1) + (A + 1) * cw);
                    na2 = (A + 1) + (A - 1) * cw - sa;
                    break;
                }
                case FilterType.HighShelf:
                {
                    double sa = 2 * Math.Sqrt(A) * alpha;
                    nb0 = A * ((A + 1) + (A - 1) * cw + sa);
                    nb1 = -2 * A * ((A - 1) + (A + 1) * cw);
                    nb2 = A * ((A + 1) + (A - 1) * cw - sa);
                    na0 = (A + 1) - (A - 1) * cw + sa;
                    na1 = 2 * ((A - 1) - (A + 1) * cw);
                    na2 = (A + 1) - (A - 1) * cw - sa;
                    break;
                }
                default: // Peak
                    nb0 = 1 + alpha * A; nb1 = -2 * cw; nb2 = 1 - alpha * A;
                    na0 = 1 + alpha / A; na1 = -2 * cw; na2 = 1 - alpha / A;
                    break;
            }
            b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0;
        }

        public void SetPassthrough() { b0 = 1; b1 = b2 = a1 = a2 = 0; }

        public void Reset() { z1 = z2 = 0; }

        public float Process(float x)
        {
            double y = b0 * x + z1 + DenormGuard;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return (float)y;
        }

        public void Process(float[] buf, int n)
        {
            for (int i = 0; i < n; i++) buf[i] = Process(buf[i]);
        }

        /// <summary>Resposta em magnitude (dB) na frequencia indicada.</summary>
        public double MagnitudeDb(double freq, double fs)
        {
            double w = 2 * Math.PI * freq / fs;
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = b0 + b1 * c1 + b2 * c2, ni = -(b1 * s1 + b2 * s2);
            double dr = 1 + a1 * c1 + a2 * c2, di = -(a1 * s1 + a2 * s2);
            double mag2 = (nr * nr + ni * ni) / Math.Max(dr * dr + di * di, 1e-300);
            return 10 * Math.Log10(Math.Max(mag2, 1e-30));
        }
    }

    public static class DspMath
    {
        public static float DbToLin(float db) => (float)Math.Pow(10.0, db / 20.0);
        public static float LinToDb(float lin) => lin <= 1e-9f ? -180f : 20f * (float)Math.Log10(lin);
        public static float PowToDb(double p) => p <= 1e-18 ? -180f : 10f * (float)Math.Log10(p);
        /// <summary>Coeficiente de um filtro de 1 polo com constante de tempo em ms.</summary>
        public static float Coef(float ms, float fs) => ms <= 0.01f ? 0f : (float)Math.Exp(-1.0 / (ms * 0.001 * fs));
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
