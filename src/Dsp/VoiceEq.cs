using System;
using System.Collections.Generic;
using System.Globalization;

namespace TakaVox.Dsp
{
    /// <summary>
    /// EQ automatica da calibracao. Mede o espectro medio da SUA voz no mic cru (so trechos com fala, com o
    /// ruido do quarto descontado) e compara com uma voz "de estudio": fala natural (ANSI S3.5-1997, esforco
    /// normal) com um pouco menos de "caixa" e um pouco mais de presenca e brilho.
    /// So corrige o EQUILIBRIO LARGO (graves, caixa, medios "de copo"/anasalados, presenca, brilho), com cerca de
    /// metade da diferenca e limites de poucos dB. Picos estreitos NAO sao cortados de proposito: com alguns
    /// segundos de fala nao da para separar uma ressonancia do mic dos formantes da voz de cada pessoa
    /// (testado com fala real: cortar picos estreitos estragaria vozes limpas).
    /// </summary>
    public static class VoiceEq
    {
        public static readonly double[] Centers = { 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500 };
        /// <summary>Fala natural em bandas de 1/3 de oitava, relativa a 1 kHz (ANSI S3.5; 100/125 Hz e acima de 8 kHz extrapolados).</summary>
        public static readonly double[] Natural = { -4.5, -2.5, -0.6, 2.5, 3.7, 3.9, 5.6, 6.2, 5.0, 2.3, 0, -1.0, -2.8, -4.7, -7.9, -8.5, -9.7, -12.7, -14.4, -14.9, -16.0, -17.5 };
        /// <summary>O que diferencia a "voz de estudio" da fala natural: menos caixa, mais presenca e brilho.</summary>
        public static readonly double[] Style = { 0, 0, 0, 0, -0.5, -1.0, -1.5, -1.5, -1.0, -0.5, 0, 0.5, 1.5, 2.5, 3.5, 4.0, 4.0, 3.5, 3.5, 4.0, 4.5, 4.5 };

        public sealed class Result
        {
            public bool Ok;
            public string Error = "";
            public double[] Deviation;   // medido - alvo, dB por banda (NaN = banda sem sinal suficiente)
            public int Frames;
            // as 5 faixas do equalizador (tipos fixos: prateleira de graves, 3 sinos, prateleira de agudos)
            public float LowGain, LowFreq = 130f;
            public float MudGain, MudFreq = 400f, MudQ = 0.9f;
            public float MidGain, MidFreq = 1000f, MidQ = 0.9f;
            public float PresGain, PresFreq = 3500f, PresQ = 0.8f;
            public float AirGain, AirFreq = 10000f;
            public readonly List<string> Notes = new List<string>();
        }

