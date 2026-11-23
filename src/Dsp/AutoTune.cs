using System;

namespace TakaVox.Dsp
{
    /// <summary>
    /// Motor de tom da voz: um unico estagio TD-PSOLA (atraso extra de 1800 amostras = 37,5 ms enquanto ativo).
    ///  - Auto-Tune: detecta a nota (YIN) e corrige a afinacao. Velocidade 0 = efeito robo (T-Pain); 100 = natural.
    ///  - Modificador de voz: muda o TOM (semitons) e o TIMBRE (formantes) de forma independente, a entonacao
    ///    (0% = monotono de robo ... 200% = bem expressiva), vibrato e uma "voz extra" em outra altura (monstro, dueto).
    /// O PSOLA copia periodos da voz (graos de 2 periodos) no ritmo do tom novo. Cada grao e re-amostrado pelo fator de
    /// formantes: isso muda o "tamanho da garganta" sem mudar o tom. E o que faz uma voz de garota soar como garota, e nao
    /// como esquilo. Tudo no dominio do tempo: poucas contas por amostra.
    /// Para a voz sair limpa (e nao com cara de "microfone ruim"):
    ///  - cada marca de periodo e colocada onde a onda mais se PARECE com o periodo anterior (correlacao), e nao no pico
    ///    mais alto: o pico pula entre as ondulacoes do 1o formante e desalinha os graos (voz aspera, "rachada");
    ///  - quando a deteccao falha no meio da voz (final de vogal, voz rangida, trecho fraco), o ultimo tom e SEGURADO por
    ///    ate 200 ms: antes esses trechos saiam no tom original, e a sua voz grossa vazava por baixo da nova;
    ///  - o tom de cada grao e interpolado entre as estimativas (sem degraus a cada 10 ms).
    /// </summary>
    public sealed class AutoTune
    {
        public const int Delay = 1800;
        const int Fs = 48000;
        const int Ring = 16384, Mask = Ring - 1;
        const int Dec = 4;                // decimacao para a deteccao (12 kHz)
        const int Win = 300;              // janela YIN (25 ms a 12 kHz)
        const int MinLag = 15;            // 800 Hz
        const int MaxLag = 200;           // 60 Hz (vozes graves e o comeco da voz rangida)
        const int DecRing = 1024, DecMask = DecRing - 1;
        const int UnvoicedHop = 240;
        const int EstSlots = 64;
        const int Margin = 4;             // amostras de folga alem do grao (interpolacao cubica)
        const int HoldBlocks = 20;        // segura o ultimo tom por ate 200 ms quando a deteccao falha no meio da voz
        const float FricativeZcr = 0.12f; // cruzamentos de zero por amostra acima disso = consoante (s, x, f): nao segura

        public static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        public static readonly string[] ScaleNames = { "Cromática (todas)", "Maior", "Menor", "Pentatônica maior", "Pentatônica menor", "Blues" };
        static readonly int[][] Scales =
        {
            new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 },
            new[] { 0, 2, 4, 5, 7, 9, 11 },
            new[] { 0, 2, 3, 5, 7, 8, 10 },
            new[] { 0, 2, 4, 7, 9 },
            new[] { 0, 3, 5, 7, 10 },
            new[] { 0, 3, 5, 6, 7, 10 },
        };

        /// <summary>
        /// Encolhe os graos quando o tom SOBE muito (0 = graos de 2 periodos sempre). Graos menores sobrepoem menos
        /// periodos diferentes: voz aguda mais limpa, sem "chorus". Ajustado pelo teste de realismo.
        /// </summary>
        public static float GrainShrink = 0.35f;

        // entrada
        readonly float[] inRing = new float[Ring];
        long inPos;
        // deteccao
        readonly float[] decRing = new float[DecRing];
        long decPos;
        readonly Biquad aa1 = new Biquad(), aa2 = new Biquad();
        int decPhase;
        readonly float[] yinBuf = new float[Win + MaxLag];
        readonly float[] diff = new float[MaxLag + 2];
        float lastPeriod;             // em amostras de 48 kHz (0 = sem voz)
        int pendingJump, holdCount;
        bool holding;                 // este bloco repete o ultimo tom (a deteccao falhou no meio da voz)
        float prevSample;             // lado do zero da ultima amostra contada (cruzamentos com histerese)
        readonly float[] zcrHist = new float[4];
        int zcrPos;
        float corrCents;              // correcao do Auto-Tune (suavizada)
        float vcCents;                // deslocamento do modificador (mantido nas consoantes, sem "piscar" o tom original)
        double meanMidi;              // media movel do tom da voz: base da entonacao
        int voicedBlocks;
        int heldNote = -1;

