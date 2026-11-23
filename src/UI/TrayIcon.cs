using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TakaVox.UI
{
    /// <summary>Icone na bandeja (perto do relogio) com menu rapido.</summary>
    sealed class TrayIcon : IDisposable
    {
        readonly NotifyIcon icon;
        readonly ToolStripMenuItem miBypass, miMonitor, miMute, miVoice;
        readonly Icon iconOn, iconMuted;   // som saindo / microfone mudo
        bool muted;

        public TrayIcon(MainWindow w)
        {
            iconOn = LoadIcon("TakaVox.ico");
            iconMuted = LoadIcon("TakaVox-mudo.ico") ?? iconOn;
            icon = new NotifyIcon { Text = "TakaVox", Icon = iconOn ?? SystemIcons.Application, Visible = true };
            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Abrir TakaVox", null, (s, e) => w.ShowFromTray()) { Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());
            miMute = new ToolStripMenuItem("Microfone mudo (Ctrl+Alt+M)", null, (s, e) => w.ToggleMute());
            miBypass = new ToolStripMenuItem("Efeitos ligados", null, (s, e) => w.ToggleBypassFromTray());
            miMonitor = new ToolStripMenuItem("Ouvir minha voz", null, (s, e) => w.ToggleMonitorFromTray());
            miVoice = new ToolStripMenuItem("Modificador de voz (" + MainWindow.HotkeyVoice + ")", null, (s, e) => w.ToggleVoice());
            menu.Items.Add(miMute);
            menu.Items.Add(miBypass);
            menu.Items.Add(miMonitor);
            menu.Items.Add(miVoice);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Sair do TakaVox", null, (s, e) => w.ExitApp(true)));
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) w.ShowFromTray(); };
        }

        static Icon LoadIcon(string file)
        {
            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
                if (File.Exists(ico)) return new Icon(ico, SystemInformation.SmallIconSize);
                if (file != "TakaVox.ico") return null;
                return Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            }
            catch { return null; }
        }

        public void Update(bool bypass, bool monitor)
        {
            miBypass.Checked = !bypass;
            miMonitor.Checked = monitor;
        }

        /// <summary>Marca o mudo no menu e troca o icone (som saindo / mudo).</summary>
        public void SetMuted(bool m)
        {
            miMute.Checked = m;
            if (m == muted) return;
            muted = m;
            var ic = m ? iconMuted : iconOn;
            if (ic != null) icon.Icon = ic;
        }

        public void SetVoice(bool on, string name)
        {
            miVoice.Checked = on;
            string t = "Modificador de voz" + (on ? ": " + name : "") + " (" + MainWindow.HotkeyVoice + ")";
            if (miVoice.Text != t) miVoice.Text = t;
        }

        public void SetTooltip(string text)
        {
            if (text.Length > 63) text = text.Substring(0, 63);
            if (icon.Text != text) icon.Text = text;
        }

        public void Balloon(string title, string text) => icon.ShowBalloonTip(6000, title, text, ToolTipIcon.Info);

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            iconOn?.Dispose();
            if (iconMuted != iconOn) iconMuted?.Dispose();
        }
    }
}
