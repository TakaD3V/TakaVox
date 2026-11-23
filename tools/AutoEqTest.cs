// Teste da EQ automatica da calibracao.
// 1) "voz" sintetica com o espectro medio EXATO da fala natural (ANSI S3.5), via FFT inversa;
// 2) fala real da voz sintetizada do Windows (TTS), quando disponivel.
// Em cada uma aplica defeitos conhecidos de microfone (ressonancia de "copo", abafado, grave demais, voz fina)
// e confere se a analise acha o defeito certo, nao inventa defeito, e se o EQ resultante corrige o som.
using System;
using System.IO;
using TakaVox.Dsp;

static class AutoEqTest
{
    const int Fs = 48000, B = 480;
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok     " : "  FALHA  ") + what);
        if (!ok) fails++;
    }

    // ---------------------------------------------------------------- FFT complexa (so para gerar o sinal de teste)
    static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    static double Curve(double f)
    {
        var c = VoiceEq.Centers;
        if (f < 80 || f > 14000) return -60;
        if (f <= c[0]) return VoiceEq.Natural[0];
        if (f >= c[c.Length - 1]) return VoiceEq.Natural[c.Length - 1];
        for (int i = 0; i < c.Length - 1; i++)
            if (f < c[i + 1])
            {
                double t = Math.Log(f / c[i]) / Math.Log(c[i + 1] / c[i]);
                return VoiceEq.Natural[i] * (1 - t) + VoiceEq.Natural[i + 1] * t;
            }
        return -60;
    }

    // "voz": ruido com o espectro medio exato da fala natural (bandas de 1/3 de oitava -> densidade por Hz)
    static float[] SynthVoice(int seconds, int seed)
    {
        const int N = 1 << 19;
        var rnd = new Random(seed);
        var re = new double[N];
        var im = new double[N];
        for (int k = 1; k < N / 2; k++)
        {
            double f = k * (double)Fs / N;
            double dens = Math.Pow(10, Curve(f) / 10) / (0.2316 * f); // energia da banda espalhada na largura dela
            double a = Math.Sqrt(dens), ph = rnd.NextDouble() * 2 * Math.PI;
            re[k] = a * Math.Cos(ph); im[k] = a * Math.Sin(ph);
            re[N - k] = re[k]; im[N - k] = -im[k];
        }
        Fft(re, im, true);
        int n = Math.Min(N, seconds * Fs);
        var y = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++) { y[i] = re[i] * (0.55 + 0.45 * Math.Sin(2 * Math.PI * 3.7 * i / Fs)); sum += y[i] * y[i]; }
        return Normalize(y, sum, n);
    }

    static float[] Normalize(double[] y, double sum, int n)
    {
        double g = Math.Pow(10, -20 / 20.0) / Math.Sqrt(sum / n); // -20 dBFS RMS
        var o = new float[n];
        for (int i = 0; i < n; i++) o[i] = (float)(y[i] * g);
        return o;
    }

    // fala real: voz sintetizada do Windows (gravada de uma pessoa), 48 kHz
    static float[] TtsVoice()
    {
        try
        {
            string wav = Path.Combine(Path.GetTempPath(), "takavox_tts.wav");
            using (var s = new System.Speech.Synthesis.SpeechSynthesizer())
            {
                foreach (var v in s.GetInstalledVoices())
                    if (v.VoiceInfo.Culture.Name.StartsWith("pt")) { s.SelectVoice(v.VoiceInfo.Name); break; }
                Console.WriteLine("  (voz do Windows: " + s.Voice.Name + ")");
                s.SetOutputToWaveFile(wav, new System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
                s.Speak("Bora jogar, mano! Testando o microfone: um, dois, três, quatro, cinco. Hoje a gente vai subir de elo, " +
                        "sem desculpa. Cuidado com o sniper no meio, eu vou flanquear pela direita e você segura a bomba.");
                s.SetOutputToNull();
            }
            var bytes = File.ReadAllBytes(wav);
            File.Delete(wav);
            int data = 12;
            while (data + 8 < bytes.Length && !(bytes[data] == 'd' && bytes[data + 1] == 'a' && bytes[data + 2] == 't' && bytes[data + 3] == 'a'))
                data += 8 + BitConverter.ToInt32(bytes, data + 4);
            int len = BitConverter.ToInt32(bytes, data + 4) / 2;
            var y = new double[len];
            double sum = 0;
            for (int i = 0; i < len; i++) { y[i] = BitConverter.ToInt16(bytes, data + 8 + 2 * i) / 32768.0; sum += y[i] * y[i]; }
            return Normalize(y, sum, len);
        }
        catch (Exception ex) { Console.WriteLine("  (sem voz do Windows: " + ex.Message + ")"); return null; }
    }

    static float[] Color(float[] x, params (FilterType t, double f, double q, double g)[] fl)
    {
        var y = (float[])x.Clone();
        foreach (var s in fl) { var bq = new Biquad(); bq.Set(s.t, Fs, s.f, s.q, s.g); bq.Process(y, y.Length); }
        return y;
    }

    // monta os dados como a calibracao do app: 3 s de silencio (so ruido) + fala, com VAD por bloco
    static VoiceEq.Result Run(float[] voice, double noiseDbfs, int seed)
    {
        var rnd = new Random(seed);
        int sil = 3 * Fs, n = sil + voice.Length;
        var audio = new float[n];
        double na = Math.Pow(10, noiseDbfs / 20) * Math.Sqrt(3);
        for (int i = 0; i < n; i++) audio[i] = (float)((rnd.NextDouble() * 2 - 1) * na) + (i >= sil ? voice[i - sil] : 0f);
        int blocks = n / B;
        var rms = new float[blocks];
        var vad = new float[blocks];
        for (int b = 0; b < blocks; b++)
        {
            double e = 0;
            for (int i = b * B; i < (b + 1) * B; i++) e += audio[i] * audio[i];
            rms[b] = (float)(10 * Math.Log10(e / B + 1e-20));
            vad[b] = b * B >= sil ? 0.95f : 0.02f;
        }
        return VoiceEq.Analyze(audio, n, rms, vad, blocks, sil / B, (float)noiseDbfs);
    }

    static void Show(string name, VoiceEq.Result r)
    {
        Console.WriteLine($"  {name}: " + (r.Ok ? VoiceEq.Summary(r) : "não analisou: " + r.Error));
        foreach (var note in r.Notes) Console.WriteLine("           • " + note);
    }

    static double Mean(double[] v, double f0, double f1)
    {
        double s = 0; int n = 0;
        for (int c = 0; c < VoiceEq.Centers.Length; c++)
            if (VoiceEq.Centers[c] >= f0 && VoiceEq.Centers[c] <= f1 && !double.IsNaN(v[c])) { s += v[c]; n++; }
        return n == 0 ? double.NaN : s / n;
    }

    static VoiceEq.Result Corrected(float[] voice, VoiceEq.Result r, int seed)
    {
        var p = new ChainParams();
        VoiceEq.Apply(r, p);
        var y = (float[])voice.Clone();
        foreach (var b in p.Bands) { var bq = new Biquad(); bq.Set(b.Type, Fs, b.Freq, b.Q, b.GainDb); bq.Process(y, y.Length); }
        return Run(y, -75, seed);
    }

    // compara sempre com a mesma voz SEM defeito: o que interessa e o quanto a EQ muda por causa do defeito
    static void Suite(string label, float[] voice, bool synthetic)
    {
        Console.WriteLine($"=== {label} ===");
        var clean = Run(voice, -75, 2);
        Show("sem defeito", clean);
        Check(clean.Ok, "analisou a voz");
        if (!clean.Ok) return;
        if (synthetic)
            Check(Math.Abs(clean.LowGain) <= 1 && Math.Abs(clean.MudGain) <= 1.5 && Math.Abs(clean.MidGain) <= 1.5 &&
                  clean.PresGain >= 0.5 && clean.PresGain <= 3.5 && clean.AirGain >= 1 && clean.AirGain <= 4,
                "voz equilibrada: só um toque leve de presença e brilho");
        else
            Check(clean.MidGain > -2.5 && clean.MudGain > -2.5, "voz limpa: nenhum corte forte nos médios");

        // o nivel absoluto nao importa (nivelador e compressor vem depois do EQ): vale o EQUILIBRIO entre as faixas
        double MidBal(VoiceEq.Result x) => x.MidGain - (x.LowGain + x.PresGain) / 2;
        double MudMidBal(VoiceEq.Result x) => (x.MudGain + x.MidGain) / 2 - (x.LowGain + x.PresGain) / 2;
        double need = synthetic ? 1.5 : 1.0;

        var cup = Color(voice, (FilterType.Peak, 1000, 1.2, 7)); // medios "inchados" em volta de 1 kHz
        var r = Run(cup, -75, 3);
        Show("copo (+7 dB em 1 kHz)", r);
        Check(r.Ok && MidBal(r) <= MidBal(clean) - need, $"tira os médios de copo em relação ao resto ({MidBal(clean):+0.0} → {MidBal(r):+0.0} dB)");
        if (r.Ok)
        {
            var after = Corrected(cup, r, 4);
            double Rel(VoiceEq.Result x) => Mean(x.Deviation, 630, 1600) - 0.5 * (Mean(x.Deviation, 125, 315) + Mean(x.Deviation, 2000, 4000));
            Check(after.Ok && Rel(after) < Rel(r) - 1, $"com o EQ aplicado, médios × resto cai de {Rel(r):+0.0} para {Rel(after):+0.0} dB");
        }

        var honk = Color(voice, (FilterType.Peak, 700, 1.5, 6));
        r = Run(honk, -40, 5);
        Show("caixa em 700 Hz + ruído alto", r);
        Check(r.Ok && MudMidBal(r) <= MudMidBal(clean) - 0.9, $"tira a caixa mesmo com ruído alto ({MudMidBal(clean):+0.0} → {MudMidBal(r):+0.0} dB)");

        var muffled = Color(voice, (FilterType.HighShelf, 3000, 0.71, -8));
        r = Run(muffled, -75, 6);
        Show("abafado (−8 dB acima de 3 kHz)", r);
        Check(r.Ok && r.PresGain >= Math.Min(6, clean.PresGain + 2) && r.AirGain >= Math.Min(5, clean.AirGain + 2),
            $"devolve presença ({clean.PresGain:+0.0} → {r.PresGain:+0.0}) e brilho ({clean.AirGain:+0.0} → {r.AirGain:+0.0})");

        var boomy = Color(voice, (FilterType.LowShelf, 200, 0.71, 8));
        r = Run(boomy, -75, 7);
        Show("grave demais (+8 dB abaixo de 200 Hz)", r);
        Check(r.Ok && r.LowGain <= clean.LowGain - 1.5, $"tira grave ({clean.LowGain:+0.0} → {r.LowGain:+0.0} dB)");

        var thin = Color(voice, (FilterType.LowShelf, 250, 0.71, -8));
        r = Run(thin, -75, 8);
        Show("voz fina (−8 dB abaixo de 250 Hz)", r);
        Check(r.Ok && r.LowGain >= Math.Min(3, clean.LowGain + 1.5), $"devolve corpo ({clean.LowGain:+0.0} → {r.LowGain:+0.0} dB)");
    }

    static int Main()
    {
        Suite("voz sintética com o espectro médio da fala", SynthVoice(7, 1), true);
        var tts = TtsVoice();
        if (tts != null) Suite("fala real (voz do Windows)", tts, false);

        Console.WriteLine("=== pouca fala ===");
        var shortVoice = new float[Fs / 5];
        Array.Copy(SynthVoice(1, 9), shortVoice, shortVoice.Length);
        var r = Run(shortVoice, -75, 9);
        Check(!r.Ok && r.Error.Length > 0, "com só 0,2 s de fala não mexe no EQ (" + r.Error + ")");

        Console.WriteLine(fails == 0 ? "TUDO OK" : "FALHAS: " + fails);
        return fails;
    }
}
