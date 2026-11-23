using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TakaVox.Audio;
using TakaVox.Interop;

namespace TakaVox
{
    /// <summary>Ajustes do Windows feitos pelo TakaVox (todos reversiveis pelo botao "Restaurar").</summary>
    public static class SystemTweaks
    {
        const string AudioKey = @"Software\Microsoft\Multimedia\Audio";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "TakaVox";

        /// <summary>
        /// "Quando o Windows detectar comunicacao": 0 = silenciar outros sons, 1 = reduzir 80% (padrao),
        /// 2 = reduzir 50%, 3 = nao fazer nada. null = valor ausente (padrao).
        /// </summary>
        public static int? GetDucking()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(AudioKey))
                {
                    var v = k?.GetValue("UserDuckingPreference");
                    if (v is int i) return i;
                }
            }
            catch { }
            return null;
        }

        public static void SetDucking(int? value)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(AudioKey))
            {
                if (value.HasValue) k.SetValue("UserDuckingPreference", value.Value, RegistryValueKind.DWord);
                else if (k.GetValue("UserDuckingPreference") != null) k.DeleteValue("UserDuckingPreference");
            }
        }

        public static bool GetAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    var v = k?.GetValue(RunName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        public static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray");
                else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
            }
        }

        /// <summary>Garante o caminho atual no "iniciar com o Windows" (se o app mudou de pasta).</summary>
        public static void RefreshAutostartPath()
        {
            if (GetAutostart()) SetAutostart(true);
        }

        /// <summary>Pacote oficial do VB-CABLE baixado sob demanda (nao vem dentro do instalador do app: licenca da VB-Audio).</summary>
        public static string VbCableDir => Path.Combine(Log.Dir, "vbcable");
        public static string VbCableSetupPath => Path.Combine(VbCableDir, "VBCABLE_Setup_x64.exe");

        /// <summary>
        /// Baixa o pacote OFICIAL do VB-CABLE do site da VB-Audio, sem modificar nada,
        /// e confere a assinatura digital do instalador. Retorna o caminho do setup.
        /// </summary>
        public static string DownloadVbCable(Action<string> progress)
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            string name = "VBCABLE_Driver_Pack46.zip";
            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "TakaVox";
                    string html = wc.DownloadString("https://vb-audio.com/Cable/index.htm");
                    int best = -1;
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(html, @"VBCABLE_Driver_Pack(\d+)\.zip"))
                        if (int.TryParse(m.Groups[1].Value, out int v) && v > best) best = v;
                    if (best > 0) name = $"VBCABLE_Driver_Pack{best}.zip";
                }
            }
            catch (Exception ex) { Log.Write("Página do VB-CABLE indisponível, usando versão conhecida: " + ex.Message); }

            Directory.CreateDirectory(VbCableDir);
            string zip = Path.Combine(VbCableDir, name);
            progress?.Invoke("Baixando " + name + " do site oficial (vb-audio.com)…");
            using (var wc = new System.Net.WebClient())
            {
                wc.Headers[System.Net.HttpRequestHeader.UserAgent] = "TakaVox";
                wc.DownloadFile("https://download.vb-audio.com/Download_CABLE/" + name, zip);
            }
            progress?.Invoke("Conferindo a assinatura digital…");
            foreach (var f in Directory.GetFiles(VbCableDir)) if (!f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) File.Delete(f);
            System.IO.Compression.ZipFile.ExtractToDirectory(zip, VbCableDir);
            if (!File.Exists(VbCableSetupPath)) throw new FileNotFoundException("O pacote baixado não tem o instalador esperado.");
            if (!Signature.IsTrusted(VbCableSetupPath, "BUREL"))
                throw new InvalidOperationException("A assinatura digital do instalador do VB-CABLE não confere. Por segurança, não vou executar.");
            Log.Change("Pacote oficial do VB-CABLE baixado de vb-audio.com e assinatura conferida (" + name + ").");
            return VbCableSetupPath;
        }

        /// <summary>Abre o instalador OFICIAL do VB-CABLE (o Windows pede confirmacao de administrador).</summary>
        public static Process LaunchVbCableInstaller(bool uninstall = false)
        {
            string exe = VbCableSetupPath;
            if (!File.Exists(exe)) throw new FileNotFoundException("Instalador do VB-CABLE não encontrado", exe);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            return Process.Start(psi);
        }

        public static void OpenSoundControlPanel(bool recordingTab)
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,," + (recordingTab ? "1" : "0")) { UseShellExecute = true }); } catch { }
        }

        /// <summary>Faz o mic do TakaVox (CABLE Output) virar o microfone padrao em todos os papeis.</summary>
        public static bool MakeVirtualMicDefault(out string name)
        {
            name = null;
            var cab = AudioDevices.FindCableOutput();
            if (cab == null) return false;
            name = cab.Name;
            return AudioDevices.SetDefault(cab.Id, ERole.Console, ERole.Multimedia, ERole.Communications);
        }

        public static bool IsVirtualMicDefault()
        {
            var cab = AudioDevices.FindCableOutput();
            if (cab == null) return false;
            return AudioDevices.GetDefaultId(true, ERole.Console) == cab.Id &&
                   AudioDevices.GetDefaultId(true, ERole.Communications) == cab.Id;
        }
    }
}
