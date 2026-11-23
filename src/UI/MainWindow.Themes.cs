using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TakaVox.UI
{
    /// <summary>Pagina "Temas": escolhe o visual do app (muda na hora, sem reiniciar).</summary>
    public sealed partial class MainWindow
    {
        readonly Dictionary<string, Border> themeTiles = new Dictionary<string, Border>();

        FrameworkElement BuildThemesPage()
        {
            var root = new StackPanel();
            root.Children.Add(Ui.Card(Ui.V(
                Ui.T("Deixe o TakaVox com a sua cara", 17, Theme.Text, true),
                Ui.M(Ui.T("Clique num tema: muda na hora, sem reiniciar. O vidro usa o desfoque do próprio Windows 11 (feito pela placa de vídeo, " +
                          "sem pesar no jogo) e só aparece com a janela aberta. Na bandeja, nenhum tema gasta nada.", 12.5, Theme.Sub, false, true), 0, 4, 0, 0))));
            var wrap = new WrapPanel { Margin = new Thickness(-6, 6, -6, 0) };
            foreach (var t in Theme.All) wrap.Children.Add(ThemeTile(t));
            root.Children.Add(Ui.M(wrap, 0, 8, 0, 0));
            root.Children.Add(Ui.M(Ui.Card(Ui.V(
                Ui.T("Dicas", 15, Theme.Text, true),
                Ui.M(Ui.T("• OLED: preto puro — em telas OLED os pixels pretos ficam apagados (mais contraste e menos consumo).", 12.5, Theme.Sub, false, true), 0, 6, 0, 0),
                Ui.M(Ui.T("• Vidro e Vidro claro: precisam dos \"Efeitos de transparência\" ligados em Configurações do Windows › Personalização › Cores.", 12.5, Theme.Sub, false, true), 0, 4, 0, 0),
                Ui.M(Ui.T("• As janelas dos plugins mantêm o visual do FL Studio em qualquer tema.", 12.5, Theme.Sub, false, true), 0, 4, 0, 0))), 0, 14, 0, 0));
            return root;
        }

        static SolidColorBrush Fixed(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>Uma "mini janela" desenhada com as cores do tema (independente do tema atual).</summary>
        static FrameworkElement ThemePreview(ThemeDef t)
        {
            var g = new Grid { Width = 214, Height = 118, ClipToBounds = true };
            var clip = new RectangleGeometry(new Rect(0, 0, 214, 118), 9, 9);
            clip.Freeze();
            g.Clip = clip;
            if (t.Glass)
            {
                // o que o vidro mostra: o papel de parede desfocado
                var wall = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                wall.GradientStops.Add(new GradientStop(Color.FromRgb(0x5B, 0x2A, 0x86), 0));
                wall.GradientStops.Add(new GradientStop(Color.FromRgb(0x23, 0x5C, 0xC4), 0.55));
                wall.GradientStops.Add(new GradientStop(Color.FromRgb(0xE0, 0x5A, 0x9C), 1));
                wall.Freeze();
                g.Children.Add(new Rectangle { Fill = wall });
                g.Children.Add(new Rectangle { Fill = Fixed(t.Light ? Color.FromArgb(0x8C, 0xF5, 0xF6, 0xFA) : Color.FromArgb(0x8C, 0x14, 0x16, 0x1E)) });
            }
            g.Children.Add(new Rectangle { Fill = Fixed(t.Bg) });
            // barra lateral com "menu"
            var side = new Canvas { Width = 52, HorizontalAlignment = HorizontalAlignment.Left, Background = Fixed(t.Side) };
            for (int i = 0; i < 4; i++)
            {
                var pill = new Border { Width = 38, Height = 10, CornerRadius = new CornerRadius(3), Background = Fixed(i == 0 ? t.NavSel : Color.FromArgb(0, 0, 0, 0)) };
                Canvas.SetLeft(pill, 7); Canvas.SetTop(pill, 24 + i * 16);
                side.Children.Add(pill);
                var line = new Border { Width = i == 0 ? 22 : 18, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Fixed(i == 0 ? t.Text : t.Sub) };
                Canvas.SetLeft(line, 13); Canvas.SetTop(line, 27.5 + i * 16);
                side.Children.Add(line);
                if (i == 0)
                {
                    var bar = new Border { Width = 2, Height = 8, CornerRadius = new CornerRadius(1), Background = Fixed(t.Accent) };
                    Canvas.SetLeft(bar, 7); Canvas.SetTop(bar, 25);
                    side.Children.Add(bar);
                }
            }
            var logo = new Ellipse { Width = 12, Height = 12, Fill = Fixed(t.Accent) };
            Canvas.SetLeft(logo, 8); Canvas.SetTop(logo, 7);
            side.Children.Add(logo);
            g.Children.Add(side);

            // area: dois cartoes com texto, botao, interruptor e slider
            var area = new Canvas { Margin = new Thickness(52, 0, 0, 0) };
            Border Card(double x, double y, double w, double h)
            {
                var c = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(6), Background = Fixed(t.Card), BorderBrush = Fixed(t.Border), BorderThickness = new Thickness(1) };
                Canvas.SetLeft(c, x); Canvas.SetTop(c, y);
                area.Children.Add(c);
                return c;
            }
            void Bar(double x, double y, double w, double h, Color c)
            {
                var b = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(h / 2), Background = Fixed(c) };
                Canvas.SetLeft(b, x); Canvas.SetTop(b, y);
                area.Children.Add(b);
            }
            Card(8, 8, 146, 52);
            Bar(16, 16, 60, 5, t.Text);
            Bar(16, 26, 96, 3.5, t.Sub);
            Bar(16, 33, 80, 3.5, t.Sub);
            var btn = new Border { Width = 38, Height = 12, CornerRadius = new CornerRadius(4), Background = Fixed(t.Accent) };
            Canvas.SetLeft(btn, 16); Canvas.SetTop(btn, 42);
            area.Children.Add(btn);
            // interruptor ligado
            Bar(122, 15, 22, 11, t.Accent);
            var knob = new Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
            Canvas.SetLeft(knob, 135); Canvas.SetTop(knob, 17);
            area.Children.Add(knob);
            Card(8, 66, 146, 44);
            Bar(16, 74, 46, 4, t.Text);
            // slider
            Bar(16, 90, 126, 3, t.Track);
            Bar(16, 90, 78, 3, t.Accent);
            var thumb = new Ellipse { Width = 9, Height = 9, Fill = Brushes.White, Stroke = Fixed(t.Accent), StrokeThickness = 2 };
            Canvas.SetLeft(thumb, 90); Canvas.SetTop(thumb, 87);
            area.Children.Add(thumb);
            // medidor (poco escuro)
            Bar(128, 72, 8, 32, t.Well);
            Bar(129.5, 88, 5, 15, t.Good);
            g.Children.Add(area);
            return g;
        }

        Border ThemeTile(ThemeDef t)
        {
            var check = Ui.Icon("", 12, Theme.OnAccent);
            var badge = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Theme.Accent, Child = check,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0),
            };
            check.HorizontalAlignment = HorizontalAlignment.Center;
            var top = new Grid();
            top.Children.Add(ThemePreview(t));
            top.Children.Add(badge);
            var name = Ui.H(Ui.T(t.Name, 14, Theme.Text, true));
            if (t.Key == "escuro") name.Children.Add(Ui.M(Ui.T("padrão", 11, Theme.Dim), 8, 2, 0, 0));
            var body = Ui.V(top, Ui.M(name, 2, 9, 0, 0), Ui.M(Ui.T(t.Desc, 11.5, Theme.Sub, false, true), 2, 3, 0, 0));
            var tile = new Border
            {
                Width = 236, Padding = new Thickness(10, 10, 10, 12), Margin = new Thickness(6), CornerRadius = new CornerRadius(14),
                Background = Theme.Card, BorderThickness = new Thickness(2), Cursor = Cursors.Hand, Child = body,
                ToolTip = "Usar o tema " + t.Name,
            };
            tile.MouseLeftButtonUp += (s, e) => SetTheme(t.Key);
            tile.MouseEnter += (s, e) => { if (Theme.Current != t) tile.BorderBrush = Theme.Dim; };
            tile.MouseLeave += (s, e) => PaintThemeTile(t.Key);
            tile.Tag = badge;
            themeTiles[t.Key] = tile;
            PaintThemeTile(t.Key);
            return tile;
        }

        void PaintThemeTile(string key)
        {
            if (!themeTiles.TryGetValue(key, out var tile)) return;
            bool sel = Theme.Current.Key == key;
            tile.BorderBrush = sel ? Theme.Accent : Theme.Border;
            ((Border)tile.Tag).Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Troca o tema do app inteiro (na hora) e salva a escolha.</summary>
        void SetTheme(string key)
        {
            var t = Theme.Find(key) ?? Theme.All[0];
            if (app.ThemeKey != t.Key) { app.ThemeKey = t.Key; MarkDirty(); }
            Theme.Set(t);
            ApplyWindowTheme();
            UpdateMuteButton();
            RefreshAll();
            foreach (var k in themeTiles.Keys) PaintThemeTile(k);
            InvalidateTree(this);
            string frozen = Theme.FrozenBrushes();
            Log.Write("Tema: " + t.Name + (frozen.Length > 0 ? " (atenção: cores fixas em " + frozen + ")" : ""));
        }

        /// <summary>Redesenha o que e desenhado a mao (medidores, graficos) com as cores novas.</summary>
        static void InvalidateTree(DependencyObject d)
        {
            if (d is UIElement u) u.InvalidateVisual();
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) InvalidateTree(VisualTreeHelper.GetChild(d, i));
        }
    }
}
