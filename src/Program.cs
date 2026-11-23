using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using TakaVox.Audio;
using TakaVox.Dsp;

namespace TakaVox
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // leitor de plugins (processo separado: se um plugin travar ao ser lido, so este processo cai)
            int sp = Array.IndexOf(args, "--scan-plugins");
            if (sp >= 0)
                return sp + 2 < args.Length ? Plugins.PluginScanner.ChildMain(args[sp + 1], args[sp + 2]) : 2;

            bool tray = Array.IndexOf(args, "--tray") >= 0;
            int si = Array.IndexOf(args, "--snapshot");
            string snapDir = si >= 0 && si + 1 < args.Length ? args[si + 1] : null;
            bool snapshot = snapDir != null;
            if (Array.IndexOf(args, "--uninstall") >= 0)
            {
                var ua = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                int r = Lifecycle.Uninstall();
                return r;
            }

            // os testes automaticos (pasta TAKAVOX_DATA) usam nomes proprios: nunca acordam nem fecham o TakaVox de verdade
            string data = Environment.GetEnvironmentVariable("TAKAVOX_DATA");
            string inst = string.IsNullOrEmpty(data) ? "" : "." + ((uint)data.ToLowerInvariant().GetHashCode()).ToString("X8");
            Mutex mutex = null;
            if (!snapshot)
            {
                mutex = new Mutex(true, @"Local\TakaVox.SingleInstance" + inst, out bool created);
                if (!created)
                {
                    // ja esta rodando: so pede para a janela aparecer
                    try { using (var ev = EventWaitHandle.OpenExisting(@"Local\TakaVox.Show" + inst)) ev.Set(); } catch { }
                    return 0;
                }
            }

            Log.Write("==== TakaVox iniciado" + (tray ? " (bandeja)" : "") + (snapshot ? " (snapshot)" : ""));
            // evita pausas longas do coletor de lixo enquanto o audio roda
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("ERRO FATAL: " + e.ExceptionObject);

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("ERRO na interface: " + e.Exception);
                e.Handled = true;
            };
            UI.Theme.Apply(application);

            if (!snapshot)
            {
                Lifecycle.MigrateLegacy();
                Lifecycle.RegisterUninstall();
            }
            var settings = new AppSettings();
            var chain = new ChainParams();
            AppSettings.Load(settings, chain);
            chain.Bypass = false;
            if (settings.PresetName.Contains("Voz" + "Pro")) settings.PresetName = "Padrão TakaVox";

            var engine = new AudioEngine(chain);
            var win = new UI.MainWindow(engine, settings, snapshot);

            if (snapshot)
            {
                // fora da tela e sem foco: as capturas nao atrapalham quem esta usando o PC (ou jogando)
                win.ShowActivated = false;
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = -20000; win.Top = 0;
                win.Show();
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    try { win.SaveSnapshots(snapDir); } catch (Exception ex) { Log.Write("snapshot: " + ex); Console.Error.WriteLine(ex); }
                    engine.Dispose();
                    application.Shutdown();
                };
                t.Start();
            }
            else
            {
                var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TakaVox.Show" + inst);
                var waiter = new Thread(() =>
                {
                    while (true)
                    {
                        showEvent.WaitOne();
                        win.Dispatcher.BeginInvoke(new Action(win.ShowFromTray));
                    }
                }) { IsBackground = true, Name = "TakaVox show" };
                waiter.Start();
                // pedido para fechar com calma (instalador/desinstalador): salva tudo, inclusive o estado dos plugins
                var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TakaVox.Exit" + inst);
                var exitWaiter = new Thread(() =>
                {
                    exitEvent.WaitOne();
                    win.Dispatcher.BeginInvoke(new Action(() => win.ExitApp(false)));
                }) { IsBackground = true, Name = "TakaVox exit" };
                exitWaiter.Start();
                if (!tray) win.Show();
            }

            int code = application.Run();
            GC.KeepAlive(mutex);
            return code;
        }
    }
}
