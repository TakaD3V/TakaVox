// Mede o consumo real do motor de audio do TakaVox (com as configuracoes do usuario) por varios minutos:
// CPU do processo e por thread, memoria (privada, working set, heap .NET), coletas de lixo, tempo por bloco
// e custo da analise do removedor de ventoinha. Nao mexe no microfone padrao do Windows: usa o mic e manda
// o audio para uma saida com volume 0.
// Uso: perftest.exe <segundos> "<mic>" "<saida>" [semtom|-] [voz do modificador|ia:N|-]
//   ia:N = voz com IA (Beatrice), voz N do primeiro modelo instalado em TAKAVOX_DATA\ia
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using TakaVox;
using TakaVox.Audio;
using TakaVox.Dsp;

static class PerfTest
{
    static int Main(string[] args)
    {
        int secs = args.Length > 0 ? int.Parse(args[0]) : 60;
        string micName = args.Length > 1 ? args[1] : "M8";
        string outName = args.Length > 2 ? args[2] : "Voicemeeter In 1";
        var app = new AppSettings();
        var p = new ChainParams();
        AppSettings.Load(app, p); // as mesmas configuracoes do app do usuario
        if (args.Length > 3 && args[3] == "semtom") p.ToneOn = false;
        p.VcOn = false;
        TakaVox.Plugins.AiVoice ai = null;
        List<TakaVox.Plugins.BeatriceModel> models = null;
        string plugin = null;
        if (args.Length > 4 && args[4].StartsWith("ia:"))
        {
            models = TakaVox.Plugins.BeatriceModel.LoadAll(TakaVox.Plugins.Beatrice.ModelsDir);
            plugin = TakaVox.Plugins.Beatrice.FindPlugin();
            if (models.Count == 0 || plugin == null) { Console.WriteLine("voz com IA não instalada em " + TakaVox.Plugins.Beatrice.Root); return 2; }
            ai = new TakaVox.Plugins.AiVoice { SourceMidi = 47.5f };
            p.VcOn = true; p.VcAiOn = true; p.VcAiModel = models[0].Code; p.VcAiVoice = int.Parse(args[4].Substring(3)); p.Touch();
            Console.WriteLine("voz com IA: " + models[0].Voices[p.VcAiVoice].Name);
        }
        else if (args.Length > 4 && args[4] != "-")
        {
            var v = VoicePresets.Find(args[4]);
            if (v == null) { Console.WriteLine("voz não encontrada: " + args[4]); return 2; }
            v.ApplyTo(p); p.VcOn = true; p.Touch();
            Console.WriteLine("modificador de voz: " + v.Name);
        }
        DeviceInfo mic = AudioDevices.List(true).FirstOrDefault(d => d.Name.Contains(micName));
        DeviceInfo outDev = AudioDevices.List(false).FirstOrDefault(d => d.Name.Contains(outName));
        if (mic == null || outDev == null) { Console.WriteLine("mic ou saída não encontrados"); return 2; }
        Console.WriteLine($"mic: {mic.Name} · saída (volume 0): {outDev.Name} · IA: {VoiceChain.EngineNames[p.NoiseEngine]} · removedor de ventoinha: {(p.ToneOn ? "ligado" : "desligado")}");

        var proc = Process.GetCurrentProcess();
        var eng = new AudioEngine(p);
        if (ai != null) eng.Ai = ai;
        eng.SetInput(mic.Id, mic.Name);
        eng.SetOutput(outDev.Id);
        eng.MainOutput.Volume = 0f;
        // a "interface": atualiza a voz com IA 4x por segundo, como o app
        var uiStop = false;
        var ui = new Thread(() => { while (!uiStop) { try { ai?.Update(p, models, plugin); } catch (Exception ex) { Console.WriteLine("ia: " + ex.Message); } Thread.Sleep(250); } }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Thread.Sleep(3000); // IA carregando
        if (ai != null) Console.WriteLine($"voz com IA: {ai.State} (carregou em {ai.LoadMs} ms), saindo pela IA: {eng.AiActive}");
        proc.Refresh();
        var thr0 = new Dictionary<int, double>();
        foreach (ProcessThread t in proc.Threads) try { thr0[t.Id] = t.TotalProcessorTime.TotalMilliseconds; } catch { }
        var cpuStart = proc.TotalProcessorTime;
        var cpuLast = cpuStart;
        var sw = Stopwatch.StartNew();
        double lastMs = sw.Elapsed.TotalMilliseconds;
        double an0 = ToneRemover.StatAnalysisMs;
        int ac0 = ToneRemover.StatAnalyses;
        for (int t = 10; t <= secs; t += 10)
        {
            Thread.Sleep(10000);
            proc.Refresh();
            double now = sw.Elapsed.TotalMilliseconds;
            double cpu = (proc.TotalProcessorTime - cpuLast).TotalMilliseconds / (now - lastMs) * 100;
            cpuLast = proc.TotalProcessorTime;
            lastMs = now;
            int an = ToneRemover.StatAnalyses - ac0;
            double anMs = ToneRemover.StatAnalysisMs - an0;
            Console.WriteLine($"t={t,3}s CPU {cpu,5:0.0}% de 1 núcleo · privada {proc.PrivateMemorySize64 / 1048576,4} MB · working set {proc.WorkingSet64 / 1048576,4} MB · heap .NET {GC.GetTotalMemory(false) / 1048576.0,5:0.0} MB · " +
                              $"GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} · bloco máx {eng.MaxProcessMs,4:0.0} ms · lentos {eng.SlowBlocks} · cortes {eng.ToneNotches.Length} · " +
                              $"análises {an} ({(an > 0 ? anMs / an : 0):0.0} ms cada, {anMs / (now - (now - 10000 * (t / 10))) * 100 / 1:0.00}%)");
            eng.MaxProcessMs = 0;
            if (ai != null) Console.WriteLine($"      IA: {ai.State}, na saída: {eng.AiActive}, {ai.ReadMsPerBlock():0.00} ms/bloco na thread dela (pior {ai.MaxMs:0.0}), atrasos {ai.Late}, pulos {ai.Skipped}, esperas {ai.Waits}");
        }
        proc.Refresh();
        double total = (proc.TotalProcessorTime - cpuStart).TotalMilliseconds / sw.Elapsed.TotalMilliseconds * 100;
        Console.WriteLine($"MÉDIA: {total:0.0}% de 1 núcleo ({total / Environment.ProcessorCount:0.0}% do PC com {Environment.ProcessorCount} núcleos lógicos)");
        Console.WriteLine($"removedor de ventoinha: {ToneRemover.StatAnalyses - ac0} análises, {(ToneRemover.StatAnalysisMs - an0) / Math.Max(1, ToneRemover.StatAnalyses - ac0):0.0} ms cada = {(ToneRemover.StatAnalysisMs - an0) / sw.Elapsed.TotalMilliseconds * 100:0.0}% de 1 núcleo");
        Console.WriteLine("threads mais pesadas (% de 1 núcleo | prioridade):");
        foreach (ProcessThread th in proc.Threads.Cast<ProcessThread>()
                     .Select(th => new { th, ms = SafeMs(th) - (thr0.TryGetValue(th.Id, out var v) ? v : 0) })
                     .OrderByDescending(x => x.ms).Take(6).Select(x => x.th))
        {
            double ms = SafeMs(th) - (thr0.TryGetValue(th.Id, out var v) ? v : 0);
            Console.WriteLine($"   thread {th.Id,6}: {ms / sw.Elapsed.TotalMilliseconds * 100,5:0.0}%  prioridade {th.CurrentPriority}");
        }
        uiStop = true;
        ai?.Shutdown();
        eng.Dispose();
        return 0;
    }

    static double SafeMs(ProcessThread t) { try { return t.TotalProcessorTime.TotalMilliseconds; } catch { return 0; } }
}
