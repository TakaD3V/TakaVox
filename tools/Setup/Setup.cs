// Instalador do TakaVox: um unico .exe com o app inteiro (IA incluida) dentro. Instala por usuario, sem administrador.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TakaVox.Setup
{
    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool silent = Array.Exists(args, a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            UI.Theme.Apply(app);
            if (silent)
            {
                // instalacao silenciosa: atalhos + iniciar com o Windows, sem janelas
                try { new SetupWindow(false).RunSilent(Array.Exists(args, a => a.Equals("--launch", StringComparison.OrdinalIgnoreCase))); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            app.Run(new SetupWindow(true));
            return 0;
        }
    }

    sealed class SetupWindow : Window
    {
        const string AppName = "TakaVox";
        const string Version = "1.0.0";
        static readonly string Legacy = "Voz" + "Pro";
        static readonly string Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        static readonly string ExePath = Path.Combine(Target, AppName + ".exe");

        readonly Grid host = new Grid();
        CheckBox cbDesktop, cbAutostart, cbLaunch;
        Border barFill;
        TextBlock status;
        Button primary;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static readonly SolidColorBrush Bg = B("#0D0F14"), Card = B("#161A23"), Text = B("#E9ECF2"), Sub = B("#8D95A8"), Accent = B("#8B5CF6"), Good = B("#22D3A6");
        static SolidColorBrush B(string h) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); b.Freeze(); return b; }
        static TextBlock T(string s, double size, Brush fg, bool bold = false) => new TextBlock { Text = s, FontSize = size, Foreground = fg, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };

        public void RunSilent(bool launch)
        {
            DoInstall(true, true);
            // atualizacao silenciosa: volta direto para a bandeja, sem abrir janela na frente do jogo
            if (launch) Process.Start(new ProcessStartInfo(ExePath, "--tray") { UseShellExecute = true, WorkingDirectory = Target });
        }

        public SetupWindow(bool interactive)
        {
            if (!interactive) return;
            Title = "Instalar " + AppName;
            Width = 560; Height = 430;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            SourceInitialized += (s, e) => { try { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); } catch { } };
            Content = host;
            ShowWelcome();
        }

        static Image Logo(double size)
        {
            try
            {
                var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                var dec = new IconBitmapDecoder(st, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame best = dec.Frames[0];
                foreach (var f in dec.Frames) if (f.PixelWidth >= size && (best.PixelWidth < size || f.PixelWidth < best.PixelWidth)) best = f;
                return new Image { Source = best, Width = size, Height = size };
            }
            catch { return null; }
        }

        void Page(params UIElement[] items)
        {
            host.Children.Clear();
            var sp = new StackPanel { Margin = new Thickness(36, 30, 36, 24) };
            foreach (var i in items) if (i != null) sp.Children.Add(i);
            host.Children.Add(sp);
        }

        Button Btn(string text, Action a, bool accent = true)
        {
            var b = new Button { Content = text, Padding = new Thickness(22, 9, 22, 9), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            if (accent) b.Style = (Style)Application.Current.Resources["AccentButton"];
            b.Click += (s, e) => a();
            return b;
        }

        void ShowWelcome()
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var logo = Logo(64); if (logo != null) { logo.Margin = new Thickness(0, 0, 18, 0); head.Children.Add(logo); }
            head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { T(AppName, 28, Text, true), T("Estúdio de voz com IA • versão " + Version, 13, Sub) } });
            cbDesktop = new CheckBox { Content = T("Criar atalho na área de trabalho", 13, Text), IsChecked = true, Margin = new Thickness(0, 6, 0, 6) };
            cbAutostart = new CheckBox { Content = T("Iniciar junto com o Windows (recomendado: sua voz sempre tratada)", 13, Text), IsChecked = true, Margin = new Thickness(0, 6, 0, 6) };
            var info = T("Instala só para o seu usuário (não precisa de administrador) em:\n" + Target, 12, Sub);
            info.Margin = new Thickness(0, 14, 0, 0);
            var feat = T("✔ IA anti-ruído DeepFilterNet3 (teclado, mouse, ventoinha)   ✔ Auto-Tune   ✔ EQ, compressor e limitador de estúdio", 12.5, Good);
            feat.Margin = new Thickness(0, 18, 0, 14);
            primary = Btn("Instalar", Install);
            Page(head, feat, cbDesktop, cbAutostart, info, primary);
        }

        void Install()
        {
            bool desk = cbDesktop.IsChecked == true, auto = cbAutostart.IsChecked == true;
            barFill = new Border { Background = Accent, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 26, 0, 10) };
            bar.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(3) });
            bar.Children.Add(barFill);
            status = T("Preparando…", 13, Sub);
            Page(T("Instalando o " + AppName + "…", 22, Text, true), bar, status);
            Task.Run(() => DoInstall(desk, auto)).ContinueWith(t => Dispatcher.Invoke(() =>
            {
                if (t.IsFaulted) ShowError(t.Exception.GetBaseException().Message);
                else ShowDone();
            }));
        }

        void Report(double frac, string msg)
        {
            if (barFill == null) return; // modo silencioso
            Dispatcher.BeginInvoke(new Action(() =>
            {
                barFill.Width = Math.Max(0, Math.Min(1, frac)) * ((FrameworkElement)barFill.Parent).ActualWidth;
                status.Text = msg;
            }));
        }

        void DoInstall(bool desktop, bool autostart)
        {
            Report(0.02, "Fechando o " + AppName + " se estiver aberto…");
            // primeiro pede para fechar com calma (salva configuracoes e o estado dos plugins); se nao fechar, encerra
            try { using (var ev = System.Threading.EventWaitHandle.OpenExisting(@"Local\" + AppName + ".Exit")) ev.Set(); } catch { }
            foreach (var p in Process.GetProcessesByName(AppName))
                try { if (!p.WaitForExit(8000)) { p.Kill(); p.WaitForExit(4000); } } catch { }
            foreach (var p in Process.GetProcessesByName(Legacy))
                try { p.Kill(); p.WaitForExit(4000); } catch { }

            Directory.CreateDirectory(Target);
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(st, ZipArchiveMode.Read))
            {
                int n = zip.Entries.Count, i = 0;
                foreach (var e in zip.Entries)
                {
                    i++;
                    string dest = Path.GetFullPath(Path.Combine(Target, e.FullName));
                    if (!dest.StartsWith(Target, StringComparison.OrdinalIgnoreCase)) continue; // seguranca: nada fora da pasta
                    if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    Report(0.05 + 0.85 * i / n, "Copiando " + e.FullName + "…");
                    for (int attempt = 0; ; attempt++)
                    {
                        try { e.ExtractToFile(dest, true); break; }
                        catch (IOException) when (attempt < 10) { System.Threading.Thread.Sleep(400); }
                    }
                }
            }

            Report(0.92, "Criando atalhos…");
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));
            string deskLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");
            if (desktop) CreateShortcut(deskLnk); else if (File.Exists(deskLnk)) File.Delete(deskLnk);

            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (autostart) k.SetValue(AppName, "\"" + ExePath + "\" --tray");
                else if (k.GetValue(AppName) != null) k.DeleteValue(AppName);
            }
            long kb = 0;
            foreach (var f in Directory.GetFiles(Target, "*", SearchOption.AllDirectories)) kb += new FileInfo(f).Length / 1024;
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Taka");
                k.SetValue("DisplayIcon", ExePath + ",0");
                k.SetValue("InstallLocation", Target);
                k.SetValue("UninstallString", "\"" + ExePath + "\" --uninstall");
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            Report(1.0, "Pronto!");
        }

        static void CreateShortcut(string lnkPath)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            try
            {
                dynamic lnk = shell.CreateShortcut(lnkPath);
                lnk.TargetPath = ExePath;
                lnk.WorkingDirectory = Target;
                lnk.IconLocation = ExePath + ",0";
                lnk.Description = AppName + " – estúdio de voz com IA";
                lnk.Save();
                Marshal.FinalReleaseComObject(lnk);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        void ShowDone()
        {
            cbLaunch = new CheckBox { Content = T("Abrir o " + AppName + " agora", 13, Text), IsChecked = true, Margin = new Thickness(0, 18, 0, 0) };
            var logo = Logo(56);
            if (logo != null) { logo.HorizontalAlignment = HorizontalAlignment.Left; logo.Margin = new Thickness(0, 0, 0, 14); }
            var next = T("No primeiro uso o app te guia: instalar o microfone virtual (1 clique) e calibrar sua voz (10 s).", 13, Sub);
            next.Margin = new Thickness(0, 10, 0, 0);
            Page(logo, T("Instalado! 🎙", 24, Text, true), next, cbLaunch, Btn("Concluir", () =>
            {
                if (cbLaunch.IsChecked == true) try { Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = Target }); } catch { }
                Close();
            }));
        }

        void ShowError(string msg)
        {
            Page(T("Não deu para instalar", 22, Text, true), T(msg, 13, Sub), Btn("Fechar", Close, false));
        }
    }
}
