// Teste do gerenciador de plugins do app: leitor em processo separado (com um plugin que derruba o processo
// e outro que congela), cache, cadeia (adicionar, ligar/desligar, ordem, remover), salvar/restaurar com o estado
// de cada plugin, protecoes contra travamento e a VoiceChain com plugins.
// Usa uma pasta de dados temporaria (TAKAVOX_DATA): nao mexe nas configuracoes reais do TakaVox.
// Uso: pluginmgrtest.exe <pasta com tvhost.dll, tvtest.vst3 e tvtest2.dll>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using TakaVox;
using TakaVox.Dsp;
using TakaVox.Plugins;

static class PluginManagerTest
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok     " : "  FALHA  ") + what);
        if (!ok) fails++;
    }

    static long phase;
    // passa um seno pela cadeia (como a thread de audio faz) e mede o ganho/atraso
    static double Err(PluginChain chain, int blocks, int delay, double gain)
    {
        var x = new float[480];
        var inp = new List<float>();
        var outp = new List<float>();
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < 480; i++) { x[i] = 0.4f * (float)Math.Sin(2 * Math.PI * 440 * (phase + i) / 48000.0); inp.Add(x[i]); }
            phase += 480;
            chain.Process(x, 480);
            outp.AddRange(x);
        }
        double e = 0;
        for (int i = 960 + delay; i < outp.Count; i++) e = Math.Max(e, Math.Abs(outp[i] - gain * inp[i - delay]));
        return e;
    }

    static void WaitScan(PluginManager m, double maxSec)
    {
        var sw = Stopwatch.StartNew();
        Thread.Sleep(50);
        while (m.Scanning && sw.Elapsed.TotalSeconds < maxSec) Thread.Sleep(50);
    }

    [STAThread]
    static int Main(string[] args)
    {
        int sp = Array.IndexOf(args, "--scan-plugins");
        if (sp >= 0) return PluginScanner.ChildMain(args[sp + 1], args[sp + 2]);

        string bin = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        string work = Path.Combine(Path.GetTempPath(), "takavox_pmtest");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        string data = Path.Combine(work, "dados"), plugDir = Path.Combine(work, "plugins");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(plugDir, "sub"));
        Environment.SetEnvironmentVariable("TAKAVOX_DATA", data);
        Environment.SetEnvironmentVariable("TAKAVOX_SCAN_TIMEOUT", "4");
        File.Copy(Path.Combine(bin, "tvtest.vst3"), Path.Combine(plugDir, "tvtest.vst3"));
        File.Copy(Path.Combine(bin, "tvtest2.dll"), Path.Combine(plugDir, "sub", "tvtest2.dll"));
        File.Copy(Path.Combine(bin, "tvtest2.dll"), Path.Combine(plugDir, "tvtest2_trava.dll"));
        File.Copy(Path.Combine(bin, "tvtest2.dll"), Path.Combine(plugDir, "tvtest2_demora.dll"));
        File.Copy(Path.Combine(bin, "tvtest2.dll"), Path.Combine(plugDir, "FL Studio VSTi.dll"));
        File.Copy(Path.Combine(bin, "tvhost.dll"), Path.Combine(plugDir, "nao_e_plugin.dll"));

        Console.WriteLine("[procurar plugins]");
        var m = new PluginManager();
        var notices = new List<string>();
        m.Notice += notices.Add;
        m.Start();
        Check(m.Available && m.Started && m.Slots.Count == 0, "gerenciador iniciado, cadeia vazia");
        m.AddFolder(plugDir);
        var sw = Stopwatch.StartNew();
        m.ScanAsync(false);
        WaitScan(m, 90);
        Check(!m.Scanning, $"procura terminou em {sw.Elapsed.TotalSeconds:0.0} s ({m.ScanStatus})");
        var mine = m.Library.Where(e => e.Path.StartsWith(plugDir, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var e in mine)
            Console.WriteLine($"           · {e.FileName} [{e.ClassIndex}] '{e.Name}' → {(e.Usable ? "ok" : e.IsInstrument ? "instrumento" : e.Hidden ? "oculto" : "erro: " + e.Error)}");
        Check(mine.Count(e => e.Usable && e.Format == 3) == 2, "VST3: os 2 efeitos do arquivo");
        Check(mine.Count(e => e.Usable && e.Format == 2) == 1, "VST2 na subpasta encontrado");
        var trava = mine.FirstOrDefault(e => e.FileName == "tvtest2_trava.dll");
        Check(trava?.Error != null && trava.Error.Contains("travou"), "plugin que derruba o processo: marcado como 'travou' e o resto continuou sendo lido");
        var demora = mine.FirstOrDefault(e => e.FileName == "tvtest2_demora.dll");
        Check(demora?.Error != null && demora.Error.Contains("demorou"), "plugin que congela: cancelado pelo tempo limite");
        Check(!mine.Any(e => e.FileName == "nao_e_plugin.dll"), "DLL comum (não é plugin) ignorada sem carregar");
        var fl = mine.FirstOrDefault(e => e.FileName == "FL Studio VSTi.dll");
        Check(fl != null && fl.IsInstrument, "ponte de instrumento do FL Studio reconhecida sem carregar");
        sw.Restart();
        m.ScanAsync(false);
        WaitScan(m, 30);
        Check(sw.Elapsed.TotalSeconds < 2 && m.Library.Count(e => e.Path.StartsWith(plugDir, StringComparison.OrdinalIgnoreCase)) == mine.Count,
            $"segunda procura usa o cache: {sw.Elapsed.TotalSeconds:0.00} s");

        Console.WriteLine("[cadeia]");
        var gain = mine.First(e => e.Usable && e.Name == "TV Teste Ganho");
        var v2 = mine.First(e => e.Usable && e.Format == 2);
        var s1 = m.Add(gain, out string err);
        Check(s1 != null && s1.Loaded && m.Chain.Items.Length == 1 && m.Chain.LatencySamples == 64, "adicionou TV Teste Ganho (atraso 64 amostras) " + err);
        s1.Inst.SetParam(0, 0.25); // 0,5x
        var s2 = m.Add(v2, out err);
        Check(s2 != null && m.Chain.Items.Length == 2, "adicionou o VST2 " + err);
        s2.Inst.SetParam(0, 1.0);  // 2x
        double e1 = Err(m.Chain, 8, 64, 1.0);
        Check(e1 < 1e-5, $"áudio passa pelos dois na ordem: 0,5x · 2x, atraso 64 (erro {e1:E1})");
        m.SetEnabled(s1, false);
        Check(m.Chain.LatencySamples == 0, "desligar um plugin tira o atraso dele");
        e1 = Err(m.Chain, 6, 0, 2.0);
        Check(e1 < 1e-5, $"plugin desligado não processa (só o VST2: 2x) (erro {e1:E1})");
        m.SetEnabled(s1, true);
        m.Move(s2, -1);
        Check(m.Slots[0] == s2 && m.Chain.Items[0] == s2.Inst, "ordem trocada (VST2 primeiro)");
        m.SetAllOn(false);
        e1 = Err(m.Chain, 4, 0, 1.0);
        Check(m.Chain.LatencySamples == 0 && e1 < 1e-6, "botão geral desliga todos (voz passa direto)");
        m.SetAllOn(true);
        Err(m.Chain, 3, 64, 1.0);
        e1 = Err(m.Chain, 6, 64, 1.0);
        Check(e1 < 1e-5, $"ligado de novo (erro {e1:E1})");
        var s3 = m.Add(mine.First(e => e.Usable && e.Name == "TV Teste Inverte"), out err);
        Check(s3 != null && m.Slots.Count == 3 && m.Chain.LatencySamples == 128, "terceiro plugin (atraso total 128)");
        m.Remove(s3);
        Check(m.Slots.Count == 2 && m.Chain.Items.Length == 2 && m.Chain.LatencySamples == 64 && !s3.Loaded, "remover tira da cadeia e libera o plugin");
        var bad = m.Add(trava, out err);
        Check(bad == null && err != null, "arquivo com problema não entra na cadeia: " + err);

        Console.WriteLine("[salvar e restaurar]");
        m.Shutdown();
        Check(File.Exists(Path.Combine(data, "plugins_cadeia.txt")) && !File.Exists(Path.Combine(data, "plugins_sessao.txt")), "saída normal: cadeia salva, sem marcador de sessão");
        var m2 = new PluginManager();
        m2.Start();
        Check(m2.Slots.Count == 2 && m2.Slots[0].Name == "TV Teste VST2" && m2.Slots[1].Name == "TV Teste Ganho" && m2.Slots.All(s => s.Loaded),
            "cadeia restaurada na mesma ordem: " + string.Join(", ", m2.Slots.Select(s => s.Name)));
        var pv2 = m2.Slots[0].Inst.GetParam(0);
        var pg = m2.Slots[1].Inst.GetParam(0);
        Check(pv2 != null && Math.Abs(pv2.Value - 1.0) < 1e-6 && pg != null && Math.Abs(pg.Value - 0.25) < 1e-6,
            $"ajustes de cada plugin restaurados (VST2 {pv2?.Value}, VST3 {pg?.Value})");
        Err(m2.Chain, 3, 64, 1.0);
        e1 = Err(m2.Chain, 6, 64, 1.0);
        Check(e1 < 1e-5, $"o som depois de reabrir é igual ao de antes (erro {e1:E1})");
        Check(m2.Library.Count > 0 && m2.UserFolders.Count == 1, "biblioteca e pastas também voltaram");

        Console.WriteLine("[proteções]");
        string key = m2.Slots[1].Key;
        File.WriteAllText(Path.Combine(data, "plugins_abrindo.txt"), key); // simula: o app caiu abrindo esse plugin
        var m3 = new PluginManager();
        var n3 = new List<string>();
        m3.Notice += n3.Add;
        m3.Start();
        var cs = m3.Slots.First(s => s.Key == key);
        Check(!cs.Loaded && !cs.Enabled && cs.Error != null && n3.Any(x => x.Contains("fechou o TakaVox")), "plugin que derrubou o app ao abrir: não abre sozinho de novo (" + cs.Error + ")");
        Check(m3.Chain.Bypass && n3.Any(x => x.Contains("inesperada")), "a sessão anterior caiu: plugins começam desligados");
        Check(m3.Reload(cs, out err) && cs.Loaded && cs.Enabled, "dá para abrir de novo pelo botão " + err);
        m3.SetAllOn(true);
        var g3 = m3.Slots.First(s => s.Name == "TV Teste Ganho");
        g3.Inst.SetParam(2, 1.0); // parametro "Travar" do plugin de teste
        Err(m3.Chain, 2, 0, 1);
        m3.Tick();
        Check(g3.Crashed && m3.Chain.LatencySamples == 0 && n3.Any(x => x.Contains("travou e foi desligado")), "plugin que trava no áudio: desligado na hora, com aviso");
        e1 = Err(m3.Chain, 6, 0, 2.0);
        Check(e1 < 1e-5, $"a voz continua passando pelo resto da cadeia (erro {e1:E1})");
        Check(m3.Reload(g3, out err) && g3.Loaded && !g3.Crashed && m3.Chain.LatencySamples == 64, "abrir de novo depois de travar " + err);
        Err(m3.Chain, 3, 64, 1.0);
        e1 = Err(m3.Chain, 6, 64, 1.0);
        Check(e1 < 1e-5, $"voltou com os ajustes salvos (erro {e1:E1})");

        Console.WriteLine("[VoiceChain com plugins]");
        using (var vc = new VoiceChain(new ChainParams(), true))
        {
            int baseLat = vc.LatencySamples;
            vc.Plugins = m3.Chain;
            Check(vc.LatencySamples == baseLat + 64, $"latência da cadeia inclui os plugins ({baseLat} + 64 = {vc.LatencySamples})");
            var x = new float[480];
            var rnd = new Random(1);
            bool finite = true;
            var t = Stopwatch.StartNew();
            for (int b = 0; b < 300; b++)
            {
                for (int i = 0; i < 480; i++) x[i] = (float)(rnd.NextDouble() - 0.5) * 0.2f;
                vc.Process(x);
                foreach (var v in x) if (float.IsNaN(v) || float.IsInfinity(v)) finite = false;
            }
            Check(finite, $"3 s de áudio pela cadeia completa + plugins, sem erro ({t.Elapsed.TotalMilliseconds / 300:0.00} ms por bloco)");
        }
        m3.Shutdown();
        Console.WriteLine(fails == 0 ? "TUDO OK" : "FALHAS: " + fails);
        return fails;
    }
}
