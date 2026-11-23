using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TakaVox.UI
{
    /// <summary>Atalhos para montar a interface em codigo.</summary>
    static class Ui
    {
        public static TextBlock T(string s, double size = 13, Brush fg = null, bool bold = false, bool wrap = false)
        {
            return new TextBlock
            {
                Text = s,
                FontSize = size,
                Foreground = fg ?? Theme.Text,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            };
        }

        public static TextBlock Icon(string glyph, double size = 16, Brush fg = null)
        {
            return new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = size, Foreground = fg ?? Theme.Text, VerticalAlignment = VerticalAlignment.Center };
        }

        public static Border Card(UIElement content, double pad = 18)
        {
            return new Border
            {
                Background = Theme.Card,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(pad),
                Margin = new Thickness(0, 0, 0, 14),
                Child = content,
            };
        }

        public static StackPanel V(params UIElement[] items)
        {
            var s = new StackPanel();
            foreach (var i in items) if (i != null) s.Children.Add(i);
            return s;
        }

        public static StackPanel H(params UIElement[] items)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) if (i != null) s.Children.Add(i);
            return s;
        }

        public static T M<T>(T e, double l, double t, double r, double b) where T : FrameworkElement
        {
            e.Margin = new Thickness(l, t, r, b);
            return e;
        }

        public static Button Btn(string text, Action click, string style = null, string icon = null, string tip = null)
        {
            object content = text;
            if (icon != null)
            {
                var sp = H(Icon(icon, 14, null), T(text, 13, null));
                ((TextBlock)sp.Children[0]).Margin = new Thickness(0, 0, 8, 0);
                ((TextBlock)sp.Children[0]).Foreground = style == "AccentButton" ? Theme.OnAccent : style == "DangerButton" ? Theme.DangerFg : Theme.Text;
                ((TextBlock)sp.Children[1]).Foreground = style == "AccentButton" ? Theme.OnAccent : style == "DangerButton" ? Theme.DangerFg : Theme.Text;
                content = sp;
            }
            var b = new Button { Content = content };
            if (style != null) b.Style = (Style)Application.Current.Resources[style];
            if (tip != null) b.ToolTip = tip;
            b.Click += (s, e) =>
            {
                try { click(); }
                catch (Exception ex) { Log.Write("Erro no botão '" + text + "': " + ex); MessageBox.Show(ex.Message, "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            return b;
        }

        public static Grid Columns(params GridLength[] widths)
        {
            var g = new Grid();
            foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            return g;
        }

        public static GridLength Star(double v = 1) => new GridLength(v, GridUnitType.Star);
        public static GridLength Px(double v) => new GridLength(v);
        public static GridLength Auto => GridLength.Auto;

        public static UIElement At(UIElement e, int col, int row = 0, int colSpan = 1)
        {
            Grid.SetColumn(e, col);
            Grid.SetRow(e, row);
            if (colSpan > 1) Grid.SetColumnSpan(e, colSpan);
            return e;
        }

        public static Border Pill(string text, Brush fg, Brush bg)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = bg,
                Padding = new Thickness(10, 4, 10, 4),
                Child = T(text, 12, fg, true),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        public static FrameworkElement SectionTitle(string title, string subtitle = null)
        {
            var s = V(T(title, 15, Theme.Text, true));
            if (subtitle != null) s.Children.Add(M(T(subtitle, 12.5, Theme.Sub, false, true), 0, 3, 0, 0));
            s.Margin = new Thickness(0, 0, 0, 12);
            return s;
        }
    }

    abstract class Binder { public abstract void Refresh(); }

    sealed class SliderBinder : Binder
    {
        readonly Slider s; readonly TextBlock v; readonly Func<double> get; readonly Func<double, string> fmt;
        public SliderBinder(Slider s, TextBlock v, Func<double> get, Func<double, string> fmt) { this.s = s; this.v = v; this.get = get; this.fmt = fmt; }
        public override void Refresh() { double x = get(); s.Value = x; v.Text = fmt(x); }
    }

    sealed class SwitchBinder : Binder
    {
        readonly CheckBox c; readonly Func<bool> get;
        public SwitchBinder(CheckBox c, Func<bool> get) { this.c = c; this.get = get; }
        public override void Refresh() { c.IsChecked = get(); }
    }

    sealed class KnobBinder : Binder
    {
        readonly Knob k; readonly Func<double> get;
        public KnobBinder(Knob k, Func<double> get) { this.k = k; this.get = get; }
        public override void Refresh() { k.SetSilently(get()); }
    }

    sealed class ActionBinder : Binder
    {
        readonly Action a;
        public ActionBinder(Action a) { this.a = a; }
        public override void Refresh() { a(); }
    }
}
