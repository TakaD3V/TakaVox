using System;

namespace TakaVox.Dsp
{
    /// <summary>
    /// Portao de voz: expansor para baixo guiado pelo nivel E pela deteccao de voz da IA (VAD).
    /// Usa antecipacao (lookahead) para nunca cortar o inicio das palavras.
    /// </summary>
    public sealed class VoiceGate
    {
        readonly float fs;
        readonly float[] delay;
        int dpos;
        float env, gain = 1f;
        int holdLeft;
        bool open;

        public float GainDb { get; private set; }
        public bool IsOpen => open;
        public int LatencySamples => delay.Length;

        public VoiceGate(float sampleRate, int lookaheadSamples)
        {
            fs = sampleRate;
            delay = new float[Math.Max(1, lookaheadSamples)];
        }

        public void Process(float[] buf, int n, ChainParams p, float vad, bool vadAvailable)
        {
            bool enabled = p.GateOn;
            float thrOpen = DspMath.DbToLin(p.GateThresholdDb);
            float thrClose = DspMath.DbToLin(p.GateThresholdDb - 4f);
            float floor = enabled ? DspMath.DbToLin(p.GateRangeDb) : 1f;
            float aEnvA = DspMath.Coef(0.5f, fs), aEnvR = DspMath.Coef(60f, fs);
            float aOpen = DspMath.Coef(1.5f, fs), aClose = DspMath.Coef(p.GateReleaseMs, fs);
            int holdSamples = (int)(p.GateHoldMs * fs / 1000f);
            bool vadOk = !(p.GateUseVad && vadAvailable) || vad >= p.GateVadThreshold;
            float minGain = 1f;

            for (int i = 0; i < n; i++)
            {
                float x = buf[i];
                float ax = x < 0 ? -x : x;
                env = ax > env ? aEnvA * env + (1 - aEnvA) * ax : aEnvR * env + (1 - aEnvR) * ax;
                if (env < 1e-12f) env = 1e-12f;

                bool trig = vadOk && env > (open ? thrClose : thrOpen);
                if (trig) { open = true; holdLeft = holdSamples; }
                else if (holdLeft > 0) holdLeft--;
                else open = false;

                float target = open || !enabled ? 1f : floor;
                float a = target > gain ? aOpen : aClose;
                gain = a * gain + (1 - a) * target;
                if (gain < minGain) minGain = gain;

                float y = delay[dpos];
                delay[dpos] = x;
                if (++dpos == delay.Length) dpos = 0;
                buf[i] = y * gain;
            }
            GainDb = DspMath.LinToDb(minGain);
        }
    }

    /// <summary>Nivelador automatico lento: mantem a voz no volume alvo, so se ajusta enquanto voce fala.</summary>
    public sealed class Leveler
    {
        readonly float fs;
        double msq;
        float gainDb, appliedLin = 1f;

        public float GainDb => gainDb;

        public Leveler(float sampleRate) { fs = sampleRate; }

        public void Process(float[] buf, int n, ChainParams p, bool speechActive)
        {
            if (p.AgcOn)
            {
                double sum = 0;
                for (int i = 0; i < n; i++) sum += buf[i] * buf[i];
                double blockMs = sum / n;
                if (speechActive && blockMs > 1e-10)
                {
                    msq = msq <= 0 ? blockMs : msq + (blockMs - msq) * 0.03; // ~330 ms
                    float level = DspMath.PowToDb(msq);
                    float desired = DspMath.Clamp(p.AgcTargetDb - level, -p.AgcMaxCutDb, p.AgcMaxBoostDb);
                    float step = p.AgcSpeed * n / fs;
                    float diff = desired - gainDb;
                    // reduz mais rapido do que aumenta (evita estouros)
                    gainDb += DspMath.Clamp(diff, -step * 3f, step);
                }
            }
            else
            {
                // desligado: volta suavemente para 0 dB
                float step = 12f * n / fs;
                gainDb += DspMath.Clamp(-gainDb, -step, step);
            }
            gainDb = DspMath.Clamp(gainDb, -24f, 24f);

            float target = DspMath.DbToLin(gainDb);
            float g0 = appliedLin, dg = (target - g0) / n;
            for (int i = 0; i < n; i++) buf[i] *= g0 + dg * i;
            appliedLin = target;
        }
    }

    /// <summary>De-esser por divisao de banda: reduz so os agudos quando o "S" fica ardido.</summary>
    public sealed class DeEsser
    {
        readonly float fs;
        readonly Biquad hp = new Biquad();
        float envHi, envFull, g = 1f;
        float lastFreq = -1;

        public float GrDb { get; private set; }

        public DeEsser(float sampleRate) { fs = sampleRate; }