        // estimativas por bloco (para colocar as marcas no tempo certo)
        readonly long[] estTime = new long[EstSlots];
        readonly float[] estPeriod = new float[EstSlots];
        readonly float[] estCents = new float[EstSlots];
        int estCount;

        // marcas de analise
        const int MarkSlots = 512, MarkMask = MarkSlots - 1;
        readonly long[] markPos = new long[MarkSlots];
        readonly float[] markT = new float[MarkSlots];
        readonly bool[] markVoiced = new bool[MarkSlots];
        long markHead;
        long lastMark;
        bool lastVoiced;
        float lastT;

        /// <summary>Um fluxo de sintese (a voz principal ou a voz extra), com sua propria soma sobreposta.</summary>
        sealed class Stream
        {
            public readonly float[] Ola = new float[Ring], OlaW = new float[Ring];
            public double Next;       // proxima marca de sintese
            public long Tail;         // primeira marca de analise ainda util para este fluxo
            public void Clear() { Array.Clear(Ola, 0, Ring); Array.Clear(OlaW, 0, Ring); Next = 0; Tail = 0; }
        }
        readonly Stream main = new Stream(), extra = new Stream();
        bool extraOn;
        float extraRamp;
        bool started;

        // transicao liga/desliga
        bool wasOn;
        int fadeIn;

        // para a interface
        public float DetectedHz { get; private set; }
        public float TargetHz { get; private set; }
        public float CentsOff { get; private set; }
        public int TargetMidi { get; private set; } = -1;
        /// <summary>Tom estimado da voz que sai (com modificador/Auto-Tune); 0 = sem voz agora.</summary>
        public float OutHz { get; private set; }
        public bool Voiced => lastPeriod > 0;

        public AutoTune()
        {
            // anti-alias antes de decimar para 12 kHz (Butterworth 4a ordem em 2,6 kHz)
            aa1.Set(FilterType.LowPass, Fs, 2600, 0.5412, 0);
            aa2.Set(FilterType.LowPass, Fs, 2600, 1.3066, 0);
        }

        public static string NoteName(int midi) => midi < 0 ? "–" : NoteNames[midi % 12] + (midi / 12 - 1);

        /// <summary>O modificador de voz precisa do motor de tom? (mexe em tom, timbre, entonacao, vibrato ou voz extra)</summary>
        public static bool ShapeActive(ChainParams p) =>
            p.VcOn && !p.VcAiOn && (Math.Abs(p.VcPitch) >= 0.05f || Math.Abs(p.VcFormant) >= 0.5f || Math.Abs(p.VcIntonation - 100f) >= 1f ||
                       p.VcVibDepth >= 1f || p.VcExtraMix >= 1f);

        /// <summary>Este estagio roda agora? (Auto-Tune ligado ou modificador mexendo no tom/timbre)</summary>
        public static bool Needed(ChainParams p) => p.TuneOn || ShapeActive(p);

        /// <summary>Timbre em % -> fator dos formantes (1,24 = formantes 24% mais altos = garganta menor).</summary>
        public static float FormantRatio(float pct) => DspMath.Clamp(1f + pct / 100f, 0.6f, 1.7f);

        /// <summary>Esquece o audio guardado (quando outro motor, como a voz com IA, assume o lugar deste).</summary>
        public void Clear() { if (wasOn) { Reset(); wasOn = false; } }

