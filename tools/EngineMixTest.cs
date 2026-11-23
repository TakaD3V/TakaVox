// Teste da MISTURA FINAL do motor de audio (sem abrir nenhum dispositivo): voz + sons do sound pad, volume dos sons,
// limitador da soma, mudo (com e sem "sons saem no mudo"), abaixar a voz (ducking) e o descanso (economia automatica).
// Chama o ProcessBlock do motor direto, bloco a bloco, e le o que sairia no microfone virtual.
// Uso: enginemixtest.exe   (TAKAVOX_DATA = pasta de dados do teste)
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TakaVox.Audio;
using TakaVox.Dsp;

static class EngineMixTest
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
        string wav = Path.Combine(Path.GetTempPath(), "takavox_mix_tts.wav");
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
        int pos = 12, off = -1, len = 0;
        while (pos + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, pos, 4);
            int l = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data") { off = pos + 8; len = Math.Min(l, bytes.Length - off); break; }
            pos += 8 + l + (l & 1);
        }
        var x = new float[len / 2];
        for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(bytes, off + 2 * i) / 32768f;
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

    static AudioEngine eng;
    static float[] block;
    static MethodInfo pb;

    static float[] Run(float[] input)
    {
        var y = new float[input.Length - input.Length % B];
        var outBlk = new float[B];
        for (int i = 0; i + B <= input.Length; i += B)
        {
            Array.Copy(input, i, block, 0, B);
            pb.Invoke(eng, null);
            eng.TapOut.Snapshot(outBlk);
            Array.Copy(outBlk, 0, y, i, B);
        }
        return y;
    }

    static SoundClip Tone(double hz, double amp, double sec)
    {
        var d = new short[(int)(Fs * sec)];
        for (int i = 0; i < d.Length; i++) d[i] = (short)Math.Round(amp * 32767 * Math.Sin(2 * Math.PI * hz * i / Fs));
        return new SoundClip(d);
    }

    static int Main()
    {
        var p = new ChainParams();
        eng = new AudioEngine(p); // sem SetInput/SetOutput: nenhum dispositivo e aberto
        block = (float[])typeof(AudioEngine).GetField("block", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(eng);
        pb = typeof(AudioEngine).GetMethod("ProcessBlock", BindingFlags.NonPublic | BindingFlags.Instance);
        var silence = new float[Fs * 2];
        Run(new float[Fs]); // aquece a cadeia (IA anti-ruido)

        Console.WriteLine("[sons no microfone]");
        var tone = Tone(1000, 0.25, 1.5);
        eng.Sounds.Play(new SoundPlay { Key = 1, Clip = tone, Gain = 1f, Start = 0, End = tone.Length });
        var y1 = Run(silence);
        double lvl = Db(Rms(y1, B * 10, B * 100)), expect = Db(0.25 / Math.Sqrt(2));
        Check(Math.Abs(lvl - expect) < 0.5, $"o som sai no microfone virtual no volume certo ({lvl:0.00} dBFS, esperado {expect:0.00})");
        Check(Rms(y1, Fs * 3 / 2 + B * 4, y1.Length) < 1e-4, "e acaba quando termina o arquivo");

        Console.WriteLine("[limitador da soma]");
        var loud = Tone(440, 0.9, 1);
        eng.Sounds.Play(new SoundPlay { Key = 2, Clip = loud, Gain = 2.5f, Start = 0, End = loud.Length, Mode = 2 });
        eng.Sounds.Play(new SoundPlay { Key = 3, Clip = loud, Gain = 2.5f, Start = 0, End = loud.Length, Mode = 2 });
        var y2 = Run(silence);
        float peak = y2.Max(v => Math.Abs(v));
        float ceil = (float)Math.Pow(10, Math.Min(-0.3f, p.LimiterCeilingDb) / 20);
        Check(peak <= ceil + 1e-4f, $"dois sons altíssimos juntos não passam do teto ({20 * Math.Log10(peak):0.00} dBFS, teto {p.LimiterCeilingDb:0.0})");
        double flat = Db(Rms(y2, Fs / 2, Fs / 2 + 4800));
        Check(flat > Db(ceil / Math.Sqrt(2)) - 1.5, $"o limitador segura sem cortar o som ({flat:0.0} dBFS de média)");

        Console.WriteLine("[mudo]");
        p.Mute = true;
        eng.Sounds.WhileMuted = true;
        eng.Sounds.Play(new SoundPlay { Key = 4, Clip = tone, Gain = 1f, Start = 0, End = tone.Length });
        var y3 = Run(silence);
        Check(Math.Abs(Db(Rms(y3, B * 10, B * 100)) - expect) < 0.5, "mudo: os sons continuam saindo (opção ligada)");
        eng.Sounds.WhileMuted = false;
        eng.Sounds.Play(new SoundPlay { Key = 5, Clip = tone, Gain = 1f, Start = 0, End = tone.Length });
        var y4 = Run(silence);
        Check(Rms(y4, B * 10, B * 100) < 1e-4, "mudo com a opção desligada: os sons também calam");
        p.Mute = false;
        eng.Sounds.WhileMuted = true;
        Run(new float[Fs]);

        Console.WriteLine("[abaixar a voz]");
        var speech = Tts("Bora jogar galera, testando o sound pad do TakaVox. Um, dois, três, quatro, cinco, seis, sete, oito.");
        var voice0 = Run(speech);
        var quiet = new SoundClip(new short[speech.Length]); // som mudo: so liga o "tem som tocando"
        eng.Sounds.DuckDb = -12f;
        eng.Sounds.Play(new SoundPlay { Key = 6, Clip = quiet, Gain = 1f, Start = 0, End = quiet.Length });
        var voice1 = Run(speech);
        double d = Db(Rms(voice1, Fs, voice1.Length - Fs)) - Db(Rms(voice0, Fs, voice0.Length - Fs));
        Check(Math.Abs(d - (-12)) < 1.0, $"a voz abaixa {d:0.0} dB enquanto o som toca (pedido -12)");
        eng.Sounds.StopAll();
        eng.Sounds.DuckDb = 0;
        Run(new float[Fs]);

        Console.WriteLine("[descanso]");
        var before = Run(speech);
        long rb0 = eng.RestBlocks;
        eng.Resting = true;
        var y5 = Run(speech);
        Check(y5.All(v => v == 0f) && eng.RestBlocks - rb0 == speech.Length / B, "em descanso: microfone virtual em silêncio e nada de IA rodando");
        eng.Resting = false;
        var y6 = Run(speech);
        double back = Db(Rms(y6, Fs, y6.Length - Fs)), normal = Db(Rms(before, Fs, before.Length - Fs));
        Check(Math.Abs(back - normal) < 1.0, $"ao acordar, a voz volta igual ({back:0.0} dBFS; antes do descanso {normal:0.0})");
        double firstWord = Db(Rms(y6, 0, Fs / 2)), firstBefore = Db(Rms(before, 0, Fs / 2));
        Check(Math.Abs(firstWord - firstBefore) < 3.0, $"a primeira palavra depois do descanso sai inteira ({firstWord:0.0} dBFS; antes {firstBefore:0.0})");

        eng.Dispose();
        Console.WriteLine(fails == 0 ? "TUDO OK" : fails + " FALHA(S)");
        return fails == 0 ? 0 : 1;
    }
}
