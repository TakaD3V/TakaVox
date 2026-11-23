// Teste de REALISMO do modificador de voz (o que faz a voz soar como "microfone ruim").
// Fala masculina sintetica "dificil", com o tom verdadeiro conhecido amostra a amostra:
//   tremor natural do tom e do volume (jitter/shimmer), sopro, voz rangida (vocal fry) no fim das frases,
//   consoantes (s, x, f, z, t, k, p, m, n, l, r), ressonancias de vogais e eco de quarto.
// Mede, nos trechos com voz:
//   vazamento - a voz ORIGINAL (grossa) aparecendo por baixo da nova;
//   limpeza   - periodicidade no tom novo (graos desalinhados = voz aspera, "rachada");
//   afinacao  - erro do tom de saida;
//   chiado    - agudos (5-12 kHz) a mais que a voz original; ruido nas pausas.
// Uso: voicerealism.exe [pasta-para-wavs]
// Compile com /define:OLD e o AutoTuneOld.cs (copia do motor antigo) para comparar os dois motores.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using TakaVox.Dsp;

static class VoiceRealism
{
    const int Fs = 48000, B = 480;

    // ======================================================================== fala sintetica
    sealed class Ph
    {
        public string N; public double Dur;
        public double F1, F2, F3, B1 = 80, B2 = 110, B3 = 160;
        public double Av, Af, Fn, Bn;
        public bool Stop, HasF;
    }
    static Ph V(string n, double f1, double f2, double f3, double dur) => new Ph { N = n, Dur = dur, F1 = f1, F2 = f2, F3 = f3, Av = 1, HasF = true };
    static Ph Fr(string n, double af, double fn, double bn, double av, double dur) => new Ph { N = n, Dur = dur, Af = af, Fn = fn, Bn = bn, Av = av };
    static Ph Na(string n, double f1, double f2, double f3, double av, double dur) => new Ph { N = n, Dur = dur, F1 = f1, F2 = f2, F3 = f3, B1 = 60, Av = av, HasF = true };
    static Ph St(string n, double fn, double bn, double dur) => new Ph { N = n, Dur = dur, Stop = true, Fn = fn, Bn = bn };

    static Ph Phone(char c, double vdur)
    {
        switch (c)
        {
            case 'a': return V("a", 730, 1090, 2440, vdur);
            case 'e': return V("e", 530, 1840, 2480, vdur);
            case 'i': return V("i", 300, 2250, 3000, vdur);
            case 'o': return V("o", 570, 840, 2410, vdur);
            case 'u': return V("u", 320, 870, 2240, vdur);
            case 's': return Fr("s", 1.0, 6200, 2600, 0, 0.11);
            case 'x': return Fr("x", 0.9, 3000, 1600, 0, 0.11);
            case 'f': return Fr("f", 0.35, 6000, 7000, 0, 0.09);
            case 'z': return Fr("z", 0.55, 6200, 2600, 0.45, 0.09);
            case 'm': return Na("m", 250, 1100, 2200, 0.45, 0.08);
            case 'n': return Na("n", 250, 1500, 2500, 0.45, 0.07);
            case 'l': return Na("l", 350, 1100, 2700, 0.7, 0.07);
            case 'r': return Na("r", 420, 1300, 1800, 0.75, 0.035);
            case 't': return St("t", 4500, 3000, 0.09);
            case 'k': return St("k", 2000, 1200, 0.10);
            case 'p': return St("p", 1200, 2500, 0.09);
        }
        throw new ArgumentException(c.ToString());
    }

    static readonly string[] Phrases = { "masitokazelunopari", "faxinapolesumaterika", "tekurasomizalefo", "bolakimuzinatora" };

    sealed class Res // ressonador de Klatt (ganho 1 no DC)
    {
        double y1, y2;
        public double Run(double x, double f, double bw)
        {
            double c = -Math.Exp(-2 * Math.PI * bw / Fs), b = 2 * Math.Exp(-Math.PI * bw / Fs) * Math.Cos(2 * Math.PI * f / Fs), a = 1 - b - c;
            double y = a * x + b * y1 + c * y2; y2 = y1; y1 = y;
            return y;
        }
    }

    sealed class Speech { public float[] X; public float[] F0; public bool[] Creak; }

