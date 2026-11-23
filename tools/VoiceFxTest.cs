// Teste do modificador de voz (motor de tom PSOLA + VoiceFx + presets).
//  1) Vogais sinteticas com tom e formantes CONHECIDOS (voz masculina de ~110 Hz): mede o tom de saida, o fator real
//     dos formantes (envelope LPC), a periodicidade (graos bem alinhados = som limpo) e o volume.
//  2) Fala real (voz do Windows): sem NaN/estouro, sem buracos/estalos, volume estavel, troca rapida de presets.
//  3) Efeitos (telefone, eco, reverb, sussurro, robo sintetizado) e CPU por bloco de cada preset.
// Uso: voicefxtest.exe [pasta para salvar WAVs de exemplo]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TakaVox.Dsp;

static class VoiceFxTest
{
    const int Fs = 48000, B = 480;
    static int fails;
    static bool Diag;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok     " : "  FALHA  ") + what);
        if (!ok) fails++;
    }

    // ---------------------------------------------------------------- sinais
    static float[] Tts(string text)
    {
        string wav = Path.Combine(Path.GetTempPath(), "takavox_vfx_tts.wav");
        using (var s = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            foreach (var v in s.GetInstalledVoices())
                if (v.VoiceInfo.Culture.Name.StartsWith("pt")) { s.SelectVoice(v.VoiceInfo.Name); break; }
            s.SetOutputToWaveFile(wav, new System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            s.Speak(text);
            s.SetOutputToNull();
        }
        var bytes = File.ReadAllBytes(wav);
        File.Delete(wav);
        int pos = 12, dataOff = -1, dataLen = 0;
        while (pos + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int len = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data") { dataOff = pos + 8; dataLen = Math.Min(len, bytes.Length - dataOff); break; }
            pos += 8 + len + (len & 1);
        }
        var x = new float[dataLen / 2];
        for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(bytes, dataOff + 2 * i) / 32768f;
        return x;
    }

    sealed class Synth { public float[] X; public float[] F0; public List<(int a, int b, string what)> Seg = new List<(int, int, string)>(); }

    /// <summary>Voz masculina sintetica: pulsos glotais (~110 Hz, entonacao lenta + jitter) por ressonadores de formantes.</summary>
    static Synth MaleVowels(double baseHz = 110)
    {
        var rnd = new Random(7);
        var sy = new Synth();
        var x = new List<float>(); var f0 = new List<float>();
        void Silence(double sec) { int n = (int)(sec * Fs); for (int i = 0; i < n; i++) { x.Add((float)((rnd.NextDouble() - 0.5) * 2e-4)); f0.Add(0); } }
        void Vowel(double sec, double[] F, double[] Bw, string name)
        {
            int n = (int)(sec * Fs), start = x.Count;
            var y1 = new double[F.Length]; var y2 = new double[F.Length];
            double ph = 0, g1 = 0, g2 = 0, gPrev = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (start + i) / (double)Fs;
                double hz = baseHz * (1 + 0.08 * Math.Sin(2 * Math.PI * 0.7 * t)) * (1 + 0.004 * (rnd.NextDouble() - 0.5));
                ph += hz / Fs;
                double e = 0;
                if (ph >= 1) { ph -= 1; e = 1; }
                // inclinacao glotal (-12 dB/oit) + radiacao dos labios (+6 dB/oit)
                double g = e + 1.9 * g1 - 0.9025 * g2; g2 = g1; g1 = g;
                double s = g - gPrev; gPrev = g;
                for (int k = 0; k < F.Length; k++)
                {
                    double r = Math.Exp(-Math.PI * Bw[k] / Fs), th = 2 * Math.PI * F[k] / Fs;
                    double a1 = 2 * r * Math.Cos(th), a2 = -r * r, gn = 1 - a1 - a2;
                    double yk = gn * s + a1 * y1[k] + a2 * y2[k];
                    y2[k] = y1[k]; y1[k] = yk; s = yk;
                }
                x.Add((float)s); f0.Add((float)hz);
            }
            // envelope de amplitude (ataque/soltura de 40 ms) e nivel ~ -20 dBFS
            double ss = 0; for (int i = start; i < x.Count; i++) ss += x[i] * x[i];
            double gain = 0.1 / Math.Sqrt(ss / n);
            int ramp = Fs / 25;
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)ramp);
                x[start + i] = (float)(x[start + i] * gain * env);
            }
            sy.Seg.Add((start + ramp * 2, x.Count - ramp * 2, name));
        }
        void Fricative(double sec)
        {
            int n = (int)(sec * Fs), start = x.Count;
            var hp = new Biquad(); hp.Set(FilterType.HighPass, Fs, 4000, 0.7, 0);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (Fs / 50.0));
                x.Add((float)(hp.Process((float)(rnd.NextDouble() - 0.5)) * 0.12 * env)); f0.Add(0);
            }
            sy.Seg.Add((start, x.Count, "s"));
        }
        Silence(0.3);
        Vowel(1.6, new double[] { 730, 1090, 2440, 3400 }, new double[] { 90, 110, 160, 250 }, "a");
        Silence(0.12); Fricative(0.25); Silence(0.08);
        Vowel(1.6, new double[] { 270, 2290, 3010, 3700 }, new double[] { 60, 100, 160, 250 }, "i");
        Silence(0.3);
        sy.X = x.ToArray(); sy.F0 = f0.ToArray();
        return sy;
    }

    // ---------------------------------------------------------------- processamento
    static float[] RunTune(float[] x, ChainParams p, VoiceFx fx = null, bool post = true, Action<int, ChainParams> perBlock = null)
    {
        var t = new AutoTune();
        var y = new float[x.Length];
        var blk = new float[B];
        for (int o = 0, bi = 0; o + B <= x.Length; o += B, bi++)
        {
            perBlock?.Invoke(bi, p);
            Array.Copy(x, o, blk, 0, B);
            t.Process(blk, B, p);
            if (fx != null) { fx.Pre(blk, B, p); if (post) fx.Post(blk, B, p); }
            Array.Copy(blk, 0, y, o, B);
        }
        return y;
    }

    static int LatencyOf(ChainParams p) => AutoTune.Needed(p) ? AutoTune.Delay : 0;

    // ---------------------------------------------------------------- medidas
    static float[] Decimate4(float[] x, int a, int len)
    {
        var lp1 = new Biquad(); var lp2 = new Biquad();
        lp1.Set(FilterType.LowPass, Fs, 5000, 0.54, 0); lp2.Set(FilterType.LowPass, Fs, 5000, 1.31, 0);
        var d = new float[len / 4];
        for (int i = 0, k = 0; i < len && k < d.Length; i++)
        {
            float v = lp2.Process(lp1.Process(x[a + i]));
            if ((i & 3) == 3) d[k++] = v;
        }
        return d;
    }

    /// <summary>Tom (Hz) e periodicidade (pico da autocorrelacao normalizada) de um trecho, a 12 kHz.</summary>
    static (double hz, double per) Pitch(float[] x, int a, int len)
    {
        var d = Decimate4(x, a, len);
        int n = d.Length, minL = 12000 / 650, maxL = 12000 / 45;
        int w = n - maxL - 2;
        if (w < 200) return (0, 0);
        double best = -1; int bl = 0;
        var nc = new double[maxL + 2];
        for (int L = minL - 1; L <= maxL + 1; L++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 0; i < w; i++) { xy += d[i] * d[i + L]; xx += d[i] * d[i]; yy += d[i + L] * d[i + L]; }
            nc[L] = xx > 0 && yy > 0 ? xy / Math.Sqrt(xx * yy) : 0;
        }
        for (int L = minL; L <= maxL; L++) if (nc[L] > best) { best = nc[L]; bl = L; }
        // prefere o menor periodo quase tao bom (evita erro de oitava para baixo)
        for (int L = minL; L <= maxL; L++)
            if (nc[L] > 0.9 * best && nc[L] >= nc[L - 1] && nc[L] >= nc[L + 1]) { bl = L; best = nc[L]; break; }
        double a1 = nc[bl - 1], b1 = nc[bl], c1 = nc[bl + 1], den = a1 - 2 * b1 + c1;
        double lag = bl + (Math.Abs(den) > 1e-12 ? 0.5 * (a1 - c1) / den : 0);
        return (12000.0 / lag, best);
    }

    /// <summary>Envelope LPC (dB) a 12 kHz, ordem 14, em 'nf' frequencias de 0 a 6 kHz.</summary>
    static double[] Envelope(float[] x, int a, int len, int nf)
    {
        var d = Decimate4(x, a, len);
        int n = d.Length, ord = 14;
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = (i > 0 ? d[i] - 0.9 * d[i - 1] : d[i]) * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (n - 1)));
        var r = new double[ord + 1];
        for (int k = 0; k <= ord; k++) { double s = 0; for (int i = k; i < n; i++) s += w[i] * w[i - k]; r[k] = s; }
        r[0] *= 1.0001;
        var A = new double[ord + 1]; var tmp = new double[ord + 1]; A[0] = 1; double E = r[0];
        for (int i = 1; i <= ord; i++)
        {
            double acc = r[i]; for (int j = 1; j < i; j++) acc += A[j] * r[i - j];
            double k = -acc / E; Array.Copy(A, tmp, ord + 1);
            A[i] = k; for (int j = 1; j < i; j++) A[j] = tmp[j] + k * tmp[i - j];
            E *= 1 - k * k;
        }
        var env = new double[nf];
        for (int f = 0; f < nf; f++)
        {
            double om = Math.PI * f / (nf - 1), re = 0, im = 0;
            for (int k = 0; k <= ord; k++) { re += A[k] * Math.Cos(om * k); im -= A[k] * Math.Sin(om * k); }
            // desfaz a pre-enfase para comparar o envelope "real"
            double pre = 1 - 2 * 0.9 * Math.Cos(om) + 0.81;
            env[f] = -10 * Math.Log10(re * re + im * im + 1e-30) + 10 * Math.Log10(pre);
        }
        return env;
    }

    /// <summary>Fator que melhor leva o envelope de entrada ao de saida (formantes x alpha).</summary>
    static double FormantFactor(double[] envIn, double[] envOut, double maxHz = 4600)
    {
        int nf = envIn.Length; double df = 6000.0 / (nf - 1);
        double bestC = -2, bestA = 1;
        for (double al = 0.55; al <= 1.9; al += 0.005)
        {
            var u = new List<double>(); var v = new List<double>();
            for (int f = 0; f < nf; f++)
            {
                double hz = f * df;
                if (hz < 250 || hz > maxHz) continue;
                double src = hz / al / df;
                if (src < 1 || src >= nf - 2) continue;
                int i0 = (int)src; double fr = src - i0;
                u.Add(envOut[f]); v.Add(envIn[i0] * (1 - fr) + envIn[i0 + 1] * fr);
            }
            if (u.Count < 50) continue;
            double mu = u.Average(), mv = v.Average(), su = 0, sv = 0, suv = 0;
            for (int i = 0; i < u.Count; i++) { su += (u[i] - mu) * (u[i] - mu); sv += (v[i] - mv) * (v[i] - mv); suv += (u[i] - mu) * (v[i] - mv); }
            double c = suv / Math.Sqrt(su * sv + 1e-30);
            if (c > bestC) { bestC = c; bestA = al; }
        }
        return bestA;
    }

    static double Rms(float[] x, int a, int b) { double s = 0; for (int i = a; i < b; i++) s += x[i] * (double)x[i]; return Math.Sqrt(s / Math.Max(1, b - a)); }
    static double Db(double v) => 20 * Math.Log10(Math.Max(1e-12, v));
    static double Median(List<double> l) { if (l.Count == 0) return double.NaN; var s = l.OrderBy(v => v).ToList(); return s[s.Count / 2]; }

    // ---------------------------------------------------------------- testes
    static void ShapeTest(Synth sy, string name, Action<ChainParams> set, double expSt, double expAlpha, bool expectVoice = true)
    {
        var p = new ChainParams(); p.VcOn = true; set(p); p.Sanitize();
        var y = RunTune(sy.X, p);
        int lat = LatencyOf(p);
        var cents = new List<double>(); var perIn = new List<double>(); var perOut = new List<double>(); var midIn = new List<double>();
        var alphas = new List<double>(); var lvl = new List<double>();
        foreach (var sg in sy.Seg.Where(s => s.what != "s"))
        {
            for (int a = sg.a; a + 4096 <= sg.b; a += 2400)
            {
                if (a + lat + 4096 > y.Length) break;
                var pi = Pitch(sy.X, a, 4096); var po = Pitch(y, a + lat, 4096);
                if (pi.hz <= 0) continue;
                perIn.Add(pi.per); perOut.Add(po.per);
                if (po.hz > 0) { cents.Add(1200 * Math.Log(po.hz / pi.hz, 2)); midIn.Add(1200 * Math.Log(pi.hz / 110, 2)); }
                var ei = Envelope(sy.X, a, 2048, 301); var eo = Envelope(y, a + lat, 2048, 301);
                alphas.Add(FormantFactor(ei, eo));
                lvl.Add(Db(Rms(y, a + lat, a + lat + 2048) / Rms(sy.X, a, a + 2048)));
            }
        }
        double mc = Median(cents), ma = Median(alphas), mpi = perIn.Average(), mpo = perOut.Average(), ml = Median(lvl);
        // com entonacao != 100%, o desvio em relacao a media da voz e ampliado/reduzido
        double k = p.VcIntonation / 100.0, mIn = midIn.Count > 0 ? midIn.Average() : 0;
        double errC = cents.Count == 0 ? 9999 : Median(cents.Select((c, i) => Math.Abs(c - (expSt * 100 + (k - 1) * (midIn[i] - mIn)))).ToList());
        Console.WriteLine($"  [{name}] tom {mc / 100:+0.00;-0.00} st (esperado {expSt:+0.0;-0.0}), formantes x{ma:0.000} (esperado x{expAlpha:0.000}), " +
                          $"periodicidade {mpi:0.000}→{mpo:0.000}, volume {ml:+0.0;-0.0} dB");
        Check(errC < 25, $"{name}: tom certo (erro mediano {errC:0} cents)");
        Check(Math.Abs(ma / expAlpha - 1) < 0.06, $"{name}: formantes certos (x{ma:0.000} vs x{expAlpha:0.000})");
        Check(mpo > 0.85, $"{name}: som limpo/periódico (autocorrelação {mpo:0.000})");
        Check(Math.Abs(ml) < 4, $"{name}: volume parecido com o original ({ml:+0.0;-0.0} dB)");
        // consoante "s": deve continuar presente (nao some, nao estoura)
        var s0 = sy.Seg.First(s => s.what == "s");
        double sLvl = Db(Rms(y, s0.a + lat, s0.b + lat) / Rms(sy.X, s0.a, s0.b));
        Check(sLvl > -9 && sLvl < 6, $"{name}: consoante 's' preservada ({sLvl:+0.0;-0.0} dB)");
    }

    static (int drops, int spikes, double lvlDb, float peak) Glitches(float[] x, float[] y, int lat)
    {
        int fr = 960, drops = 0, spikes = 0; float peak = 0;
        var li = new List<double>(); var lo = new List<double>();
        for (int a = 0; a + fr + lat <= y.Length; a += fr)
        {
            double ei = Rms(x, a, a + fr), eo = Rms(y, a + lat, a + lat + fr);
            li.Add(Db(ei)); lo.Add(Db(eo));
        }
        for (int i = 2; i < li.Count - 2; i++)
        {
            // fala forte e estavel na entrada (3 quadros) mas a saida cai > 15 dB num quadro so = buraco
            bool steady = li[i - 1] > -32 && li[i] > -32 && li[i + 1] > -32 && Math.Abs(li[i - 1] - li[i + 1]) < 6;
            if (steady && lo[i] < Math.Min(lo[i - 1], lo[i + 1]) - 12)
            {
                drops++;
                if (Diag)
                {
                    Console.Write($"      buraco em {i * fr / (double)Fs:0.000}s: entrada");
                    for (int k = i - 2; k <= i + 2; k++) Console.Write($" {li[k]:0}");
                    Console.Write("  saída");
                    for (int k = i - 2; k <= i + 2; k++) Console.Write($" {lo[k]:0}");
                    var pp = Pitch(x, Math.Max(0, i * fr - 1500), 4096);
                    Console.WriteLine($"  (tom da entrada {pp.hz:0} Hz, per {pp.per:0.00})");
                }
            }
            if (lo[i] > Math.Max(li[i - 1], Math.Max(li[i], li[i + 1])) + 12 && lo[i] > -40) spikes++;
        }
        for (int i = 0; i < y.Length; i++) peak = Math.Max(peak, Math.Abs(y[i]));
        double si = 0, so = 0; int c = 0;
        for (int i = 0; i < li.Count; i++) if (li[i] > -40) { si += Math.Pow(10, li[i] / 10); so += Math.Pow(10, lo[i] / 10); c++; }
        return (drops, spikes, c > 0 ? 10 * Math.Log10(so / si) : 0, peak);
    }

    static void SaveWav(string path, float[] x)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + x.Length * 2); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32767, Math.Min(32767, v * 32767)));
        }
    }

    static int Main(string[] args)
    {
        Diag = args.Contains("diag");
        args = args.Where(a => a != "diag").ToArray();
        string wavDir = args.Length > 0 ? args[0] : null;
        if (wavDir != null) Directory.CreateDirectory(wavDir);
        var sy = MaleVowels();

        Console.WriteLine("1) Voz masculina sintética (~110 Hz) — tom e timbre separados");
        ShapeTest(sy, "sem mudança (PSOLA neutro)", p => { p.VcExtraMix = 0; p.VcIntonation = 99; }, 0, 1.0);
        ShapeTest(sy, "garota de anime", p => VoicePresets.Find("Garota de anime").ApplyTo(p), 11, 1.22);
        ShapeTest(sy, "anime kawaii", p => VoicePresets.Find("Anime kawaii").ApplyTo(p), 12, 1.28);
        ShapeTest(sy, "garota", p => VoicePresets.Find("Garota").ApplyTo(p), 8, 1.17);
        ShapeTest(sy, "só timbre +30%", p => { p.VcFormant = 30; }, 0, 1.30);
        ShapeTest(sy, "só tom +12", p => { p.VcPitch = 12; }, 12, 1.0);
        ShapeTest(sy, "esquilo", p => VoicePresets.Find("Esquilo").ApplyTo(p), 12, 1.55);
        ShapeTest(sy, "voz grossa", p => VoicePresets.Find("Voz grossa").ApplyTo(p), -4, 0.88);
        ShapeTest(sy, "gigante", p => VoicePresets.Find("Gigante").ApplyTo(p), -9, 0.75);

        // entonacao 0% = monotono: a variacao de tom (8%) deve sumir
        {
            var p = new ChainParams(); p.VcOn = true; p.VcIntonation = 0; p.Sanitize();
            var y = RunTune(sy.X, p);
            var hzIn = new List<double>(); var hzOut = new List<double>();
            foreach (var sg in sy.Seg.Where(s => s.what != "s"))
                for (int a = sg.a; a + 2048 <= sg.b; a += 1200)
                {
                    var pi = Pitch(sy.X, a, 2048); var po = Pitch(y, a + AutoTune.Delay, 2048);
                    if (pi.hz > 0 && po.hz > 0) { hzIn.Add(1200 * Math.Log(pi.hz / 110, 2)); hzOut.Add(1200 * Math.Log(po.hz / 110, 2)); }
                }
            double sdIn = Math.Sqrt(hzIn.Select(v => (v - hzIn.Average()) * (v - hzIn.Average())).Average());
            double sdOut = Math.Sqrt(hzOut.Skip(hzOut.Count / 4).Select(v => (v - hzOut.Average()) * (v - hzOut.Average())).Average());
            Console.WriteLine($"  [robô/monótono] variação do tom {sdIn:0} → {sdOut:0} cents");
            Check(sdOut < sdIn * 0.35, "entonação 0% deixa a voz monótona");
        }
        // voz extra uma oitava abaixo: aparece energia no tom dela (~55 Hz), onde a voz principal (~73 Hz) nao tem
        {
            var p = new ChainParams(); VoicePresets.Find("Monstro").ApplyTo(p); p.VcOn = true;
            p.VcDrive = 0; p.VcLowDb = 0; p.VcHighDb = 0;
            var y = RunTune(sy.X, p);
            var p0 = new ChainParams(); p0.CopyVoiceFrom(p); p0.VcExtraMix = 0;
            var y0 = RunTune(sy.X, p0);
            // a voz principal sai identica nas duas rodadas: a diferenca e so a voz extra
            float m = p.VcExtraMix / 100f, nrm = (float)Math.Sqrt(1 + m * m);
            var ex = new float[y.Length];
            for (int i = 0; i < y.Length; i++) ex[i] = (y[i] * nrm - y0[i]) / m;
            var sg = sy.Seg[0];
            var pe = Pitch(ex, sg.a + AutoTune.Delay + 4800, 4096);
            var pm = Pitch(y0, sg.a + AutoTune.Delay + 4800, 4096);
            var pin = Pitch(sy.X, sg.a + 4800, 4096);
            double ce = 1200 * Math.Log(pe.hz / pin.hz, 2), cm = 1200 * Math.Log(pm.hz / pin.hz, 2);
            Console.WriteLine($"  [monstro] voz principal {cm / 100:+0.0;-0.0} st, voz extra {ce / 100:+0.0;-0.0} st (periodicidade {pe.per:0.00})");
            Check(Math.Abs(ce + 1200) < 40 && Math.Abs(cm + 700) < 40 && pe.per > 0.8, "voz extra uma oitava abaixo, principal -7 st");
            var g = Glitches(sy.X, y, AutoTune.Delay);
            Check(g.peak < 1.0f && !float.IsNaN(g.peak), $"monstro: sem estouro (pico {g.peak:0.00})");
        }

        Console.WriteLine();
        Console.WriteLine("2) Fala real (voz do Windows)");
        var speech = Tts("Oi gente, tudo bem? Hoje a gente vai jogar Valorant juntos. Fala sério, que partida incrível! " +
                         "Será que dá para ganhar mais uma? Vamos lá, sem medo, cuidado com o sniper no meio.");
        // pausas curtas a -70 dB como as da IA (nao zero absoluto)
        var rnd = new Random(3);
        for (int i = 0; i < speech.Length; i++) speech[i] += (float)((rnd.NextDouble() - 0.5) * 6e-4);
        Console.WriteLine($"  fala: {speech.Length / (double)Fs:0.0} s");
        var timing = new List<(string, double)>();
        foreach (var pr in VoicePresets.All)
        {
            var p = new ChainParams(); pr.ApplyTo(p); p.VcOn = true;
            var fx = new VoiceFx();
            var sw = Stopwatch.StartNew();
            var y = RunTune(speech, p, fx);
            sw.Stop();
            double usPerBlock = sw.Elapsed.TotalMilliseconds * 1000 / (speech.Length / B);
            timing.Add((pr.Name, usPerBlock));
            var g = Glitches(speech, y, LatencyOf(p));
            bool finite = y.All(v => !float.IsNaN(v) && !float.IsInfinity(v));
            bool fxPreset = p.VcEchoMix > 0 || p.VcReverbMix > 0 || p.VcBreath >= 50 || p.VcSynth > 0 || p.VcFilter > 0 || p.VcRingMix > 0 || p.VcMetal > 0 || p.VcTremDepth > 0;
            string info = $"{pr.Name}: buracos {g.drops}, picos {g.spikes}, volume {g.lvlDb:+0.0;-0.0} dB, pico {g.peak:0.00}, {usPerBlock:0} µs/bloco";
            Check(finite && g.peak < 2.5f && g.drops <= 2 && (fxPreset || g.spikes <= 2) && Math.Abs(g.lvlDb) < 9, info);
            if (wavDir != null) SaveWav(Path.Combine(wavDir, "voz_" + AppSafe(pr.Name) + ".wav"), y);
        }
        if (wavDir != null) SaveWav(Path.Combine(wavDir, "voz_original.wav"), speech);

        // troca rapida de presets (a cada 0,2 s) e liga/desliga: nada de NaN, estouro ou travar
        {
            var p = new ChainParams(); p.VcOn = true;
            var fx = new VoiceFx();
            int k = 0;
            var y = RunTune(speech, p, fx, true, (bi, pp) =>
            {
                if (bi % 20 != 0) return;
                k++;
                if (k % 7 == 0) { pp.VcOn = !pp.VcOn; pp.Touch(); return; }
                VoicePresets.All[k % VoicePresets.All.Length].ApplyTo(pp); pp.VcOn = true; pp.Touch();
            });
            float peak = y.Max(v => Math.Abs(v));
            bool finite = y.All(v => !float.IsNaN(v) && !float.IsInfinity(v));
            Check(finite && peak < 3f, $"troca rápida de vozes: estável (pico {peak:0.00})");
        }

        Console.WriteLine();
        Console.WriteLine("3) Efeitos");
        {
            // sussurro: mesmo envelope, sem tom
            var p = new ChainParams(); p.VcOn = true; p.VcBreath = 100; p.Sanitize();
            var y = RunTune(sy.X, p, new VoiceFx());
            var sg = sy.Seg[0];
            var po = Pitch(y, sg.a + 4800, 4096); var pi = Pitch(sy.X, sg.a + 4800, 4096);
            var ei = Envelope(sy.X, sg.a + 4800, 4096, 301); var eo = Envelope(y, sg.a + 4800, 4096, 301);
            double lv = Db(Rms(y, sg.a, sg.b) / Rms(sy.X, sg.a, sg.b));
            // (acima de ~2 kHz o sussurro tem ar a mais, de proposito: compara so F1/F2)
            double fw = FormantFactor(ei, eo, 2200);
            Console.WriteLine($"  [sussurro] periodicidade {pi.per:0.00}→{po.per:0.00}, formantes x{fw:0.00}, volume {lv:+0.0;-0.0} dB");
            Check(po.per < 0.5 && Math.Abs(fw - 1) < 0.08 && Math.Abs(lv) < 3, "sussurro: tira o tom e mantém as vogais e o volume");
        }
        {
            // robo sintetizado: tom fixo de 110 Hz (mesmo com a voz a ~110±9 Hz) e envelope preservado
            var p = new ChainParams(); p.VcOn = true; p.VcSynth = 100; p.VcSynthHz = 150; p.Sanitize();
            var y = RunTune(sy.X, p, new VoiceFx());
            var sg = sy.Seg.First(q => q.what == "i");
            var po = Pitch(y, sg.a + 4800, 4096);
            var ei = Envelope(sy.X, sg.a + 4800, 4096, 301); var eo = Envelope(y, sg.a + 4800, 4096, 301);
            Console.WriteLine($"  [robô sintetizado] tom {po.hz:0} Hz (esperado 150), periodicidade {po.per:0.00}, formantes x{FormantFactor(ei, eo):0.00}");
            Check(Math.Abs(po.hz - 150) < 6 && Math.Abs(FormantFactor(ei, eo) - 1) < 0.08, "robô sintetizado: tom fixo e vogais preservadas");
        }
        {
            // sopro 50% (maximo antes de virar sussurro): ar com a forma da voz (ressonancias dela) nos agudos, sem chiado por cima e sem mudar o volume.
            // (a 1a versao somava ruido branco: +24 dB acima de 5 kHz na garota de anime = "microfone ruim")
            var p = new ChainParams(); p.VcOn = true; p.VcBreath = 50; p.Sanitize();
            var y = RunTune(sy.X, p, new VoiceFx());
            var sg = sy.Seg[0];
            double Hf(float[] sig, double fc)
            {
                var h = new Biquad(); h.Set(FilterType.HighPass, Fs, fc, 0.7, 0);
                double e = 0;
                for (int i = sg.a; i < sg.b; i++) { float u = h.Process(sig[i]); e += u * u; }
                return e;
            }
            double air = 10 * Math.Log10(Hf(y, 1500) / Hf(sy.X, 1500)), hiss = 10 * Math.Log10(Hf(y, 5000) / Hf(sy.X, 5000));
            double lv = Db(Rms(y, sg.a, sg.b) / Rms(sy.X, sg.a, sg.b));
            Console.WriteLine($"  [sopro 50%] ar acima de 1,5 kHz {air:+0.0;-0.0} dB, acima de 5 kHz {hiss:+0.0;-0.0} dB, volume {lv:+0.0;-0.0} dB");
            Check(air > 1.0 && air < 8 && hiss < 6 && Math.Abs(lv) < 1.5, "sopro: ar nos agudos da voz, sem chiado, mesmo volume");
        }
        {
            // telefone: corta graves e agudos (voz masculina: fundamental de ~110 Hz)
            var p = new ChainParams(); VoicePresets.Find("Telefone").ApplyTo(p); p.VcOn = true;
            var y = RunTune(sy.X, p, new VoiceFx());
            double lo1 = BandDb(sy.X, 60, 180), lo2 = BandDb(y, 60, 180), hi1 = BandDb(sy.X, 5000, 12000), hi2 = BandDb(y, 5000, 12000), m1 = BandDb(sy.X, 800, 2500), m2 = BandDb(y, 800, 2500);
            Console.WriteLine($"  [telefone] graves {lo2 - lo1:+0;-0} dB, agudos {hi2 - hi1:+0;-0} dB, médios {m2 - m1:+0;-0} dB");
            Check(lo2 - lo1 < -15 && hi2 - hi1 < -18 && Math.Abs(m2 - m1) < 6, "telefone: só a faixa de 300–3400 Hz");
        }
        {
            // eco: um clique volta depois do tempo certo, com o volume certo
            var p = new ChainParams(); p.VcOn = true; p.VcEchoMix = 50; p.VcEchoMs = 250; p.VcEchoFb = 40; p.Sanitize();
            var fx = new VoiceFx();
            var x = new float[Fs * 2]; x[4800] = 0.5f;
            var y = RunTune(x, p, fx);
            int at = 4800 + 250 * 48;
            float e1 = 0; int where = 0;
            for (int i = at - 50; i < at + 50; i++) if (Math.Abs(y[i]) > e1) { e1 = Math.Abs(y[i]); where = i; }
            Console.WriteLine($"  [eco] volta em {(where - 4800) / 48.0:0} ms com {Db(e1 / 0.5):0.0} dB");
            Check(Math.Abs(where - at) < 40 && Math.Abs(Db(e1 / 0.5) - Db(0.5 * 0.35 * 1.6)) < 6, "eco no tempo certo");
        }
        {
            // reverb: cauda que decai (sem explodir)
            var p = new ChainParams(); p.VcOn = true; p.VcReverbMix = 40; p.VcReverbSize = 95; p.Sanitize();
            var fx = new VoiceFx();
            var x = new float[Fs * 6];
            Array.Copy(speech, 0, x, 0, Math.Min(speech.Length, Fs * 2));
            var y = RunTune(x, p, fx);
            double tail1 = Rms(y, Fs * 2 + 2400, Fs * 2 + 7200), tail2 = Rms(y, Fs * 5, Fs * 6);
            Console.WriteLine($"  [reverb] cauda logo depois {Db(tail1):0} dBFS, 3 s depois {Db(tail2):0} dBFS");
            Check(tail1 > 1e-4 && tail2 < tail1 * 0.05, "reverb: cauda natural que some");
            // silencio digital absoluto depois (mic mudo): sem numeros "denormais" lentos
            var z = new float[Fs * 20];
            var sw = Stopwatch.StartNew();
            var blk = new float[B];
            for (int o = 0; o + B <= z.Length; o += B) { Array.Clear(blk, 0, B); fx.Post(blk, B, p); }
            sw.Stop();
            Console.WriteLine($"  [reverb] 20 s de silêncio absoluto: {sw.Elapsed.TotalMilliseconds:0} ms de CPU");
            Check(sw.Elapsed.TotalMilliseconds < 400, "sem lentidão no silêncio (denormais)");
        }

        Console.WriteLine();
        Console.WriteLine("4) CPU (motor de tom + efeitos), por bloco de 10 ms");
        foreach (var t in timing.OrderByDescending(t => t.Item2).Take(6))
            Console.WriteLine($"  {t.Item1,-20} {t.Item2,6:0} µs/bloco = {t.Item2 / 100.0:0.00}% de 1 núcleo");
        double worst = timing.Max(t => t.Item2);
        Check(worst < 600, $"pior preset abaixo de 6% de 1 núcleo ({worst / 100:0.00}%)");

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "TUDO OK" : $"{fails} FALHA(S)");
        return fails == 0 ? 0 : 1;
    }

    static float[] Slice(float[] x, int a, int b) { var r = new float[Math.Max(0, Math.Min(b, x.Length) - a)]; Array.Copy(x, a, r, 0, r.Length); return r; }

    static string AppSafe(string s) { foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s.Replace(' ', '_'); }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    /// <summary>Energia (dB) na faixa [lo, hi] Hz, espectro medio (Welch, Hann de 8192).</summary>
    static double BandDb(float[] x, double lo, double hi)
    {
        const int N = 8192;
        var re = new double[N]; var im = new double[N];
        double sum = 0; int frames = 0;
        for (int a = 0; a + N <= x.Length; a += N / 2)
        {
            for (int i = 0; i < N; i++) { re[i] = x[a + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); im[i] = 0; }
            Fft(re, im);
            int b0 = (int)Math.Ceiling(lo * N / Fs), b1 = (int)Math.Floor(hi * N / Fs);
            for (int k = b0; k <= b1; k++) sum += re[k] * re[k] + im[k] * im[k];
            frames++;
        }
        return 10 * Math.Log10(sum / Math.Max(1, frames) + 1e-30);
    }

    static double BandDbIir(float[] x, double lo, double hi)
    {
        var a = new Biquad(); a.Set(FilterType.HighPass, Fs, lo, 0.7, 0);
        var b = new Biquad(); b.Set(FilterType.HighPass, Fs, lo, 0.7, 0);
        var c = new Biquad(); c.Set(FilterType.LowPass, Fs, hi, 0.7, 0);
        var d = new Biquad(); d.Set(FilterType.LowPass, Fs, hi, 0.7, 0);
        double s = 0;
        foreach (var v in x) { float u = d.Process(c.Process(b.Process(a.Process(v)))); s += u * u; }
        return 10 * Math.Log10(s / x.Length + 1e-30);
    }
}
