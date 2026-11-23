// Captura do editor de som do sound pad (fora da tela, sem pegar o foco) para conferir o layout.
// Uso: soundeditorshot.exe <pasta-de-saida>   (TAKAVOX_DATA com uma biblioteca de sons)
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TakaVox;
using TakaVox.Audio;
using TakaVox.UI;

static class SoundEditorShot
{
    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(app);
        Theme.Set("escuro");
        var lib = new SoundLibrary();
        lib.Load();
        if (lib.Items.Count == 0) { Console.WriteLine("biblioteca vazia"); return 1; }
        var it = lib.Items[0];
        if (string.IsNullOrEmpty(it.Hotkey)) it.Hotkey = "NumPad1";
        var owner = new Window { Left = -20000, Top = 0, Width = 200, Height = 100, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        owner.Show();
        var ed = new SoundEditor(owner, it, lib, new SoundBoard(), lib.Categories, (h, k) => null)
        {
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
        };
        ed.Show();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        t.Tick += (s, e) =>
        {
            t.Stop();
            var el = (FrameworkElement)ed.Content;
            el.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)el.ActualWidth + 40, (int)el.ActualHeight + 34, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(Theme.Bg, null, new Rect(0, 0, el.ActualWidth + 40, el.ActualHeight + 34));
            rtb.Render(dv);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(Path.Combine(outDir, "editor_som.png"))) enc.Save(fs);
            Console.WriteLine($"ok {el.ActualWidth}x{el.ActualHeight}");
            ed.Close();
            // janelinha "aperte a tecla" (definir a tecla de um som em 1 clique)
            var kd = new KeyCaptureDialog(owner, it.Name, "NumPad1", hk => new KeyCheck { Ok = true, Message = "✓ Livre. Aperte Enter ou clique em Salvar." })
            {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            };
            kd.Show();
            var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t2.Tick += (s2, e2) =>
            {
                t2.Stop();
                var k = (FrameworkElement)kd.Content;
                k.UpdateLayout();
                var r2 = new RenderTargetBitmap((int)k.ActualWidth + 40, (int)k.ActualHeight + 34, 96, 96, PixelFormats.Pbgra32);
                var dv2 = new DrawingVisual();
                using (var dc = dv2.RenderOpen()) dc.DrawRectangle(Theme.Bg, null, new Rect(0, 0, k.ActualWidth + 40, k.ActualHeight + 34));
                r2.Render(dv2);
                r2.Render(k);
                var enc2 = new PngBitmapEncoder();
                enc2.Frames.Add(BitmapFrame.Create(r2));
                using (var fs = File.Create(Path.Combine(outDir, "tecla_som.png"))) enc2.Save(fs);
                kd.Close();
                owner.Close();
                app.Shutdown();
            };
            t2.Start();
        };
        t.Start();
        app.Run();
        return 0;
    }
}