        void Reset()
        {
            Array.Clear(inRing, 0, Ring);
            Array.Clear(decRing, 0, DecRing);
            Array.Clear(zcrHist, 0, zcrHist.Length);
            main.Clear(); extra.Clear();
            inPos = 0; decPos = 0; decPhase = 0; lastPeriod = 0; pendingJump = 0; holdCount = 0; holding = false; prevSample = 0; zcrPos = 0;
            corrCents = 0; vcCents = 0; heldNote = -1;
            meanMidi = 0; voicedBlocks = 0;
            estCount = 0; markHead = 0; lastMark = 0; lastVoiced = false; lastT = 0; started = false; extraOn = false; extraRamp = 0;
            aa1.Reset(); aa2.Reset();
            DetectedHz = TargetHz = CentsOff = OutHz = 0; TargetMidi = -1;
        }

        public void Process(float[] x, int n, ChainParams p)
        {
            bool on = Needed(p);
            if (!on)
            {
                if (wasOn) { Reset(); wasOn = false; }
                DetectedHz = 0; TargetMidi = -1; OutHz = 0;
                return;
            }
            if (!wasOn) { Reset(); wasOn = true; fadeIn = 960; }
            bool vc = ShapeActive(p);

            // 1) guarda a entrada e alimenta o detector
            double e = 0;
            for (int i = 0; i < n; i++)
            {
                float v = x[i];
                if (v > -1e-20f && v < 1e-20f) v = 0f;   // nada de numeros "denormais" (lentissimos) no silencio
                inRing[(inPos + i) & Mask] = v;
                e += v * v;
                float lp = aa2.Process(aa1.Process(v));
                if (++decPhase == Dec) { decPhase = 0; decRing[decPos & DecMask] = lp; decPos++; }
            }
            // cruzamentos de zero "de verdade" (com histerese de 15% do RMS): o chiado do s/x/f cruza o tempo todo;
            // o quase-silencio entre os pulsos da voz rangida nao conta
            float th = 0.15f * (float)Math.Sqrt(e / n);
            int zc = 0;
            float zs = prevSample;
            for (int i = 0; i < n; i++)
            {
                float v = inRing[(inPos + i) & Mask];
                if (zs >= 0f ? v < -th : v > th) { zc++; zs = v; }
                else if (zs == 0f && Math.Abs(v) > th) zs = v;
            }
            prevSample = zs;
            inPos += n;
            float rmsDb = DspMath.PowToDb(e / n);
            // consoante chiada (s, x, f) em qualquer parte da janela de deteccao (~4 blocos)
            zcrHist[zcrPos++ & 3] = zc / (float)n;
            float zcr = Math.Max(Math.Max(zcrHist[0], zcrHist[1]), Math.Max(zcrHist[2], zcrHist[3]));

            // 2) detecta a nota do bloco atual e decide o tom de saida
            float period = DetectPeriod(rmsDb, zcr);
            UpdateCorrection(period, p, vc);
            // a estimativa descreve o centro da janela de analise
            long center = inPos - (Win + MaxLag) * Dec / 2;
            int slot = estCount % EstSlots;
            estTime[slot] = center; estPeriod[slot] = period; estCents[slot] = vcCents + corrCents;
            estCount++;

            if (!started)
            {
                if (inPos < Delay + 1200) { Array.Clear(x, 0, n); return; }
                lastMark = inPos - Delay - 600;
                main.Next = lastMark;
                main.Tail = extra.Tail = markHead;
                started = true;
            }

            // 3) marcas de analise (sincronas com o periodo da voz)
            PlaceMarks();

            // 4) sintese PSOLA ate cobrir o trecho de saida
            long end = inPos - Delay, start = end - n;
            float vib = vc ? p.VcVibDepth : 0f, vibRate = p.VcVibRate;
            Synthesize(main, end, vc ? FormantRatio(p.VcFormant) : 1f, 0f, false, vib, vibRate);

            bool wantExtra = vc && p.VcExtraMix >= 1f;
            if (wantExtra && !extraOn)
            {
                extra.Clear();
                extra.Next = start; extra.Tail = main.Tail;
                extraRamp = 0f; extraOn = true;
            }
            else if (!wantExtra) extraOn = false;
            if (extraOn) Synthesize(extra, end, FormantRatio(p.VcExtraFormant), (p.VcExtraPitch - p.VcPitch) * 100f, true, vib, vibRate);
            else extra.Tail = main.Tail;

            // 5) saida (voz principal + voz extra, mantendo o volume parecido)
            float mixE = extraOn ? p.VcExtraMix / 100f : 0f;
            for (int i = 0; i < n; i++)
            {
                int idx = (int)((start + i) & Mask);
                float y = Take(main, idx);
                if (extraOn)
                {
                    float ye = Take(extra, idx);
                    if (extraRamp < 1f) extraRamp = Math.Min(1f, extraRamp + 1f / 960f);
                    float m = mixE * extraRamp;
                    y = (y + ye * m) / (float)Math.Sqrt(1f + m * m);
                }
                if (fadeIn > 0) { y *= 1f - fadeIn / 960f; fadeIn--; }
                x[i] = y;
            }
        }

