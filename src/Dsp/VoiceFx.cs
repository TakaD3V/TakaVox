using System;

namespace TakaVox.Dsp
{
    /// <summary>
    /// Efeitos do modificador de voz (mono, 48 kHz, tudo leve; nada roda com o modificador desligado).
    ///  Pre (logo depois do motor de tom, antes do nivelador):
    ///   - Sopro/sussurro e robo sintetizado: o "trato vocal" (LPC, 24 polos, a cada 10 ms) e excitado por ruido
    ///     (sussurro) ou por um dente de serra sem serrilhado (vocoder), no mesmo volume da sua voz. O sopro leve
    ///     e esse mesmo ruido com a forma da sua voz, so nos agudos e no ritmo de cada pulso (soa como ar da voz,
    ///     nao como chiado de microfone ruim);
    ///   - Graves e brilho da voz modificada.
    ///  Pos (depois do compressor): distorcao, filtros (telefone, radio, megafone, abafado, radio antigo),
    ///  modulador em anel, metalico, tremolo, eco e reverb.
    /// </summary>
    public sealed class VoiceFx
    {
        const float Fs = 48000f;
        public static readonly string[] FilterNames = { "Nenhum", "Telefone", "Rádio comunicador", "Megafone", "Abafado (atrás da parede)", "Rádio antigo" };

        int lastVersion = int.MinValue;

        public VoiceFx()
        {
            for (int i = 0; i < AWin; i++) awin[i] = (float)(0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (AWin - 1)));
            // janela de atraso (alarga um pouco os formantes: sussurro sem "apito")
            for (int k = 0; k <= Order; k++) { double w = 2 * Math.PI * 100.0 * k / Fs; lagw[k] = Math.Exp(-0.5 * w * w); }
            airHp.Set(FilterType.HighPass, Fs, 1500, 0.707, 0);
            for (int i = 0; i < AWin; i++) winEnergy += awin[i] * (double)awin[i];
        }

        void Update(ChainParams p)
        {
            lastVersion = p.Version;
            bool lo = Math.Abs(p.VcLowDb) > 0.05f, hi = Math.Abs(p.VcHighDb) > 0.05f;
            if (lo && !lowOn) lowSh.Reset();
            if (hi && !highOn) highSh.Reset();
            lowOn = lo; highOn = hi;
            lowSh.Set(FilterType.LowShelf, Fs, 250, 0.71, p.VcLowDb);
            highSh.Set(FilterType.HighShelf, Fs, 3500, 0.71, p.VcHighDb);

            if (p.VcFilter != curFilter)
            {
                curFilter = p.VcFilter;
                foreach (var f in filt) f.Reset();
                filtCount = 0; filtDriveDb = 0;
                switch (curFilter)
                {
                    case 1: // telefone: so 300-3400 Hz, com bordas ingremes
                        F(FilterType.HighPass, 300, 0.54); F(FilterType.HighPass, 300, 1.31);
                        F(FilterType.LowPass, 3400, 0.54); F(FilterType.LowPass, 3400, 1.31); F(FilterType.LowPass, 3800, 0.71);
                        filtDriveDb = 4; break;
                    case 2: // radio comunicador (walkie-talkie)
                        F(FilterType.HighPass, 480, 0.9); F(FilterType.LowPass, 2700, 0.9);
                        F(FilterType.Peak, 1700, 1.2, 6); F(FilterType.LowPass, 3200, 0.54); F(FilterType.LowPass, 3200, 1.31);
                        filtDriveDb = 16; break;
                    case 3: // megafone
                        F(FilterType.HighPass, 650, 0.54); F(FilterType.HighPass, 650, 1.31);
                        F(FilterType.Peak, 1500, 2.2, 8); F(FilterType.LowPass, 4300, 0.54); F(FilterType.LowPass, 4300, 1.31);
                        filtDriveDb = 12; break;
                    case 4: // abafado (atras da parede)
                        F(FilterType.LowPass, 650, 0.54); F(FilterType.LowPass, 650, 1.31); F(FilterType.LowShelf, 160, 0.7, 2);
                        break;
                    case 5: // radio antigo (AM)
                        F(FilterType.HighPass, 220, 0.71); F(FilterType.LowPass, 4200, 0.54);
                        F(FilterType.LowPass, 4200, 1.31); F(FilterType.Peak, 1100, 0.8, 3);
                        filtDriveDb = 7; break;
                }
            }
            combLen = Math.Max(2, Math.Min(CombLen - 8, (int)Math.Round(Fs / Math.Max(50f, p.VcMetalHz))));
        }

