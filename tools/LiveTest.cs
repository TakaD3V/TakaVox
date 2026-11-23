// Teste ao vivo do motor: captura o mic real, processa e entrega numa saida (volume 0 = sem som).
// Uso: livetest.exe <segundos> "<nome do mic>" "<nome da saida>"
using System;
using System.Diagnostics;
using System.Threading;
using TakaVox.Audio;
using TakaVox.Dsp;

static class LiveTest
{
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 20;
        string micName = args.Length > 1 ? args[1] : "M8";
        string outName = args.Length > 2 ? args[2] : "Voicemeeter In 1";
        DeviceInfo mic = null, outDev = null;
        foreach (var d in AudioDevices.List(true)) if (d.Name.Contains(micName)) mic = d;
        foreach (var d in AudioDevices.List(false)) if (d.Name.Contains(outName)) outDev = d;
        if (mic == null) { Console.WriteLine("mic nao encontrado"); return 2; }
        Console.WriteLine("mic:   " + mic.Name);
        Console.WriteLine("saida: " + (outDev?.Name ?? "(nenhuma)"));

        var p = new ChainParams();
        var eng = new AudioEngine(p);
        eng.SetInput(mic.Id, mic.Name);
        if (outDev != null)
        {
            eng.SetOutput(outDev.Id);
            eng.MainOutput.Volume = 0f; // silencio garantido
        }
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime; var sw = Stopwatch.StartNew();
        long lastBlocks = 0;
        int fails = 0;
        for (int t = 2; t <= secs; t += 2)
        {
            Thread.Sleep(2000);
            var m = eng.Meters;
            long blocks = m.Blocks;
            double rate = (blocks - lastBlocks) / 2.0;
            lastBlocks = blocks;
            var f = eng.MainFifo;
            var mo = eng.MainOutput;
            proc.Refresh();
            double cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            Console.WriteLine($"t={t,2}s cap={(eng.CaptureRunning ? "ok" : eng.CaptureStatus)} blocos/s={rate,5:0} " +
                $"out={(mo == null ? "-" : mo.Running ? "ok" : mo.Status)} fila={f.FillMs,5:0.0}ms ratio={(f.Ratio - 1) * 1e6,7:0}ppm " +
                $"vazia={f.Underruns} silencio={mo?.SilenceInserted ?? 0} descont={eng.CaptureGlitches} maxbloco={eng.MaxProcessMs,5:0.0}ms lentos={eng.SlowBlocks} in={m.InRmsDb,6:0.0} cpu={cpu:0.00}%");
            eng.MaxProcessMs = 0;
            if (t >= 4 && (rate < 95 || rate > 105)) fails++;
        }
        int under = eng.MainFifo.Underruns;
        eng.Dispose();
        Console.WriteLine(fails == 0 ? $"OK (underruns totais: {under})" : $"PROBLEMAS: {fails}");
        return fails;
    }
}