    static Speech Speak(int seed)
    {
        var rnd = new Random(seed);
        double Gauss() { double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble(); return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2); }
        double Noise() => rnd.NextDouble() * 2 - 1;
        var x = new List<float>(); var f0 = new List<float>(); var cr = new List<bool>();
        var R = new Res[5]; for (int i = 0; i < 5; i++) R[i] = new Res();
        var fric = new Res();
        double sF1 = 500, sF2 = 1500, sF3 = 2500, sB1 = 80, sB2 = 110, sB3 = 160, sAv = 0, sAf = 0, sAh = 0, sFn = 5000, sBn = 3000;
        double tPer = 0, T0 = Fs / 120.0, amp = 1, oq = 0.6, tilt = 0;
        bool alt = false;
        void Pause(double sec) { int n = (int)(sec * Fs); for (int i = 0; i < n; i++) { x.Add(0); f0.Add(0); cr.Add(false); } }

        Pause(0.4);
        foreach (var text in Phrases)
        {
            var ph = new List<Ph>();
            for (int i = 0; i < text.Length; i++)
            {
                bool last = i == text.Length - 1;
                double vd = last ? 0.42 : 0.13 + 0.09 * rnd.NextDouble();
                char c = text[i] == 'b' ? 'p' : text[i]; // 'b' ~ 'p' com voz
                var p = Phone(c, vd);
                if (text[i] == 'b') { p.Av = 0.35; }
                ph.Add(p);
            }
            for (int i = ph.Count - 1; i >= 0; i--) // consoantes herdam os formantes da vogal seguinte
                if (!ph[i].HasF) { var nx = i + 1 < ph.Count ? ph[i + 1] : ph[i - 1]; ph[i].F1 = nx.F1; ph[i].F2 = nx.F2; ph[i].F3 = nx.F3; }
            int total = 0; foreach (var p in ph) total += (int)(p.Dur * Fs);
            int k0 = 0; double wander = rnd.NextDouble() * 6.28;
            foreach (var p in ph)
            {
                int n = (int)(p.Dur * Fs);
                for (int i = 0; i < n; i++, k0++)
                {
                    double u = k0 / (double)total, t = x.Count / (double)Fs;
                    // entonacao: acento no comeco, declinacao, queda final; voz rangida nos ultimos 180 ms
                    double hz = 118 + 30 * Math.Exp(-Math.Pow((u - 0.22) / 0.09, 2)) + 14 * Math.Exp(-Math.Pow((u - 0.62) / 0.08, 2))
                                - 24 * u - (u > 0.85 ? 60 * (u - 0.85) : 0) + 3 * Math.Sin(2 * Math.PI * 1.3 * t + wander);
                    bool creak = total - k0 < 0.18 * Fs;
                    // alvos do fonema
                    double tAv = p.Av, tAf = p.Af, tAh = 0, tFn = p.Fn, tBn = p.Bn;
                    if (p.Stop)
                    {
                        double q = i / (double)n;
                        tAv = 0; tAf = 0;
                        if (q > 0.62 && q < 0.62 + 0.008 * Fs / n) { tAf = 1.0; }        // explosao
                        else if (q >= 0.62) { tAh = 0.35; }                              // aspiracao
                    }
                    if (creak) { tAv *= 0.7; }
                    double kf = 1 - Math.Exp(-1.0 / (0.012 * Fs)), ka = 1 - Math.Exp(-1.0 / (0.006 * Fs)), kn = 1 - Math.Exp(-1.0 / (0.003 * Fs));
                    sF1 += kf * (p.F1 - sF1); sF2 += kf * (p.F2 - sF2); sF3 += kf * (p.F3 - sF3);
                    sB1 += kf * (p.B1 - sB1); sB2 += kf * (p.B2 - sB2); sB3 += kf * (p.B3 - sB3);
                    sAv += ka * (tAv - sAv); sAf += kn * (tAf - sAf); sAh += kn * (tAh - sAh);
                    sFn += kn * (tFn > 0 ? tFn - sFn : 0); sBn += kn * (tBn > 0 ? tBn - sBn : 0);

                    // fonte glotal (KLGLOTT88) com jitter, shimmer e voz rangida (periodos alternados)
                    tPer += 1;
                    if (tPer >= T0)
                    {
                        tPer -= T0;
                        if (creak)
                        {
                            alt = !alt;
                            T0 = Fs / 58.0 * (alt ? 1.12 : 0.88) * (1 + 0.03 * Gauss());
                            amp = (alt ? 1.0 : 0.6) * (1 + 0.08 * Gauss()); oq = 0.35;
                        }
                        else
                        {
                            T0 = Fs / hz * (1 + 0.009 * Gauss());
                            amp = 1 + 0.05 * Gauss(); oq = 0.6;
                        }
                    }
                    double tau = tPer / (oq * T0);
                    double src = tau < 1 ? (2 * tau - 3 * tau * tau) * amp : 0;
                    tilt = 0.7 * src + 0.3 * tilt;
                    double asp = Noise() * (tau < 1 ? 1.0 : 0.3) * 0.06;
                    double e = sAv * (tilt + asp) + sAh * Noise() * 0.5;
                    double v = R[0].Run(e, sF1, sB1);
                    v = R[1].Run(v, sF2, sB2);
                    v = R[2].Run(v, sF3, sB3);
                    v = R[3].Run(v, 3400, 250);
                    v = R[4].Run(v, 4500, 300);
                    double fz = fric.Run(Noise(), sFn, sBn) * sAf * 0.6;
                    x.Add((float)(v + fz));
                    bool voiced = sAv > 0.3;
                    f0.Add(voiced ? (float)(Fs / T0) : 0f);
                    cr.Add(voiced && creak);
                }
            }
            Pause(0.5);
        }

