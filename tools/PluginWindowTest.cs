// Teste da janela de plugin no estilo do FL Studio (src/UI/PluginWindow.cs), sem aparecer na tela:
// as janelas abrem FORA da area visivel, sem foco, e sao capturadas com PrintWindow (inclui a janela nativa do plugin).
//  - plugin com janela propria (tvtest.vst3): embutida na moldura, tamanho certo, troca para knobs e volta;
//  - plugin sem janela (mda): painel de knobs com todos os parametros; knob muda o parametro de verdade;
//  - presets de fabrica (programas): lista, nomes, troca pelas setas.
// Uso: pluginwindowtest.exe <pasta-com-tvtest.vst3-e-mda-vst3.vst3> <pasta-para-capturas>
// (compilar junto com todos os .cs de src/, com /main:PluginWindowTest)
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TakaVox.Plugins;
using TakaVox.UI;

static class PluginWindowTest
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok     " : "  FALHA  ") + what); if (!ok) fails++; }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static void Pump(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end)
        {
            var f = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => f.Continue = false));
            Dispatcher.PushFrame(f);
            System.Threading.Thread.Sleep(15);
        }
    }

    static void Capture(Window w, string path)
    {
        var h = new WindowInteropHelper(w).Handle;
        GetWindowRect(h, out var r);
        int width = r.R - r.L, height = r.B - r.T;
        using (var bmp = new System.Drawing.Bitmap(width, height))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(h, hdc, 2); // PW_RENDERFULLCONTENT: inclui a janela nativa do plugin
            g.ReleaseHdc(hdc);
            bmp.Save(path);
        }
        Console.WriteLine($"  captura: {Path.GetFileName(path)} ({width}x{height})");
    }

    static PluginSlot Load(string path, int cls, string name)
    {
        var inst = PluginInstance.Load(path, cls, "", null, out string err);
        if (inst == null) { Console.WriteLine("  não abriu " + name + ": " + err); return null; }
        return new PluginSlot { Path = path, ClassIndex = cls, Format = inst.Format, Name = inst.Name, Vendor = inst.Vendor, Inst = inst };
    }

    static PluginWindow OpenOffscreen(Window owner, PluginSlot s, bool knobs)
    {
        var w = new PluginWindow(owner, s, knobs) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000, Top = 100 };
        w.Open();
        Pump(500);
        return w;
    }

    [STAThread]
    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : "build/native", outDir = args.Length > 1 ? args[1] : "build/test/plugwin";
        Directory.CreateDirectory(outDir);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(app);
        if (!Tvh.Init(out string e)) { Console.WriteLine("tvhost: " + e); return 2; }
        var owner = new Window { Width = 10, Height = 10, Left = -9000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        owner.Show();

        // 1) plugin com janela propria
        Console.WriteLine("1) Plugin com janela própria (tvtest.vst3)");
        var t = Load(Path.Combine(dir, "tvtest.vst3"), 0, "tvtest");
        if (t != null)
        {
            Check(t.Inst.HasEditor, "tem janela própria");
            var w = OpenOffscreen(owner, t, false);
            Check(w.ShowingOriginal, "abre a janela original embutida");
            Check(t.Inst.EditorOpen, "a janela nativa está aberta dentro da moldura");
            Check(w.ActualWidth >= 390 && w.ActualHeight > 60, $"moldura com tamanho de verdade ({w.ActualWidth:0}x{w.ActualHeight:0})");
            Capture(w, Path.Combine(outDir, "plugin_original.png"));
            w.Close(); Pump(200);
            Check(!t.Inst.EditorOpen, "fechar a moldura fecha a janela do plugin");
            var wk = OpenOffscreen(owner, t, true);
            Check(!wk.ShowingOriginal && !t.Inst.EditorOpen, "modo knobs (sem a janela nativa)");
            Capture(wk, Path.Combine(outDir, "plugin_knobs_tvtest.png"));
            wk.Close(); Pump(100);
            t.Inst.Free();
        }

        // 2) plugins sem janela (mda): knobs + presets
        Console.WriteLine("2) Plugins sem janela (mda-vst3)");
        string mda = Path.Combine(dir, "mda-vst3.vst3");
        var infos = Tvh.Scan(mda, out string se);
        Console.WriteLine($"  {infos.Length} efeitos no mda-vst3");
        int withPrograms = 0, shown = 0;
        foreach (var inf in infos)
        {
            if (inf.IsInstrument != 0) continue;
            var s = Load(mda, inf.ClassIndex, inf.Name);
            if (s == null) continue;
            int pc = s.Inst.ProgramCount;
            if (pc > 1) withPrograms++;
            bool capture = inf.Name == "mda Delay" || inf.Name == "mda Ambience" || inf.Name == "mda RePsycho!" || (pc > 1 && withPrograms == 1);
            if (capture)
            {
                var w = OpenOffscreen(owner, s, false);
                Check(!w.ShowingOriginal, inf.Name + ": sem janela própria -> knobs");
                Capture(w, Path.Combine(outDir, "mda_" + inf.Name.Replace("mda ", "").Replace("!", "").Replace(' ', '_') + ".png"));
                if (pc > 1)
                {
                    string n0 = s.Inst.ProgramName(0), n1 = s.Inst.ProgramName(1);
                    Check(n0.Length > 0 && n1.Length > 0, $"{inf.Name}: {pc} presets de fábrica ('{n0}', '{n1}', ...)");
                    var before = s.Inst.GetParam(0)?.Value ?? -1;
                    bool set = s.Inst.SetProgram(1);
                    Pump(200);
                    Check(set && s.Inst.CurrentProgram == 1, $"{inf.Name}: troca para o preset 2");
                }
                // um knob muda o parametro de verdade
                var p0 = s.Inst.GetParam(0);
                if (p0 != null)
                {
                    double target = p0.Value > 0.5 ? 0.2 : 0.8;
                    s.Inst.SetParam(0, target);
                    var p1 = s.Inst.GetParam(0);
                    Check(p1 != null && Math.Abs(p1.Value - target) < 0.02, $"{inf.Name}: parâmetro '{p0.Name}' muda ({p0.Display} -> {p1?.Display})");
                }
                w.Close(); Pump(100);
                shown++;
            }
            s.Inst.Free();
        }
        Console.WriteLine($"  {withPrograms} efeitos com presets de fábrica");
        Check(shown >= 3, "janelas de knobs abertas para os mda");

        owner.Close();
        Console.WriteLine(fails == 0 ? "TUDO OK" : fails + " FALHA(S)");
        return fails == 0 ? 0 : 1;
    }
}