        void F(FilterType t, double f, double q, double g = 0) { filt[filtCount++].Set(t, Fs, f, q, g); }

        // ================================================================== PRE: textura (LPC) + graves/brilho
        const int Order = 24, AWin = 1024, Sub = 4, MaxBlock = 4096;
        const float PreEmph = 0.95f;
        readonly float[] hist = new float[AWin];          // entrada pre-enfatizada (ultimas AWin amostras)
        readonly float[] awin = new float[AWin], wbuf = new float[AWin];
        readonly double[] r = new double[Order + 1], lagw = new double[Order + 1];
        readonly double[] ac = new double[Order + 1], acPrev = new double[Order + 1];
        readonly float[] kOld = new float[Order + 1], kNew = new float[Order + 1], kNow = new float[Order + 1];
        readonly float[] latW = new float[Order + 1], latS = new float[Order + 1];
        float preMem, deW, deS;
        uint rng = 0x9E3779B9u;
        double sawPh;
        bool lpcOn;
        readonly Biquad airHp = new Biquad();
        readonly Biquad lowSh = new Biquad(), highSh = new Biquad();
        bool lowOn, highOn;

        public void Pre(float[] x, int n, ChainParams p)
        {
            if (p.Version != lastVersion) Update(p);
            if (!p.VcOn) { lpcOn = false; return; }
            float breath = p.VcBreath / 100f, synth = p.VcSynth / 100f;
            if ((breath > 0.001f || synth > 0.001f) && n <= MaxBlock)
            {
                if (!lpcOn) ResetLpc();
                lpcOn = true;
                Texture(x, n, breath, synth, p.VcSynthHz);
            }
            else lpcOn = false;
            if (lowOn) lowSh.Process(x, n);
            if (highOn) highSh.Process(x, n);
        }

        void ResetLpc()
        {
            Array.Clear(hist, 0, AWin); Array.Clear(kOld, 0, Order + 1); Array.Clear(kNew, 0, Order + 1);
            Array.Clear(latW, 0, Order + 1); Array.Clear(latS, 0, Order + 1);
            preMem = deW = deS = 0; lpcPowOld = lpcPowNew = 0; envFast = envSlow = 0; prevSaw = 0; sawPh = 0;
            slowIn = slowW = slowS = 0; corrW = corrS = 1f;
            airHp.Reset();
            Array.Clear(apX, 0, 6); Array.Clear(apY, 0, 6);
        }

