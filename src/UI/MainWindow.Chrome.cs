using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using TakaVox.Audio;
using TakaVox.Dsp;

namespace TakaVox.UI
{
    /// <summary>Janela "de app profissional": barra de titulo propria, cantos arredondados, atalhos globais e protecoes do retorno.</summary>
    public sealed partial class MainWindow
    {
        public const string AppVersion = "1.0.0";
        Button maxBtn, muteBtn;
        Grid outerGrid;
        HwndSource hwndSource;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        const int HK_MUTE = 0xB001, HK_TUNE = 0xB002, HK_BYPASS = 0xB003, HK_VOICE = 0xB004;

        void SetupChrome()
        {
            SetChrome(false);
            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                try { int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, 4); } catch { }   // cantos arredondados (Win 11)
                ApplyWindowTheme();
                RegisterHotkeys(hwnd);
            };
            StateChanged += (s, e) => UpdateMaximizeState();
            // cria a janela (HWND) mesmo iniciando escondido na bandeja: os atalhos globais funcionam sempre
            new WindowInteropHelper(this).EnsureHandle();
        }

        void SetChrome(bool glass)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 40,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = glass ? new Thickness(-1) : new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        }

        /// <summary>
        /// Aplica o tema na janela do Windows: barra/borda clara ou escura e, nos temas de vidro, o desfoque acrilico do
        /// Windows 11 atras do app (feito pelo DWM na placa de video; nada roda com a janela fechada na bandeja).
        /// </summary>
        void ApplyWindowTheme()
        {
            var t = Theme.Current;
            if (snapshotMode) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            bool wasGlass = WindowChrome.GetWindowChrome(this)?.GlassFrameThickness.Left < 0;
            if (wasGlass != t.Glass) SetChrome(t.Glass);
            try
            {
                int dark = t.Light ? 0 : 1; DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
                var bc = t.Border;
                int border = t.Glass ? unchecked((int)0xFFFFFFFF) : (bc.B << 16) | (bc.G << 8) | bc.R;   // vidro: borda padrao do Windows
                DwmSetWindowAttribute(hwnd, 34, ref border, 4);
                var src = HwndSource.FromHwnd(hwnd);
                if (t.Glass)
                {
                    if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
                    var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref m);
                    int backdrop = 3; DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);   // acrilico (vidro fosco)
                }
                else
                {
                    int backdrop = 1; DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4);   // sem fundo do sistema
                    if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Color.FromRgb(t.Bg.R, t.Bg.G, t.Bg.B);
                    var m = new Margins { L = 0, R = 0, T = 0, B = 1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref m);
                }
            }
            catch { }
        }

        void UpdateMaximizeState()
        {
            if (outerGrid == null) return;
            // maximizado, o Windows "estica" a janela alem da tela: compensa para nada ficar cortado
            outerGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            if (maxBtn != null) maxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        UIElement BuildTitleBar()
        {
            var bar = new Grid { Background = Theme.Side, Height = 40 };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = Ui.H();
            left.Margin = new Thickness(14, 0, 0, 0);
            left.VerticalAlignment = VerticalAlignment.Center;
            titleIcon = AppIconImage(18, P.Mute);
            if (titleIcon != null) left.Children.Add(Ui.M(titleIcon, 0, 0, 9, 0));
            left.Children.Add(Ui.T("TakaVox", 12.5, Theme.Text, true));
            left.Children.Add(Ui.M(Ui.T("v" + AppVersion, 11, Theme.Dim), 8, 1, 0, 0));
            bar.Children.Add(Ui.At(left, 0));

            var min = new Button { Content = "", Style = (Style)Application.Current.Resources["TitleButton"], ToolTip = "Minimizar" };
            maxBtn = new Button { Content = "", Style = (Style)Application.Current.Resources["TitleButton"], ToolTip = "Maximizar" };
            var close = new Button { Content = "", Style = (Style)Application.Current.Resources["TitleClose"], ToolTip = "Fechar (continua na bandeja)" };
            min.Click += (s, e) => WindowState = WindowState.Minimized;
            maxBtn.Click += (s, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            close.Click += (s, e) => Close();
            var btns = Ui.H(min, maxBtn, close);
            foreach (UIElement b in btns.Children) WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            bar.Children.Add(Ui.At(btns, 1));
            return bar;
        }

        // ------------------------------------------------------------ icone (som saindo / mudo)

        Image titleIcon;
        bool? iconMuted;

        /// <summary>Arquivo do icone ao lado do .exe: TakaVox.ico (som saindo) ou TakaVox-mudo.ico (microfone mudo).</summary>
        static string IconFile(bool muted)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string f = Path.Combine(dir, muted ? "TakaVox-mudo.ico" : "TakaVox.ico");
            return File.Exists(f) ? f : Path.Combine(dir, "TakaVox.ico");
        }

        /// <summary>O quadro do .ico mais proximo de 'px' pixels (o menor que ainda cobre).</summary>
        static BitmapFrame IconFrame(bool muted, double px)
        {
            try
            {
                string ico = IconFile(muted);
                if (!File.Exists(ico)) return null;
                var dec = new IconBitmapDecoder(new Uri(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame best = dec.Frames[0];
                foreach (var f in dec.Frames) if (f.PixelWidth >= px && (best.PixelWidth < px || f.PixelWidth < best.PixelWidth)) best = f;
                return best;
            }
            catch { return null; }
        }

        /// <summary>Icone do app como imagem (nitido: usa o quadro do tamanho certo para a tela).</summary>
        static Image AppIconImage(double size, bool muted = false)
        {
            var frame = IconFrame(muted, size * 2);
            if (frame == null) return null;
            var img = new Image { Source = frame, Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        /// <summary>Troca o icone da janela/barra de tarefas e da barra de titulo conforme o mudo.</summary>
        void UpdateAppIcon()
        {
            bool m = P.Mute;
            if (iconMuted == m) return;
            iconMuted = m;
            try
            {
                string ico = IconFile(m);
                if (File.Exists(ico)) Icon = BitmapFrame.Create(new Uri(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var f = IconFrame(m, 36);
                if (titleIcon != null && f != null) titleIcon.Source = f;
            }
            catch { }
        }

        // ------------------------------------------------------------ mudo

        Button BuildMuteButton()
        {
            muteBtn = Ui.Btn("", ToggleMute, null, null, "Corta o microfone na hora (Discord e jogos não escutam nada). Atalho: " + HotkeyMute);
            muteBtn.MinWidth = 112;
            muteBtn.Margin = new Thickness(0, 0, 10, 0);
            muteBtn.VerticalAlignment = VerticalAlignment.Center;
            UpdateMuteButton();
            return muteBtn;
        }

        public void ToggleMute()
        {
            P.Mute = !P.Mute;
            UpdateMuteButton();
            UpdateAppIcon();
            tray?.SetMuted(P.Mute);
            Log.Write("Mudo: " + (P.Mute ? "ligado" : "desligado"));
        }

        void UpdateMuteButton()
        {
            if (muteBtn == null) return;
            bool m = P.Mute;
            Brush fg = m ? Brushes.White : (Brush)Theme.Text;
            muteBtn.Content = Ui.H(Ui.M(Ui.Icon(m ? "" : "", 13, fg), 0, 0, 8, 0), Ui.T(m ? "MUDO" : "MUTAR", 12.5, fg, true));
            muteBtn.Background = m ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x3A)) : (Brush)Theme.BtnBg;
            muteBtn.BorderBrush = m ? muteBtn.Background : Theme.BtnBorder;
        }

        // ------------------------------------------------------------ atalhos globais

        void RegisterHotkeys(IntPtr hwnd)
        {
            if (snapshotMode || hwnd == IntPtr.Zero) return;
            hwndSource = HwndSource.FromHwnd(hwnd);
            hwndSource?.AddHook(WndProc);
            HotkeyMute = Register(hwnd, HK_MUTE, Key.M);
            HotkeyTune = Register(hwnd, HK_TUNE, Key.T);
            HotkeyBypass = Register(hwnd, HK_BYPASS, Key.B);
            HotkeyVoice = Register(hwnd, HK_VOICE, Key.V);
            HotkeyStopSounds = Register(hwnd, HK_STOPSOUNDS, Key.P);
            Log.Write($"Atalhos: mudo={HotkeyMute} auto-tune={HotkeyTune} efeitos={HotkeyBypass} modificador={HotkeyVoice} parar-sons={HotkeyStopSounds}");
            RegisterSoundHotkeys();
            Closed += (s, e) =>
            {
                UnregisterHotKey(hwnd, HK_MUTE); UnregisterHotKey(hwnd, HK_TUNE); UnregisterHotKey(hwnd, HK_BYPASS); UnregisterHotKey(hwnd, HK_VOICE);
                UnregisterHotKey(hwnd, HK_STOPSOUNDS);
                foreach (var id in soundHotkeys) UnregisterHotKey(hwnd, id);
            };
        }

        public static string HotkeyMute = "—", HotkeyTune = "—", HotkeyBypass = "—", HotkeyVoice = "—";

        /// <summary>Tenta Ctrl+Alt+tecla; se outro programa ja usa, tenta Ctrl+Alt+Shift+tecla.</summary>
        static string Register(IntPtr hwnd, int id, Key key)
        {
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (RegisterHotKey(hwnd, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, vk)) return "Ctrl+Alt+" + key;
            if (RegisterHotKey(hwnd, id, MOD_CONTROL | MOD_ALT | 0x4 /*SHIFT*/ | MOD_NOREPEAT, vk)) return "Ctrl+Alt+Shift+" + key;
            Log.Write("Atalho Ctrl+Alt+" + key + " ocupado por outro programa (e a alternativa com Shift também).");
            return "indisponível";
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                switch (wParam.ToInt32())
                {
                    case HK_MUTE: ToggleMute(); tray?.Balloon(P.Mute ? "Microfone MUDO" : "Microfone ligado", P.Mute ? $"Ninguém te escuta agora ({HotkeyMute} para voltar)." : "Sua voz voltou a sair."); break;
                    case HK_TUNE: P.TuneOn = !P.TuneOn; P.Touch(); OnParamChanged(); tray?.Balloon("Auto-Tune " + (P.TuneOn ? "LIGADO" : "desligado"), "Atalho: " + HotkeyTune); break;
                    case HK_BYPASS: SetBypass(!P.Bypass); break;
                    case HK_VOICE: ToggleVoice(); break;
                    case HK_STOPSOUNDS: StopAllSounds(); break;
                    default:
                        int id = wParam.ToInt32();
                        if (id >= HK_SOUND && id <= 0xBFFF) PlaySoundKey(id - HK_SOUND);
                        break;
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        // ------------------------------------------------------------ protecoes do retorno

        /// <summary>Antes de ligar o retorno numa saida que parece caixa de som, confirma (evita microfonia).</summary>
        bool ConfirmMonitorDevice(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (("|" + app.MonitorConfirmed + "|").Contains("|" + id + "|")) return true;
            int ff = AudioDevices.GetFormFactor(id);
            if (ff == 3 || ff == 5) return true; // fone de ouvido / headset
            var r = MessageBox.Show(
                "O retorno vai tocar em: " + (AudioDevices.GetNameById(id) ?? "saída") + "\n\n" +
                "Isso é um FONE DE OUVIDO?\n\n" +
                "• Sim → pode ligar.\n" +
                "• Não, é caixa de som → não ligue: o som da caixa volta no microfone e vira microfonia (aquela interferência/eco robótico).",
                "Ouvir minha voz", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return false;
            app.MonitorConfirmed = (app.MonitorConfirmed + "|" + id).Trim('|');
            MarkDirty();
            return true;
        }

        int howlTicks;
        int howlBin = -1;
        readonly float[] howlBuf = new float[4096];
        readonly float[] howlSpec = new float[2049];

        /// <summary>Se aparecer microfonia (um tom unico, forte e constante) com o retorno ligado, desliga o retorno.</summary>
        void HowlCheck()
        {
            if (!app.MonitorOn) { howlTicks = 0; return; }
            engine.TapIn.Snapshot(howlBuf);
            fft.PowerDb(howlBuf, howlSpec);
            int k1 = -1; float v1 = -200;
            for (int k = 9; k < 1400; k++) if (howlSpec[k] > v1) { v1 = howlSpec[k]; k1 = k; }
            float v2 = -200;
            for (int k = 9; k < 1400; k++)
                if (Math.Abs(k - k1) > 4 && howlSpec[k] > howlSpec[k - 1] && howlSpec[k] >= howlSpec[k + 1] && howlSpec[k] > v2) v2 = howlSpec[k];
            bool tonal = v1 > -16f && v1 - v2 > 22f && (howlBin < 0 || Math.Abs(k1 - howlBin) <= 2);
            howlBin = tonal ? k1 : -1;
            howlTicks = tonal ? howlTicks + 1 : 0;
            if (howlTicks >= 3)
            {
                howlTicks = 0;
                SetMonitorOn(false);
                Log.Change("Microfonia detectada com o retorno ligado: retorno desligado automaticamente.");
                Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(
                    "Detectei microfonia: o som que sai no seu fone/caixa está voltando para o microfone.\n\n" +
                    "Desliguei o \"Ouvir minha voz\". Use um fone de ouvido (e não muito alto) para se ouvir.",
                    "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning)));
            }
        }
    }
}