        static float Take(Stream st, int idx)
        {
            float w = st.OlaW[idx], v = st.Ola[idx];
            st.Ola[idx] = 0f; st.OlaW[idx] = 0f;
            return w > 0.25f ? v / w : v * 4f;
        }

        float Unvoiced()
        {
            lastPeriod = 0; pendingJump = 0; holdCount = 0; DetectedHz = 0;
            return 0;
        }

        float DetectPeriod(float rmsDb, float zcr)
        {
            holding = false;
            if (decPos < Win + MaxLag || rmsDb < -55f) return Unvoiced();
            long s0 = decPos - (Win + MaxLag);
            for (int i = 0; i < Win + MaxLag; i++) yinBuf[i] = decRing[(s0 + i) & DecMask];

            // funcao diferenca + media cumulativa normalizada (YIN)
            diff[0] = 1f;
            double running = 0;
            for (int tau = 1; tau <= MaxLag; tau++)
            {
                double d = 0;
                for (int j = 0; j < Win; j++) { float q = yinBuf[j] - yinBuf[j + tau]; d += q * q; }
                running += d;
                diff[tau] = running > 0 ? (float)(d * tau / running) : 1f;
            }
            bool fricative = zcr > FricativeZcr;
            int best = -1;
            for (int tau = MinLag; tau <= MaxLag; tau++)
            {
                if (diff[tau] < 0.15f)
                {
                    while (tau + 1 <= MaxLag && diff[tau + 1] < diff[tau]) tau++;
                    best = tau; break;
                }
            }
            if (best < 0)
            {
                float m = float.MaxValue; int at = -1;
                for (int tau = MinLag; tau <= MaxLag; tau++) if (diff[tau] < m) { m = diff[tau]; at = tau; }
                bool ok = false;
                if (!fricative && at > 0)
                {
                    if (m <= 0.3f) ok = true;
                    // voz que ja vinha (mais fraca ou soprosa agora): aceita um periodo menos nitido perto do anterior
                    else if (m <= 0.45f && lastPeriod > 0 && Math.Abs(at * Dec - lastPeriod) < 0.25f * lastPeriod) ok = true;
                }
                if (ok) best = at;
                else
                {
                    // deteccao falhou no meio da voz (final de vogal, voz rangida): segura o ultimo tom em vez de
                    // deixar sair o tom original. Consoante chiada ou silencio: solta na hora.
                    if (!fricative && lastPeriod > 0 && holdCount < HoldBlocks && rmsDb > -48f)
                    {
                        holdCount++;
                        holding = true;
                        return lastPeriod;
                    }
                    return Unvoiced();
                }
            }
            holdCount = 0;
            // interpolacao parabolica
            float tauF = best;
            if (best > MinLag && best < MaxLag)
            {
                float a = diff[best - 1], b = diff[best], c = diff[best + 1];
                float den = a - 2 * b + c;
                if (Math.Abs(den) > 1e-9f) tauF = best + 0.5f * (a - c) / den;
            }
            float period = tauF * Dec;
            // protecao contra pulos de oitava: so aceita salto grande se repetir
            if (lastPeriod > 0 && Math.Abs(period - lastPeriod) / lastPeriod > 0.25f)
            {
                if (++pendingJump < 2) return lastPeriod;
            }
            pendingJump = 0;
            lastPeriod = period;
            DetectedHz = Fs / period;
            return period;
        }

