using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TakaVox.Dsp
{
    /// <summary>
    /// Removedor de ruido tonal (ventoinha, ventilador, motor, ar-condicionado, zumbido eletrico).
    /// Nas pausas da fala ele procura os "apitos" fixos do ruido de fundo (picos estreitos que estao la o tempo
    /// todo) e corta cada um com um filtro notch bem estreito ANTES da IA. Assim a IA nao precisa cavar a voz no
    /// meio de um ruido forte bem na regiao do corpo da voz (120-500 Hz) - que e o que deixa a voz fina/"de copo".
    /// Sem atraso (filtros IIR). Acompanha a ventoinha se ela mudar de velocidade. Sem ruido tonal, nao faz nada.
    /// </summary>
    public sealed class ToneRemover : IDisposable
    {
        public const int MaxNotches = 8;
        const int Fs = 48000, Blk = 480;
        const int FftSize = 16384;                 // 2,9 Hz por ponto (interpolado: ~0,2 Hz)
        const int Windows = 7;                     // ~1,2 s do ruido mais recente (janelas com 50% de sobreposicao)
        const int RingLen = FftSize * 6;           // ~2 s de "so ruido"
        // abaixo de 70 Hz o filtro de graves ja cuida; acima de 1 kHz ficam a clareza da voz e picos que andam
        // (fala/musica ao fundo): ali a IA ja resolve sozinha e um corte so atrapalharia a voz
        const double MinFreq = 70, MaxFreq = 1000;
        const double MinProminence = 10;           // pico pelo menos 10 dB acima do ruido em volta
        const double MinPresence = 0.8;            // presente em 80% das janelas (ventoinha e continua; musica/fala nao)
        const int MinWindows = 5;                  // pelo menos ~1 s de ruido analisado antes de cortar qualquer coisa

        /// <summary>Um corte ativo (publicado pela analise para a thread de audio).</summary>
        public struct Notch
        {
            public double Freq, DepthDb, Q, ProminenceDb;
        }

        // ---------------------------------------------------------------- thread de audio -> analise
        readonly float[] ring = new float[RingLen];
        long ringWrite;          // so a thread de audio escreve
        long ringPublished;      // lido pela analise
        readonly bool inline;    // offline/testes: analisa na hora (deterministico)
        long lastInlineAnalysis;
        readonly Thread worker;
        volatile bool stop;
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        // ---------------------------------------------------------------- analise -> thread de audio
        static readonly Notch[] Empty = new Notch[0];
        volatile Notch[] published = new Notch[0];
        public Notch[] Active => published;

        // ---------------------------------------------------------------- filtros (thread de audio)
        sealed class Slot
        {
            public readonly Biquad Bq = new Biquad();
            public bool Used;
            public double Freq, TargetFreq, Q;
            public double Mix, TargetMix;   // 0 = sem corte ... 1 = corte total (profundidade = 20*log10(1 - Mix))
        }
        readonly Slot[] slots = Enumerable.Range(0, MaxNotches).Select(_ => new Slot()).ToArray();
        readonly float[] tmp = new float[Blk];
        Notch[] lastSeen;
        bool enabled = true;

        public ToneRemover(bool inlineAnalysis)
        {
            inline = inlineAnalysis;
            if (!inline)
            {
                worker = new Thread(Worker) { IsBackground = true, Name = "TakaVox anti-ventoinha", Priority = ThreadPriority.BelowNormal };
                worker.Start();
            }
        }

        static double MixFor(double depthDb) => 1 - Math.Pow(10, depthDb / 20);

        /// <summary>Thread de audio: aplica os cortes no bloco (antes da IA).</summary>
        public void Apply(float[] x, int n, bool on)
        {
            var pub = on ? published : Empty; // desligado = os cortes somem com rampa
            if (on != enabled || !ReferenceEquals(pub, lastSeen))
            {
                enabled = on;
                lastSeen = pub;
                Retarget(pub);
            }
            foreach (var s in slots)
            {
                if (!s.Used) continue;
                if (Math.Abs(s.Freq - s.TargetFreq) > 0.005)
                {
                    double d = s.TargetFreq - s.Freq;
                    s.Freq += Math.Max(-0.4, Math.Min(0.4, d)); // segue a ventoinha devagar
                    s.Bq.Set(FilterType.Notch, Fs, s.Freq, s.Q, 0);
                }
                // notch + mistura: largura exata do notch, profundidade controlada pela mistura (rampa sem estalo)
                double m0 = s.Mix;
                double m1 = s.Mix < s.TargetMix ? Math.Min(s.TargetMix, s.Mix + 0.1) : Math.Max(s.TargetMix, s.Mix - 0.1);
                s.Mix = m1;
                Array.Copy(x, tmp, n);
                s.Bq.Process(tmp, n);
                double dm = (m1 - m0) / n;
                for (int i = 0; i < n; i++) x[i] += (float)((m0 + dm * i) * (tmp[i] - x[i]));
                if (s.TargetMix == 0 && s.Mix == 0)
                {
                    s.Used = false;
                    s.Bq.Reset();
                }
            }
        }

        void Retarget(Notch[] pub)
        {
            var matched = new bool[slots.Length];
            foreach (var nt in pub)
            {
                Slot hit = null;
                bool fresh = false;
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i].Used && !matched[i] && Math.Abs(slots[i].TargetFreq - nt.Freq) < Math.Max(3.0, 0.02 * nt.Freq)) { hit = slots[i]; matched[i] = true; break; }
                if (hit == null)
                    for (int i = 0; i < slots.Length; i++)
                        if (!slots[i].Used)
                        {
                            hit = slots[i];
                            matched[i] = true;
                            fresh = true;
                            hit.Used = true;
                            hit.Freq = nt.Freq;
                            hit.Mix = 0;
                            hit.Bq.Reset();
                            break;
                        }
                if (hit == null) continue;
                bool qChanged = Math.Abs(hit.Q - nt.Q) > 0.01;
                hit.TargetFreq = nt.Freq;
                hit.TargetMix = MixFor(nt.DepthDb);
                hit.Q = nt.Q;
                if (fresh || qChanged) hit.Bq.Set(FilterType.Notch, Fs, hit.Freq, hit.Q, 0);
            }
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Used && !matched[i]) slots[i].TargetMix = 0; // some devagar
        }

        /// <summary>
        /// Thread de audio: entrega o bloco ORIGINAL (antes dos cortes) quando ele e so ruido (sem fala),
        /// para a analise achar os apitos. Os blocos com fala nunca entram: a voz nao vira "ruido".
        /// </summary>
        public void Feed(float[] preNotch, int n, bool noiseOnly)
        {
            if (!enabled || !noiseOnly) return;
            long w = ringWrite;
            for (int i = 0; i < n; i++) ring[(int)((w + i) % RingLen)] = preNotch[i];
            ringWrite = w + n;
            Volatile.Write(ref ringPublished, ringWrite);
            if (inline && ringWrite - lastInlineAnalysis >= 9600) // a cada 0,2 s de ruido
            {
                lastInlineAnalysis = ringWrite;
                AnalyzeOnce();
            }
        }

        void Worker()
        {
            while (!stop)
            {
                wake.WaitOne(400);
                if (stop) break;
                try { AnalyzeOnce(); }
                catch (Exception ex) { Log.Write("Anti-ventoinha: " + ex.Message); }
            }
        }

        // ---------------------------------------------------------------- analise (thread propria)
        sealed class Cand
        {
            public double F, P, Width;
            public int Hits, Misses;
            public bool Active;
        }
        readonly List<Cand> cands = new List<Cand>();
        const int Hop = FftSize / 2;
        Fft fft;
        float[] seg;
        double[][] winPow;       // potencia de cada janela: cada uma e calculada UMA vez (grade fixa de Hop amostras)
        int winCount, winNext;
        long nextWinStart;

        /// <summary>Diagnostico: tempo total gasto na analise (todas as instancias) e quantas analises.</summary>
        public static double StatAnalysisMs;
        public static int StatAnalyses;

        void AnalyzeOnce()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { AnalyzeCore(); }
            finally
            {
                StatAnalysisMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                StatAnalyses++;
            }
        }

        void AnalyzeCore()
        {
            long w = Volatile.Read(ref ringPublished);
            if (fft == null)
            {
                fft = new Fft(FftSize);
                seg = new float[FftSize];
                winPow = new double[Windows][];
                for (int i = 0; i < Windows; i++) winPow[i] = new double[FftSize / 2 + 1];
            }
            // dados que o anel ja sobrescreveu nao servem mais: pula para o que ainda esta la
            long oldest = w - (RingLen - Hop);
            if (nextWinStart < oldest) nextWinStart = (oldest + Hop - 1) / Hop * Hop;
            // so as janelas NOVAS (cada uma e calculada uma unica vez)
            bool any = false;
            while (nextWinStart + FftSize <= w)
            {
                long start = nextWinStart;
                for (int i = 0; i < FftSize; i++) seg[i] = ring[(int)((start + i) % RingLen)];
                fft.Power(seg, winPow[winNext]);
                winNext = (winNext + 1) % Windows;
                if (winCount < Windows) winCount++;
                nextWinStart += Hop;
                any = true;
            }
            if (!any || winCount < MinWindows) return; // nada novo (voce esta falando) ou ainda juntando ruido
            var ordered = new double[winCount][];      // mais recente primeiro
            for (int k = 0; k < winCount; k++) ordered[k] = winPow[(winNext - 1 - k + Windows) % Windows];
            Track(FindTones(ordered, winCount));
            Publish();
        }

        /// <summary>
        /// Picos estreitos no espectro medio das janelas (potencia linear, mais recente primeiro), que tambem
        /// aparecem em 80% das janelas sozinhas (som continuo: ventoinha/zumbido; musica ou resto de fala nao passa).
        /// </summary>
        static List<(double f, double prom, double width)> FindTones(double[][] win, int count)
        {
            int nbins = win[0].Length;
            var S = new double[nbins];
            for (int b = 0; b < nbins; b++)
            {
                double e = 0;
                for (int w = 0; w < count; w++) e += win[w][b];
                S[b] = 10 * Math.Log10(e / count + 1e-30);
            }
            var peaks = DetectPeaks(S);
            var res = new List<(double, double, double)>();
            double df = (double)Fs / FftSize;
            var nb = new double[49];
            int recent = Math.Min(3, count);
            foreach (var p in peaks)
            {
                int k = (int)Math.Round(p.f / df), present = 0;
                for (int w = 0; w < count; w++)
                {
                    var s = win[w];
                    double top = Math.Max(s[k - 1], Math.Max(s[k], s[k + 1]));
                    int m = 0;
                    for (int j = k - 24; j <= k + 24; j++) if (Math.Abs(j - k) > 3) nb[m++] = s[j];
                    Array.Sort(nb, 0, m);
                    if (top >= 3.98 * nb[m / 2]) present++; // >= +6 dB sobre o chao, nesta janela sozinha
                }
                if (present < MinPresence * count) continue;
                // frequencia ATUAL: so as 3 janelas mais recentes (a media longa atrasaria se a ventoinha mudar de velocidade)
                double R(int j) { double e = 0; for (int w = 0; w < recent; w++) e += win[w][j]; return 10 * Math.Log10(e / recent + 1e-30); }
                int kr = k;
                double rk = R(k);
                for (int j = k - 2; j <= k + 2; j++) { double rj = R(j); if (rj > rk) { rk = rj; kr = j; } }
                double a = R(kr - 1), bb = rk, c = R(kr + 1), den = a - 2 * bb + c;
                double delta = Math.Abs(den) < 1e-9 ? 0 : Math.Max(-0.5, Math.Min(0.5, 0.5 * (a - c) / den));
                res.Add(((kr + delta) * df, p.prom, p.width));
            }
            return res;
        }

        /// <summary>Picos estreitos no espectro (dB): frequencia, quanto sobressai e largura.</summary>
        static List<(double f, double prom, double width)> DetectPeaks(double[] S)
        {
            double df = (double)Fs / FftSize;
            const int Nb = 24; // vizinhanca de +-70 Hz para medir o "chao" do ruido
            var res = new List<(double, double, double)>();
            var nb = new double[2 * Nb + 1];
            int k0 = Math.Max(Nb + 1, (int)(MinFreq / df)), k1 = Math.Min(S.Length - Nb - 2, (int)(MaxFreq / df));
            for (int k = k0; k <= k1; k++)
            {
                if (S[k] < S[k - 1] || S[k] <= S[k + 1]) continue;
                int m = 0;
                for (int j = k - Nb; j <= k + Nb; j++) if (Math.Abs(j - k) > 3) nb[m++] = S[j];
                Array.Sort(nb, 0, m);
                double prom = S[k] - nb[m / 2];
                if (prom < MinProminence) continue;
                double a = S[k - 1], b = S[k], c = S[k + 1], den = a - 2 * b + c;
                double delta = Math.Abs(den) < 1e-9 ? 0 : Math.Max(-0.5, Math.Min(0.5, 0.5 * (a - c) / den));
                int l = 0, r = 0;
                while (l < 12 && S[k - l - 1] > b - 6) l++;
                while (r < 12 && S[k + r + 1] > b - 6) r++;
                res.Add(((k + delta) * df, prom, (l + r + 1) * df));
            }
            res.Sort((x, y) => y.Item2.CompareTo(x.Item2));
            if (res.Count > 12) res.RemoveRange(12, res.Count - 12);
            return res;
        }

        void Track(List<(double f, double prom, double width)> peaks)
        {
            int n0 = cands.Count; // os novos desta rodada entram no fim e nao sao comparados de novo
            var used = new bool[n0];
            foreach (var p in peaks)
            {
                int best = -1;
                for (int i = 0; i < n0; i++)
                    if (!used[i] && Math.Abs(cands[i].F - p.f) < Math.Max(3.0, 0.01 * p.f) && (best < 0 || Math.Abs(cands[i].F - p.f) < Math.Abs(cands[best].F - p.f))) best = i;
                if (best >= 0)
                {
                    var c = cands[best];
                    used[best] = true;
                    c.F = 0.3 * c.F + 0.7 * p.f; // segue rapido a velocidade atual da ventoinha
                    c.P = 0.6 * c.P + 0.4 * p.prom;
                    c.Width = 0.6 * c.Width + 0.4 * p.width;
                    c.Hits++;
                    c.Misses = 0;
                    if (c.Hits >= 3) c.Active = true; // estavel em varias analises seguidas: e ruido de verdade
                }
                else cands.Add(new Cand { F = p.f, P = p.prom, Width = p.width, Hits = 1 });
            }
            for (int i = 0; i < n0; i++)
                if (!used[i]) cands[i].Misses++;
            cands.RemoveAll(c => c.Misses >= (c.Active ? 8 : 3));
            // junta candidatos que acabaram no mesmo lugar (o mesmo apito medido duas vezes)
            cands.Sort((a, b) => b.P.CompareTo(a.P));
            for (int i = 0; i < cands.Count; i++)
                for (int j = cands.Count - 1; j > i; j--)
                    if (Math.Abs(cands[i].F - cands[j].F) < Math.Max(2.0, 0.015 * cands[i].F))
                    {
                        cands[i].Hits = Math.Max(cands[i].Hits, cands[j].Hits);
                        cands[i].Active |= cands[j].Active;
                        cands.RemoveAt(j);
                    }
            if (cands.Count > 32) cands.RemoveRange(32, cands.Count - 32);
        }

        void Publish()
        {
            var act = cands.Where(c => c.Active).OrderByDescending(c => c.P).Take(MaxNotches).Select(c =>
            {
                // largura do notch (-3 dB): o pico medido (que ja inclui a janela da FFT) + folga para a ventoinha oscilar;
                // harmonicos altos oscilam mais (k vezes a fundamental): no minimo 2,5% da frequencia (ate 1 kHz: <= 25 Hz)
                double bw = Math.Min(c.F / 8, Math.Max(Math.Max(4.5, 0.025 * c.F), 0.8 * c.Width + 2.0));
                return new Notch { Freq = c.F, Q = c.F / bw, DepthDb = -Math.Max(12, Math.Min(30, c.P + 6)), ProminenceDb = c.P };
            }).OrderBy(nn => nn.Freq).ToArray();
            var old = published;
            bool same = old.Length == act.Length;
            for (int i = 0; same && i < act.Length; i++)
                same = Math.Abs(old[i].Freq - act[i].Freq) < 0.05 && Math.Abs(old[i].DepthDb - act[i].DepthDb) < 0.5 && Math.Abs(old[i].Q - act[i].Q) < 0.5;
            if (same) return;
            published = act;
            // historico no log (ao vivo): quando comeca/para de cortar ou a ventoinha muda bem de velocidade
            if (inline) return;
            bool changed = act.Length != logged.Length;
            for (int i = 0; !changed && i < act.Length; i++) changed = Math.Abs(act[i].Freq - logged[i].Freq) > 0.03 * act[i].Freq;
            if (!changed || (DateTime.Now - loggedAt).TotalSeconds < 10) return;
            logged = act;
            loggedAt = DateTime.Now;
            Log.Write(act.Length == 0 ? "Anti-ventoinha: nenhum ruído tonal agora."
                : "Anti-ventoinha: removendo " + string.Join(", ", act.Select(nn => $"{nn.Freq:0.0} Hz (+{nn.ProminenceDb:0} dB)")));
        }
        Notch[] logged = new Notch[0];
        DateTime loggedAt = DateTime.MinValue;

        /// <summary>Testes: copia os cortes de outro removedor ja estabilizado (sem rampa).</summary>
        public void CopyNotchesFrom(ToneRemover other)
        {
            published = other.published;
            lastSeen = published;
            Retarget(published);
            foreach (var s in slots)
                if (s.Used) { s.Mix = s.TargetMix; s.Freq = s.TargetFreq; s.Bq.Set(FilterType.Notch, Fs, s.Freq, s.Q, 0); }
        }

        /// <summary>
        /// Analisa um trecho so de ruido de uma vez (calibracao): retorna os apitos continuos e a fundamental
        /// provavel. eletrico = serie em multiplos exatos de 60 Hz (rede eletrica).
        /// </summary>
        public static List<(double f, double prom)> DetectTones(float[] audio, int start, int len, out double fundamental, out bool electric)
        {
            fundamental = 0;
            electric = false;
            var list = new List<(double, double)>();
            if (len < FftSize * 2) return list;
            var f = new Fft(FftSize);
            var sg = new float[FftSize];
            var wins = new List<double[]>();
            for (int s = start; s + FftSize <= start + len && wins.Count < 24; s += FftSize / 2)
            {
                Array.Copy(audio, s, sg, 0, FftSize);
                var d = new double[FftSize / 2 + 1];
                f.Power(sg, d);
                wins.Add(d);
            }
            if (wins.Count < 2) return list;
            foreach (var p in FindTones(wins.ToArray(), wins.Count).Where(p => p.prom >= 12).Take(8)) list.Add((p.f, p.prom));
            list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            if (list.Count == 0) return list;
            // fundamental: o menor pico que explica pelo menos metade dos outros como multiplos
            foreach (var cand in list.Select(p => p.Item1))
            {
                int ok = list.Count(p => { double h = p.Item1 / cand; return Math.Round(h) >= 1 && Math.Abs(h - Math.Round(h)) * cand < 2.5; });
                if (ok * 2 >= list.Count) { fundamental = cand; break; }
            }
            if (fundamental == 0) fundamental = list.OrderByDescending(p => p.Item2).First().Item1;
            electric = Math.Abs(fundamental / 60 - Math.Round(fundamental / 60)) * 60 < 0.6;
            return list;
        }

        public void Dispose()
        {
            stop = true;
            wake.Set();
        }
    }
}