        /// <summary>
        /// audio: mic cru da calibracao; rmsDb/vad: um valor por bloco de 480 amostras (mesma ordem do audio);
        /// speechStart: primeiro bloco da fase "fale"; noiseDb: ruido medido no silencio.
        /// </summary>
        public static Result Analyze(float[] audio, int audioLen, float[] rmsDb, float[] vad, int blocks, int speechStart, float noiseDb)
        {
            const int N = 2048, B = 480, Span = 5; // janela de 2048 = ~4,3 blocos
            var r = new Result();
            var fft = new Fft(N);
            var seg = new float[N];
            var db = new float[N / 2 + 1];
            var sp = new double[N / 2 + 1];
            var nz = new double[N / 2 + 1];
            int ns = 0, nn = 0;
            for (int b = 0; b + Span <= blocks; b++)
            {
                int start = b * B;
                if (start + N > audioLen) break;
                bool speech = b >= speechStart + 20 && b % 2 == 0;
                if (speech)
                    for (int k = b; k < b + Span; k++)
                        if (vad[k] < 0.6f || rmsDb[k] < noiseDb + 10f) { speech = false; break; }
                bool noise = !speech && b >= 10 && b + Span <= speechStart - 2;
                if (!speech && !noise) continue;
                Array.Copy(audio, start, seg, 0, N);
                fft.PowerDb(seg, db);
                var acc = speech ? sp : nz;
                for (int k = 0; k <= N / 2; k++) acc[k] += Math.Pow(10, db[k] / 10);
                if (speech) ns++; else nn++;
            }
            r.Frames = ns;
            if (ns < 25)
            {
                r.Error = "pouca fala para medir o timbre";
                return r;
            }
            var lvl = new double[Centers.Length];
            var reliable = new bool[Centers.Length];
            for (int c = 0; c < Centers.Length; c++)
            {
                double lo = Centers[c] / Math.Pow(2, 1.0 / 6), hi = Centers[c] * Math.Pow(2, 1.0 / 6);
                double es = 0, en = 0;
                for (int k = 1; k <= N / 2; k++)
                {
                    double f = k * 48000.0 / N;
                    if (f < lo || f >= hi) continue;
                    es += sp[k] / ns;
                    en += nn > 0 ? nz[k] / nn : 0;
                }
                reliable[c] = es > 4 * en && es > 1e-14;  // voz pelo menos 6 dB acima do ruido nessa banda
                lvl[c] = 10 * Math.Log10(Math.Max(es - en, es * 0.1) + 1e-30);
            }
            // nivel de referencia = MEDIANA de toda a faixa da voz (125 Hz - 5 kHz, 16 bandas): um defeito que
            // ocupa poucas bandas (medios inchados, agudos abafados, grave sobrando) nao arrasta a referencia,
            // entao cada defeito aparece na propria faixa e e corrigido nela
            var diffs = new List<double>();
            for (int c = 0; c < Centers.Length; c++)
                if (Centers[c] >= 125 && Centers[c] <= 5000 && reliable[c]) diffs.Add(lvl[c] - (Natural[c] + Style[c]));
            if (diffs.Count < 10)
            {
                r.Error = "o sinal da voz ficou fraco demais perto do ruído";
                return r;
            }
            diffs.Sort();
            double off = diffs.Count % 2 == 1 ? diffs[diffs.Count / 2] : 0.5 * (diffs[diffs.Count / 2 - 1] + diffs[diffs.Count / 2]);
            r.Deviation = new double[Centers.Length];
            for (int c = 0; c < Centers.Length; c++)
                r.Deviation[c] = reliable[c] ? lvl[c] - off - (Natural[c] + Style[c]) : double.NaN;
            var dev = r.Deviation;

            // cada faixa corrige ~60% da diferenca media na sua regiao, com limites de poucos dB
            double low = Mean(dev, 100, 160), mud = Mean(dev, 250, 500), mid = Mean(dev, 630, 1600);
            double pres = Mean(dev, 2000, 5000), air = Mean(dev, 8000, 12500);
            r.LowGain = double.IsNaN(low) ? 0 : Clamp(-0.5 * low, -4, 3);    // grave varia muito de pessoa para pessoa: 50%
            r.MudGain = double.IsNaN(mud) ? 0 : Clamp(-0.6 * mud, -5, 2);
            r.MidGain = double.IsNaN(mid) ? 0 : Clamp(-0.6 * mid, -5, 2);
            r.PresGain = double.IsNaN(pres) ? 2 : Clamp(-0.6 * pres, -3, 6);
            r.AirGain = double.IsNaN(air) ? 2 : Clamp(-0.6 * air, -3, 5);    // sem sinal confiavel la em cima: brilho leve

            // explicacao em portugues
            if (r.MidGain <= -1.5f) r.Notes.Add($"médios 'de copo'/anasalados sobrando: −{-r.MidGain:0.0} dB em 1 kHz");
            if (r.MudGain <= -1.5f) r.Notes.Add($"som de caixa/embolado: −{-r.MudGain:0.0} dB em 400 Hz");
            if (r.LowGain >= 1f) r.Notes.Add($"falta corpo (graves): +{r.LowGain:0.0} dB");
            else if (r.LowGain <= -1f) r.Notes.Add($"grave sobrando (mic muito perto da boca?): −{-r.LowGain:0.0} dB");
            if (r.PresGain >= 3f) r.Notes.Add($"voz abafada (falta presença): +{r.PresGain:0.0} dB");
            if (r.AirGain >= 3f) r.Notes.Add($"falta brilho: +{r.AirGain:0.0} dB nos agudos");
            r.Ok = true;
            return r;
        }

        /// <summary>Aplica o resultado nas 5 faixas do equalizador (mantendo os tipos de cada faixa).</summary>
        public static void Apply(Result r, ChainParams p)
        {
            var b = p.Bands;
            Set(b[0], r.LowFreq, r.LowGain, 0.71f);
            Set(b[1], r.MudFreq, r.MudGain, r.MudQ);
            Set(b[2], r.MidFreq, r.MidGain, r.MidQ);
            Set(b[3], r.PresFreq, r.PresGain, r.PresQ);
            Set(b[4], r.AirFreq, r.AirGain, 0.71f);
            p.EqOn = true;
        }

        static void Set(EqBand b, float f, float g, float q)
        {
            b.On = true;
            b.Freq = f;
            b.GainDb = (float)Math.Round(g * 2) / 2f; // meio dB
            b.Q = q;
        }

        public static string Summary(Result r) =>
            $"grave {Sg(r.LowGain)} · caixa {Sg(r.MudGain)} · médios {Sg(r.MidGain)} · presença {Sg(r.PresGain)} · brilho {Sg(r.AirGain)}";

        static string Sg(float v)
        {
            double x = Math.Round(v * 2) / 2;
            return (x >= 0 ? "+" : "−") + Math.Abs(x).ToString("0.0", CultureInfo.GetCultureInfo("pt-BR")) + " dB";
        }

        static double Mean(double[] v, double f0, double f1)
        {
            double s = 0;
            int n = 0;
            for (int c = 0; c < Centers.Length; c++)
                if (Centers[c] >= f0 && Centers[c] <= f1 && !double.IsNaN(v[c])) { s += v[c]; n++; }
            return n == 0 ? double.NaN : s / n;
        }

        static float Clamp(double v, double lo, double hi) => (float)Math.Max(lo, Math.Min(hi, v));
    }
}
