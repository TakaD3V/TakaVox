// Teste da VOZ COM IA dentro da cadeia de voz do TakaVox (VoiceChain + AiVoice + Beatrice), como o app usa:
//  1) instalacao: importa os zips baixados do site oficial para uma pasta de dados de teste;
//  2) liga a IA (carga em segundo plano), processa fala masculina e confere tom de saida, volume, CPU e atraso;
//  3) aprende o tom medio de quem fala (o medidor) e casa com a voz alvo;
//  4) troca de voz no mesmo modelo e de modelo (oficial <-> JVS);
//  5) liga/desliga a IA no meio da fala: cruza sem estalo e volta para a voz normal;
//  6) IA quebrada (modelo com arquivos estragados): sai sozinha e a voz normal volta (o mic nunca fica mudo);
//  7) efeitos (reverb) por cima da IA.
// Uso: aichaintest.exe <zip do plugin> <zip do modelo> [pasta-wavs]   (TAKAVOX_DATA = pasta de dados do teste)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TakaVox;
using TakaVox.Dsp;
using TakaVox.Plugins;

static class AiChainTest
{
    const int Fs = 48000, B = 480;
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok     " : "  FALHA  ") + what);
        if (!ok) fails++;
    }

    static float[] Tts(string text)
    {
        string wav = Path.Combine(Path.GetTempPath(), "takavox_aichain_tts.wav");
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
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            int len = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data") { dataOff = pos + 8; dataLen = Math.Min(len, bytes.Length - dataOff); break; }
            pos += 8 + len + (len & 1);
        }
        var x = new float[dataLen / 2];
        for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(bytes, dataOff + 2 * i) / 32768f;
        return x;
    }

    static void SaveWav(string path, float[] x)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + x.Length * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(v * 32767))));
        }
    }

    static float[] PitchTrack(float[] x)
    {
        int n = x.Length / 4;
        var d = new float[n];
        for (int i = 0; i < n; i++) d[i] = (x[4 * i] + x[4 * i + 1] + x[4 * i + 2] + x[4 * i + 3]) * 0.25f;
        const int W = 480, MinLag = 14, MaxLag = 240, Hop = 120;
        int frames = Math.Max(0, (n - W - MaxLag) / Hop);
        var f0 = new float[frames];
        var df = new double[MaxLag + 1];
        var cm = new double[MaxLag + 1];
        for (int f = 0; f < frames; f++)
        {
            int s = f * Hop;
            double e = 0;
            for (int i = 0; i < W; i++) e += d[s + i] * d[s + i];
            if (e / W < 1e-5) continue;
            for (int tau = 1; tau <= MaxLag; tau++)
            {
                double sum = 0;
                for (int i = 0; i < W; i++) { double v = d[s + i] - d[s + i + tau]; sum += v * v; }
                df[tau] = sum;
            }
            double run = 0;
            for (int tau = 1; tau <= MaxLag; tau++) { run += df[tau]; cm[tau] = run > 0 ? df[tau] * tau / run : 1; }
            for (int tau = MinLag; tau <= MaxLag; tau++)
                if (cm[tau] < 0.15) { while (tau + 1 <= MaxLag && cm[tau + 1] < cm[tau]) tau++; f0[f] = 12000f / tau; break; }
        }
        return f0;
    }

    static double MedianMidi(float[] f0, int from = 0, int to = int.MaxValue)
    {
        var m = f0.Skip(from).Take(Math.Max(0, Math.Min(to, f0.Length) - from)).Where(v => v > 0).Select(v => Beatrice.HzToMidi(v)).OrderBy(v => v).ToArray();
        return m.Length == 0 ? 0 : m[m.Length / 2];
    }

    static double Rms(float[] x, int a = 0, int b = -1)
    {
        if (b < 0) b = x.Length;
        double s = 0;
        for (int i = a; i < b; i++) s += x[i] * (double)x[i];
        return Math.Sqrt(s / Math.Max(1, b - a));
    }
    static double Db(double v) => 20 * Math.Log10(Math.Max(1e-9, v));

    [System.Runtime.InteropServices.DllImport("avrt.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

    sealed class Rig
    {
        public ChainParams P = new ChainParams();
        public VoiceChain Chain;
        public AiVoice Ai = new AiVoice();
        public List<BeatriceModel> Models;
        public string Plugin;
        public List<double> Ms = new List<double>();
        int sinceUpdate;

        public Rig(bool offline = true)
        {
            P.VcOn = false;
            Chain = new VoiceChain(P, offline) { Ai = Ai };
            Plugin = Beatrice.FindPlugin();
            Models = BeatriceModel.LoadAll(Beatrice.ModelsDir);
        }

        public void Update() => Ai.Update(P, Models, Plugin);

        /// <summary>Espera a IA ficar pronta (carga em segundo plano), alimentando silencio como o app faria.</summary>
        public bool WaitReady(int ms = 5000)
        {
            var sw = Stopwatch.StartNew();
            var z = new float[B];
            while (sw.ElapsedMilliseconds < ms)
            {
                Update();
                if (Ai.Ready) { for (int i = 0; i < 5; i++) { Array.Clear(z, 0, B); Chain.Process(z); } return Chain.AiActive || true; }
                Array.Clear(z, 0, B);
                Chain.Process(z);
                Thread.Sleep(10);
            }
            return false;
        }

        /// <summary>Processa x pela cadeia (a "interface" atualiza a IA a cada 250 ms de audio). onBlock pode mudar P no meio.</summary>
        public float[] Run(float[] x, Action<int> onBlock = null)
        {
            var y = new float[x.Length];
            var blk = new float[B];
            for (int i = 0, k = 0; i + B <= x.Length; i += B, k++)
            {
                onBlock?.Invoke(k);
                if (++sinceUpdate >= 25) { sinceUpdate = 0; Update(); }
                Array.Copy(x, i, blk, 0, B);
                long t0 = Stopwatch.GetTimestamp();
                Chain.Process(blk);
                Ms.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                Array.Copy(blk, 0, y, i, B);
            }
            return y;
        }
    }

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("uso: aichaintest <zip do plugin> <zip do modelo> [pasta-wavs]"); return 2; }
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        string wavDir = args.Length > 2 ? args[2] : null;
        if (wavDir != null) Directory.CreateDirectory(wavDir);
        Console.WriteLine("dados de teste: " + Log.Dir);

        // ------------------------------------------------------------ 1) instalacao
        Console.WriteLine("[instalação]");
        try { if (Directory.Exists(Beatrice.Root)) Directory.Delete(Beatrice.Root, true); } catch { }
        Check(!Beatrice.Installed, "começa sem nada instalado");
        string msg = Beatrice.Import(new[] { args[0], args[1] }, out int plugs, out int models);
        Console.WriteLine("   " + msg);
        Check(plugs == 1 && models == 2, $"importou 1 plugin e 2 modelos (oficial + JVS que vem junto do plugin): {plugs} e {models}");
        Check(Beatrice.Installed && Beatrice.FindPlugin() != null, "instalado: " + Beatrice.PluginVersion(Beatrice.FindPlugin()));
        string msg2 = Beatrice.Import(new[] { args[0], args[1] }, out plugs, out models);
        Check(plugs == 1 && models == 2, "importar de novo não quebra nada");

        // ------------------------------------------------------------ fala de teste (masculina, ~117 Hz)
        var female = Tts("Olá pessoal, tudo bem com vocês? Hoje eu vou testar a voz nova no Discord. " +
                         "Será que ficou parecida com uma garota de anime de verdade? Vamos jogar uma partida rápida e ver o que a galera acha. " +
                         "Atenção, tem dois inimigos no bomb A, eu vou entrar pelo meio com a smoke.");
        var male = new float[female.Length];
        {
            var tune = new AutoTune();
            var cp = new ChainParams { VcOn = true, VcPitch = -8f, VcFormant = -18f };
            var blk = new float[B];
            for (int i = 0; i + B <= female.Length; i += B)
            {
                Array.Copy(female, i, blk, 0, B);
                tune.Process(blk, B, cp);
                Array.Copy(blk, 0, male, i, B);
            }
        }
        // 1 s de silencio antes (o portao/IA anti-ruido acomodam)
        var input = new float[Fs + male.Length];
        Array.Copy(male, 0, input, Fs, male.Length);
        var f0in = PitchTrack(input);
        double srcMidi = MedianMidi(f0in);
        Console.WriteLine($"   fala: {male.Length / (double)Fs:0.0} s, tom médio {Beatrice.MidiToHz(srcMidi):0} Hz");

        // ------------------------------------------------------------ 2) IA ligada
        Console.WriteLine("[voz com IA ligada]");
        var rig = new Rig();
        var official = rig.Models.First(m => !m.IsJvs);
        var jvs = rig.Models.FirstOrDefault(m => m.IsJvs);
        rig.P.VcOn = true; rig.P.VcAiOn = true; rig.P.VcAiModel = official.Code; rig.P.VcAiVoice = 1; // Tsukuyomi (natural)
        rig.P.Touch();
        rig.Ai.SourceMidi = 48; // ainda nao sabe o tom de quem fala (padrao)
        var sw = Stopwatch.StartNew();
        Check(rig.WaitReady(), $"IA carregou em segundo plano ({sw.ElapsedMilliseconds} ms; plugin {rig.Ai.LoadMs} ms), estado '{rig.Ai.State}'");
        var y1 = rig.Run(input);
        var f0a = PitchTrack(y1);
        double outA = MedianMidi(f0a, f0a.Length / 2); // segunda metade: o medidor ja aprendeu o tom de quem fala
        var tgt = official.Voices[1];
        Console.WriteLine($"   medidor: {rig.Ai.MeterFrames} quadros de voz, tom médio {Beatrice.MidiToHz(rig.Ai.MeterMedian):0} Hz (real {Beatrice.MidiToHz(srcMidi):0} Hz); usado {Beatrice.MidiToHz(rig.Ai.SourceMidi):0} Hz");
        Check(Math.Abs(rig.Ai.MeterMedian - srcMidi) < 1.0, $"o medidor aprendeu o tom de quem fala (erro {rig.Ai.MeterMedian - srcMidi:+0.0;-0.0} st)");
        Check(rig.Ai.SourceChanged && Math.Abs(rig.Ai.SourceMidi - srcMidi) < 1.0, "e passou a usar esse tom (o app salva nos ajustes)");
        Check(Math.Abs(outA - tgt.AveragePitch) < 1.5, $"saída no tom da Tsukuyomi: {Beatrice.MidiToHz(outA):0} Hz (alvo {tgt.AverageHz:0} Hz)");
        Check(rig.Chain.AiActive, "a cadeia está saindo pela IA");
        double inDb = Db(Rms(input, Fs)), outDb = Db(Rms(y1, Fs));
        Check(Math.Abs(outDb - inDb) < 8, $"volume: entrada {inDb:0.0} dBFS -> saída {outDb:0.0} dBFS (o nivelador e o compressor acertam)");
        Check(!y1.Any(v => float.IsNaN(v) || Math.Abs(v) > 1.0001f), "sem NaN nem estouro");
        // CPU em tempo real (um bloco a cada 10 ms, prioridade de audio como a thread do app): sem IA x com IA.
        // Cadeia no modo AO VIVO, como no app (a analise do removedor de ventoinha roda na thread dela).
        // AICHAIN_SKIP_PERF=1 pula esta parte (ex.: alguem jogando no PC: a medida nao vale e ainda atrapalha o jogo).
        if (Environment.GetEnvironmentVariable("AICHAIN_SKIP_PERF") != "1")
        {
            var live = new Rig(false);
            live.P.VcOn = true; live.P.VcAiOn = true; live.P.VcAiModel = official.Code; live.P.VcAiVoice = 1; live.P.Touch();
            live.WaitReady();
            var warm = new float[B];
            for (int k = 0; k < 600 && live.Chain.ActiveEngine != 1; k++) { Array.Clear(warm, 0, B); live.Chain.Process(warm); Thread.Sleep(10); }
            Console.WriteLine("   motor anti-ruído ao vivo: " + live.Chain.EngineName);
            uint task = 0;
            AvSetMmThreadCharacteristicsW("Pro Audio", ref task);
            timeBeginPeriod(1);
            double[] Paced(bool ai)
            {
                live.P.VcAiOn = ai; live.P.Touch(); live.Update();
                var t = new List<double>(4000);
                var blk = new float[B];
                long next = Stopwatch.GetTimestamp();
                long period = Stopwatch.Frequency / 100;
                for (int i = Fs, k = 0; i + B <= input.Length && k < 1200; i += B, k++)
                {
                    if (k % 25 == 0) live.Update();
                    Array.Copy(input, i, blk, 0, B);
                    long t0 = Stopwatch.GetTimestamp();
                    live.Chain.Process(blk);
                    if (k >= 30) t.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    next += period;
                    while (Stopwatch.GetTimestamp() < next - Stopwatch.Frequency / 1000) Thread.Sleep(1);
                    while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
                }
                return t.OrderBy(v => v).ToArray();
            }
            var off = Paced(false);
            long w0 = live.Ai.Waits, wt0 = live.Ai.WaitTicks, l0 = live.Ai.Late;
            live.Ai.ReadMsPerBlock();
            var on = Paced(true);
            double workerMs = live.Ai.ReadMsPerBlock();
            Console.WriteLine($"   thread da IA: {workerMs:0.00} ms/bloco (pior {live.Ai.MaxMs:0.0} ms); a thread de áudio esperou {live.Ai.Waits - w0} vezes " +
                              $"({(live.Ai.WaitTicks - wt0) * 1000.0 / Stopwatch.Frequency:0} ms no total), atrasou {live.Ai.Late - l0} blocos, pulou {live.Ai.Skipped}");
            timeEndPeriod(1);
            Console.WriteLine($"   tempo real, SEM IA: média {off.Average():0.00} ms/bloco, p99 {off[(int)(off.Length * 0.99)]:0.0}, pior {off.Last():0.0}");
            Console.WriteLine($"   tempo real, COM IA: média {on.Average():0.00} ms/bloco, p99 {on[(int)(on.Length * 0.99)]:0.0}, pior {on.Last():0.0}, >10 ms: {on.Count(v => v > 10)}");
            Check(on.Average() - off.Average() < 4, $"a IA soma {on.Average() - off.Average():0.0} ms por bloco de 10 ms (~{(on.Average() - off.Average()) * 10:0}% de 1 núcleo)");
            Check(on[(int)(on.Length * 0.99)] < 10, "99% dos blocos cabem nos 10 ms com folga");
            live.Ai.Shutdown();
            live.Chain.Dispose();
        }
        int lat = rig.Chain.LatencySamples;
        Check(lat > AiVoice.Latency, $"atraso da cadeia informado {lat / 48.0:0} ms (inclui os {AiVoice.Latency / 48.0:0} ms da IA)");
        if (wavDir != null) { SaveWav(Path.Combine(wavDir, "cadeia_entrada.wav"), input); SaveWav(Path.Combine(wavDir, "cadeia_ia_tsukuyomi.wav"), y1); }

        // ------------------------------------------------------------ descanso: aquecer e voltar
        {
            var warm = new float[B];
            for (int k = 0; k < 6; k++) { Array.Copy(input, Fs + k * B, warm, 0, B); rig.Chain.WarmUp(warm); }
            var after = rig.Run(input);
            Check(!after.Any(v => float.IsNaN(v)) && Db(Rms(after, Fs)) > inDb - 8, "volta do descanso (aquecimento da IA anti-ruído) sem erro, volume normal");
        }

        // ------------------------------------------------------------ 4) troca de voz e de modelo
        Console.WriteLine("[troca de voz]");
        rig.P.VcAiVoice = 3; rig.P.Touch(); rig.Update(); // OLUNE
        var y2 = rig.Run(input);
        var f0b = PitchTrack(y2);
        double outB = MedianMidi(f0b, f0b.Length / 4);
        Check(Math.Abs(outB - official.Voices[3].AveragePitch) < 1.5, $"OLUNE: {Beatrice.MidiToHz(outB):0} Hz (alvo {official.Voices[3].AverageHz:0} Hz)");
        if (jvs != null)
        {
            var fem = jvs.Voices.OrderByDescending(v => v.AveragePitch).First();
            sw.Restart();
            rig.P.VcAiModel = jvs.Code; rig.P.VcAiVoice = fem.Index; rig.P.Touch(); rig.Update();
            long switchMs = sw.ElapsedMilliseconds;
            var y3 = rig.Run(input);
            var f0c = PitchTrack(y3);
            double outC = MedianMidi(f0c, f0c.Length / 4);
            Check(Math.Abs(outC - fem.AveragePitch) < 1.5, $"modelo JVS ({fem.Name}) trocado em {switchMs} ms: {Beatrice.MidiToHz(outC):0} Hz (alvo {fem.AverageHz:0} Hz)");
            rig.P.VcAiModel = official.Code; rig.P.VcAiVoice = 0; rig.P.Touch(); rig.Update();
            if (wavDir != null) SaveWav(Path.Combine(wavDir, "cadeia_ia_jvs.wav"), y3);
        }

        // ------------------------------------------------------------ 5) liga/desliga no meio da fala
        Console.WriteLine("[liga/desliga no meio da fala]");
        int offAt = (Fs + 8 * Fs) / B, onAt = (Fs + 15 * Fs) / B;
        var y4 = rig.Run(input, k =>
        {
            if (k == offAt) { rig.P.VcAiOn = false; rig.P.Touch(); rig.Update(); }
            if (k == onAt) { rig.P.VcAiOn = true; rig.P.Touch(); rig.Update(); }
        });
        var f0d = PitchTrack(y4);
        int fOff = offAt * B / 480, fOn = onAt * B / 480; // quadros de 10 ms
        double midOff = MedianMidi(f0d, fOff + 30, fOn);
        double midOn = MedianMidi(f0d, fOn + 60);
        Check(Math.Abs(midOff - srcMidi) < 1.5, $"desligada: voltou a voz normal ({Beatrice.MidiToHz(midOff):0} Hz)");
        Check(Math.Abs(midOn - official.Voices[0].AveragePitch) < 1.5, $"religada: Tsukuyomi de novo ({Beatrice.MidiToHz(midOn):0} Hz)");
        double jump = 0;
        foreach (int at in new[] { offAt, onAt })
            for (int i = at * B - 960; i < at * B + 1920; i++) jump = Math.Max(jump, Math.Abs(y4[i] - y4[i - 1]));
        double typical = 0;
        for (int i = Fs * 3; i < Fs * 7; i++) typical = Math.Max(typical, Math.Abs(y4[i] - y4[i - 1]));
        Check(jump <= typical * 1.2 + 0.02, $"sem estalo na troca (maior salto {jump:0.000}, normal da fala {typical:0.000})");
        if (wavDir != null) SaveWav(Path.Combine(wavDir, "cadeia_liga_desliga.wav"), y4);

        // ------------------------------------------------------------ 7) efeito por cima
        Console.WriteLine("[efeitos por cima da IA]");
        rig.P.VcReverbMix = 40; rig.P.VcReverbSize = 70; rig.P.Touch(); rig.Update();
        var y5 = rig.Run(input);
        double tail = Db(Rms(y5, input.Length - Fs / 2, input.Length)), tailDry = Db(Rms(y1, input.Length - Fs / 2, input.Length));
        Check(rig.Chain.AiActive && tail > tailDry + 6, $"reverb soma cauda depois da fala ({tailDry:0} -> {tail:0} dBFS) com a IA ligada");
        rig.P.VcReverbMix = 0; rig.P.Touch();

        // ------------------------------------------------------------ 6) IA quebrada
        Console.WriteLine("[IA quebrada]");
        string bad = Path.Combine(Beatrice.ModelsDir, "zz_quebrado");
        Directory.CreateDirectory(bad);
        foreach (var f in Directory.GetFiles(official.Dir)) File.Copy(f, Path.Combine(bad, Path.GetFileName(f)), true);
        foreach (var f in Directory.GetFiles(bad, "*.bin")) File.WriteAllBytes(f, new byte[64]); // pesos estragados
        var rig2 = new Rig();
        var broken = rig2.Models.First(m => m.Key == "zz_quebrado");
        string failed = null;
        rig2.Ai.Failed += r => failed = r;
        rig2.P.VcOn = true; rig2.P.VcAiOn = true; rig2.P.VcAiModel = broken.Code; rig2.P.Touch();
        bool ready = rig2.WaitReady();
        var y6 = rig2.Run(input);
        rig2.Update();
        double lastDb = Db(Rms(y6, y6.Length - 6 * Fs, y6.Length - Fs));
        Console.WriteLine($"   plugin abriu: {ready}; estado '{rig2.Ai.State}', erro '{rig2.Ai.Error}'");
        Check(failed != null || !ready, "a IA quebrada foi percebida (" + (failed ?? rig2.Ai.Error ?? "não carregou") + ")");
        Check(lastDb > inDb - 12, $"o microfone não ficou mudo: a voz normal saiu ({lastDb:0.0} dBFS no fim)");
        rig2.Ai.Shutdown();
        try { Directory.Delete(bad, true); } catch { }

        rig.Ai.Shutdown();
        rig.Chain.Dispose();
        rig2.Chain.Dispose();
        Console.WriteLine(fails == 0 ? "TUDO OK" : fails + " FALHA(S)");
        return fails == 0 ? 0 : 1;
    }
}
