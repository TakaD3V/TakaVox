using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TakaVox.Audio;

namespace TakaVox.UI
{
    /// <summary>Atalhos de teclado do sound pad: texto salvo ("Ctrl+Alt+NumPad1"), texto bonito ("Ctrl+Alt+Num 1") e registro no Windows.</summary>
    static class HotkeyText
    {
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

        public static string Format(ModifierKeys m, Key k)
        {
            var parts = new List<string>();
            if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(k.ToString());
            return string.Join("+", parts);
        }

        public static bool Parse(string s, out uint mods, out uint vk, out Key key)
        {
            mods = 0; vk = 0; key = Key.None;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim())
                {
                    case "Ctrl": mods |= MOD_CONTROL; break;
                    case "Alt": mods |= MOD_ALT; break;
                    case "Shift": mods |= MOD_SHIFT; break;
                    case "Win": mods |= MOD_WIN; break;
                    default: return false;
                }
            }
            if (!Enum.TryParse(parts[parts.Length - 1].Trim(), out key) || key == Key.None) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0 && (mods != 0 || AllowedAlone(key));
        }

        /// <summary>Teclas que podem ser atalho sozinhas (sem Ctrl/Alt): nao atrapalham digitar nem jogar.</summary>
        public static bool AllowedAlone(Key k) =>
            (k >= Key.NumPad0 && k <= Key.Divide) || (k >= Key.F1 && k <= Key.F11) || (k >= Key.F13 && k <= Key.F24) || k == Key.Pause || k == Key.Scroll;

        public static string KeyName(Key k)
        {
            if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num " + (int)(k - Key.NumPad0);
            switch (k)
            {
                case Key.Multiply: return "Num *";
                case Key.Add: return "Num +";
                case Key.Subtract: return "Num -";
                case Key.Divide: return "Num /";
                case Key.Decimal: return "Num ,";
                case Key.Return: return "Enter";
                case Key.Space: return "Espaço";
                case Key.Back: return "Backspace";
                case Key.Delete: return "Del";
                case Key.Insert: return "Ins";
                case Key.Prior: return "PgUp";
                case Key.Next: return "PgDn";
                case Key.Scroll: return "Scroll Lock";
                case Key.Left: return "←";
                case Key.Right: return "→";
                case Key.Up: return "↑";
                case Key.Down: return "↓";
            }
            return k.ToString();
        }

        public static string Display(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var parts = s.Split('+').ToList();
            if (Enum.TryParse(parts[parts.Count - 1], out Key k)) parts[parts.Count - 1] = KeyName(k);
            return string.Join("+", parts);
        }
    }

    /// <summary>Caixa que grava um atalho: aperte a combinacao; Backspace/Del limpa.</summary>
    sealed class HotkeyBox : TextBox
    {
        string value = "";
        public event Action Changed;

        public HotkeyBox()
        {
            SetResourceReference(StyleProperty, typeof(TextBox)); // o estilo do tema (subclasse nao herda o estilo implicito)
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            Cursor = Cursors.Hand;
            ToolTip = "Clique aqui e aperte a combinação (ex.: Ctrl+Alt+1 ou uma tecla do teclado numérico). Backspace apaga.";
            Refresh();
        }

        public string Value
        {
            get => value;
            set { this.value = value ?? ""; Refresh(); }
        }

        void Refresh() => Text = value.Length == 0 ? (IsKeyboardFocused ? "aperte a combinação…" : "nenhum") : HotkeyText.Display(value);

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); Refresh(); }
        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); Refresh(); }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            var m = Keyboard.Modifiers;
            if (k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt || k == Key.LeftShift || k == Key.RightShift ||
                k == Key.LWin || k == Key.RWin || k == Key.ImeProcessed || k == Key.DeadCharProcessed)
            {
                Text = HotkeyText.Format(m, Key.None).Replace("None", "…");
                return;
            }
            if (m == ModifierKeys.None && (k == Key.Back || k == Key.Delete || k == Key.Escape))
            {
                Value = "";
                Changed?.Invoke();
                return;
            }
            if (k == Key.Tab && m == ModifierKeys.None) { e.Handled = false; return; }
            if (m == ModifierKeys.None && !HotkeyText.AllowedAlone(k))
            {
                Text = "use Ctrl/Alt/Shift junto (ou o teclado numérico / F1–F11)";
                return;
            }
            Value = HotkeyText.Format(m, k);
            Changed?.Invoke();
        }
    }

    /// <summary>Forma de onda com o trecho que toca (arraste as bordas) e a posicao da previa.</summary>
    sealed class TrimWave : FrameworkElement
    {
        public byte[] Peaks = new byte[0];
        public double Duration = 1, Start, End;   // segundos (End 0 = fim)
        public double Playhead = -1;              // segundos (-1 = nada tocando)
        public Color Accent = Colors.MediumPurple;
        public event Action Changed;
        int drag;                                 // 0 nada, 1 comeco, 2 fim

        public TrimWave() { Height = 110; Cursor = Cursors.SizeWE; Focusable = false; }

        double EndOrFull => End > 0 ? Math.Min(End, Duration) : Duration;
        double X(double sec) => Duration <= 0 ? 0 : sec / Duration * ActualWidth;
        double Sec(double x) => Math.Max(0, Math.Min(Duration, x / Math.Max(1, ActualWidth) * Duration));

        protected override void OnRender(DrawingContext dc)
        {
            Paint.Ensure();
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRoundedRectangle(Paint.Well, null, new Rect(0, 0, w, h), 8, 8);
            if (Peaks.Length == 0 || w < 4) return;
            double mid = h / 2, xs = X(Start), xe = X(EndOrFull);
            var on = new SolidColorBrush(Accent); on.Freeze();
            var off = new SolidColorBrush(Color.FromArgb(0x55, Accent.R, Accent.G, Accent.B)); off.Freeze();
            int n = Peaks.Length;
            double bw = w / n;
            for (int i = 0; i < n; i++)
            {
                double x = i * bw;
                double a = Peaks[i] / 255.0 * (mid - 6);
                if (a < 0.6) a = 0.6;
                bool inside = x + bw * 0.5 >= xs && x + bw * 0.5 <= xe;
                dc.DrawRectangle(inside ? on : off, null, new Rect(x + bw * 0.15, mid - a, Math.Max(1, bw * 0.7), a * 2));
            }
            // fora do trecho: escurecido
            var shade = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)); shade.Freeze();
            if (xs > 0) dc.DrawRectangle(shade, null, new Rect(0, 0, xs, h));
            if (xe < w) dc.DrawRectangle(shade, null, new Rect(xe, 0, w - xe, h));
            var handle = new Pen(Brushes.White, 2); handle.Freeze();
            dc.DrawLine(handle, new Point(xs, 4), new Point(xs, h - 4));
            dc.DrawLine(handle, new Point(xe, 4), new Point(xe, h - 4));
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(xs - 4, mid - 10, 8, 20), 3, 3);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(xe - 4, mid - 10, 8, 20), 3, 3);
            if (Playhead >= 0)
            {
                var ph = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66)), 2); ph.Freeze();
                double xp = X(Playhead);
                dc.DrawLine(ph, new Point(xp, 0), new Point(xp, h));
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            double x = e.GetPosition(this).X, xs = X(Start), xe = X(EndOrFull);
            drag = Math.Abs(x - xs) <= Math.Abs(x - xe) ? 1 : 2;
            CaptureMouse();
            Move(x);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag != 0 && e.LeftButton == MouseButtonState.Pressed) Move(e.GetPosition(this).X);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            drag = 0;
            ReleaseMouseCapture();
        }

        void Move(double x)
        {
            double s = Sec(x);
            const double MinLen = 0.05;
            if (drag == 1) Start = Math.Max(0, Math.Min(s, EndOrFull - MinLen));
            else if (drag == 2)
            {
                double e = Math.Max(Start + MinLen, Math.Min(Duration, s));
                End = e >= Duration - 0.005 ? 0 : e;
            }
            Start = Math.Round(Start, 3);
            End = Math.Round(End, 3);
            InvalidateVisual();
            Changed?.Invoke();
        }
    }

    /// <summary>Editor de um som do sound pad (nome, emoji, cor, trecho, volume, fade, loop, modo e atalho).</summary>
    sealed class SoundEditor : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly SoundItem orig, it;
        readonly SoundLibrary lib;
        readonly SoundBoard board;
        readonly Func<string, int, string> checkHotkey;
        readonly TrimWave wave;
        readonly TextBlock timeText, loudText, hotkeyProblem;
        readonly DispatcherTimer timer;
        public bool Saved { get; private set; }
        public bool Deleted { get; private set; }

        static readonly string[] QuickEmojis = { "😂", "🔥", "💀", "😱", "👏", "🎵", "🎉", "🚨", "💥", "🤡", "😎", "🐐", "👀", "🙏", "😡", "🥲", "🔊", "🎮", "⚡", "❤" };
        static readonly string[] ModeNames = { "Apertar de novo: recomeça do início", "Apertar de novo: para o som", "Apertar de novo: toca outro por cima" };

        public SoundEditor(Window owner, SoundItem item, SoundLibrary lib, SoundBoard board, IList<string> categories, Func<string, int, string> checkHotkey)
        {
            orig = item;
            it = item.Clone();
            this.lib = lib;
            this.board = board;
            this.checkHotkey = checkHotkey;
            Owner = owner;
            Title = "Editar som – " + item.Name;
            Width = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.Bg;
            Foreground = Theme.Text;
            FontFamily = Theme.Ui;
            FontSize = 13;
            UseLayoutRounding = true;
            SourceInitialized += (s, e) =>
            {
                int dark = Theme.Current.Light ? 0 : 1;
                try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4); } catch { }
            };

            var root = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };

            // nome + emoji
            var nameBox = new TextBox { Text = it.Name, MaxLength = 60 };
            nameBox.TextChanged += (s, e) => it.Name = nameBox.Text;
            var emojiBox = new TextBox { Text = it.Emoji, MaxLength = 8, Width = 64, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 16, HorizontalContentAlignment = HorizontalAlignment.Center };
            emojiBox.TextChanged += (s, e) => it.Emoji = emojiBox.Text.Trim();
            var top = Ui.Columns(Ui.Star(), Ui.Px(12), Ui.Auto);
            top.Children.Add(Ui.At(Labeled("Nome", nameBox), 0));
            top.Children.Add(Ui.At(Labeled("Emoji", emojiBox), 2));
            root.Children.Add(top);
            var quick = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var em in QuickEmojis)
            {
                string e2 = em;
                var b = new Button { Content = new TextBlock { Text = em, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 15 }, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 4), ToolTip = "Usar " + em };
                b.Click += (s, e) => emojiBox.Text = e2;
                quick.Children.Add(b);
            }
            root.Children.Add(quick);

            // cor + categoria
            var colors = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var swatches = new List<Border>();
            foreach (var c in SoundLibrary.Palette)
            {
                string cc = c;
                var sw = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, Background = Brush(c), BorderThickness = new Thickness(2) };
                sw.MouseLeftButtonUp += (s, e) => { it.Color = cc; foreach (var x in swatches) x.BorderBrush = (string)x.Tag == it.Color ? Brushes.White : Brushes.Transparent; wave.Accent = ParseColor(cc); wave.InvalidateVisual(); };
                sw.Tag = c;
                sw.BorderBrush = c == it.Color ? Brushes.White : Brushes.Transparent;
                swatches.Add(sw);
                colors.Children.Add(sw);
            }
            // categorias novas nascem no botao "Nova categoria" da pagina; aqui so escolhe
            var catBox = new ComboBox { MinWidth = 180 };
            catBox.Items.Add("Sem categoria");
            foreach (var c in categories) catBox.Items.Add(c);
            if (!string.IsNullOrEmpty(it.Category) && !categories.Contains(it.Category)) catBox.Items.Add(it.Category);
            catBox.SelectedItem = string.IsNullOrEmpty(it.Category) ? "Sem categoria" : it.Category;
            catBox.SelectionChanged += (s, e) => it.Category = catBox.SelectedIndex <= 0 ? "" : (string)catBox.SelectedItem;
            var row2 = Ui.Columns(Ui.Star(), Ui.Px(12), Ui.Auto);
            row2.Margin = new Thickness(0, 10, 0, 0);
            row2.Children.Add(Ui.At(Labeled("Cor", colors), 0));
            row2.Children.Add(Ui.At(Labeled("Categoria", catBox), 2));
            root.Children.Add(row2);

            // trecho
            wave = new TrimWave { Peaks = it.Peaks ?? new byte[0], Duration = Math.Max(0.01, it.Duration), Start = it.StartSec, End = it.EndSec, Accent = ParseColor(it.Color), Margin = new Thickness(0, 14, 0, 0) };
            timeText = Ui.T("", 12.5, Theme.Sub);
            wave.Changed += () => { it.StartSec = wave.Start; it.EndSec = wave.End; UpdateTimes(); };
            root.Children.Add(Ui.M(Ui.T("Trecho que toca (arraste as bordas brancas)", 13, Theme.Text, true), 0, 14, 0, 0));
            root.Children.Add(wave);
            var reset = Ui.Btn("Som inteiro", () => { wave.Start = 0; wave.End = 0; wave.InvalidateVisual(); it.StartSec = 0; it.EndSec = 0; UpdateTimes(); }, null, null, "Volta a tocar o arquivo do começo ao fim.");
            reset.Padding = new Thickness(10, 3, 10, 3);
            var trow = Ui.Columns(Ui.Star(), Ui.Auto);
            trow.Margin = new Thickness(0, 6, 0, 0);
            trow.Children.Add(Ui.At(timeText, 0));
            trow.Children.Add(Ui.At(reset, 1));
            root.Children.Add(trow);

            // volume e normalizacao
            loudText = Ui.T("", 12, Theme.Sub, false, true);
            root.Children.Add(Ui.M(SliderLine("Volume", -24, 12, 0.5, it.VolumeDb, v => it.VolumeDb = (float)v, v => (v > 0 ? "+" : "") + v.ToString("0.0") + " dB"), 0, 12, 0, 0));
            var norm = new CheckBox { Content = Ui.T("Igualar volume com os outros sons (normalizar)", 13, Theme.Text), IsChecked = it.Normalize, Margin = new Thickness(0, 4, 0, 0) };
            norm.Checked += (s, e) => { it.Normalize = true; UpdateTimes(); };
            norm.Unchecked += (s, e) => { it.Normalize = false; UpdateTimes(); };
            root.Children.Add(norm);
            root.Children.Add(Ui.M(loudText, 26, 2, 0, 0));

            root.Children.Add(Ui.M(SliderLine("Fade de entrada", 0, 2000, 10, it.FadeInMs, v => it.FadeInMs = (int)v, v => v < 1 ? "sem" : v.ToString("0") + " ms"), 0, 8, 0, 0));
            root.Children.Add(SliderLine("Fade de saída", 0, 3000, 10, it.FadeOutMs, v => it.FadeOutMs = (int)v, v => v < 1 ? "sem" : v.ToString("0") + " ms"));

            var loop = new CheckBox { Content = Ui.T("Repetir sem parar (loop) até apertar de novo", 13, Theme.Text), IsChecked = it.Loop, Margin = new Thickness(0, 8, 0, 0) };
            var mode = new ComboBox { MinWidth = 300 };
            foreach (var m in ModeNames) mode.Items.Add(m);
            mode.SelectedIndex = Math.Max(0, Math.Min(2, it.Mode));
            loop.Checked += (s, e) => { it.Loop = true; if (it.Mode == 0) mode.SelectedIndex = 1; };
            loop.Unchecked += (s, e) => it.Loop = false;
            mode.SelectionChanged += (s, e) => it.Mode = Math.Max(0, mode.SelectedIndex);
            root.Children.Add(loop);
            root.Children.Add(Ui.M(Labeled("Ao apertar de novo", mode), 0, 8, 0, 0));

            // atalho
            var hk = new HotkeyBox { Value = it.Hotkey, MinWidth = 220 };
            hotkeyProblem = Ui.T("", 12, Theme.Warn, false, true);
            hk.Changed += () => { it.Hotkey = hk.Value; CheckHotkey(); };
            var clear = Ui.Btn("Limpar", () => { hk.Value = ""; it.Hotkey = ""; CheckHotkey(); }, null, null, "Sem atalho.");
            clear.Padding = new Thickness(10, 3, 10, 3);
            var hrow = Ui.H(hk, Ui.M(clear, 8, 0, 0, 0));
            root.Children.Add(Ui.M(Labeled("Atalho de teclado (funciona até dentro do jogo)", hrow), 0, 12, 0, 0));
            root.Children.Add(Ui.M(hotkeyProblem, 0, 4, 0, 0));
            CheckHotkey();

            // botoes
            var preview = Ui.Btn("Ouvir (só você)", Preview, null, "\uE768", "Toca só no seu fone, com os ajustes desta janela.");
            var stop = Ui.Btn("Parar", () => { board.Stop(-orig.Key); board.Stop(orig.Key); }, null, "\uE71A", "Para a prévia.");
            var del = Ui.Btn("Excluir", () =>
            {
                if (MessageBox.Show(this, "Excluir o som \"" + orig.Name + "\"?", "TakaVox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Deleted = true;
                Close();
            }, "DangerButton", "\uE74D", "Tira o som do sound pad (apaga a cópia que o TakaVox guardou).");
            var save = Ui.Btn("Salvar", Save, "AccentButton", "\uE74E", null);
            var cancel = Ui.Btn("Cancelar", Close, null, null, null);
            var buttons = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
            buttons.Margin = new Thickness(0, 18, 0, 0);
            buttons.Children.Add(Ui.At(preview, 0));
            buttons.Children.Add(Ui.At(Ui.M(stop, 8, 0, 0, 0), 1));
            buttons.Children.Add(Ui.At(Ui.M(del, 0, 0, 8, 0), 3));
            buttons.Children.Add(Ui.At(Ui.M(cancel, 0, 0, 8, 0), 4));
            buttons.Children.Add(Ui.At(save, 5));
            root.Children.Add(buttons);

            Content = root;
            UpdateTimes();
            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
            timer.Tick += (s, e) =>
            {
                double ph = -1;
                if (board.IsPlaying(-orig.Key, out float pos))
                {
                    double len = (it.EndSec > 0 ? Math.Min(it.EndSec, it.Duration) : it.Duration) - it.StartSec;
                    ph = it.StartSec + pos * Math.Max(0, len);
                }
                if (Math.Abs(ph - wave.Playhead) > 1e-4) { wave.Playhead = ph; wave.InvalidateVisual(); }
            };
            timer.Start();
            Closed += (s, e) => { timer.Stop(); board.Stop(-orig.Key); };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && !(Keyboard.FocusedElement is HotkeyBox)) Close(); };
        }

        static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush(ParseColor(hex)); b.Freeze(); return b; }

        public static Color ParseColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(string.IsNullOrEmpty(hex) ? SoundLibrary.Palette[0] : hex); }
            catch { return (Color)ColorConverter.ConvertFromString(SoundLibrary.Palette[0]); }
        }

        static FrameworkElement Labeled(string label, UIElement control) => Ui.V(Ui.M(Ui.T(label, 12, Theme.Sub), 0, 0, 0, 4), control);

        static FrameworkElement SliderLine(string label, double min, double max, double step, double value, Action<double> set, Func<double, string> fmt)
        {
            var g = Ui.Columns(Ui.Px(140), Ui.Star(), Ui.Px(80));
            g.Margin = new Thickness(0, 3, 0, 3);
            var s = new Slider { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true };
            var vt = Ui.T(fmt(s.Value), 12.5, Theme.Sub, true);
            vt.HorizontalAlignment = HorizontalAlignment.Right;
            s.ValueChanged += (o, e) => { set(s.Value); vt.Text = fmt(s.Value); };
            g.Children.Add(Ui.At(Ui.T(label, 13, Theme.Text), 0));
            g.Children.Add(Ui.At(Ui.M(s, 8, 0, 8, 0), 1));
            g.Children.Add(Ui.At(vt, 2));
            return g;
        }

        void UpdateTimes()
        {
            double end = it.EndSec > 0 ? Math.Min(it.EndSec, it.Duration) : it.Duration;
            timeText.Text = $"{Sec(it.StartSec)} até {Sec(end)}  ·  toca {Sec(Math.Max(0, end - it.StartSec))} de {Sec(it.Duration)}";
            loudText.Text = it.Lufs > -70
                ? $"Volume medido: {it.Lufs:0} LUFS (pico {it.PeakDb:0.0} dBFS)." + (it.Normalize ? $" Vai tocar perto de {SoundLibrary.TargetLufs:0} LUFS, como os outros." : " Vai tocar no volume original do arquivo.")
                : "Som muito baixo ou mudo: a normalização não mexe nele.";
        }

        static string Sec(double s) => s < 60 ? s.ToString("0.00", CultureInfo.CurrentCulture) + " s" : TimeSpan.FromSeconds(s).ToString(@"m\:ss");

        void CheckHotkey()
        {
            string p = string.IsNullOrEmpty(it.Hotkey) ? null : checkHotkey(it.Hotkey, orig.Key);
            hotkeyProblem.Text = p ?? "";
            hotkeyProblem.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void Preview()
        {
            var clip = lib.GetClip(orig, out var err);
            if (clip == null) { MessageBox.Show(this, "Não deu para tocar: " + err, "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            board.Stop(-orig.Key);
            var p = SoundLibrary.MakePlay(it, clip, true);
            p.Key = orig.Key;
            p.Mode = 2;
            board.Play(p);
        }

        void Save()
        {
            if (string.IsNullOrWhiteSpace(it.Name)) it.Name = "Som";
            bool trimChanged = Math.Abs(orig.StartSec - it.StartSec) > 1e-6 || Math.Abs(orig.EndSec - it.EndSec) > 1e-6;
            orig.Name = it.Name.Trim(); orig.Emoji = it.Emoji; orig.Color = it.Color; orig.Category = it.Category;
            orig.VolumeDb = it.VolumeDb; orig.Normalize = it.Normalize; orig.StartSec = it.StartSec; orig.EndSec = it.EndSec;
            orig.FadeInMs = it.FadeInMs; orig.FadeOutMs = it.FadeOutMs; orig.Loop = it.Loop; orig.Mode = it.Mode; orig.Hotkey = it.Hotkey;
            if (trimChanged) lib.Reanalyze(orig);
            Saved = true;
            Close();
        }
    }
}
