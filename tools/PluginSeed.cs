// Prepara uma pasta de dados de teste (TAKAVOX_DATA) com a biblioteca escaneada e alguns efeitos na cadeia,
// para tirar prints da pagina Plugins. Precisa rodar de dentro da pasta do app (usa a pasta "plugins" dele).
// Uso: pluginseed.exe "<nome do efeito>" ["<outro>"...]
using System;
using System.Linq;
using System.Threading;
using TakaVox.Plugins;

static class PluginSeed
{
    [STAThread]
    static int Main(string[] args)
    {
        int sp = Array.IndexOf(args, "--scan-plugins");
        if (sp >= 0) return PluginScanner.ChildMain(args[sp + 1], args[sp + 2]);
        var m = new PluginManager();
        m.Start();
        m.ScanAsync(false);
        Thread.Sleep(100);
        while (m.Scanning) Thread.Sleep(50);
        Console.WriteLine(m.ScanStatus);
        foreach (var e in m.Library.Where(x => x.Usable)) Console.WriteLine("  " + e.Name + " — " + PluginManager.Describe(e));
        foreach (var name in args)
        {
            var e = m.Library.FirstOrDefault(x => x.Usable && x.Name == name);
            if (e == null) { Console.WriteLine("não achei: " + name); continue; }
            var s = m.Add(e, out string err);
            Console.WriteLine(s != null ? "na cadeia: " + name : "erro: " + err);
        }
        m.Shutdown();
        return 0;
    }
}
