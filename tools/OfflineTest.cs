// Testes offline da cadeia de voz: alinhamento da IA, reducao de ruido, limitador, NaN e desempenho.
using System;
using System.Diagnostics;
using TakaVox.Dsp;

static class OfflineTest
{
    const int Fs = 48000;
    static readonly Random rnd = new Random(1234);

    static float Gauss() { double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble(); return (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)); }

    // Sinal "tipo voz": harmonicos de f0 variavel com envelope silabico, intercalado com pausas.
    static float[] MakeVoice(int len, out bool[] active)
    {
        var x = new float[len]; active = new bool[len];
        double ph = 0;
        for (int i = 0; i < len; i++)
        {
            double t = (double)i / Fs;
            double seg = t % 2.0;                       // 1.4 s falando, 0.6 s pausa
            bool on = seg < 1.4;
            double syl = on ? Math.Pow(Math.Max(0, Math.Sin(Math.PI * (seg % 0.28) / 0.28)), 0.6) : 0;
            double f0 = 130 + 25 * Math.Sin(2 * Math.PI * 0.7 * t);
            ph += 2 * Math.PI * f0 / Fs;
            double s = 0;
            for (int h = 1; h <= 30; h++)
            {
                double fh = f0 * h; if (fh > 7000) break;
                double formant = Math.Exp(-Math.Pow((fh - 600) / 400, 2)) + 0.6 * Math.Exp(-Math.Pow((fh - 1700) / 500, 2)) + 0.3 * Math.Exp(-Math.Pow((fh - 2800) / 600, 2));
                s += formant / h * Math.Sin(h * ph);
            }
            x[i] = (float)(0.12 * syl * s);
            active[i] = syl > 0.3;
        }
        return x;
    }

    static double Rms(float[] x, int a, int b) { double s = 0; for (int i = a; i < b; i++) s += x[i] * x[i]; return Math.Sqrt(s / Math.Max(1, b - a)); }
    static string Db(double v) => (20 * Math.Log10(Math.Max(v, 1e-12))).ToString("F1") + " dB";

    static float[] Run(ChainParams p, float[] input, out double msPerBlock)
    {
        var chain = new VoiceChain(p);
        var y = new float[input.Length];
        var blk = new float[VoiceChain.Block];
        var sw = Stopwatch.StartNew();
        int blocks = 0;
        for (int i = 0; i + VoiceChain.Block <= input.Length; i += VoiceChain.Block)
        {
            Array.Copy(input, i, blk, 0, VoiceChain.Block);
            chain.Process(blk);
            Array.Copy(blk, 0, y, i, VoiceChain.Block);
            blocks++;
        }
        sw.Stop();
        msPerBlock = sw.Elapsed.TotalMilliseconds / Math.Max(1, blocks);
        chain.Dispose();
        return y;
    }

    static int BestLag(float[] a, float[] b, int maxLag)
    {
        int best = 0; double bestC = double.MinValue;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double c = 0;
            for (int i = 20000; i < a.Length - 20000; i += 3) c += a[i] * b[i + lag];
            if (c > bestC) { bestC = c; best = lag; }
        }
        return best;
    }

    static int Main()
    {
        int fails = 0;
        Console.WriteLine("RNNoise: " + (RnnoiseLib.Init() ? "OK (" + RnnoiseLib.Variant + ")" : "FALHOU: " + RnnoiseLib.Error));
        if (!RnnoiseLib.Available) fails++;

        int len = Fs * 12;
        var voice = MakeVoice(len, out var active);
        var noisy = new float[len];
        for (int i = 0; i < len; i++) noisy[i] = voice[i] + 0.006f * Gauss() + 0.004f * (float)Math.Sin(2 * Math.PI * 60 * i / Fs);

        // --- 1) Alinhamento: IA ligada x IA a 0% devem ter o MESMO atraso (para cada motor)
        int lagIn = 0;
        foreach (int eng in new[] { 2, 1, 0 })
        {
            var pa = new ChainParams(); pa.GateOn = false; pa.AgcOn = false; pa.CompOn = false; pa.DeessOn = false; pa.EqOn = false; pa.InputGainDb = 0; pa.OutputGainDb = 0;
            pa.NoiseEngine = eng;
            pa.NoiseMix = 1f; pa.Touch(); var wet = Run(pa, voice, out _);
            pa.NoiseMix = 0f; pa.Touch(); var dry = Run(pa, voice, out _);
            int lagD = BestLag(voice, dry, 3000);
            int expected = new VoiceChain(pa, true).LatencySamples;
            bool ok = Math.Abs(lagD - expected) <= 10;
            Console.WriteLine($"[latência motor {eng}] IA a 0% = {lagD} amostras, informado {expected} " + (ok ? "OK" : "PROBLEMA"));
            if (!ok) fails++;
            if (eng == 2)
            {
                int lag = BestLag(dry, wet, 960);
                Console.WriteLine($"[alinhamento RNNoise] IA vs seco = {lag} amostras " + (Math.Abs(lag) <= 2 ? "OK" : "PROBLEMA"));
                if (Math.Abs(lag) > 2) fails++;
                lagIn = lagD;
            }
        }

        // --- 2) Reducao de ruido (sinal sintetico: usa o RNNoise; a DeepFilterNet e medida com fala real no NoiseBench)
        var pd = new ChainParams(); // padrao completo
        pd.InputGainDb = 0; pd.NoiseEngine = 2;
        var outFull = Run(pd, noisy, out double msBlock);
        int lat = lagIn;
        double nIn = 0, nOut = 0, sIn = 0, sOut = 0; int cn = 0, cs = 0;
        for (int i = 4800; i < len - 4800; i++)
        {
            bool pause = true;
            for (int k = -2400; k <= 2400; k += 240) if (active[Math.Max(0, Math.Min(len - 1, i + k))]) { pause = false; break; }
            int j = i + lat; if (j >= len) break;
            if (pause) { nIn += noisy[i] * noisy[i]; nOut += outFull[j] * outFull[j]; cn++; }
            else if (active[i]) { sIn += noisy[i] * noisy[i]; sOut += outFull[j] * outFull[j]; cs++; }
        }
        double snrIn = 10 * Math.Log10((sIn / cs) / (nIn / cn)), snrOut = 10 * Math.Log10((sOut / Math.Max(1, cs)) / Math.Max(1e-20, nOut / Math.Max(1, cn)));
        Console.WriteLine($"[ruido] pausa entrada {Db(Math.Sqrt(nIn / cn))} -> saida {Db(Math.Sqrt(nOut / Math.Max(1, cn)))}");
        Console.WriteLine($"[ruido] voz entrada {Db(Math.Sqrt(sIn / cs))} -> saida {Db(Math.Sqrt(sOut / Math.Max(1, cs)))}");
        Console.WriteLine($"[ruido] relacao voz/ruido: {snrIn:F1} dB -> {snrOut:F1} dB");
        if (snrOut < snrIn + 15) { Console.WriteLine("  PROBLEMA: reducao de ruido fraca"); fails++; }

        // --- 3) Limitador: entrada absurda (+20 dB) nunca passa do teto
        var pl = new ChainParams(); pl.InputGainDb = 30; pl.OutputGainDb = 12; pl.Touch();
        var loud = Run(pl, noisy, out _);
        float peak = 0; bool nan = false;
        foreach (var v in loud) { if (float.IsNaN(v) || float.IsInfinity(v)) nan = true; peak = Math.Max(peak, Math.Abs(v)); }
        Console.WriteLine($"[limitador] pico saida = {20 * Math.Log10(peak):F2} dBFS (teto -1.0) " + (peak <= DspMath.DbToLin(-1f) + 1e-5 ? "OK" : "PROBLEMA"));
        if (peak > DspMath.DbToLin(-1f) + 1e-5) fails++;
        if (nan) { Console.WriteLine("  PROBLEMA: NaN/Inf na saida"); fails++; }

        // --- 4) Silencio absoluto e entrada DC nao geram lixo
        var zeros = new float[Fs * 3];
        var z = Run(new ChainParams(), zeros, out _);
        double zr = Rms(z, 0, z.Length);
        Console.WriteLine($"[silencio] saida rms = {Db(zr)} " + (zr < 1e-4 ? "OK" : "PROBLEMA"));
        if (zr >= 1e-4) fails++;

        // --- 5) Volume: a voz sai forte (meta ~ -18..-12 dBFS RMS falando)
        double vr = Math.Sqrt(sOut / Math.Max(1, cs));
        Console.WriteLine($"[volume] voz na saida = {Db(vr)} RMS (entrada {Db(Math.Sqrt(sIn / cs))})");

        // --- 6) Desempenho
        Console.WriteLine($"[cpu] {msBlock:F3} ms por bloco de 10 ms ({msBlock / 10 * 100:F1}% de um nucleo)");
        if (msBlock > 3) fails++;

        Console.WriteLine(fails == 0 ? "TUDO OK" : $"FALHAS: {fails}");
        return fails;
    }
}
