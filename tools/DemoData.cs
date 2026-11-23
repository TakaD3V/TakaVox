// Monta uma pasta de dados de DEMONSTRACAO (para as capturas do README): sons sinteticos no sound pad, com nomes,
// emojis, cores, categorias e teclas. Nada de dados de verdade do usuario.
// Uso: demodata.exe   (TAKAVOX_DATA = pasta de demonstracao)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TakaVox;
using TakaVox.Audio;

static class DemoData
{
    const int Fs = 48000;
    static readonly Random R = new Random(42);

    static float[] Make(double sec, Func<double, double> f)
    {
        var x = new float[(int)(sec * Fs)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)f(i / (double)Fs);
        return x;
    }
    static double Noise() => R.NextDouble() * 2 - 1;
    static double Env(double t, double a, double d) => t < a ? t / a : Math.Exp(-(t - a) / d);
    static double Sq(double ph) => Math.Sin(ph) >= 0 ? 1 : -1;
    static double Saw(double ph) => 2 * ((ph / (2 * Math.PI)) % 1.0) - 1;

    static void WriteWav(string path, float[] x)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + x.Length * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Fs); w.Write(Fs * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(x.Length * 2);
            foreach (var v in x) w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(v * 32767))));
        }
    }

    static int Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "takavox_demo_sons");
        Directory.CreateDirectory(tmp);
        var sounds = new List<(string name, string emoji, string cat, string key, float[] x)>
        {
            ("Risada", "😂", "Memes", "NumPad1", Make(2.2, t => 0.5 * Noise() * Math.Max(0, Math.Sin(2 * Math.PI * 5 * t)) * Env(t, 0.05, 1.5))),
            ("Bruh", "💀", "Memes", "NumPad2", Make(0.9, t => 0.6 * Saw(2 * Math.PI * (110 - 40 * t) * t) * Env(t, 0.02, 0.4))),
            ("Air horn", "📯", "Memes", "NumPad3", Make(1.6, t => 0.25 * (Saw(2 * Math.PI * 440 * t) + Saw(2 * Math.PI * 554 * t) + Saw(2 * Math.PI * 659 * t)) * Env(t, 0.01, 2.0))),
            ("Erro", "❌", "Memes", "NumPad4", Make(0.7, t => 0.5 * Sq(2 * Math.PI * (t < 0.3 ? 660 : 440) * t) * (t % 0.35 < 0.3 ? 1 : 0))),
            ("Vinheta da live", "🎬", "Live", "NumPad5", Make(4.0, t => { int n = (int)(t / 0.25) % 8; double hz = 261.6 * Math.Pow(2, new[] { 0, 4, 7, 12, 7, 4, 0, 12 }[n] / 12.0); return 0.45 * Math.Sin(2 * Math.PI * hz * t) * Env(t % 0.25, 0.005, 0.2) * (1 - t / 4.2); })),
            ("Vitória", "🏆", "Live", "NumPad6", Make(2.6, t => 0.2 * (Math.Sin(2 * Math.PI * 523 * t) + Math.Sin(2 * Math.PI * 659 * t) + Math.Sin(2 * Math.PI * 784 * t) + Math.Sin(2 * Math.PI * 1046 * t)) * Env(t, 0.03, 1.2))),
            ("Aplausos", "👏", "Live", "NumPad7", Make(3.2, t => 0.6 * Noise() * (R.NextDouble() < 0.12 ? 1 : 0.15) * Env(t, 0.3, 1.6))),
            ("Rufar de tambor", "🥁", "Live", "NumPad8", Make(3.0, t => 0.6 * Noise() * Env(t % (0.12 - 0.03 * t / 3), 0.002, 0.03) * (0.4 + 0.6 * t / 3))),
            ("Suspense", "😱", "Efeitos", "", Make(5.0, t => 0.35 * Math.Sin(2 * Math.PI * 55 * t) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 6 * t)) * Env(t, 1.0, 3.0))),
            ("Sino", "🔔", "Efeitos", "", Make(2.4, t => 0.3 * (Math.Sin(2 * Math.PI * 880 * t) + 0.6 * Math.Sin(2 * Math.PI * 2320 * t) + 0.3 * Math.Sin(2 * Math.PI * 4170 * t)) * Env(t, 0.003, 0.7))),
        };
        var lib = new SoundLibrary();
        lib.Load();
        foreach (var c in new[] { "Memes", "Live", "Efeitos" }) if (!lib.Categories.Contains(c)) lib.Categories.Add(c);
        int k = 0;
        foreach (var s in sounds)
        {
            // um pouco de silencio no comeco: o sound pad tira sozinho
            var x = new float[s.x.Length + Fs / 5];
            Array.Copy(s.x, 0, x, Fs / 5, s.x.Length);
            string wav = Path.Combine(tmp, s.name + ".wav");
            WriteWav(wav, x);
            var it = SoundLibrary.ImportFile(wav, s.cat, out var err);
            if (it == null) { Console.WriteLine("falhou " + s.name + ": " + err); return 1; }
            it.Key = lib.NextKey();
            it.Name = s.name;
            it.Emoji = s.emoji;
            it.Hotkey = s.key;
            it.Color = SoundLibrary.Palette[k++ % SoundLibrary.Palette.Length];
            if (s.name == "Suspense") { it.Loop = true; it.Mode = 1; }
            lib.Items.Add(it);
            File.Delete(wav);
        }
        lib.Save();
        Console.WriteLine($"ok: {lib.Items.Count} sons de demonstração em {SoundLibrary.Dir}");
        return 0;
    }
}