        void UpdateCorrection(float period, ChainParams p, bool vc)
        {
            if (period <= 0)
            {
                corrCents *= 0.85f; // sem voz: solta a correcao do Auto-Tune
                heldNote = -1;
                TargetHz = 0; CentsOff = 0; TargetMidi = -1; OutHz = 0;
                if (!vc) vcCents = 0f; // com o modificador, mantem o ultimo deslocamento (as bordas das vogais nao "piscam")
                return;
            }
            float f0 = Fs / period;
            float midi = 69f + 12f * (float)Math.Log(f0 / 440.0, 2);
            float shifted = midi;
            if (vc)
            {
                // entonacao: afasta (ou aproxima) o tom da media da sua voz; 0% = sempre a mesma nota (robo)
                if (!holding)
                {
                    voicedBlocks++;
                    double a = Math.Max(1.0 / voicedBlocks, 1.0 / 150.0);
                    meanMidi = voicedBlocks == 1 ? midi : meanMidi + a * (midi - meanMidi);
                }
                float k = DspMath.Clamp(p.VcIntonation, 0f, 200f) / 100f;
                shifted = (float)(meanMidi + (midi - meanMidi) * k) + p.VcPitch;
            }
            vcCents = (shifted - midi) * 100f;

            if (p.TuneOn)
            {
                // o Auto-Tune afina a voz JA modificada (ex.: garota de anime afinada)
                float speed = DspMath.Clamp(p.TuneSpeed, 0, 100);
                float amount = DspMath.Clamp(p.TuneAmount, 0, 100) / 100f;
                int target = Nearest(shifted, p.TuneKey, p.TuneScale);
                // histerese: nao fica pulando entre duas notas vizinhas
                if (heldNote >= 0 && heldNote != target && Math.Abs(shifted - heldNote) < 0.62f && InScale(heldNote, p.TuneKey, p.TuneScale)) target = heldNote;
                heldNote = target;
                float desired = (target - shifted) * 100f * amount;
                // velocidade: 0 -> instantaneo (robo) ... 100 -> ~250 ms (natural)
                float tauMs = speed * 2.5f;
                float ac = tauMs < 1f ? 0f : (float)Math.Exp(-10.0 / tauMs);
                corrCents = ac * corrCents + (1 - ac) * desired;
                TargetMidi = target;
                TargetHz = (float)(440.0 * Math.Pow(2, (target - 69) / 12.0));
                CentsOff = (shifted - target) * 100f;
            }
            else { corrCents = 0f; heldNote = -1; TargetMidi = -1; TargetHz = 0; CentsOff = 0; }
            OutHz = (float)(f0 * Math.Pow(2, (vcCents + corrCents) / 1200.0));
        }

        static bool InScale(int midi, int key, int scale)
        {
            var sc = Scales[Math.Max(0, Math.Min(Scales.Length - 1, scale))];
            int rel = ((midi - key) % 12 + 12) % 12;
            return Array.IndexOf(sc, rel) >= 0;
        }