        // eco de quarto (reflexoes + cauda curta) e ruido do microfone
        var dry = x.ToArray();
        var y = new float[dry.Length];
        int[] combD = { 1694, 1760, 1622, 1547 }; int[] apD = { 605, 480 };
        var combs = new float[4][]; var cpos = new int[4]; for (int i = 0; i < 4; i++) combs[i] = new float[combD[i]];
        var aps = new float[2][]; var apos = new int[2]; for (int i = 0; i < 2; i++) aps[i] = new float[apD[i]];
        for (int i = 0; i < dry.Length; i++)
        {
            double s = dry[i];
            double er = (i >= 312 ? 0.30 * dry[i - 312] : 0) + (i >= 528 ? 0.20 * dry[i - 528] : 0) + (i >= 816 ? 0.12 * dry[i - 816] : 0);
            double wet = 0;
            for (int c = 0; c < 4; c++)
            {
                float d = combs[c][cpos[c]];
                combs[c][cpos[c]] = (float)(s + 0.5 * d);
                cpos[c] = (cpos[c] + 1) % combD[c];
                wet += d;
            }
            for (int a = 0; a < 2; a++)
            {
                float d = aps[a][apos[a]];
                double o = -0.5 * wet + d;
                aps[a][apos[a]] = (float)(wet + 0.5 * o);
                apos[a] = (apos[a] + 1) % apD[a];
                wet = o;
            }
            y[i] = (float)(s + er + 0.18 * wet);
        }
        float peak = 0; foreach (var v in y) peak = Math.Max(peak, Math.Abs(v));
        var rn = new Random(seed + 1);
        for (int i = 0; i < y.Length; i++) y[i] = y[i] * 0.5f / peak + (float)((rn.NextDouble() * 2 - 1) * 4.4e-4); // ruido ~ -72 dBFS
        return new Speech { X = y, F0 = f0.ToArray(), Creak = cr.ToArray() };
    }

    // ======================================================================== processamento
    static float[] Run(float[] x, ChainParams p, bool old)
    {
        var y = (float[])x.Clone();
        var buf = new float[B];
        var vfx = new VoiceFx();
        Action<float[], int, ChainParams> tune;
#if OLD
        if (old) tune = new AutoTuneOld().Process; else
#endif
        tune = new AutoTune().Process;
        for (int i = 0; i + B <= y.Length; i += B)
        {
            Array.Copy(y, i, buf, 0, B);
            tune(buf, B, p);
            vfx.Pre(buf, B, p);
            vfx.Post(buf, B, p);
            Array.Copy(buf, 0, y, i, B);
        }
        return y;
    }

    // ======================================================================== medidas
    sealed class Score
    {
        public int Voiced, Leak, CreakN, CreakLeak;
        public double Clean, CleanCore, CreakClean, PitchErr, HfDelta, PauseDb, Cpu;
        public string Name;
    }

    static double Nacf(float[] y, int s, int n, int lag)
    {
        double ab = 0, aa = 0, bb = 0;
        for (int i = s; i < s + n - lag; i++) { double a = y[i], b = y[i + lag]; ab += a * b; aa += a * a; bb += b * b; }
        return aa * bb > 1e-20 ? ab / Math.Sqrt(aa * bb) : 0;
    }

    static double BestNacf(float[] y, int s, int n, double lo, double hi, out int bestLag)
    {
        double best = -1; bestLag = (int)lo;
        for (int L = (int)Math.Floor(lo); L <= (int)Math.Ceiling(hi); L++)
        {
            if (L < 2 || L >= n / 2) continue;
            double c = Nacf(y, s, n, L);
            if (c > best) { best = c; bestLag = L; }
        }
        return best;
    }

    static readonly Fft fft = new Fft(2048);
    static double HfRatioDb(float[] y, int s)
    {
        var fr = new float[2048]; var pw = new double[1025];
        Array.Copy(y, s, fr, 0, 2048);
        fft.Power(fr, pw);
        double lo = 0, hi = 0;
        for (int k = 0; k <= 1024; k++)
        {
            double f = k * (double)Fs / 2048;
            if (f >= 200 && f < 5000) lo += pw[k]; else if (f >= 5000 && f < 12000) hi += pw[k];
        }
        return 10 * Math.Log10((hi + 1e-20) / (lo + 1e-20));
    }

    static Score Measure(Speech sp, float[] y, float semis, float intonation, string name)
    {
        var sc = new Score { Name = name };
        int D = AutoTune.Delay, N = 1920;
        var x = sp.X; var f0 = sp.F0;
        // media do tom (como o motor faz) para a entonacao
        double mean = 0; int nb = 0;
        var meanAt = new double[x.Length / B + 1];
        for (int b = 0; b * B < x.Length; b++)
        {
            int c = b * B; double hz = 0; int cnt = 0;
            for (int i = c; i < Math.Min(x.Length, c + B); i++) if (f0[i] > 0) { hz += f0[i]; cnt++; }
            if (cnt > B / 2) { double m = 69 + 12 * Math.Log(hz / cnt / 440, 2); nb++; double a = Math.Max(1.0 / nb, 1.0 / 150); mean = nb == 1 ? m : mean + a * (m - mean); }
            meanAt[b] = mean;
        }
        double sumClean = 0, sumCore = 0, sumCreak = 0, sumErr = 0, sumHf = 0; int nCore = 0, nHf = 0;
        double pauseE = 0; int pauseN = 0;
        for (int c = D + N; c + N < y.Length; c += B)
        {
            int s = c - N / 2, si = s - D;
            if (si < 0 || si + N >= x.Length) continue;
            int vcount = 0, ccount = 0; double hzSum = 0;
            for (int i = si; i < si + N; i++) if (f0[i] > 0) { vcount++; hzSum += f0[i]; if (sp.Creak[i]) ccount++; }
            if (vcount == 0)
            {
                double ex = 0; for (int i = si; i < si + N; i++) ex += x[i] * (double)x[i];
                if (ex / N < 1e-6) { double ey = 0; for (int i = s; i < s + N; i++) ey += y[i] * (double)y[i]; pauseE += ey / N; pauseN++; }
                continue;
            }
            if (vcount < N * 0.9) continue;
            double hzIn = hzSum / vcount;
            bool creak = ccount > N / 2;
            double midiIn = 69 + 12 * Math.Log(hzIn / 440, 2);
            double m = meanAt[Math.Min(meanAt.Length - 1, (si + N / 2) / B)];
            double cents = semis * 100 + (intonation / 100.0 - 1) * (midiIn - m) * 100;
            double Tin = Fs / hzIn, Tout = Tin / Math.Pow(2, cents / 1200);
            if (creak) Tin = Fs / 58.0; // periodos alternados: o "tom" percebido e o par
            double pShift = BestNacf(y, s, N, Tout * 0.97, Tout * 1.03, out _);
            double pLeak = BestNacf(y, s, N, Tin * 0.97, Tin * 1.03, out _);
            bool leak = pLeak > 0.5 && pLeak > pShift + 0.15;
            sc.Voiced++;
            if (leak) sc.Leak++;
            sumClean += pShift;
            // nucleo: voz tambem 30 ms antes e depois (sem bordas)
            bool core = !creak;
            for (int i = Math.Max(0, si - 1440); i < Math.Min(x.Length, si + N + 1440) && core; i += 48) if (f0[i] <= 0) core = false;
            if (core) { sumCore += pShift; nCore++; }
            if (creak) { sc.CreakN++; sumCreak += pShift; if (leak) sc.CreakLeak++; }
            if (!creak)
            {
                BestNacf(y, s, N, Tout * 0.75, Tout * 1.33, out int L);
                sumErr += Math.Abs(1200 * Math.Log(Tout / L, 2));
                if (s + 2048 < y.Length && si + 2048 < x.Length) { sumHf += HfRatioDb(y, s) - HfRatioDb(x, si); nHf++; }
            }
        }
        int nv = Math.Max(1, sc.Voiced), nn = Math.Max(1, sc.Voiced - sc.CreakN);
        sc.Clean = sumClean / nv; sc.CleanCore = sumCore / Math.Max(1, nCore); sc.CreakClean = sumCreak / Math.Max(1, sc.CreakN);
        sc.PitchErr = sumErr / nn; sc.HfDelta = sumHf / Math.Max(1, nHf);
        sc.PauseDb = pauseN > 0 ? 10 * Math.Log10(pauseE / pauseN + 1e-20) : -200;
        return sc;
    }

    static void Print(Score s)
    {
        Console.WriteLine($"  {s.Name,-34} limpeza {s.Clean:0.000} (núcleo {s.CleanCore:0.000}, rangida {s.CreakClean:0.000})  " +
                          $"vazamento {100.0 * s.Leak / Math.Max(1, s.Voiced),5:0.0}% (rangida {100.0 * s.CreakLeak / Math.Max(1, s.CreakN),5:0.0}%)  " +
                          $"afinação {s.PitchErr,5:0.0} c  agudos {s.HfDelta,+5:+0.0;-0.0} dB  pausas {s.PauseDb,6:0} dB  CPU {s.Cpu:0.00}%");
    }

    static void SaveWav(string path, float[] x)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + x.Length * 2);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(v * 32767))));
        }
    }

    static ChainParams Voice(float pitch, float formant, float inton, float breath = 0, float low = 0, float high = 0)
    {
        var p = new ChainParams();
        p.VcOn = true; p.VcPitch = pitch; p.VcFormant = formant; p.VcIntonation = inton; p.VcBreath = breath; p.VcLowDb = low; p.VcHighDb = high;
        p.Sanitize(); p.Touch();
        return p;
    }

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : null;
        if (dir != null) Directory.CreateDirectory(dir);
        var sp = Speak(11);
        Console.WriteLine($"fala sintética: {sp.X.Length / (double)Fs:0.0} s");
        if (dir != null) SaveWav(Path.Combine(dir, "00_entrada_voz_masculina.wav"), sp.X);

        var runs = new List<(string name, ChainParams p, bool old, float shrink, string wav)>();
