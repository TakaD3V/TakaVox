using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace TakaVox.UI
{
    /// <summary>Resultado da conferencia de uma tecla para um som.</summary>
    sealed class KeyCheck
    {
        public bool Ok;              // pode salvar
        public string Message = "";  // aviso/erro para mostrar
        public string TakenFrom;     // nome do som que usava a tecla (salvar troca a tecla de dono)
    }

    /// <summary>
    /// "Aperte a tecla": janelinha que grava a tecla (ou combinacao) de um som do sound pad. A tecla vira um atalho
    /// global do Windows: toca o som ate com o jogo em tela cheia.
    /// </summary>
    sealed class KeyCaptureDialog : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly Func<string, KeyCheck> check;
        readonly TextBlock keyText, msgText;
        readonly Button saveBtn;
        string value;

        /// <summary>A tecla escolhida ("" = sem tecla). Null = cancelou.</summary>
        public string Result { get; private set; }

        public KeyCaptureDialog(Window owner, string soundName, string current, Func<string, KeyCheck> check)
        {
            this.check = check;
            value = current ?? "";
            Owner = owner;
            Title = "Tecla do som";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.Bg;
            Foreground = Theme.Text;
            FontFamily = Theme.Ui;
            FontSize = 13;
            SourceInitialized += (s, e) =>
            {
                int dark = Theme.Current.Light ? 0 : 1;
                try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4); } catch { }
            };

            keyText = new TextBlock
            {
                FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            };
            var keyBox = new Border
            {
                CornerRadius = new CornerRadius(12), Background = Theme.Card2, BorderBrush = Theme.Accent, BorderThickness = new Thickness(2),
                Padding = new Thickness(12, 14, 12, 14), Margin = new Thickness(0, 12, 0, 0), Child = keyText,
            };
            msgText = Ui.T("", 12.5, Theme.Sub, false, true);
            msgText.Margin = new Thickness(0, 10, 0, 0);
            var hint = Ui.T("Funcionam sozinhas: as teclas do teclado numérico (com o Num Lock ligado) e F1–F11. " +
                            "As outras precisam de Ctrl, Alt ou Shift junto, para não atrapalhar quando você digita. " +
                            "Backspace tira a tecla.", 12, Theme.Dim, false, true);
            hint.Margin = new Thickness(0, 10, 0, 0);

            saveBtn = Ui.Btn("Salvar", () => { Result = value; Close(); }, "AccentButton", "", null);
            var none = Ui.Btn("Sem tecla", () => { Result = ""; Close(); }, null, null, "Tira a tecla deste som.");
            var cancel = Ui.Btn("Cancelar", Close, null, null, null);
            var btns = Ui.H(none, Ui.M(cancel, 8, 0, 0, 0), Ui.M(saveBtn, 8, 0, 0, 0));
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.Margin = new Thickness(0, 16, 0, 0);

            var root = Ui.V(
                Ui.T("Aperte a tecla para tocar “" + soundName + "”", 15, Theme.Text, true, true),
                Ui.M(Ui.T("Funciona até dentro do jogo, em tela cheia.", 12.5, Theme.Sub, false, true), 0, 3, 0, 0),
                keyBox, msgText, hint, btns);
            root.Margin = new Thickness(20, 16, 20, 18);
            Content = root;
            Refresh();
            // os botoes nao podem roubar as teclas (Enter/Espaco num botao focado apertaria o botao)
            Loaded += (s, e) => Keyboard.Focus(this);
        }

        void Refresh()
        {
            keyText.Text = value.Length == 0 ? "aperte uma tecla…" : HotkeyText.Display(value);
            keyText.Foreground = value.Length == 0 ? Theme.Dim : Theme.Text;
            if (value.Length == 0) { msgText.Text = ""; saveBtn.IsEnabled = false; return; }
            var c = check(value);
            msgText.Text = c.Message;
            msgText.Foreground = c.Ok ? (c.TakenFrom != null ? Theme.Warn : Theme.Good) : Theme.Bad;
            saveBtn.IsEnabled = c.Ok;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            var m = Keyboard.Modifiers;
            if (k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt || k == Key.LeftShift || k == Key.RightShift ||
                k == Key.LWin || k == Key.RWin || k == Key.ImeProcessed || k == Key.DeadCharProcessed)
                return;
            if (m == ModifierKeys.None && k == Key.Escape) { Close(); return; }
            if (m == ModifierKeys.None && (k == Key.Back || k == Key.Delete)) { value = ""; Refresh(); msgText.Text = "Sem tecla: clique em \"Sem tecla\" para salvar assim."; return; }
            if (m == ModifierKeys.None && k == Key.Return && saveBtn.IsEnabled) { Result = value; Close(); return; }
            if (m == ModifierKeys.None && !HotkeyText.AllowedAlone(k))
            {
                value = "";
                keyText.Text = HotkeyText.KeyName(k);
                keyText.Foreground = Theme.Dim;
                msgText.Text = "Essa tecla sozinha atrapalharia quando você digita: aperte junto com Ctrl, Alt ou Shift (ou use o teclado numérico / F1–F11).";
                msgText.Foreground = Theme.Warn;
                saveBtn.IsEnabled = false;
                return;
            }
            value = HotkeyText.Format(m, k);
            Refresh();
        }
    }
}