        static int Nearest(float midi, int key, int scale)
        {
            int baseNote = (int)Math.Round(midi);
            int best = baseNote; float bestD = float.MaxValue;
            for (int k = baseNote - 6; k <= baseNote + 6; k++)
            {
                if (!InScale(k, key, scale)) continue;
                float d = Math.Abs(midi - k);
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }

        /// <summary>(periodo, deslocamento em cents) num instante: interpolado entre as duas estimativas vizinhas.</summary>
        void EstimateAt(long t, out float period, out float cents)
        {
            int n = Math.Min(estCount, EstSlots);
            int sb = -1, sa = -1;
            long tb = long.MinValue, ta = long.MaxValue;
            for (int i = 0; i < n; i++)
            {
                int s = (estCount - 1 - i) % EstSlots;
                long tt = estTime[s];
                if (tt <= t) { if (tt > tb) { tb = tt; sb = s; } }
                else if (tt < ta) { ta = tt; sa = s; }
            }
            if (sb < 0 && sa < 0) { period = 0; cents = 0; return; }
            if (sb < 0) { period = estPeriod[sa]; cents = estCents[sa]; return; }
            if (sa < 0) { period = estPeriod[sb]; cents = estCents[sb]; return; }
            float pb = estPeriod[sb], pa = estPeriod[sa];
            float f = (float)(t - tb) / (ta - tb);
            if (pb > 0 && pa > 0)
            {
                period = pb + (pa - pb) * f;
                cents = estCents[sb] + (estCents[sa] - estCents[sb]) * f;
            }
            else
            {
                int s = f < 0.5f ? sb : sa;
                period = estPeriod[s]; cents = estCents[s];
            }
        }

        void PlaceMarks()
        {
            long tail = extraOn ? Math.Min(main.Tail, extra.Tail) : main.Tail;
            while (markHead - tail < MarkSlots - 4)
            {
                EstimateAt(lastMark, out float T, out _);
                bool voiced = T > 0;
                float step = voiced ? T : UnvoicedHop;
                long pred = lastMark + (long)Math.Round(step);
                int half = (int)Math.Ceiling(step);
                if (pred + half + Margin > inPos) break;
                long mark = pred;
                if (voiced)
                {
                    // dentro da voz: alinha pela forma da onda; no comeco de um trecho com voz: no golpe mais forte
                    bool chain = lastVoiced && Math.Abs(lastT - T) < 0.3f * T;
                    mark = chain ? AlignToPrevious(lastMark, pred, T) : AnchorPeak(pred, T);
                    if (mark <= lastMark + (long)(T * 0.5f)) mark = pred;
                    if (mark + half + Margin > inPos) break;
                }
                int slot = (int)(markHead & MarkMask);
                markPos[slot] = mark; markT[slot] = step; markVoiced[slot] = voiced;
                markHead++;
                lastMark = mark; lastVoiced = voiced; lastT = step;
            }
        }

        /// <summary>Posicao perto de 'pred' onde o periodo mais se parece com o anterior (correlacao normalizada).</summary>
        long AlignToPrevious(long prevMark, long pred, float T)
        {
            int h = Math.Max(8, (int)(T * 0.5f));
            int r = Math.Max(3, (int)(T * 0.12f));
            double best = double.MinValue, bestC = 0;
            long bestK = pred;
            // busca grossa (passo 2) e depois fina (+-1)
            for (long k = pred - r; k <= pred + r; k += 2)
            {
                double c = Ncc(prevMark, k, h, 2);
                double score = c - 0.04 * Math.Abs(k - pred) / r;
                if (score > best) { best = score; bestK = k; }
            }
            long k0 = bestK;
            best = double.MinValue;
            for (long k = k0 - 1; k <= k0 + 1; k++)
            {
                double c = Ncc(prevMark, k, h, 1);
                double score = c - 0.04 * Math.Abs(k - pred) / r;
                if (score > best) { best = score; bestC = c; bestK = k; }
            }
            return bestC >= 0.35 ? bestK : pred;
        }

        double Ncc(long a, long b, int h, int stride)
        {
            double sab = 0, saa = 0, sbb = 0;
            for (int j = -h; j < h; j += stride)
            {
                float va = inRing[(a + j) & Mask], vb = inRing[(b + j) & Mask];
                sab += va * vb; saa += va * va; sbb += vb * vb;
            }
            double d = saa * sbb;
            return d > 1e-30 ? sab / Math.Sqrt(d) : 0;
        }

        /// <summary>Primeira marca de um trecho com voz: no ponto mais forte de um periodo (o "golpe" da glote).</summary>
        long AnchorPeak(long pred, float T)
        {
            int r = (int)(T * 0.5f);
            long m = pred;
            float mx = -1f;
            for (long k = pred - r; k <= pred + r; k++)
            {
                float v = Math.Abs(inRing[k & Mask]);
                if (v > mx) { mx = v; m = k; }
            }
            return m;
        }

        /// <summary>
        /// Coloca graos ate cobrir o trecho de saida. alpha = fator dos formantes; offsetCents = altura extra
        /// (voz extra); a voz extra so "canta" as partes com voz (as consoantes ficam so na principal).
        /// </summary>
        void Synthesize(Stream st, long end, float alpha, float offsetCents, bool isExtra, float vibDepth, float vibRate)
        {
            double vibW = 2 * Math.PI * vibRate / Fs;
            while (true)
            {
                long s = (long)Math.Round(st.Next);
                // descarta marcas que ja ficaram para tras
                while (markHead - st.Tail >= 2)
                {
                    int t0 = (int)(st.Tail & MarkMask), t1 = (int)((st.Tail + 1) & MarkMask);
                    if (Math.Abs(markPos[t1] - s) <= Math.Abs(markPos[t0] - s)) st.Tail++;
                    else break;
                }
                if (markHead == st.Tail) break;
                int m = (int)(st.Tail & MarkMask);
                long a = markPos[m];
                float T = markT[m];
                bool voiced = markVoiced[m];
                float ratio = 1f;
                if (voiced)
                {
                    EstimateAt(a, out _, out float cents);
                    cents += offsetCents;
                    if (vibDepth > 0f) cents += vibDepth * (float)Math.Sin(vibW * s);
                    ratio = (float)Math.Pow(2.0, cents / 1200.0);
                }
                // meia largura do grao na saida (graos um pouco menores quando o tom sobe muito)
                float shrink = ratio > 1f ? 1f / (1f + GrainShrink * (ratio - 1f)) : 1f;
                int hs = Math.Max(16, (int)Math.Round(T * shrink / alpha));
                if (s - hs >= end) break;                              // ja cobre o trecho de saida
                if (a + (long)Math.Ceiling(hs * (double)alpha) + Margin > inPos) break; // falta entrada (nao deve acontecer)
                if (markHead - st.Tail < 2 && a < s - hs) break;       // espera a proxima marca

                AddGrain(st, s, a, hs, alpha, isExtra && !voiced ? 0f : 1f);
                st.Next += Math.Max(8.0, T / ratio);
            }
        }

        /// <summary>Grao com janela Hann de 2*hs amostras centrado em s, lendo a entrada em a no passo alpha.</summary>
        void AddGrain(Stream st, long s, long a, int hs, float alpha, float amp)
        {
            float[] ola = st.Ola, olaW = st.OlaW;
            double cd = Math.Cos(Math.PI / hs), sd = Math.Sin(Math.PI / hs);
            double cw = -1.0, sw = 0.0;   // cos/sen de (pi*j/hs), partindo de j = -hs
            if (amp == 0f)
            {
                // so o peso: a voz extra fica em silencio aqui, sem degrau nas bordas
                for (int j = -hs; j < hs; j++)
                {
                    olaW[(int)((s + j) & Mask)] += (float)(0.5 + 0.5 * cw);
                    double nc = cw * cd - sw * sd; sw = sw * cd + cw * sd; cw = nc;
                }
                return;
            }
            if (alpha == 1f)
            {
                for (int j = -hs; j < hs; j++)
                {
                    float w = (float)(0.5 + 0.5 * cw);
                    int o = (int)((s + j) & Mask);
                    ola[o] += inRing[(a + j) & Mask] * w;
                    olaW[o] += w;
                    double nc = cw * cd - sw * sd; sw = sw * cd + cw * sd; cw = nc;
                }
                return;
            }
            // formantes: le o grao mais rapido (alpha > 1, garganta menor) ou mais devagar (alpha < 1), interpolacao cubica
            double pos = a - hs * (double)alpha;
            for (int j = -hs; j < hs; j++)
            {
                float w = (float)(0.5 + 0.5 * cw);
                long ip = (long)Math.Floor(pos);
                float fr = (float)(pos - ip);
                float xm = inRing[(ip - 1) & Mask], x0 = inRing[ip & Mask], x1 = inRing[(ip + 1) & Mask], x2 = inRing[(ip + 2) & Mask];
                float c1 = 0.5f * (x1 - xm);
                float c2 = xm - 2.5f * x0 + 2f * x1 - 0.5f * x2;
                float c3 = 0.5f * (x2 - xm) + 1.5f * (x0 - x1);
                float v = ((c3 * fr + c2) * fr + c1) * fr + x0;
                int o = (int)((s + j) & Mask);
                ola[o] += v * w;
                olaW[o] += w;
                pos += alpha;
                double nc = cw * cd - sw * sd; sw = sw * cd + cw * sd; cw = nc;
            }
        }
    }
}