        public void Process(float[] buf, int n, ChainParams p)
        {
            if (p.DeessFreq != lastFreq)
            {
                hp.Set(FilterType.HighPass, fs, p.DeessFreq, 0.7071, 0);
                lastFreq = p.DeessFreq;
            }
            bool on = p.DeessOn;
            float aA = DspMath.Coef(0.8f, fs), aR = DspMath.Coef(50f, fs);
            float gA = DspMath.Coef(1.0f, fs), gR = DspMath.Coef(60f, fs);
            float thr = p.DeessThresholdDb, maxRed = p.DeessMaxDb;
            float minG = 1f;
            for (int i = 0; i < n; i++)
            {
                float x = buf[i];
                float hi = hp.Process(x);
                float ah = Math.Abs(hi), af = Math.Abs(x);
                envHi = ah > envHi ? aA * envHi + (1 - aA) * ah : aR * envHi + (1 - aR) * ah;
                envFull = af > envFull ? aA * envFull + (1 - aA) * af : aR * envFull + (1 - aR) * af;
                float tg = 1f;
                if (on && envFull > 0.003f) // so age com voz presente (> -50 dBFS)
                {
                    float rel = 20f * (float)Math.Log10((envHi + 1e-9f) / (envFull + 1e-9f));
                    float excess = rel - thr;
                    if (excess > 0) tg = DspMath.DbToLin(-Math.Min(maxRed, excess * 2f));
                }
                g = tg < g ? gA * g + (1 - gA) * tg : gR * g + (1 - gR) * tg;
                if (g < minG) minG = g;
                buf[i] = x - hi * (1f - g);
            }
            if (envHi < 1e-12f) envHi = 1e-12f;
            if (envFull < 1e-12f) envFull = 1e-12f;
            GrDb = DspMath.LinToDb(minG);
        }
    }

    /// <summary>Compressor (detector RMS, joelho suave, ataque/liberacao no dominio de dB).</summary>
    public sealed class Compressor
    {
        readonly float fs;
        float msq = 1e-10f;
        float grDb;
        float makeupPrev = 1f;

        public float GrDb { get; private set; }

        public Compressor(float sampleRate) { fs = sampleRate; }

        static float GainComputer(float x, float t, float r, float w)
        {
            float d = x - t;
            if (2 * d < -w) return x;
            if (w > 0 && 2 * Math.Abs(d) <= w)
            {
                float k = d + w / 2;
                return x + (1f / r - 1f) * k * k / (2 * w);
            }
            return t + d / r;
        }

        public void Process(float[] buf, int n, ChainParams p)
        {
            float makeup = p.CompOn ? DspMath.DbToLin(p.CompMakeupDb) : 1f;
            float m0 = makeupPrev, dm = (makeup - makeupPrev) / n;
            makeupPrev = makeup;
            if (!p.CompOn)
            {
                // relaxa o estado para nao dar "pancada" ao religar
                grDb *= 0.5f;
                for (int i = 0; i < n; i++) buf[i] *= m0 + dm * i;
                GrDb = 0;
                return;
            }
            float aRms = DspMath.Coef(5f, fs);
            float aA = DspMath.Coef(p.CompAttackMs, fs), aR = DspMath.Coef(p.CompReleaseMs, fs);
            float t = p.CompThresholdDb, r = Math.Max(1f, p.CompRatio), w = p.CompKneeDb;
            float maxGr = 0;
            for (int i = 0; i < n; i++)
            {
                float x = buf[i];
                msq = aRms * msq + (1 - aRms) * x * x;
                if (msq < 1e-12f) msq = 1e-12f;
                float lvl = 10f * (float)Math.Log10(msq);
                float gc = GainComputer(lvl, t, r, w) - lvl; // <= 0
                grDb = gc < grDb ? aA * grDb + (1 - aA) * gc : aR * grDb + (1 - aR) * gc;
                if (grDb < maxGr) maxGr = grDb;
                buf[i] = x * DspMath.DbToLin(grDb) * (m0 + dm * i);
            }
            GrDb = maxGr;
        }
    }

    /// <summary>
    /// Limitador "brickwall" com antecipacao: a saida nunca passa do teto (sem distorcer/estourar).
    /// Ganho = media movel (L) do minimo deslizante (L+1) do ganho necessario; liberacao suave.
    /// </summary>
    public sealed class Limiter
    {
        readonly float fs;
        readonly int L;
        readonly float[] delay;
        readonly float[] req;
        readonly float[] held;
        int dpos, rpos, hpos;
        double heldSum;
        float g = 1f;

        public float GrDb { get; private set; }
        public int LatencySamples => L;

        public Limiter(float sampleRate, int lookahead)
        {
            fs = sampleRate;
            L = Math.Max(4, lookahead);
            delay = new float[L];
            req = new float[L + 1];
            held = new float[L];
            for (int i = 0; i < req.Length; i++) req[i] = 1f;
            for (int i = 0; i < held.Length; i++) held[i] = 1f;
            heldSum = L;
        }

        public void Process(float[] buf, int n, float ceilingDb, float releaseMs)
        {
            float ceil = DspMath.DbToLin(ceilingDb);
            float aR = DspMath.Coef(releaseMs, fs);
            float minG = 1f;
            for (int i = 0; i < n; i++)
            {
                float x = buf[i];
                if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
                float ax = x < 0 ? -x : x;
                req[rpos] = ax > ceil ? ceil / ax : 1f;
                if (++rpos == req.Length) rpos = 0;

                float m = 1f;
                for (int k = 0; k < req.Length; k++) if (req[k] < m) m = req[k];

                heldSum += m - held[hpos];
                held[hpos] = m;
                if (++hpos == L) hpos = 0;
                float box = (float)(heldSum / L);

                float rel = g + (1f - g) * (1f - aR);
                g = box < rel ? box : rel;
                if (g < minG) minG = g;

                float y = delay[dpos];
                delay[dpos] = x;
                if (++dpos == L) dpos = 0;
                y *= g;
                if (y > ceil) y = ceil; else if (y < -ceil) y = -ceil;
                buf[i] = y;
            }
            // corrige deriva numerica da soma
            double s = 0;
            for (int k = 0; k < L; k++) s += held[k];
            heldSum = s;
            GrDb = DspMath.LinToDb(minG);
        }
    }
}
