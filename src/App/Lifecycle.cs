using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using TakaVox.Audio;
using TakaVox.Dsp;
using TakaVox.Interop;

namespace TakaVox
{
    /// <summary>Migracao da versao anterior (nome antigo) e desinstalacao.</summary>
    public static class Lifecycle
    {
        // nome da primeira versao do app (antes de virar TakaVox)
        static readonly string Legacy = "Voz" + "Pro";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TakaVox";

        static string Programs => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        /// <summary>Importa configuracoes da versao antiga e remove os restos dela (processo, atalhos, autostart, pasta).</summary>
        public static void MigrateLegacy()
        {
            if (Legacy == "TakaVox") return; // ainda nao renomeado
            try
            {
                foreach (var p in Process.GetProcessesByName(Legacy))
                {
                    try { p.Kill(); p.WaitForExit(3000); Log.Write("Versão antiga fechada (pid " + p.Id + ")"); } catch { }
                }
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string oldDir = Path.Combine(appData, Legacy), newDir = Log.Dir;
                if (Directory.Exists(oldDir) && !File.Exists(Path.Combine(newDir, "config.ini")))
                {
                    Directory.CreateDirectory(newDir);
                    foreach (var f in Directory.GetFiles(oldDir))
                    {
                        string name = Path.GetFileName(f);
                        if (name.StartsWith(Legacy, StringComparison.OrdinalIgnoreCase)) name = "TakaVox" + name.Substring(Legacy.Length);
                        File.Copy(f, Path.Combine(newDir, name), true);
                    }
                    string oldPresets = Path.Combine(oldDir, "presets");
                    if (Directory.Exists(oldPresets))
                    {
                        Directory.CreateDirectory(AppSettings.PresetDir);
                        foreach (var f in Directory.GetFiles(oldPresets)) File.Copy(f, Path.Combine(AppSettings.PresetDir, Path.GetFileName(f)), true);
                    }
                    // a IA nova muda o significado do limiar do portao e pede uma calibracao nova
                    string cfg = Path.Combine(newDir, "config.ini");
                    if (File.Exists(cfg))
                    {
                        string t = File.ReadAllText(cfg);
                        t = t.Replace("Padrão " + Legacy, "Padrão TakaVox");
                        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?m)^GateThresholdDb=.*$", "GateThresholdDb=-55");
                        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?m)^Calibrated=.*$", "Calibrated=0");
                        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?m)^MonitorOn=.*$", "MonitorOn=0");
                        File.WriteAllText(cfg, t);
                    }
                    Log.Change($"Configurações, presets e histórico importados da versão anterior ({Legacy} → TakaVox).");
                }
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k?.GetValue(Legacy) != null) { k.DeleteValue(Legacy); Log.Change($"Inicialização automática antiga ({Legacy}) removida."); }
                }
                foreach (var lnk in new[] { Path.Combine(Programs, Legacy + ".lnk"), Path.Combine(Desktop, Legacy + ".lnk") })
                    if (File.Exists(lnk)) { File.Delete(lnk); Log.Write("Atalho antigo removido: " + lnk); }
                string oldInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Legacy);
                if (Directory.Exists(oldInstall) && !AppDomain.CurrentDomain.BaseDirectory.StartsWith(oldInstall, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(oldInstall, true); Log.Change($"Pasta da versão antiga removida ({oldInstall})."); }
                    catch (Exception ex) { Log.Write("Não deu para apagar a pasta antiga: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log.Write("Migração: " + ex.Message); }
        }

        /// <summary>Registra o app em "Configuracoes > Apps instalados" (para desinstalar pelo Windows).</summary>
        public static void RegisterUninstall()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string dir = Path.GetDirectoryName(exe);
                if (!dir.EndsWith(Path.Combine("Programs", "TakaVox"), StringComparison.OrdinalIgnoreCase)) return; // so a copia instalada
                long kb = 0;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) kb += new FileInfo(f).Length / 1024;
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "TakaVox");
                    k.SetValue("DisplayVersion", UI.MainWindow.AppVersion);
                    k.SetValue("Publisher", "Taka");
                    k.SetValue("DisplayIcon", exe + ",0");
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                    k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall");
                    k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("URLInfoAbout", "https://github.com/");
                }
            }
            catch (Exception ex) { Log.Write("Registro de desinstalação: " + ex.Message); }
        }

        /// <summary>Desinstala: devolve o Windows ao normal, remove atalhos/registro e agenda a remocao da pasta.</summary>
        public static int Uninstall()
        {
            if (MessageBox.Show("Desinstalar o TakaVox?\n\nO microfone padrão do Windows volta para o seu microfone de verdade e o app é removido.\n" +
                                "(O VB-CABLE continua instalado; remova em Configurações › Apps se quiser.)",
                    "Desinstalar TakaVox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return 1;
            int me = Process.GetCurrentProcess().Id;
            try { using (var ev = System.Threading.EventWaitHandle.OpenExisting(@"Local\TakaVox.Exit")) ev.Set(); } catch { }
            foreach (var p in Process.GetProcessesByName("TakaVox"))
                if (p.Id != me) try { if (!p.WaitForExit(8000)) { p.Kill(); p.WaitForExit(3000); } } catch { }

            var app = new AppSettings(); var chain = new ChainParams();
            AppSettings.Load(app, chain);
            try
            {
                string mic = AudioDevices.IsActive(app.InputDeviceId) ? app.InputDeviceId : AudioDevices.GuessPhysicalMic(app.InputDeviceName)?.Id;
                if (mic != null && SystemTweaks.IsVirtualMicDefault())
                    AudioDevices.SetDefault(mic, ERole.Console, ERole.Multimedia, ERole.Communications);
                SystemTweaks.SetDucking(int.TryParse(app.OrigDucking, out var d) ? d : (int?)null);
                SystemTweaks.SetAutostart(false);
            }
            catch (Exception ex) { Log.Write("Desinstalação (Windows): " + ex.Message); }
            foreach (var lnk in new[] { Path.Combine(Programs, "TakaVox.lnk"), Path.Combine(Desktop, "TakaVox.lnk") })
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

            bool removeData = MessageBox.Show("Apagar também suas configurações, presets e histórico?", "Desinstalar TakaVox",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string cmd = "/c timeout /t 2 /nobreak >nul";
            if (dir.EndsWith(Path.Combine("Programs", "TakaVox"), StringComparison.OrdinalIgnoreCase)) cmd += $" & rmdir /s /q \"{dir}\"";
            if (removeData) cmd += $" & rmdir /s /q \"{Log.Dir}\"";
            try { Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() }); } catch { }
            MessageBox.Show("TakaVox desinstalado. Valeu por usar! 🎙", "TakaVox", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
    }
}
