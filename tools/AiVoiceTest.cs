// Teste da VOZ COM IA (Beatrice 2, VST3, hospedado pelo tvhost.dll do TakaVox).
//  1) modelos: le os .toml (nomes, tom medio, retratos);
//  2) plugin: carrega, lista os parametros (ID, nome, passos, texto) e confere as escalas (semitons, dB);
//  3) sem modelo: o plugin solta silencio (o app precisa perceber e voltar para a voz normal);
//  4) troca o modelo pelo estado (como o app faz), com o audio rodando: quanto tempo fica mudo;
//  5) fala: voz do Windows (Maria) abaixada para voz masculina (~120 Hz) pelo motor de tom do TakaVox;
//     para cada voz: tom = media da voz alvo - media de quem fala; processa em blocos de 10 ms como a thread de audio
//     e mede CPU por bloco (media e pior), atraso, volume, buracos e o tom de saida.
// Uso: aivoicetest.exe <pasta .vst3> <pasta de modelos> [pasta-para-wavs]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TakaVox.Dsp;
using TakaVox.Plugins;

static unsafe class AiVoiceTest
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
        string wav = Path.Combine(Path.GetTempPath(), "takavox_ai_tts.wav");
        using (var s = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            foreach (var v in s.GetInstalledVoices())
                if (v.VoiceInfo.Culture.Name.StartsWith("pt")) { s.SelectVoice(v.VoiceInfo.Name); break; }
            s.Rate = 1;
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

    /// <summary>Tom por quadro de 10 ms (YIN a 12 kHz; 0 = sem voz).</summary>
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
            if (e / W < 1e-5) continue; // abaixo de -50 dBFS: pausa
            for (int tau = 1; tau <= MaxLag; tau++)
            {
                double sum = 0;
                for (int i = 0; i < W; i++) { double v = d[s + i] - d[s + i + tau]; sum += v * v; }
                df[tau] = sum;
            }
            double run = 0;
            cm[0] = 1;
            for (int tau = 1; tau <= MaxLag; tau++) { run += df[tau]; cm[tau] = run > 0 ? df[tau] * tau / run : 1; }
            int best = -1;
            for (int tau = MinLag; tau <= MaxLag; tau++)
            {
                if (cm[tau] < 0.15)
                {
                    while (tau + 1 <= MaxLag && cm[tau + 1] < cm[tau]) tau++;
                    best = tau;
                    break;
                }
            }
            if (best > 0) f0[f] = 12000f / best;
        }
        return f0;
    }

    static double MedianMidi(float[] f0)
    {
        var m = f0.Where(v => v > 0).Select(v => Beatrice.HzToMidi(v)).OrderBy(v => v).ToArray();
        return m.Length == 0 ? 0 : m[m.Length / 2];
    }

    static double VoicedShare(float[] f0, float[] reference)
    {
        int both = 0, refV = 0;
        for (int i = 0; i < Math.Min(f0.Length, reference.Length); i++)
            if (reference[i] > 0) { refV++; if (f0[i] > 0) both++; }
        return refV == 0 ? 0 : both / (double)refV;
    }

    static double Rms(float[] x, int a = 0, int b = -1)
    {
        if (b < 0) b = x.Length;
        double s = 0;
        for (int i = a; i < b; i++) s += x[i] * (double)x[i];
        return Math.Sqrt(s / Math.Max(1, b - a));
    }
    static double Db(double v) => 20 * Math.Log10(Math.Max(1e-9, v));

    /// <summary>Atraso (ms) pela correlacao dos envelopes de 1 ms.</summary>
    static double LatencyMs(float[] a, float[] b)
    {
        int fr = 48, n = Math.Min(a.Length, b.Length) / fr;
        var ea = new double[n]; var eb = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sa = 0, sb = 0;
            for (int k = 0; k < fr; k++) { sa += Math.Abs(a[i * fr + k]); sb += Math.Abs(b[i * fr + k]); }
            ea[i] = sa; eb[i] = sb;
        }
        double ma = ea.Average(), mb = eb.Average();
        int best = 0; double bestC = double.MinValue;
        for (int lag = 0; lag <= 400; lag++)
        {
            double c = 0;
            for (int i = 0; i + lag < n; i++) c += (ea[i] - ma) * (eb[i + lag] - mb);
            if (c > bestC) { bestC = c; best = lag; }
        }
        return best;
    }

    sealed class RunStats { public float[] Y; public double MeanMs, MaxMs, P99Ms; public int Errors; }

    static RunStats Run(IntPtr h, float[] x)
    {
        var y = new float[x.Length];
        var buf = new float[B];
        var t = new List<double>();
        int errors = 0;
        fixed (float* p = buf)
        {
            for (int i = 0; i + B <= x.Length; i += B)
            {
                Array.Copy(x, i, buf, 0, B);
                long t0 = Stopwatch.GetTimestamp();
                if (Tvh.tvh_process(h, p, B) != 0) errors++;
                t.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                Array.Copy(buf, 0, y, i, B);
            }
        }
        var s = t.Skip(Math.Min(20, t.Count / 4)).OrderBy(v => v).ToArray();
        return new RunStats { Y = y, MeanMs = s.Average(), MaxMs = s.Last(), P99Ms = s[(int)(s.Length * 0.99)], Errors = errors };
    }

    static string Text(IntPtr h, int id, double norm)
    {
        var sb = new StringBuilder(128);
        return Tvh.tvh_param_text(h, id, norm, sb, sb.Capacity) == 1 ? sb.ToString() : "?";
    }

    static void SetPlain(IntPtr h, int id, double plain) => Tvh.tvh_param_set_id(h, id, Tvh.tvh_param_to_norm(h, id, plain));

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("uso: aivoicetest <pasta .vst3> <pasta de modelos> [pasta-wavs]"); return 2; }
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        { uint task0 = 0; AvSetMmThreadCharacteristicsW("Pro Audio", ref task0); } // como a thread da voz com IA do app
        string vst3 = args[0], modelsDir = args[1], wavDir = args.Length > 2 ? args[2] : null;
        if (wavDir != null) Directory.CreateDirectory(wavDir);

        // ------------------------------------------------------------ 1) modelos
        Console.WriteLine("[modelos]");
        foreach (var toml in Directory.GetFiles(modelsDir, "*.toml", SearchOption.AllDirectories))
            if (BeatriceModel.Load(toml, out var le) == null) Console.WriteLine("   não leu " + Path.GetFileName(toml) + ": " + le);
        var models = BeatriceModel.LoadAll(modelsDir);
        Check(models.Count >= 1, "modelos encontrados: " + models.Count);
        foreach (var m in models)
        {
            Console.WriteLine($"   {m.Name} ({m.Version}, {m.Voices.Count} vozes){(m.IsJvs ? " [JVS]" : "")}");
            foreach (var v in m.Voices.Take(m.IsJvs ? 3 : 99))
                Console.WriteLine($"     #{v.Index} {v.Name}: tom médio {v.AverageHz:0} Hz{(v.Portrait != null ? ", retrato " + Path.GetFileName(v.Portrait) : "")}");
        }
        var official = models.FirstOrDefault(m => !m.IsJvs);
        var jvs = models.FirstOrDefault(m => m.IsJvs);
        Check(official != null && official.Voices.Count == 4 && official.Voices[0].Name.Contains("つくよみ"), "modelo oficial com 4 vozes (Tsukuyomi-chan primeiro)");
        Check(official != null && official.Voices.All(v => v.Portrait != null), "retratos das vozes oficiais");
        Check(official != null && Math.Abs(official.Voices[0].AveragePitch - 65.625) < 1e-6, "tom médio lido do .toml (65,625)");

        // ------------------------------------------------------------ 2) plugin
        Console.WriteLine("[plugin]");
        Check(Tvh.Init(out var initErr), "tvhost iniciou " + (initErr ?? ""));
        var err = new StringBuilder(512);
        var sw = Stopwatch.StartNew();
        IntPtr h = Tvh.tvh_load(vst3, 0, Fs, B, null, 0, err, err.Capacity);
        Check(h != IntPtr.Zero, $"carregou o Beatrice em {sw.ElapsedMilliseconds} ms {err}");
        if (h == IntPtr.Zero) return 1;
        var nm = new StringBuilder(256);
        Tvh.tvh_get_name(h, nm, nm.Capacity);
        Console.WriteLine($"   nome '{nm}', canais {Tvh.tvh_channels(h)}, latência informada {Tvh.tvh_latency(h)} amostras");
        int pc = Tvh.tvh_param_count(h);
        Console.WriteLine($"   {pc} parâmetros:");
        var ids = new Dictionary<string, int>();
        for (int i = 0; i < pc; i++)
        {
            int id = Tvh.tvh_param_id(h, i);
            var pn = new StringBuilder(256); var pd = new StringBuilder(256);
            int r = Tvh.tvh_param_info(h, i, pn, pn.Capacity, pd, pd.Capacity, out double val, out int steps, out double def);
            if (r == 0) continue; // escondido
            ids[pn.ToString()] = id;
            if (i < 40) Console.WriteLine($"     [{i}] id {id} '{pn}' = {pd} (norm {val:0.###}, passos {steps}, padrão {def:0.###})");
        }
        Check(ids.TryGetValue("Pitch Shift", out int pidPitch) && pidPitch == Beatrice.PPitch, "Pitch Shift tem o ID esperado (4)");
        Check(ids.TryGetValue("Voice", out int pidVoice) && pidVoice == Beatrice.PVoice, "Voice tem o ID esperado (2)");
        Check(ids.TryGetValue("Formant Shift", out int pidF) && pidF == Beatrice.PFormant, "Formant Shift tem o ID esperado (3)");
        Check(ids.TryGetValue("Intonation Intensity", out int pidI) && pidI == Beatrice.PIntonation, "Intonation Intensity tem o ID esperado (9)");
        double n12 = Tvh.tvh_param_to_norm(h, Beatrice.PPitch, 12);
        Check(Math.Abs(n12 - 0.75) < 1e-6, $"+12 st -> {n12:0.####} normalizado (esperado 0,75), texto '{Text(h, Beatrice.PPitch, n12)}'");
        double nf = Tvh.tvh_param_to_norm(h, Beatrice.PFormant, 0.5);
        Check(Math.Abs(nf - 0.625) < 1e-6, $"timbre +0,5 st -> {nf:0.####} (esperado 0,625), texto '{Text(h, Beatrice.PFormant, nf)}'");
        double nv = Tvh.tvh_param_to_norm(h, Beatrice.PVoice, 3);
        Check(Math.Abs(Tvh.tvh_param_to_plain(h, Beatrice.PVoice, nv) - 3) < 1e-6, $"voz 3 -> {nv:0.#####} e volta 3, texto '{Text(h, Beatrice.PVoice, nv)}'");

        // ------------------------------------------------------------ 3) sem modelo = silencio
        Console.WriteLine("[sem modelo]");
        var noise = new float[Fs];
        var rnd = new Random(1);
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(Math.Sin(2 * Math.PI * 140 * i / Fs) * 0.2 + (rnd.NextDouble() - 0.5) * 0.02);
        var r0 = Run(h, noise);
        Check(Rms(r0.Y) < 1e-6, $"sem modelo o plugin solta silêncio ({Db(Rms(r0.Y)):0} dBFS) - o app precisa voltar para a voz normal");

        // ------------------------------------------------------------ 4) troca de modelo pelo estado, com o audio rodando
        Console.WriteLine("[modelo pelo estado]");
        byte[] st0 = GetState(h);
        Check(st0 != null && Beatrice.ModelOf(st0) == "", $"estado lido ({st0?.Length ?? 0} bytes), sem modelo");
        var nums = Beatrice.NumbersOf(st0);
        Console.WriteLine("   valores no estado: " + string.Join(", ", nums.Where(kv => kv.Key < 20).Select(kv => kv.Key + "=" + kv.Value.ToString("0.###"))));
        byte[] st1 = Beatrice.WithModel(st0, official.Toml);
        Check(Beatrice.ModelOf(st1) == official.Toml, "modelo escrito no estado");
        int silentBlocks = 0, blocks = 0;
        var buf = new float[B];
        var setTask = Task.Run(() => { var t0 = Stopwatch.StartNew(); int ok = Tvh.tvh_set_state(h, st1, st1.Length); return (ok, t0.ElapsedMilliseconds); });
        fixed (float* p = buf)
            while (!setTask.IsCompleted || blocks < 50)
            {
                for (int i = 0; i < B; i++) buf[i] = noise[(blocks * B + i) % noise.Length];
                Tvh.tvh_process(h, p, B);
                blocks++;
                double r = 0; for (int i = 0; i < B; i++) r += buf[i] * buf[i];
                if (r < 1e-12) silentBlocks++;
                Thread.Sleep(2);
                if (blocks > 3000) break;
            }
        var res = setTask.Result;
        Check(res.ok == 1, $"modelo carregado em {res.ElapsedMilliseconds} ms com o áudio rodando ({silentBlocks} de {blocks} blocos mudos no caminho)");
        byte[] st2 = GetState(h);
        Check(Beatrice.ModelOf(st2) == official.Toml, "o plugin guardou o modelo novo no estado");

        // ------------------------------------------------------------ 5) fala
        Console.WriteLine("[fala]");
        var female = Tts("Olá pessoal, tudo bem com vocês? Hoje eu vou testar a voz nova no Discord. " +
                         "Será que ficou parecida com uma garota de anime de verdade? Vamos jogar uma partida rápida e ver o que a galera acha. " +
                         "Atenção, tem dois inimigos no bomb A, eu vou entrar pelo meio com a smoke.");
        // voz masculina: a mesma fala com o motor de tom do TakaVox (-8 st, garganta 18% maior)
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
        var f0in = PitchTrack(male);
        double srcMidi = MedianMidi(f0in);
        Console.WriteLine($"   fala de teste: {male.Length / (double)Fs:0.0} s, tom médio {Beatrice.MidiToHz(srcMidi):0} Hz, {f0in.Count(v => v > 0) * 100 / Math.Max(1, f0in.Length)}% com voz");
        if (wavDir != null) SaveWav(Path.Combine(wavDir, "entrada_masculina.wav"), male);

        if (Environment.GetEnvironmentVariable("AIVOICE_BENCH") == "1")
        {
            // CPU com prioridade de audio (MMCSS "Pro Audio", como a thread de processamento do app)
            uint task = 0;
            IntPtr av = AvSetMmThreadCharacteristicsW("Pro Audio", ref task);
            Console.WriteLine("   MMCSS: " + (av != IntPtr.Zero ? "sim" : "não"));
            foreach (int vi in new[] { 0, 2, 0, 2 })
            {
                var v = official.Voices[vi];
                SetPlain(h, Beatrice.PVoice, v.Index);
                SetPlain(h, Beatrice.PPitch, Math.Round((v.AveragePitch - srcMidi) * 8) / 8);
                Run(h, new float[B * 40]);
                var times = new List<double>();
                var bb = new float[B];
                fixed (float* p = bb)
                    for (int i = 0; i + B <= male.Length; i += B)
                    {
                        Array.Copy(male, i, bb, 0, B);
                        long t0 = Stopwatch.GetTimestamp();
                        Tvh.tvh_process(h, p, B);
                        times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    }
                var s = times.OrderBy(x => x).ToArray();
                // padrao: tempo medio por posicao do bloco (modulo 2, 4, 8)
                string Pattern(int m) => string.Join("/", Enumerable.Range(0, m).Select(k => times.Where((x, i) => i % m == k).Average().ToString("0.0")));
                Console.WriteLine($"   voz {vi}: média {times.Average():0.00} ms, mediana {s[s.Length / 2]:0.00}, p99 {s[(int)(s.Length * 0.99)]:0.0}, pior {s.Last():0.0}, " +
                                  $">8 ms: {times.Count(x => x > 8)}, por posição mod4 {Pattern(4)}, mod8 {Pattern(8)}");
            }
            Tvh.tvh_free(h);
            return 0;
        }

        var tests = new List<BeatriceVoice>(official.Voices);
        if (jvs != null) tests.AddRange(jvs.Voices.OrderByDescending(v => v.AveragePitch).Take(2));
        foreach (var v in tests)
        {
            double shift = Math.Round((v.AveragePitch - srcMidi) * 8) / 8; // passos de 1/8 st (os do plugin)
            if (v.Model != official)
            {
                byte[] sj = Beatrice.WithModel(GetState(h), v.Model.Toml);
                Tvh.tvh_set_state(h, sj, sj.Length);
            }
            SetPlain(h, Beatrice.PVoice, v.Index);
            SetPlain(h, Beatrice.PPitch, shift);
            SetPlain(h, Beatrice.PFormant, 0);
            SetPlain(h, Beatrice.PIntonation, 1);
            // aquece (o primeiro bloco depois de trocar aplica os parametros)
            Run(h, new float[B * 20]);
            var rs = Run(h, male);
            var f0out = PitchTrack(rs.Y);
            double outMidi = MedianMidi(f0out);
            double lat = LatencyMs(male, rs.Y);
            double inDb = Db(Rms(male)), outDb = Db(Rms(rs.Y));
            int holes = 0;
            // buracos: trechos de 30 ms mudos enquanto a entrada tem voz forte
            for (int i = 4800; i + 1440 <= male.Length - 4800; i += 1440)
                if (Rms(male, i, i + 1440) > 0.03 && Rms(rs.Y, i + (int)(lat * 48), Math.Min(rs.Y.Length, i + (int)(lat * 48) + 1440)) < 0.0005) holes++;
            double voiced = VoicedShare(f0out.Skip((int)(lat / 10)).ToArray(), f0in);
            double cpu = rs.MeanMs / 10.0 * 100;
            Console.WriteLine($"   {v.Model.Key}#{v.Index} {v.Name}: alvo {v.AverageHz:0} Hz, tom {shift:+0.00;-0.00} st -> saiu {Beatrice.MidiToHz(outMidi):0} Hz " +
                              $"| CPU {rs.MeanMs:0.00} ms/bloco ({cpu:0.0}% de 1 núcleo), pior {rs.MaxMs:0.0} ms, p99 {rs.P99Ms:0.0} ms | atraso {lat:0} ms " +
                              $"| volume {inDb:0.0} -> {outDb:0.0} dBFS | voz {voiced * 100:0}% | buracos {holes}");
            Check(rs.Errors == 0, "processou sem erro");
            Check(Math.Abs(outMidi - v.AveragePitch) < 1.5, $"tom de saída perto do tom da voz alvo (erro {outMidi - v.AveragePitch:+0.0;-0.0} st)");
            Check(rs.MeanMs < 3.0, $"leve: {cpu:0.0}% de 1 núcleo (limite 30%)");
            Check(rs.P99Ms < 10.0, $"cabe na folga da thread da IA (99% dos blocos em até {rs.P99Ms:0.0} ms; a folga é de 10 ms)");
            Check(lat >= 20 && lat <= 120, $"atraso {lat:0} ms (esperado ~40–80)");
            Check(outDb > inDb - 12 && outDb < inDb + 6, "volume parecido com o da entrada");
            Check(holes == 0, "sem buracos no meio da fala");
            if (wavDir != null) SaveWav(Path.Combine(wavDir, $"ia_{v.Model.Key}_{v.Index}.wav"), rs.Y);
        }

        Tvh.tvh_free(h);
        Console.WriteLine(fails == 0 ? "TUDO OK" : fails + " FALHA(S)");
        return fails == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("avrt.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);

    static byte[] GetState(IntPtr h)
    {
        int n = Tvh.tvh_get_state(h, null, 0);
        if (n <= 0) return null;
        var b = new byte[n];
        return Tvh.tvh_get_state(h, b, n) == n ? b : null;
    }
}
