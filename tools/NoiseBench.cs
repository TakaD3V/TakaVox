// Laboratorio de ruido: fala real (TTS do Windows) + teclado mecanico + cliques de mouse + ventoinha.
// Mede cada motor de IA: atraso, quanto sobra dos cliques nas pausas, qualidade da voz (SI-SDR) e CPU.
// Tambem testa a precisao do Auto-Tune. Salva WAVs para ouvir em <pasta de saida>.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using TakaVox.Dsp;

static class NoiseBench
{
    const int Fs = 48000;
    static readonly Random rnd = new Random(42);
    static float ClickScale = 0.12f; // cliques com pico ~ -12 dBFS (teclado mecanico perto do mic)
    static float G() { double u1 = 1.0 - rnd.NextDouble(), u2 = rnd.NextDouble(); return (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)); }

    static float[] Tts(string text, int rate)
    {
        using (var s = new SpeechSynthesizer())
        using (var ms = new MemoryStream())
        {
            foreach (var v in s.GetInstalledVoices()) if (v.VoiceInfo.Culture.Name == "pt-BR") { s.SelectVoice(v.VoiceInfo.Name); break; }
            s.Rate = rate;
            s.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Fs, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            s.Speak(text);
            var b = ms.ToArray();
            var x = new float[b.Length / 2];
            for (int i = 0; i < x.Length; i++) x[i] = (short)(b[2 * i] | (b[2 * i + 1] << 8)) / 32768f;
            return x;
        }
    }

    // um "clique" de tecla/mouse: excitacao curta passando por ressonancias com decaimento rapido
    static void AddClick(float[] y, int at, float amp, double f1, double f2, double decayMs)
    {
        int len = (int)(decayMs * 8 * Fs / 1000);
        var r1 = new Biquad(); r1.Set(FilterType.Peak, Fs, f1, 6, 18);
        var r2 = new Biquad(); r2.Set(FilterType.Peak, Fs, f2, 5, 14);
        var hp = new Biquad(); hp.Set(FilterType.HighPass, Fs, 400, 0.7, 0);
        double tau = decayMs * Fs / 1000;
        for (int i = 0; i < len && at + i < y.Length; i++)
        {
            float exc = i < 48 ? G() : G() * 0.15f;
            float v = hp.Process(r2.Process(r1.Process(exc)));
            y[at + i] += (float)(amp * v * Math.Exp(-i / tau));
        }
    }

    static float Rms(float[] x, int a, int b) { double s = 0; for (int i = a; i < b; i++) s += x[i] * x[i]; return (float)Math.Sqrt(s / Math.Max(1, b - a)); }
    static string Db(double v) => (20 * Math.Log10(Math.Max(v, 1e-9))).ToString("F1");

    static void SaveWav(string path, float[] x)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int n = x.Length;
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + n * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(n * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(v * 32767))));
        }
    }

    public static List<float> SnrLog = new List<float>(), VadLog = new List<float>();
    public static List<bool> GateLog = new List<bool>();

    static float[] Run(ChainParams p, float[] input, out double msPerBlock, out int latency)
    {
        var c = new VoiceChain(p, true);
        latency = c.LatencySamples;
        var y = new float[input.Length];
        var blk = new float[VoiceChain.Block];
        SnrLog.Clear(); VadLog.Clear(); GateLog.Clear();
        var sw = Stopwatch.StartNew(); int blocks = 0;
        for (int i = 0; i + VoiceChain.Block <= input.Length; i += VoiceChain.Block)
        {
            Array.Copy(input, i, blk, 0, VoiceChain.Block);
            c.Process(blk);
            Array.Copy(blk, 0, y, i, VoiceChain.Block);
            SnrLog.Add(c.SnrDb); VadLog.Add(c.Vad); GateLog.Add(c.GateOpen);
            blocks++;
        }
        msPerBlock = sw.Elapsed.TotalMilliseconds / Math.Max(1, blocks);
        Console.WriteLine("   motor: " + c.EngineName + (c.EngineError.Length > 0 ? "  (" + c.EngineError + ")" : ""));
        c.Dispose();
        return y;
    }

    static int MeasureLag(float[] clean, float[] y, int maxLag)
    {
        int best = 0; double bestC = double.MinValue;
        for (int lag = 0; lag <= maxLag; lag += 1)
        {
            double c = 0, e = 0;
            for (int i = 20000; i < clean.Length - maxLag - 20000; i += 7) { c += clean[i] * y[i + lag]; e += y[i + lag] * y[i + lag]; }
            double nc = c / Math.Sqrt(e + 1e-12);
            if (nc > bestC) { bestC = nc; best = lag; }
        }
        return best;
    }

    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "bench";
        Directory.CreateDirectory(outDir);

        // --- fala com pausas
        string[] frases =
        {
            "Bora jogar, mano! Hoje a gente sobe de elo.",
            "Cuidado, tem dois no bomb A, um deles está sem vida.",
            "Testando o microfone com teclado e mouse ligados.",
            "Se o ruído sumir, a inteligência artificial está funcionando.",
        };
        var speech = new List<float>();
        foreach (var f in frases)
        {
            speech.AddRange(new float[(int)(1.4 * Fs)]); // pausa
            speech.AddRange(Tts(f, 1));
        }
        speech.AddRange(new float[(int)(1.4 * Fs)]);
        var clean = speech.ToArray();
        float pk = 0; foreach (var v in clean) pk = Math.Max(pk, Math.Abs(v));
        for (int i = 0; i < clean.Length; i++) clean[i] *= 0.5f / pk; // pico -6 dBFS

        // --- ruidos
        var noise = new float[clean.Length];
        // teclado: ~7 teclas/s, cada tecla = descida + subida
        for (double t = 0.3; t < clean.Length / (double)Fs - 0.3; t += 0.09 + rnd.NextDouble() * 0.12)
        {
            int at = (int)(t * Fs);
            float a = ClickScale * (0.25f + (float)rnd.NextDouble() * 0.25f);
            AddClick(noise, at, a, 2500 + rnd.Next(1500), 6500 + rnd.Next(2500), 4 + rnd.NextDouble() * 6);
            AddClick(noise, at + (int)((0.05 + rnd.NextDouble() * 0.05) * Fs), a * 0.6f, 3000 + rnd.Next(1500), 7000 + rnd.Next(2000), 3 + rnd.NextDouble() * 4);
        }
        // mouse: cliques secos a cada ~0.7 s
        for (double t = 0.5; t < clean.Length / (double)Fs - 0.3; t += 0.4 + rnd.NextDouble() * 0.6)
        {
            int at = (int)(t * Fs);
            AddClick(noise, at, ClickScale * 0.35f, 4200 + rnd.Next(800), 9000, 1.5);
            if (rnd.NextDouble() < 0.3) AddClick(noise, at + (int)(0.11 * Fs), ClickScale * 0.3f, 4200, 9000, 1.5);
        }
        // ventoinha + zumbido
        var lp = new Biquad(); lp.Set(FilterType.LowPass, Fs, 1800, 0.7, 0);
        for (int i = 0; i < noise.Length; i++) noise[i] += lp.Process(G()) * 0.004f + 0.0008f * (float)Math.Sin(2 * Math.PI * 120 * i / Fs);
        var noisy = new float[clean.Length];
        for (int i = 0; i < clean.Length; i++) noisy[i] = clean[i] + noise[i];
        SaveWav(Path.Combine(outDir, "0_original_com_ruido.wav"), noisy);
        SaveWav(Path.Combine(outDir, "0_voz_limpa_referencia.wav"), clean);

        // regioes de pausa (sem voz num raio de 60 ms) e de fala
        var pause = new bool[clean.Length];
        var env = new float[clean.Length];
        for (int i = 0; i < clean.Length; i += 480) { float r = Rms(clean, i, Math.Min(clean.Length, i + 480)); for (int k = i; k < Math.Min(clean.Length, i + 480); k++) env[k] = r; }
        for (int i = 0; i < clean.Length; i++)
        {
            bool quiet = true;
            for (int k = -2880; k <= 2880 && quiet; k += 480) { int j = i + k; if (j >= 0 && j < clean.Length && env[j] > 0.003f) quiet = false; }
            pause[i] = quiet;
        }

        Console.WriteLine($"Teste: {clean.Length / (double)Fs:0.0} s de fala (TTS pt-BR) + teclado + mouse + ventoinha");
        double inPauseRms = 0; int np = 0; float inPausePeak = 0;
        for (int i = 0; i < clean.Length; i++) if (pause[i]) { inPauseRms += noisy[i] * noisy[i]; np++; inPausePeak = Math.Max(inPausePeak, Math.Abs(noisy[i])); }
        Console.WriteLine($"Nas pausas (sem voz): ruido RMS {Db(Math.Sqrt(inPauseRms / np))} dBFS, pico dos cliques {Db(inPausePeak)} dBFS");
        Console.WriteLine($"SI-SDR da entrada (voz vs ruido): {SiSdr(clean, noisy, 0, clean):F1} dB");
        Console.WriteLine();

        var configs = new List<(string name, string file, Action<ChainParams> set)>
        {
            ("RNNoise + portão (versão anterior)", "1_rnnoise", q => { q.NoiseEngine = 2; }),
            ("DeepFilterNet3 + portão (NOVO)", "2_deepfilternet3", q => { q.NoiseEngine = 0; }),
            ("DeepFilterNet3 baixa latência + portão", "3_deepfilternet3_ll", q => { q.NoiseEngine = 1; }),
            ("DeepFilterNet3 + limpeza extra + portão", "4_deepfilternet3_extra", q => { q.NoiseEngine = 0; q.NoiseExtra = true; }),
            ("DIAG: DF3 com IA desligada (deve ser transparente)", "diag_df3_off", q => { q.NoiseEngine = 0; q.NoiseOn = false; q.GateOn = false; }),
            ("DIAG: DF3 sem portão", "diag_df3_nogate", q => { q.NoiseEngine = 0; q.GateOn = false; }),
            ("DIAG: DF3 atenuação 30 dB sem portão", "diag_df3_30db", q => { q.NoiseEngine = 0; q.GateOn = false; q.NoiseMix = 0.68f; }),
            ("DIAG: DF3 baixa latência sem portão", "diag_ll_nogate", q => { q.NoiseEngine = 1; q.GateOn = false; }),
        };
        foreach (var (name, file, set) in configs)
        {
            var p = new ChainParams();
            p.InputGainDb = 0; p.AgcOn = false; p.EqOn = false; p.CompOn = false; p.DeessOn = false; p.OutputGainDb = 0;
            p.GateRangeDb = -40; p.GateThresholdDb = -50;
            set(p); p.Sanitize(); p.Touch();
            Console.WriteLine("== " + name);
            var y = Run(p, noisy, out double ms, out int lat);
            int lag = MeasureLag(clean, y, 6000);
            double pr = 0; int pn = 0; float ppk = 0;
            for (int i = 0; i + lag < y.Length; i++) if (pause[i]) { float v = y[i + lag]; pr += v * v; pn++; ppk = Math.Max(ppk, Math.Abs(v)); }
            // referencia justa: a voz limpa passando pelo mesmo filtro de graves da cadeia
            var hp1 = new Biquad(); hp1.Set(FilterType.HighPass, Fs, p.HpfFreq, 0.54119610, 0);
            var hp2 = new Biquad(); hp2.Set(FilterType.HighPass, Fs, p.HpfFreq, 1.30656296, 0);
            var refc = new float[clean.Length];
            for (int i = 0; i < clean.Length; i++) refc[i] = hp2.Process(hp1.Process(clean[i]));
            // alinhamento fino (amostra exata) contra a referencia filtrada
            double sdr = double.MinValue; int bestLag = lag;
            for (int dl = -12; dl <= 12; dl++) { double s = SiSdr(refc, y, Math.Max(0, lag + dl), clean); if (s > sdr) { sdr = s; bestLag = Math.Max(0, lag + dl); } }
            // energia por faixa (saida / referencia) nos trechos com voz
            var fft = new Fft(2048); var bufA = new float[2048]; var bufB = new float[2048]; var dA = new float[1025]; var dB = new float[1025];
            double[] edges = { 80, 250, 500, 1000, 2000, 4000, 8000, 16000 }; var eA = new double[7]; var eB = new double[7];
            for (int s0 = 0; s0 + 2048 + bestLag < y.Length; s0 += 1024)
            {
                if (env[Math.Min(clean.Length - 1, s0 + 1024)] < 0.02f) continue;
                for (int k = 0; k < 2048; k++) { bufA[k] = refc[s0 + k]; bufB[k] = y[s0 + k + bestLag]; }
                fft.PowerDb(bufA, dA); fft.PowerDb(bufB, dB);
                for (int k = 1; k < 1025; k++)
                {
                    double f = k * 24000.0 / 1024;
                    for (int bi = 0; bi < 7; bi++) if (f >= edges[bi] && f < edges[bi + 1]) { eA[bi] += Math.Pow(10, dA[k] / 10); eB[bi] += Math.Pow(10, dB[k] / 10); }
                }
            }
            var sbands = new System.Text.StringBuilder("   faixas (saída/ref): ");
            for (int bi = 0; bi < 7; bi++) sbands.Append($"{edges[bi] / 1000:0.##}k:{10 * Math.Log10((eB[bi] + 1e-20) / (eA[bi] + 1e-20)):+0.0;-0.0} ");
            Console.WriteLine(sbands.ToString());
            Console.WriteLine($"   atraso medido {lag} amostras ({lag / 48.0:0.0} ms) | informado {lat}");
            Console.WriteLine($"   pausas: ruido RMS {Db(Math.Sqrt(pr / Math.Max(1, pn)))} dBFS, pico que sobrou {Db(ppk)} dBFS");
            Console.WriteLine($"   qualidade da voz (SI-SDR): {sdr:F1} dB   | CPU {ms:0.00} ms/bloco ({ms / 10 * 100:0.0}% de 1 núcleo)");
            // estatistica por bloco: pausas x fala (o bloco b descreve a entrada em torno de b*480 - atraso da IA)
            int pb = 0, pOpen = 0, sb = 0, sOpen = 0; var snrP = new List<float>(); var snrS = new List<float>();
            for (int b = 0; b < GateLog.Count; b++)
            {
                int t = b * 480 - (lat - 160); // aproxima o instante de entrada correspondente
                if (t < 0 || t >= clean.Length) continue;
                bool isPause = pause[t];
                bool isSpeech = env[t] > 0.02f;
                if (isPause) { pb++; if (GateLog[b]) pOpen++; if (!float.IsNaN(SnrLog[b])) snrP.Add(SnrLog[b]); }
                else if (isSpeech) { sb++; if (GateLog[b]) sOpen++; if (!float.IsNaN(SnrLog[b])) snrS.Add(SnrLog[b]); }
            }
            snrP.Sort(); snrS.Sort();
            string Q(List<float> l, double q) => l.Count == 0 ? "-" : l[(int)(q * (l.Count - 1))].ToString("0.0");
            Console.WriteLine($"   portão aberto: pausas {100.0 * pOpen / Math.Max(1, pb):0}% | fala {100.0 * sOpen / Math.Max(1, sb):0}%");
            if (snrP.Count > 0) Console.WriteLine($"   SNR da IA: pausas p50={Q(snrP, .5)} p90={Q(snrP, .9)} max={Q(snrP, 1)} | fala p10={Q(snrS, .1)} p50={Q(snrS, .5)}");
            var aligned = new float[y.Length];
            for (int i = 0; i + lag < y.Length; i++) aligned[i] = y[i + lag];
            SaveWav(Path.Combine(outDir, file + ".wav"), aligned);
        }

        // --- voz limpa (sem ruido): um bom modelo nao deve mexer
        foreach (int eng in new[] { 0, 1, 2 })
        {
            var q = new ChainParams();
            q.InputGainDb = 0; q.AgcOn = false; q.EqOn = false; q.CompOn = false; q.DeessOn = false; q.OutputGainDb = 0; q.GateOn = false;
            q.NoiseEngine = eng; q.Touch();
            Console.WriteLine("== Voz SEM ruído: " + VoiceChain.EngineNames[eng]);
            var yc = Run(q, clean, out _, out int latc);
            var h1 = new Biquad(); h1.Set(FilterType.HighPass, Fs, q.HpfFreq, 0.54119610, 0);
            var h2 = new Biquad(); h2.Set(FilterType.HighPass, Fs, q.HpfFreq, 1.30656296, 0);
            var rc = new float[clean.Length];
            for (int i = 0; i < clean.Length; i++) rc[i] = h2.Process(h1.Process(clean[i]));
            int lg = MeasureLag(rc, yc, 6000);
            double best = double.MinValue;
            for (int dl = -12; dl <= 12; dl++) best = Math.Max(best, SiSdr(rc, yc, Math.Max(0, lg + dl), clean));
            Console.WriteLine($"   SI-SDR voz limpa: {best:F1} dB (quanto maior, menos a IA mexe na voz)");
        }

        // --- Auto-Tune: tom sintetico desafinado (+35 cents acima de A3)
        Console.WriteLine();
        Console.WriteLine("== Auto-Tune");
        double f0 = 220.0 * Math.Pow(2, 35 / 1200.0);
        var tone = new float[Fs * 3];
        double ph = 0;
        for (int i = 0; i < tone.Length; i++)
        {
            ph += 2 * Math.PI * f0 / Fs;
            double s = 0; for (int h = 1; h <= 20; h++) s += Math.Sin(h * ph) / h * Math.Exp(-Math.Pow((h * f0 - 700) / 900, 2));
            tone[i] = (float)(0.3 * s);
        }
        var at2 = new AutoTune();
        var tp = new ChainParams { TuneOn = true, TuneKey = 9, TuneScale = 0, TuneSpeed = 0, TuneAmount = 100 };
        var tout = new float[tone.Length];
        var b2 = new float[480];
        for (int i = 0; i + 480 <= tone.Length; i += 480) { Array.Copy(tone, i, b2, 0, 480); at2.Process(b2, 480, tp); Array.Copy(b2, 0, tout, i, 480); }
        double inHz = MeasurePitch(tone, Fs, Fs + 24000), outHz = MeasurePitch(tout, Fs, Fs + 24000);
        Console.WriteLine($"   entrada {inHz:0.00} Hz ({1200 * Math.Log(inHz / 220, 2):+0;-0} cents de A3) -> saida {outHz:0.00} Hz ({1200 * Math.Log(outHz / 220, 2):+0;-0} cents)");
        Console.WriteLine($"   nota detectada: {AutoTune.NoteName(at2.TargetMidi)}");
        float jump = 0; for (int i = Fs; i < tout.Length - 1; i++) jump = Math.Max(jump, Math.Abs(tout[i + 1] - tout[i]));
        float maxIn = 0; for (int i = Fs; i < tone.Length - 1; i++) maxIn = Math.Max(maxIn, Math.Abs(tone[i + 1] - tone[i]));
        Console.WriteLine($"   maior salto entre amostras: saida {jump:0.000} x entrada {maxIn:0.000} (estalo se bem maior)");
        SaveWav(Path.Combine(outDir, "5_autotune_antes.wav"), tone);
        SaveWav(Path.Combine(outDir, "5_autotune_depois.wav"), tout);
        // Auto-Tune na fala real (robo) para ouvir
        var vt = new ChainParams(); vt.NoiseEngine = 0; vt.TuneOn = true; vt.TuneKey = 0; vt.TuneScale = 1; vt.TuneSpeed = 0; vt.Touch();
        var ytune = Run(vt, clean, out _, out _);
        SaveWav(Path.Combine(outDir, "6_fala_com_autotune_robo.wav"), ytune);
        return 0;
    }

    static double MeasurePitch(float[] x, int start, int end)
    {
        int minLag = 48000 / 800, maxLag = 48000 / 70, win = 4096;
        double best = 0; int bestLag = minLag;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double c = 0, e1 = 0, e2 = 0;
            for (int i = start; i < start + win; i++) { c += x[i] * x[i + lag]; e1 += x[i] * x[i]; e2 += x[i + lag] * x[i + lag]; }
            double nc = c / Math.Sqrt(e1 * e2 + 1e-12);
            if (nc > best) { best = nc; bestLag = lag; }
        }
        // refina com parabola
        Func<int, double> nccf = lag => { double c = 0, e1 = 0, e2 = 0; for (int i = start; i < start + win; i++) { c += x[i] * x[i + lag]; e1 += x[i] * x[i]; e2 += x[i + lag] * x[i + lag]; } return c / Math.Sqrt(e1 * e2 + 1e-12); };
        double a = nccf(bestLag - 1), b = nccf(bestLag), cc = nccf(bestLag + 1);
        double d = (a - cc) / (2 * (a - 2 * b + cc));
        return 48000.0 / (bestLag + (double.IsNaN(d) ? 0 : d));
    }

    static double SiSdr(float[] clean, float[] y, int lag, float[] activityRef)
    {
        double ss = 0, sy = 0;
        int n = Math.Min(clean.Length, y.Length - lag);
        for (int i = 0; i < n; i++) { if (Math.Abs(activityRef[i]) < 1e-6) continue; ss += clean[i] * clean[i]; sy += clean[i] * y[i + lag]; }
        double alpha = sy / (ss + 1e-12);
        double t2 = 0, e2 = 0;
        for (int i = 0; i < n; i++) { if (Math.Abs(activityRef[i]) < 1e-6) continue; double t = alpha * clean[i]; double e = y[i + lag] - t; t2 += t * t; e2 += e * e; }
        return 10 * Math.Log10(t2 / (e2 + 1e-12));
    }
}
