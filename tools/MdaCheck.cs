// Confere um pacote de plugins com o hospedeiro do TakaVox: le todos os efeitos, abre cada um,
// passa 2 s de um sinal parecido com voz, mede o custo e verifica se a saida e valida.
// Uso: mdacheck.exe <arquivo .vst3/.dll>
using System;
using System.Diagnostics;
using TakaVox.Plugins;

static unsafe class MdaCheck
{
    static int Main(string[] args)
    {
        string path = args[0];
        if (!Tvh.Init(out string ie)) { Console.WriteLine("hospedeiro: " + ie); return 2; }
        var list = Tvh.Scan(path, out string se);
        Console.WriteLine($"{list.Length} classes em {System.IO.Path.GetFileName(path)} {se}");
        int bad = 0;
        var buf = new float[480];
        var rnd = new Random(7);
        foreach (var e in list)
        {
            if (e.IsInstrument != 0) { Console.WriteLine($"  -      {e.Name,-18} instrumento ({e.Category})"); continue; }
            var pi = PluginInstance.Load(path, e.ClassIndex, e.Vendor, null, out string err);
            if (pi == null) { Console.WriteLine($"  FALHA  {e.Name,-18} não abriu: {err}"); bad++; continue; }
            double inSum = 0, outSum = 0; bool finite = true; long ph = 0;
            var sw = Stopwatch.StartNew();
            for (int b = 0; b < 200; b++)
            {
                for (int i = 0; i < 480; i++, ph++)
                {
                    double t = ph / 48000.0;
                    // "voz": 150 Hz com harmonicos, modulada em silabas, + um pouco de ruido
                    double env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t);
                    buf[i] = (float)(0.25 * env * (Math.Sin(2 * Math.PI * 150 * t) + 0.5 * Math.Sin(2 * Math.PI * 300 * t) + 0.25 * Math.Sin(2 * Math.PI * 450 * t)) + 0.01 * (rnd.NextDouble() - 0.5));
                    inSum += buf[i] * buf[i];
                }
                fixed (float* p = buf) Tvh.tvh_process(pi.Handle, p, 480);
                foreach (var v in buf) { if (float.IsNaN(v) || float.IsInfinity(v)) finite = false; outSum += v * v; }
            }
            double us = sw.Elapsed.TotalMilliseconds * 1000 / 200;
            double gainDb = 10 * Math.Log10((outSum + 1e-12) / (inSum + 1e-12));
            bool ok = finite && !pi.CheckCrashed();
            if (!ok) bad++;
            Console.WriteLine($"  {(ok ? "ok   " : "FALHA")}  {e.Name,-18} {pi.Channels / 100}→{pi.Channels % 100} canais  latência {pi.Latency,4}  {pi.ParamCount,2} parâm.  janela {(pi.HasEditor ? "sim" : "não")}  saída {gainDb,6:+0.0;-0.0} dB  {us,6:0.0} µs/bloco  [{e.Category}]");
            pi.Free();
        }
        Console.WriteLine(bad == 0 ? "TUDO OK" : "FALHAS: " + bad);
        return bad;
    }
}
