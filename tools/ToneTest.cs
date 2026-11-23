// Teste do removedor de ventoinha/zumbido (ToneRemover) e do efeito dele na cadeia completa com a IA.
// Ruido de ventoinha simulado igual ao medido no mic do usuario (serie de ~120,8 Hz: 121/242/363/484 Hz,
// ~-27 dBFS, oscilando devagar) + fala real (voz do Windows) com pausas.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TakaVox.Dsp;

static class ToneTest
{
    const int Fs = 48000, B = 480;
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok     " : "  FALHA  ") + what);
        if (!ok) fails++;
    }

    // ---------------------------------------------------------------- sinais
    static float[] Tts(string text)
    {
        string wav = Path.Combine(Path.GetTempPath(), "takavox_tone_tts.wav");
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
        int data = 12;
        while (data + 8 < bytes.Length && !(bytes[data] == 'd' && bytes[data + 1] == 'a' && bytes[data + 2] == 't' && bytes[data + 3] == 'a'))
            data += 8 + BitConverter.ToInt32(bytes, data + 4);
        int len = BitConverter.ToInt32(bytes, data + 4) / 2;
        var y = new float[len];
        for (int i = 0; i < len; i++) y[i] = BitConverter.ToInt16(bytes, data + 8 + 2 * i) / 32768f;
        return y;
    }

    // fala com pausas: [pausa][frase][pausa][frase]... normalizada para -20 dBFS RMS nos trechos de fala
    static float[] Speech(int seconds)
    {
        var parts = new[]
        {
            Tts("Bora jogar, mano! Hoje a gente sobe de elo."),
            Tts("Cuidado com o sniper no meio, eu vou pela direita."),
            Tts("Segura a bomba que eu já chego aí, beleza?"),
            Tts("Boa, boa! Mais um round e a gente fecha o jogo."),
        };
        var all = new List<float>();
        int p = 0;
        while (all.Count < seconds * Fs)
        {
            all.AddRange(new float[(int)(Fs * (0.9 + 0.3 * (p % 3)))]); // pausas de 0,9 a 1,5 s
            all.AddRange(parts[p % parts.Length]);
            p++;
        }
        var x = all.Take(seconds * Fs).ToArray();
        double e = 0; int n = 0;
        foreach (var v in x) if (Math.Abs(v) > 1e-4) { e += v * v; n++; }
        float g = (float)(Math.Pow(10, -20 / 20.0) / Math.Sqrt(e / Math.Max(1, n)));
        for (int i = 0; i < x.Length; i++) x[i] *= g;
        return x;
    }

    // ventoinha: serie harmonica com a fundamental andando devagar + chiado largo
    static float[] Fan(int seconds, Func<double, double> f0, double toneDbfs, double hissDbfs, int seed)
    {
        var rnd = new Random(seed);
        int n = seconds * Fs;
        var y = new float[n];
        double[] rel = { -7, 0, -4, -16 }; // como no mic do usuario: 242 Hz mais forte
        double ph = 0, lp = 0, lp2 = 0;
        double ga = Math.Pow(10, toneDbfs / 20) * Math.Sqrt(2), hs = Math.Pow(10, hissDbfs / 20) * Math.Sqrt(3) * 3;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Fs;
            ph += 2 * Math.PI * f0(t) / Fs;
            double am = 1 + 0.15 * Math.Sin(2 * Math.PI * 0.31 * t);
            double s = 0;
            for (int k = 0; k < 4; k++) s += Math.Pow(10, rel[k] / 20) * Math.Sin((k + 1) * ph + k * 1.3);
            double w = rnd.NextDouble() * 2 - 1;
            lp = 0.9 * lp + 0.1 * w; lp2 = 0.7 * lp2 + 0.3 * lp; // chiado mais forte nos graves (ventoinha)
            y[i] = (float)(ga * am * s / 1.3 + hs * lp2);
        }
        return y;
    }

    static float[] Mix(float[] a, float[] b) { var y = new float[Math.Min(a.Length, b.Length)]; for (int i = 0; i < y.Length; i++) y[i] = a[i] + b[i]; return y; }

    static bool[] SpeechBlocks(float[] clean)
    {
        int nb = clean.Length / B;
        var raw = new bool[nb];
        for (int b = 0; b < nb; b++)
        {
            double e = 0;
            for (int i = b * B; i < (b + 1) * B; i++) e += clean[i] * clean[i];
            raw[b] = 10 * Math.Log10(e / B + 1e-20) > -50;
        }
        var sp = new bool[nb];
        for (int b = 0; b < nb; b++)
            for (int j = Math.Max(0, b - 15); j <= Math.Min(nb - 1, b + 15); j++) if (raw[j]) { sp[b] = true; break; }
        return sp;
    }

    // ---------------------------------------------------------------- medidas
    // espectro medio (dB) dos trechos de 16384 amostras em que TODOS os blocos sao do tipo pedido
    static double[] Spectrum(float[] x, bool[] speech, bool wantSpeech, double fromSec, int shift = 0)
    {
        const int N = 16384;
        var fft = new Fft(N);
        var seg = new float[N]; var db = new float[N / 2 + 1]; var acc = new double[N / 2 + 1];
        int frames = 0;
        for (int s = (int)(fromSec * Fs); s + N + shift <= x.Length; s += N / 4)
        {
            bool ok = true;
            for (int b = s / B; b < (s + N) / B && ok; b++) if (b >= speech.Length || speech[b] != wantSpeech) ok = false;
            if (!ok) continue;
            Array.Copy(x, s + shift, seg, 0, N);
            fft.PowerDb(seg, db);
            for (int k = 0; k < acc.Length; k++) acc[k] += Math.Pow(10, db[k] / 10);
            frames++;
        }
        return frames == 0 ? null : acc.Select(v => 10 * Math.Log10(v / frames + 1e-30)).ToArray();
    }

    static double PeakAt(double[] S, double f)
    {
        double df = (double)Fs / 16384, m = -999;
        for (int k = (int)((f - 5) / df); k <= (int)((f + 5) / df); k++) m = Math.Max(m, S[k]);
        return m;
    }

    static double Band(double[] S, double f0, double f1)
    {
        double df = (double)Fs / 16384, e = 0;
        for (int k = (int)(f0 / df); k <= (int)(f1 / df); k++) e += Math.Pow(10, S[k] / 10);
        return 10 * Math.Log10(e + 1e-30);
    }

    static float[] RunRemover(ToneRemover tr, float[] x, bool[] speech, bool feed = true)
    {
        var y = (float[])x.Clone();
        var blk = new float[B];
        var pre = new float[B];
        for (int b = 0; b + 1 <= y.Length / B; b++)
        {
            Array.Copy(y, b * B, blk, 0, B);
            Array.Copy(blk, pre, B);
            tr.Apply(blk, B, true);
            if (feed) tr.Feed(pre, B, !speech[b]);
            Array.Copy(blk, 0, y, b * B, B);
        }
        return y;
    }

    static string Freqs(ToneRemover.Notch[] a) => a.Length == 0 ? "nenhum" : string.Join(", ", a.Select(n => $"{n.Freq:0.0} Hz ({n.DepthDb:0} dB, Q {n.Q:0})"));

    static int Main()
    {
        Console.WriteLine("Gerando fala (voz do Windows) e ventoinha simulada…");
        int secs = 30;
        var voice = Speech(secs);
        var sp = SpeechBlocks(voice);
        Func<double, double> wobble = t => 120.8 + 0.4 * Math.Sin(2 * Math.PI * t / 9);
        var fan = Fan(secs, wobble, -30, -50, 1);
        var mix = Mix(voice, fan);
        double fanRms = 10 * Math.Log10(fan.Select(v => (double)v * v).Average());
        Console.WriteLine($"  ventoinha: {fanRms:0.0} dBFS RMS (no mic do usuário: ~-26) · fala: {sp.Count(b => b) * 100 / sp.Length}% do tempo");

        Console.WriteLine("[1] ventoinha + fala: acha e tira os apitos");
        var tr = new ToneRemover(true);
        var outp = RunRemover(tr, mix, sp);
        var act = tr.Active;
        Console.WriteLine("  cortes: " + Freqs(act));
        double[] harm = { 120.8, 241.6, 362.4, 483.2 };
        int found = harm.Count(h => act.Any(n => Math.Abs(n.Freq - h) < 2.5));
        Check(found >= 3 && act.Length <= 6, $"achou {found} de 4 harmônicos da ventoinha (o mais fraco, 484 Hz, fica só 13 dB acima do chiado)");
        var sIn = Spectrum(mix, sp, false, 6);
        var sOut = Spectrum(outp, sp, false, 6);
        var red = harm.Select(h => PeakAt(sIn, h) - PeakAt(sOut, h)).ToArray();
        Console.WriteLine("  redução nas pausas: " + string.Join("  ", harm.Select((h, i) => $"{h:0} Hz −{red[i]:0} dB")));
        Check(red[0] >= 14 && red[1] >= 14 && red[2] >= 14, "os 3 apitos fortes caem pelo menos 14 dB (ventoinha oscilando ±0,4 Hz, como a real)");
        var spIn = Spectrum(mix, sp, true, 6);
        var spOut = Spectrum(outp, sp, true, 6);
        double redSpeech = PeakAt(spIn, 241.6) - PeakAt(spOut, 241.6);
        Console.WriteLine($"  durante a fala, o apito de 242 Hz cai {redSpeech:0} dB (a IA não consegue tirar ele por baixo da voz)");

        Console.WriteLine("[2] a voz não é afetada");
        var frozen = new ToneRemover(true);
        frozen.CopyNotchesFrom(tr);
        var vOut = RunRemover(frozen, voice, sp, false);
        var vIn = Spectrum(voice, sp, true, 0);
        var vO = Spectrum(vOut, sp, true, 0);
        double worst = 0;
        string worstBand = "";
        foreach (var (f0, f1) in new[] { (90.0, 180.0), (180.0, 360.0), (360.0, 720.0), (720.0, 1440.0), (1440.0, 2880.0), (2880.0, 8000.0) })
        {
            double d = Band(vO, f0, f1) - Band(vIn, f0, f1);
            if (Math.Abs(d) > Math.Abs(worst)) { worst = d; worstBand = $"{f0:0}–{f1:0} Hz"; }
        }
        double total = 10 * Math.Log10(vOut.Select(v => (double)v * v).Sum() / voice.Select(v => (double)v * v).Sum());
        Check(Math.Abs(worst) < 1.0 && Math.Abs(total) < 0.3, $"voz limpa pelos mesmos cortes: maior mudança por oitava {worst:+0.00;-0.00} dB ({worstBand}), total {total:+0.00;-0.00} dB");

        Console.WriteLine("[3] sem ventoinha: não inventa corte");
        var hissOnly = Fan(secs, wobble, -200, -50, 2);
        var tr2 = new ToneRemover(true);
        RunRemover(tr2, Mix(voice, hissOnly), sp);
        Check(tr2.Active.Length == 0, "fala + chiado comum: " + Freqs(tr2.Active));

        Console.WriteLine("[4] música no fundo (notas mudando): não confunde com ventoinha");
        var music = new float[secs * Fs];
        var rnd = new Random(5);
        double[] notes = { 130.8, 146.8, 164.8, 174.6, 196.0, 220.0, 246.9, 261.6, 293.7, 329.6 };
        double mph = 0, nf = notes[0];
        for (int i = 0; i < music.Length; i++)
        {
            if (i % (int)(0.35 * Fs) == 0) nf = notes[rnd.Next(notes.Length)];
            mph += 2 * Math.PI * nf / Fs;
            music[i] = (float)(0.03 * (Math.Sin(mph) + 0.5 * Math.Sin(2 * mph)));
        }
        var tr3 = new ToneRemover(true);
        RunRemover(tr3, Mix(Mix(voice, hissOnly), music), sp);
        Check(tr3.Active.Length <= 1, "notas de 0,35 s: " + Freqs(tr3.Active));

        Console.WriteLine("[5] ventoinha acelerando (121 → 127 Hz em 30 s): o corte acompanha");
        var fan2 = Fan(secs, t => 120.8 + 6.2 * t / secs, -30, -50, 3);
        var tr4 = new ToneRemover(true);
        var mix2 = Mix(voice, fan2);
        var out4 = RunRemover(tr4, mix2, sp);
        Console.WriteLine("  cortes no fim: " + Freqs(tr4.Active));
        var a = Spectrum(mix2, sp, false, 22);
        var c = Spectrum(out4, sp, false, 22);
        double fEnd = 120.8 + 6.2 * 27 / secs;
        double r2 = PeakAt(a, 2 * fEnd) - PeakAt(c, 2 * fEnd);
        Check(r2 >= 10, $"no fim (~{2 * fEnd:0} Hz) o apito principal ainda cai {r2:0} dB (teste duro: ventoinha mudando 5% de velocidade)");

        Console.WriteLine("[6] cadeia completa com a IA (DeepFilterNet LL): voz com ventoinha × voz limpa");
        float[] Chain(float[] x, bool toneOn)
        {
            var p = new ChainParams { ToneOn = toneOn };
            var y = (float[])x.Clone();
            using (var vc = new VoiceChain(p, true))
            {
                var blk = new float[B];
                for (int b = 0; b < y.Length / B; b++)
                {
                    Array.Copy(y, b * B, blk, 0, B);
                    vc.Process(blk);
                    Array.Copy(blk, 0, y, b * B, B);
                }
            }
            return y;
        }
        int lat;
        using (var vc = new VoiceChain(new ChainParams(), true)) lat = vc.LatencySamples;
        var cleanOut = Chain(voice, true);
        var offOut = Chain(mix, false);
        var onOut = Chain(mix, true);
        var sC = Spectrum(cleanOut, sp, true, 6, lat);
        var sOff = Spectrum(offOut, sp, true, 6, lat);
        var sOn = Spectrum(onOut, sp, true, 6, lat);
        // corpo da voz: graves/medios (120-600 Hz) em relacao a presenca (1-4 kHz), comparado com a voz limpa
        double Body(double[] S) => Band(S, 120, 600) - Band(S, 1000, 4000);
        double dOff = Body(sOff) - Body(sC), dOn = Body(sOn) - Body(sC);
        Console.WriteLine($"  corpo da voz (120–600 Hz × 1–4 kHz) comparado à voz limpa: sem o removedor {dOff:+0.0;-0.0} dB · com o removedor {dOn:+0.0;-0.0} dB");
        // zumbido que passa por baixo da voz: nivel nas frequencias da ventoinha comparado a mesma voz sem ventoinha
        double leakOff = 0, leakOn = 0;
        foreach (var h in harm) { leakOff = Math.Max(leakOff, PeakAt(sOff, h) - PeakAt(sC, h)); leakOn = Math.Max(leakOn, PeakAt(sOn, h) - PeakAt(sC, h)); }
        Console.WriteLine($"  zumbido por baixo da voz (pior harmônico, acima da voz limpa): sem o removedor +{leakOff:0.0} dB · com +{leakOn:0.0} dB");
        Check(leakOn <= leakOff + 0.5, "com o removedor o zumbido por baixo da voz não aumenta (aqui a IA sozinha já tirava)");
        Check(Math.Abs(dOn) <= Math.Abs(dOff) + 0.5, "e o corpo da voz fica pelo menos tão perto da voz limpa quanto sem ele");
        var pOff = Spectrum(offOut, sp, false, 6, lat);
        var pOn = Spectrum(onOut, sp, false, 6, lat);
        if (pOff != null && pOn != null)
            Console.WriteLine($"  nas pausas (o que o Discord recebe): sem {Band(pOff, 60, 4000):0} dB · com {Band(pOn, 60, 4000):0} dB");

        Console.WriteLine(fails == 0 ? "TUDO OK" : "FALHAS: " + fails);
        return fails;
    }
}