        float Noise()
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            return (int)rng * (1f / 2147483648f);
        }

        static double PolyBlep(double t, double dt)
        {
            if (t < dt) { t /= dt; return t + t - t * t - 1; }
            if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
            return 0;
        }

        /// <summary>Filtro do trato vocal (lattice so de polos; estavel enquanto |k| &lt; 1).</summary>
        static float Lattice(float e, float[] k, float[] b)
        {
            float f = e;
            for (int i = Order; i >= 1; i--)
            {
                f -= k[i] * b[i - 1];
                b[i] = b[i - 1] + k[i] * f;
            }
            b[0] = f;
            return f;
        }

        /// <summary>Envelope espectral (LPC) das ultimas ~21 ms da voz: coeficientes de reflexao em kNew.</summary>
        void Analyze(float[] x, int n)
        {
            if (n < AWin) Array.Copy(hist, n, hist, 0, AWin - n);
            int from = Math.Max(0, n - AWin);
            for (int i = 0; i < n; i++)
            {
                float v = x[i];
                float pe = v - PreEmph * preMem; preMem = v;
                if (i >= from) hist[AWin - (n - i)] = pe;
            }
            for (int i = 0; i < AWin; i++) wbuf[i] = hist[i] * awin[i];
            for (int k = 0; k <= Order; k++)
            {
                double s = 0;
                for (int i = k; i < AWin; i++) s += wbuf[i] * wbuf[i - k];
                r[k] = s * lagw[k];
            }
            Array.Copy(kNew, kOld, Order + 1);
            lpcPowOld = lpcPowNew;
            if (r[0] < 1e-10) { Array.Clear(kNew, 0, Order + 1); lpcPowNew = (float)(r[0] / winEnergy); return; } // silencio: filtro plano
            r[0] *= 1.003;   // piso de ruido de -25 dB: a voz a 48 kHz quase nao tem agudos e o LPC ficaria instavel
            // Levinson-Durbin
            double E = r[0];
            Array.Clear(ac, 0, Order + 1);
            for (int i = 1; i <= Order; i++)
            {
                double acc = r[i];
                for (int j = 1; j < i; j++) acc += ac[j] * r[i - j];
                double k = -acc / E;
                if (double.IsNaN(k)) { for (int q = i; q <= Order; q++) kNew[q] = 0f; break; }
                k = Math.Max(-0.985, Math.Min(0.985, k));   // ressonancias limitadas (sem apito nem estouro)
                kNew[i] = (float)k;
                Array.Copy(ac, acPrev, i);
                ac[i] = k;
                for (int j = 1; j < i; j++) ac[j] = acPrev[j] + k * acPrev[i - j];
                E *= 1 - k * k;
            }
            lpcPowNew = (float)(E / winEnergy);
        }

        // ganho do LPC (potencia do residuo por amostra): o sussurro e o robo saem no mesmo volume da voz,
        // calculado direto do envelope (sem medir a saida: nada de "bombear" nem estourar nas trocas de vogal)
        float lpcPowOld, lpcPowNew, envFast, envSlow, prevSaw;
        double winEnergy;
        // correcao lenta (~0,4 s) do volume: o modelo acerta o envelope na hora; isto tira a diferenca media que sobra
        float slowIn, slowW, slowS, corrW = 1f, corrS = 1f;
        static readonly float CSlow = 1f - (float)Math.Exp(-1.0 / (0.4 * Fs));
        static readonly float CEnv = 1f - (float)Math.Exp(-1.0 / (0.002 * Fs));
        static readonly float CEnvSlow = 1f - (float)Math.Exp(-1.0 / (0.02 * Fs));
        const float Sqrt3 = 1.7320508f;
        readonly float[] apX = new float[6], apY = new float[6];

        /// <summary>6 passa-tudo de 1a ordem: espalha cada pulso por ~1 ms (mesmo som, picos bem menores).</summary>
        float Disperse(float v)
        {
            const float a = 0.65f;
            for (int i = 0; i < 6; i++)
            {
                float y = a * v + apX[i] - a * apY[i];
                apX[i] = v; apY[i] = y;
                v = y;
            }
            return v;
        }

        void Texture(float[] x, int n, float breath, float synth, float synthHz)
        {
            Analyze(x, n);
            int zc = 0;
            float prev = hist[Math.Max(0, AWin - n - 1)];
            for (int i = 0; i < n; i++)
            {
                float v = hist[AWin - n + i];
                if ((v >= 0) != (prev >= 0)) zc++;
                prev = v;
            }
            // consoantes (s, f, x) cruzam o zero muito mais: ali o robo usa ruido em vez do pulso
            float noisy = DspMath.Clamp((zc / (float)n - 0.12f) / 0.2f, 0f, 1f);
            float sqN = (float)Math.Sqrt(noisy), sqP = (float)Math.Sqrt(1f - noisy);
            // sopro 0..50%: ar = o sussurro (ruido com as ressonancias da SUA voz), so acima de 1,5 kHz e no ritmo de
            // cada pulso, somado a voz (no maximo ~+7 dB nos agudos: ar da voz, nao chiado por cima);
            // 50..100%: a voz vai sumindo (troca de mesma potencia) e fica so o sussurro (ruido no envelope da voz)
            float u = breath <= 0.5f ? 0f : (breath - 0.5f) / 0.5f;
            float air = breath <= 0.5f ? breath / 0.5f : 1f - 0.6f * u;
            float vGain = (float)Math.Cos(u * Math.PI / 2), wGain = (float)Math.Sin(u * Math.PI / 2);
            bool doAir = air > 0.001f, doW = wGain > 0.001f, doS = synth > 0.001f, needW = doAir || doW;
            double dt = synthHz / Fs;
            float pulseNorm = (float)(1.0 / Math.Sqrt(2 * dt));
            int seg = n / Sub;
            for (int q = 0; q < Sub; q++)
            {
                float t = (q + 1) / (float)Sub;
                for (int i = 1; i <= Order; i++) kNow[i] = kOld[i] + (kNew[i] - kOld[i]) * t;
                float amp = (float)Math.Sqrt(Math.Max(0f, lpcPowOld + (lpcPowNew - lpcPowOld) * t));
                int i1 = q == Sub - 1 ? n : (q + 1) * seg;
                for (int i = q * seg; i < i1; i++)
                {
                    float v = x[i];
                    float y = v * vGain;
                    slowIn += CSlow * (v * v - slowIn);
                    float w = 0f;
                    if (needW)
                    {
                        w = Lattice(Noise() * Sqrt3 * amp, kNow, latW);
                        w += PreEmph * deW; deW = w;            // tira a pre-enfase
                        slowW += CSlow * (w * w - slowW);
                        w *= corrW;                             // mesmo volume medio da sua voz
                    }
                    if (doAir)
                    {
                        envFast += CEnv * (Math.Abs(v) - envFast);
                        envSlow += CEnvSlow * (envFast - envSlow);
                        float puls = envSlow > 1e-9f ? Math.Min(2f, envFast / envSlow) : 1f;   // mais ar no pulso da voz
                        y += airHp.Process(w) * puls * air * 1.4f;
                    }
                    if (doW) y += w * wGain;
                    if (doS)
                    {
                        sawPh += dt; if (sawPh >= 1) sawPh -= 1;
                        float saw = (float)(2 * sawPh - 1 - PolyBlep(sawPh, dt));
                        float pulse = Disperse((saw - prevSaw) * pulseNorm); prevSaw = saw;   // trem de pulsos sem serrilhado, espalhado
                        float sv = Lattice((pulse * sqP + Noise() * Sqrt3 * sqN) * amp, kNow, latS);
                        sv += PreEmph * deS; deS = sv;
                        slowS += CSlow * (sv * sv - slowS);
                        y = y * (1f - synth) + sv * corrS * synth;
                    }
                    x[i] = y;
                }
            }
            // atualiza a correcao so com voz presente (> -60 dBFS)
            if (slowIn > 1e-6f)
            {
                if (needW && slowW > 1e-12f) corrW = DspMath.Clamp((float)Math.Sqrt(slowIn / slowW), 0.3f, 3f);
                if (doS && slowS > 1e-12f) corrS = DspMath.Clamp((float)Math.Sqrt(slowIn / slowS), 0.3f, 3f);
            }
            // nada de numeros "denormais" (lentissimos) quando a voz para
            if (slowIn < 1e-20f) slowIn = 0f;
            if (slowW < 1e-20f) slowW = 0f;
            if (slowS < 1e-20f) slowS = 0f;
            if (envFast < 1e-20f) envFast = 0f;
            if (envSlow < 1e-20f) envSlow = 0f;
            if (!(Math.Abs(deW) < 1e4f) || !(Math.Abs(deS) < 1e4f))
            {
                ResetLpc();
                for (int i = 0; i < n; i++) if (!(Math.Abs(x[i]) < 1e4f)) x[i] = 0f;
            }
        }

        // ================================================================== POS: efeitos
        readonly Biquad[] filt = { new Biquad(), new Biquad(), new Biquad(), new Biquad(), new Biquad(), new Biquad() };
        int filtCount, curFilter = -1;
        float filtDriveDb;
        const int CombLen = 1024;
        readonly float[] comb = new float[CombLen];
        int combPos, combLen = 400;
        const int EchoLen = 48000;
        readonly float[] echo = new float[EchoLen];
        int echoPos;
        float echoLp;
        readonly Reverb verb = new Reverb();
        double ringPh, tremPh;
        float alIn, alOut, alGain = 1f;
        bool postOn, echoOn, verbOn, metalOn;
        const float AntiDenormal = 1e-18f;

        void ResetPost()
        {
            Array.Clear(comb, 0, CombLen); Array.Clear(echo, 0, EchoLen);
            verb.Clear();
            echoOn = verbOn = metalOn = true;
            echoLp = 0; ringPh = tremPh = 0; alIn = alOut = 0; alGain = 1f;
            foreach (var f in filt) f.Reset();
        }

        static float FastTanh(float v)
        {
            if (v > 3f) return 1f;
            if (v < -3f) return -1f;
            float v2 = v * v;
            return v * (27f + v2) / (27f + 9f * v2);
        }

        public void Post(float[] x, int n, ChainParams p)
        {
            if (p.Version != lastVersion) Update(p);
            if (!p.VcOn) { postOn = false; return; }
            if (!postOn) { ResetPost(); postOn = true; }

            bool ring = p.VcRingMix > 0.5f, metal = p.VcMetal > 0.5f;
            float driveDb = p.VcDrive / 100f * 30f + filtDriveDb;
            bool color = driveDb > 0.05f || filtCount > 0 || ring || metal;
            double pin = 0;
            if (color) for (int i = 0; i < n; i++) pin += x[i] * (double)x[i];

            // 1) distorcao
            if (driveDb > 0.05f)
            {
                float g = DspMath.DbToLin(driveDb);
                for (int i = 0; i < n; i++) x[i] = FastTanh(x[i] * g);
            }
            // 2) filtro de voz (telefone, radio...)
            for (int f = 0; f < filtCount; f++) filt[f].Process(x, n);
            // 3) modulador em anel
            if (ring)
            {
                float m = p.VcRingMix / 100f;
                double w = 2 * Math.PI * p.VcRingHz / Fs;
                for (int i = 0; i < n; i++) { x[i] *= 1f - m + m * (float)Math.Sin(ringPh); ringPh += w; }
                if (ringPh > 2 * Math.PI) ringPh %= 2 * Math.PI;
            }
            // 4) metalico (pente com realimentacao: ressoa nos harmonicos de uma nota fixa)
            if (metal)
            {
                if (!metalOn) { Array.Clear(comb, 0, CombLen); metalOn = true; }
                float fb = 0.88f * p.VcMetal / 100f, norm = 1f - fb;
                for (int i = 0; i < n; i++)
                {
                    float y = x[i] + fb * comb[(combPos - combLen) & (CombLen - 1)] + AntiDenormal;
                    comb[combPos & (CombLen - 1)] = y;
                    combPos++;
                    x[i] = y * norm;
                }
            }
            else metalOn = false;
            // 5) volume automatico: os efeitos acima mudam muito o volume (telefone tira os graves, distorcao aumenta...)
            if (color)
            {
                double pout = 0;
                for (int i = 0; i < n; i++) pout += x[i] * (double)x[i];
                float g0 = alGain;
                if (pin > 1e-7 * n)
                {
                    alIn = 0.9f * alIn + 0.1f * (float)(pin / n);
                    alOut = 0.9f * alOut + 0.1f * (float)(pout / n);
                    if (alOut > 1e-12f) alGain = DspMath.Clamp((float)Math.Sqrt(alIn / alOut), 0.25f, 4f);
                }
                for (int i = 0; i < n; i++) x[i] *= g0 + (alGain - g0) * (i + 1) / n;
            }
            // 6) tremolo
            if (p.VcTremDepth > 0.5f)
            {
                float d = p.VcTremDepth / 100f;
                double w = 2 * Math.PI * p.VcTremRate / Fs;
                for (int i = 0; i < n; i++) { x[i] *= 1f - d * 0.5f * (1f - (float)Math.Cos(tremPh)); tremPh += w; }
                if (tremPh > 2 * Math.PI) tremPh %= 2 * Math.PI;
            }
            // 7) eco (as repeticoes vao ficando abafadas, como um eco de verdade)
            if (p.VcEchoMix > 0.5f)
            {
                if (!echoOn) { Array.Clear(echo, 0, EchoLen); echoLp = 0; echoOn = true; }
                int D = Math.Max(1, Math.Min(EchoLen, (int)(p.VcEchoMs * 48f)));
                float fb = p.VcEchoFb / 100f, mix = p.VcEchoMix / 100f;
                for (int i = 0; i < n; i++)
                {
                    int rd = echoPos - D; if (rd < 0) rd += EchoLen;
                    float d = echo[rd];
                    echoLp += 0.35f * (d - echoLp);
                    echo[echoPos] = x[i] + echoLp * fb + AntiDenormal;
                    x[i] += d * mix;
                    if (++echoPos == EchoLen) echoPos = 0;
                }
            }
            else echoOn = false;
            // 8) reverb
            if (p.VcReverbMix > 0.5f)
            {
                float size = p.VcReverbSize / 100f, mix = p.VcReverbMix / 100f;
                float fb = 0.70f + 0.28f * size, damp = 0.35f - 0.15f * size;
                float dry = 1f - 0.35f * mix, wet = mix * 1.6f;
                if (!verbOn) { verb.Clear(); verbOn = true; }
                verb.Feedback = fb; verb.Damp = damp;
                for (int i = 0; i < n; i++) x[i] = x[i] * dry + verb.Process(x[i]) * wet;
            }
            else verbOn = false;
        }

        /// <summary>Reverb classico de Schroeder/Moorer (8 pentes com amortecimento + 4 passa-tudo), mono.</summary>
        sealed class Reverb
        {
            static readonly int[] CombT = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
            static readonly int[] ApT = { 556, 441, 341, 225 };
            readonly float[][] cb = new float[8][];
            readonly int[] ci = new int[8];
            readonly float[] cf = new float[8];
            readonly float[][] ab = new float[4][];
            readonly int[] ai = new int[4];
            public float Feedback = 0.84f, Damp = 0.25f;

            public Reverb()
            {
                for (int c = 0; c < 8; c++) cb[c] = new float[(int)(CombT[c] * 48000.0 / 44100.0)];
                for (int a = 0; a < 4; a++) ab[a] = new float[(int)(ApT[a] * 48000.0 / 44100.0)];
            }

            public void Clear()
            {
                for (int c = 0; c < 8; c++) { Array.Clear(cb[c], 0, cb[c].Length); cf[c] = 0; ci[c] = 0; }
                for (int a = 0; a < 4; a++) { Array.Clear(ab[a], 0, ab[a].Length); ai[a] = 0; }
            }

            public float Process(float input)
            {
                float inp = input * 0.015f + AntiDenormal;
                float sum = 0, fb = Feedback, d1 = Damp, d0 = 1f - Damp;
                for (int c = 0; c < 8; c++)
                {
                    float[] b = cb[c]; int i = ci[c];
                    float o = b[i];
                    cf[c] = o * d0 + cf[c] * d1;
                    b[i] = inp + cf[c] * fb;
                    if (++i == b.Length) i = 0;
                    ci[c] = i;
                    sum += o;
                }
                for (int a = 0; a < 4; a++)
                {
                    float[] b = ab[a]; int i = ai[a];
                    float bo = b[i];
                    float o = bo - sum;
                    b[i] = sum + bo * 0.5f;
                    if (++i == b.Length) i = 0;
                    ai[a] = i;
                    sum = o;
                }
                return sum;
            }
        }
    }
}