#if OLD
        runs.Add(("motor ANTIGO, +11 st, timbre +24%", Voice(11, 24, 100), true, 0f, "01_antigo_puro"));
        runs.Add(("motor ANTIGO, preset anime antigo", Voice(11, 24, 135, 10, -4, 3), true, 0f, "02_antigo_anime"));
#endif
        float g0 = AutoTune.GrainShrink;
        runs.Add(("motor NOVO, +11 st, timbre +24%", Voice(11, 24, 100), false, g0, "03_novo_puro"));
        runs.Add(("motor NOVO, graos fixos (shrink 0)", Voice(11, 24, 100), false, 0f, null));
        foreach (var name in new[] { "Garota de anime", "Anime kawaii", "Garota", "Criança", "Voz grossa" })
        {
            var p = new ChainParams(); p.VcOn = true; VoicePresets.Find(name).ApplyTo(p); p.Touch();
            runs.Add(("preset " + name, p, false, g0, "04_" + name.Replace(' ', '_').ToLowerInvariant()));
        }

        int fails = 0;
        foreach (var r in runs)
        {
            AutoTune.GrainShrink = r.shrink;
            var sw = Stopwatch.StartNew();
            var y = Run(sp.X, r.p, r.old);
            double cpu = sw.Elapsed.TotalSeconds / (sp.X.Length / (double)Fs) * 100;
            var s = Measure(sp, y, r.p.VcPitch, r.p.VcIntonation, r.name);
            s.Cpu = cpu;
            Print(s);
            if (dir != null && r.wav != null) SaveWav(Path.Combine(dir, r.wav + ".wav"), y);
            // vozes "de gente" (garota, anime, crianca): limpas, sem vazar a voz original e sem chiado
            if (!r.old && r.name.StartsWith("preset") && r.p.VcPitch > 0)
            {
                double leak = 100.0 * s.Leak / Math.Max(1, s.Voiced);
                bool ok = s.CleanCore >= 0.84 && leak <= 4 && s.HfDelta <= 4 && s.Cpu < 3;
                if (!ok) fails++;
                Console.WriteLine((ok ? "    ok    " : "    FALHA ") + "limpeza do núcleo >= 0,84, vazamento <= 4%, agudos <= +4 dB, CPU < 3%");
            }
        }
        AutoTune.GrainShrink = g0;
        Console.WriteLine(fails == 0 ? "TUDO OK" : fails + " FALHA(S)");
        return fails == 0 ? 0 : 1;
    }
}
