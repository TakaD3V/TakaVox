// Teste do SOUND PAD:
//  1) arquivos: grava WAV (44,1 kHz estereo, 24 bits), MP3 e M4A (codificadores do Windows) e le de volta pelo
//     Media Foundation: duracao, volume, mono, 48 kHz;
//  2) reamostragem: frequencia certa, faixa plana ate 20 kHz, rejeicao de alias (96 -> 48 kHz) e ruido;
//  3) volume percebido (LUFS, BS.1770): seno de 1 kHz em -20 dBFS de pico = -23,0 LUFS;
//  4) biblioteca: importar (copia + cache + corte do silencio + normalizacao com teto de pico), salvar e ler de novo;
//  5) mixer: toca certo (amostra por amostra), modos reiniciar/liga-desliga/sobrepor, loop, parar tudo com fade,
//     um por vez, previa so no fone, abaixar a voz (ducking) e CPU com 32 sons ao mesmo tempo.
// Uso: soundpadtest.exe   (TAKAVOX_DATA = pasta de dados do teste)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TakaVox;
using TakaVox.Audio;

static class SoundPadTest
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok     " : "  FALHA  ") + what);
        if (!ok) fails++;
    }

    static void WriteWav(string path, float[][] ch, int rate, int bits)
    {
        int n = ch[0].Length, c = ch.Length, bps = bits / 8;
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * c * bps); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)c); w.Write(rate); w.Write(rate * c * bps); w.Write((short)(c * bps)); w.Write((short)bits);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * c * bps);
            for (int i = 0; i < n; i++)
                for (int k = 0; k < c; k++)
                {
                    double v = Math.Max(-1, Math.Min(1, ch[k][i]));
                    if (bits == 16) w.Write((short)Math.Round(v * 32767));
                    else { int x = (int)Math.Round(v * 8388607); w.Write((byte)(x & 0xFF)); w.Write((byte)((x >> 8) & 0xFF)); w.Write((byte)((x >> 16) & 0xFF)); }
                }
        }
    }

    static float[] Sine(int rate, double hz, double sec, double amp)
    {
        var x = new float[(int)(rate * sec)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
        return x;
    }

    static double Rms(float[] x, int a = 0, int b = -1)
    {
        if (b < 0) b = x.Length;
        double s = 0;
        for (int i = a; i < b; i++) s += x[i] * (double)x[i];
        return Math.Sqrt(s / Math.Max(1, b - a));
    }
    static double Db(double v) => 20 * Math.Log10(Math.Max(1e-12, v));

    /// <summary>Frequencia pelo maior pico do espectro (Goertzel em volta do esperado).</summary>
    static double Freq(float[] x, int rate, double guess)
    {
        double best = 0, bestF = 0;
        int a = x.Length / 4, b = x.Length * 3 / 4;
        for (double f = guess - 20; f <= guess + 20; f += 0.25)
        {
            double w = 2 * Math.PI * f / rate, cr = 0, ci = 0;
            for (int i = a; i < b; i++) { cr += x[i] * Math.Cos(w * i); ci += x[i] * Math.Sin(w * i); }
            double m = cr * cr + ci * ci;
            if (m > best) { best = m; bestF = f; }
        }
        return bestF;
    }

    static int Main()
    {
        string dir = Path.Combine(Log.Dir, "arquivos_teste");
        try { if (Directory.Exists(Log.Dir)) Directory.Delete(Log.Dir, true); } catch { }
        Directory.CreateDirectory(dir);
        Console.WriteLine("dados de teste: " + Log.Dir);

        // ------------------------------------------------------------ 1) arquivos
        Console.WriteLine("[arquivos]");
        int r44 = 44100;
        // 0,5 s de silencio + 2 s de 440 Hz (esquerda) / 660 Hz (direita) em -12 dBFS
        var L = new float[(int)(2.5 * r44)]; var R = new float[L.Length];
        for (int i = (int)(0.5 * r44); i < L.Length; i++)
        {
            double t = i / (double)r44;
            L[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * t));
            R[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 660 * t));
        }
        string wav = Path.Combine(dir, "teste estereo 24bit.wav");
        WriteWav(wav, new[] { L, R }, r44, 24);
        var mono44 = new float[L.Length];
        for (int i = 0; i < L.Length; i++) mono44[i] = (L[i] + R[i]) * 0.5f;
        string mp3 = Path.Combine(dir, "teste.mp3"), m4a = Path.Combine(dir, "teste.m4a");
        bool encMp3 = SoundFile.Encode(mp3, mono44, r44, 128, out var e1);
        bool encM4a = SoundFile.Encode(m4a, mono44, r44, 128, out var e2);
        Check(encMp3, "MP3 gravado pelo codificador do Windows " + (e1 ?? ""));
        Check(encM4a, "M4A (AAC) gravado pelo codificador do Windows " + (e2 ?? ""));
        foreach (var f in new[] { wav, mp3, m4a })
        {
            if (!File.Exists(f)) continue;
            var sw = Stopwatch.StartNew();
            var x = SoundFile.LoadMono48k(f, out var err, out int sr, out int ch);
            long ms = sw.ElapsedMilliseconds;
            if (x == null) { Check(false, Path.GetExtension(f) + ": " + err); continue; }
            double dur = x.Length / 48000.0;
            double lvl = Db(Rms(x, (int)(0.8 * 48000), (int)(2.2 * 48000)));
            double expected = Db(Rms(mono44, (int)(0.8 * r44), (int)(2.2 * r44)));
            Console.WriteLine($"   {Path.GetExtension(f)}: {sr} Hz {ch} canal(is) -> mono 48 kHz, {dur:0.000} s, nível {lvl:0.00} dBFS (original {expected:0.00}), {ms} ms");
            Check(Math.Abs(dur - 2.5) < 0.08, $"{Path.GetExtension(f)}: duração certa ({dur:0.000} s)");
            Check(Math.Abs(lvl - expected) < 0.5, $"{Path.GetExtension(f)}: volume igual ao original ({lvl - expected:+0.00;-0.00} dB)");
        }
        string bad = Path.Combine(dir, "estragado.mp3");
        File.WriteAllBytes(bad, Encoding.ASCII.GetBytes("isto não é um mp3 de verdade, só texto"));
        var xb = SoundFile.LoadMono48k(bad, out var errBad, out _, out _);
        Check(xb == null && !string.IsNullOrEmpty(errBad), "arquivo estragado vira mensagem de erro, não trava: " + errBad);

        // ------------------------------------------------------------ 2) reamostragem
        Console.WriteLine("[reamostragem]");
        var t1 = SoundFile.Resample(Sine(44100, 1000, 1, 0.5), 44100, 48000);
        double f1 = Freq(t1, 48000, 1000);
        Check(Math.Abs(f1 - 1000) < 0.5, $"1 kHz em 44,1 kHz -> 48 kHz continua 1 kHz ({f1:0.00} Hz)");
        var t2 = SoundFile.Resample(Sine(44100, 18000, 1, 0.5), 44100, 48000);
        double g2 = Db(Rms(t2, 4800, t2.Length - 4800)) - Db(0.5 / Math.Sqrt(2));
        Check(Math.Abs(g2) < 0.1, $"18 kHz passa sem perder volume ({g2:+0.000;-0.000} dB)");
        var t3 = SoundFile.Resample(Sine(96000, 30000, 1, 0.5), 96000, 48000);
        double a3 = Db(Rms(t3, 4800, t3.Length - 4800)) - Db(0.5 / Math.Sqrt(2));
        Check(a3 < -80, $"30 kHz em 96 kHz -> 48 kHz não vira alias ({a3:0} dB)");
        var t4 = SoundFile.Resample(Sine(22050, 3000, 1, 0.5), 22050, 48000);
        double f4 = Freq(t4, 48000, 3000), g4 = Db(Rms(t4, 4800, t4.Length - 4800)) - Db(0.5 / Math.Sqrt(2));
        Check(Math.Abs(f4 - 3000) < 0.5 && Math.Abs(g4) < 0.1, $"22,05 kHz -> 48 kHz: 3 kHz certo ({f4:0.00} Hz, {g4:+0.000;-0.000} dB)");
        // distorcao: tom puro reamostrado, o resto do espectro deve ficar abaixo de -90 dB
        {
            var y = SoundFile.Resample(Sine(44100, 997, 2, 0.5), 44100, 48000);
            int a = 9600, b = y.Length - 9600;
            // ajuste exato (minimos quadrados com seno e cosseno) do tom de 997 Hz; o que sobra e ruido + distorcao
            double w = 2 * Math.PI * 997 / 48000, scc = 0, sss = 0, ssc = 0, syc = 0, sys = 0;
            for (int i = a; i < b; i++)
            {
                double c = Math.Cos(w * i), sn = Math.Sin(w * i);
                scc += c * c; sss += sn * sn; ssc += c * sn; syc += y[i] * c; sys += y[i] * sn;
            }
            double det = scc * sss - ssc * ssc, A = (syc * sss - sys * ssc) / det, Bc = (sys * scc - syc * ssc) / det;
            double res = 0, sig = 0;
            for (int i = a; i < b; i++)
            {
                double fit = A * Math.Cos(w * i) + Bc * Math.Sin(w * i);
                double e = y[i] - fit;
                res += e * e; sig += fit * fit;
            }
            double snr = 10 * Math.Log10(sig / Math.Max(1e-30, res));
            Check(snr > 90, $"reamostragem limpa: sinal/ruído+distorção {snr:0} dB");
        }

        // ------------------------------------------------------------ 3) LUFS
        Console.WriteLine("[volume percebido]");
        double lufs = SoundFile.LoudnessLufs(Sine(48000, 1000, 5, 0.1));
        Check(Math.Abs(lufs - (-23.01)) < 0.3, $"seno 1 kHz a -20 dBFS de pico = {lufs:0.00} LUFS (BS.1770: -23,01)");
        var withSilence = new float[48000 * 10];
        Array.Copy(Sine(48000, 1000, 5, 0.1), 0, withSilence, 48000 * 5, 48000 * 5);
        double l2 = SoundFile.LoudnessLufs(withSilence);
        Check(Math.Abs(l2 - lufs) < 0.5, $"silêncio não puxa a medida para baixo (porta): {l2:0.00} LUFS");

        // ------------------------------------------------------------ 4) biblioteca
        Console.WriteLine("[biblioteca]");
        var lib = new SoundLibrary();
        lib.Load();
        Check(lib.Items.Count == 0, "começa vazia");
        var items = new List<SoundItem>();
        foreach (var f in new[] { wav, mp3, m4a })
        {
            if (!File.Exists(f)) continue;
            var it = SoundLibrary.ImportFile(f, "Testes", out var err);
            Check(it != null, "importou " + Path.GetFileName(f) + " " + (err ?? ""));
            if (it == null) continue;
            it.Key = lib.NextKey();
            lib.Items.Add(it);
            items.Add(it);
            Console.WriteLine($"   {it.Name}: {it.Duration:0.00} s, trecho {it.StartSec:0.000}–{(it.EndSec > 0 ? it.EndSec : it.Duration):0.000} s, {it.Lufs:0.0} LUFS, pico {it.PeakDb:0.0} dBFS");
        }
        var w0 = items.First();
        Check(Math.Abs(w0.StartSec - 0.495) < 0.03, $"silêncio do começo cortado sozinho (começa em {w0.StartSec:0.000} s)");
        Check(File.Exists(Path.Combine(SoundLibrary.Dir, "cache", w0.Id + ".s16")) && File.Exists(Path.Combine(SoundLibrary.Dir, "arquivos", w0.FileName)), "cópia do original e cache gravados");
        lib.Categories.Add("Testes");
        lib.Save();
        var lib2 = new SoundLibrary();
        lib2.Load();
        Check(lib2.Items.Count == items.Count && lib2.Items[0].Name == w0.Name && Math.Abs(lib2.Items[0].StartSec - w0.StartSec) < 1e-9 && lib2.Categories.Contains("Testes"),
              "salvou e leu de novo (nomes, trecho, categoria)");
        var clip = lib.GetClip(w0, out var cerr);
        Check(clip != null && Math.Abs(clip.Length - 2.5 * 48000) < 4800, "cache lido para a memória " + (cerr ?? ""));
        var play = SoundLibrary.MakePlay(w0, clip, false);
        double gainDb = 20 * Math.Log10(play.Gain);
        Console.WriteLine($"   tocar: ganho {gainDb:+0.0;-0.0} dB (normalização para {SoundLibrary.TargetLufs} LUFS com teto de pico -1 dBFS)");
        Check(w0.PeakDb + gainDb <= -1.0 + 0.01, $"normalização nunca passa o pico de -1 dBFS ({w0.PeakDb + gainDb:0.00} dBFS)");
        var loud = new SoundItem { Lufs = -8f, PeakDb = -0.1f, Normalize = true };
        var pl = SoundLibrary.MakePlay(loud, clip, false);
        Check(Math.Abs(20 * Math.Log10(pl.Gain) - (-12)) < 0.01, "som alto é abaixado para o nível dos outros (-12 dB)");

        // ------------------------------------------------------------ 5) mixer
        Console.WriteLine("[mixer]");
        var board = new SoundBoard { MicGain = 1f, PhoneGain = 1f };
        var ramp = new short[48000];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = (short)(Math.Sin(i * 0.05) * 16000);
        var rc = new SoundClip(ramp);
        var mic = new float[480]; var ph = new float[480];
        board.Play(new SoundPlay { Key = 1, Clip = rc, Gain = 0.5f, Start = 0, End = ramp.Length });
        board.Mix(mic, ph, 480);
        double maxErr = 0;
        for (int i = 0; i < 480; i++) maxErr = Math.Max(maxErr, Math.Abs(mic[i] - ramp[i] / 32768f * 0.5f));
        Check(maxErr < 1e-6, $"toca o som exatamente (erro {maxErr:0.0e0})");
        Check(board.IsPlaying(1, out float pos) && pos > 0 && pos < 0.02, $"interface vê o som tocando ({pos:0.000})");
        // reiniciar: apertar de novo volta ao comeco
        board.Play(new SoundPlay { Key = 1, Clip = rc, Gain = 0.5f, Start = 0, End = ramp.Length, Mode = 0 });
        for (int k = 0; k < 3; k++) board.Mix(mic, ph, 480);
        int voices1 = board.SlotKey.Count(v => v == 1);
        Check(voices1 == 1 && board.IsPlaying(1, out float p2) && p2 < 0.05, $"modo reiniciar: um só, do começo ({voices1} tocando)");
        // liga/desliga
        board.Play(new SoundPlay { Key = 1, Clip = rc, Gain = 0.5f, Start = 0, End = ramp.Length, Mode = 1 });
        for (int k = 0; k < 4; k++) board.Mix(mic, ph, 480);
        Check(!board.IsPlaying(1, out _), "modo liga/desliga: apertar de novo para");
        // sobrepor
        for (int k = 0; k < 3; k++) board.Play(new SoundPlay { Key = 2, Clip = rc, Gain = 0.2f, Start = 0, End = ramp.Length, Mode = 2 });
        board.Mix(mic, ph, 480);
        Check(board.SlotKey.Count(v => v == 2) == 3, "modo sobrepor: 3 ao mesmo tempo");
        board.StopAll();
        board.Mix(mic, ph, 480);
        double tail = Rms(mic, 400, 480);
        for (int k = 0; k < 3; k++) board.Mix(mic, ph, 480);
        Check(!board.IsPlaying(2, out _) && Rms(mic) == 0, $"parar tudo com fade de 15 ms (sem estalo; fim do 1º bloco {Db(tail):0} dBFS)");
        // loop
        var shortClip = new SoundClip(ramp.Take(2000).ToArray());
        board.Play(new SoundPlay { Key = 3, Clip = shortClip, Gain = 0.5f, Start = 0, End = 2000, Loop = true, Mode = 1 });
        for (int k = 0; k < 20; k++) board.Mix(mic, ph, 480);
        Check(board.IsPlaying(3, out _), "loop continua depois do fim do arquivo");
        board.Play(new SoundPlay { Key = 3, Clip = shortClip, Gain = 0.5f, Start = 0, End = 2000, Loop = true, Mode = 1 });
        for (int k = 0; k < 4; k++) board.Mix(mic, ph, 480);
        Check(!board.IsPlaying(3, out _), "loop para ao apertar de novo");
        // fim natural com fade
        board.Play(new SoundPlay { Key = 4, Clip = shortClip, Gain = 0.5f, Start = 0, End = 2000, FadeOut = 480 });
        for (int k = 0; k < 6; k++) board.Mix(mic, ph, 480);
        Check(!board.IsPlaying(4, out _), "som curto acaba sozinho");
        // um por vez
        board.Exclusive = true;
        board.Play(new SoundPlay { Key = 5, Clip = rc, Gain = 0.3f, Start = 0, End = ramp.Length, Mode = 2 });
        board.Mix(mic, ph, 480);
        board.Play(new SoundPlay { Key = 6, Clip = rc, Gain = 0.3f, Start = 0, End = ramp.Length, Mode = 2 });
        for (int k = 0; k < 4; k++) board.Mix(mic, ph, 480);
        Check(!board.IsPlaying(5, out _) && board.IsPlaying(6, out _), "um som por vez: o novo para o anterior");
        board.Exclusive = false;
        board.StopAll();
        for (int k = 0; k < 4; k++) board.Mix(mic, ph, 480);
        // previa: so no fone
        board.Play(new SoundPlay { Key = 7, Clip = rc, Gain = 0.5f, Start = 0, End = ramp.Length, PreviewOnly = true });
        board.Mix(mic, ph, 480);
        Check(Rms(mic) == 0 && Rms(ph) > 0.01 && board.IsPlaying(-7, out _), "prévia do editor só no seu fone (nada para os outros)");
        board.StopAll();
        for (int k = 0; k < 4; k++) board.Mix(mic, ph, 480);
        // volumes mestre e "nao tocar no mic"
        board.MicGain = 0.5f; board.PhoneGain = 0.25f;
        board.Play(new SoundPlay { Key = 8, Clip = rc, Gain = 1f, Start = 0, End = ramp.Length });
        board.Mix(mic, ph, 480);
        Check(Math.Abs(Rms(mic) / Rms(ph) - 2) < 1e-3, "volume para os outros e para o fone separados");
        board.ToMic = false;
        board.Mix(mic, ph, 480);
        Check(Rms(mic) == 0 && Rms(ph) > 0, "\"tocar para os outros\" desligado: só no fone");
        board.ToMic = true;
        // ducking: a voz abaixa ~12 dB em ~20 ms e volta em ~300 ms
        board.DuckDb = -12f;
        board.BeginDuck(true);
        float g = 1;
        for (int i = 0; i < 4800; i++) g = board.NextDuck();
        Check(Math.Abs(20 * Math.Log10(g) - (-12)) < 0.5, $"voz abaixa {20 * Math.Log10(g):0.0} dB enquanto o som toca");
        board.StopAll();
        for (int k = 0; k < 4; k++) board.Mix(mic, ph, 480);
        board.BeginDuck(false);
        for (int i = 0; i < 72000; i++) g = board.NextDuck(); // 1,5 s
        Check(g > 0.98f, "e volta ao normal quando o som acaba");
        // CPU com 32 sons
        board.DuckDb = 0;
        for (int k = 0; k < 32; k++) board.Play(new SoundPlay { Key = 100 + k, Clip = rc, Gain = 0.03f, Start = 0, End = ramp.Length, Mode = 2 });
        var sw2 = Stopwatch.StartNew();
        int blocks = 0;
        while (blocks < 90) { board.Mix(mic, ph, 480); blocks++; }
        double perBlock = sw2.Elapsed.TotalMilliseconds / blocks;
        Check(board.Active == 32 || board.Active > 0, $"32 sons ao mesmo tempo ({board.Active} ativos)");
        Check(perBlock < 0.5, $"mixer leve: {perBlock * 1000:0} µs por bloco de 10 ms com 32 sons ({perBlock / 10 * 100:0.00}% de 1 núcleo)");
        long gc0 = GC.CollectionCount(0);
        for (int k = 0; k < 2000; k++) board.Mix(mic, ph, 480);
        Check(GC.CollectionCount(0) == gc0, "não aloca memória na thread de áudio (zero coletas de lixo)");

        Console.WriteLine(fails == 0 ? "TUDO OK" : fails + " FALHA(S)");
        return fails == 0 ? 0 : 1;
    }
}
