// Gera as imagens de divulgacao do README / GitHub (1280x640, o tamanho da "social preview" do GitHub):
//   banner.png  - logo, nome, frase e destaques + uma tela do app
//   temas.png   - mosaico com o app em varios temas
// Uso: banner.exe <logo.png> <pasta-das-capturas> <pasta-de-saida>
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

static class Banner
{
    static BitmapImage Load(string path)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.UriSource = new Uri(System.IO.Path.GetFullPath(path));
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.EndInit();
        b.Freeze();
        return b;
    }

    static void Save(FrameworkElement el, int w, int h, string path)
    {
        el.Measure(new Size(w, h));
        el.Arrange(new Rect(0, 0, w, h));
        el.UpdateLayout();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        Console.WriteLine("ok " + path);
    }

    static Brush Hex(string h) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)); b.Freeze(); return b; }

    static UIElement Shot(BitmapImage img, double w, double radius = 14)
    {
        double h = w * img.PixelHeight / img.PixelWidth;
        var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        var r = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(radius), Background = brush,
            BorderBrush = Hex("#40FFFFFF"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Opacity = 0.55, Color = Colors.Black },
        };
        return r;
    }

    static Border Pill(string text)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 6, 14, 7), Margin = new Thickness(0, 0, 10, 10),
            Background = Hex("#2EFFFFFF"), BorderBrush = Hex("#40FFFFFF"), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold },
        };
    }

    static Grid Background(int w, int h)
    {
        var g = new Grid { Width = w, Height = h, ClipToBounds = true };
        g.Children.Add(new Rectangle { Fill = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#0E0A1C"), (Color)ColorConverter.ConvertFromString("#1E0F2E"), 35) });
        var glow1 = new Ellipse { Width = 760, Height = 760, Margin = new Thickness(-260, -320, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Fill = new RadialGradientBrush((Color)ColorConverter.ConvertFromString("#557C5CFF"), Colors.Transparent) };
        var glow2 = new Ellipse { Width = 820, Height = 820, Margin = new Thickness(0, 0, -300, -380), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Fill = new RadialGradientBrush((Color)ColorConverter.ConvertFromString("#55E05A9C"), Colors.Transparent) };
        g.Children.Add(glow1);
        g.Children.Add(glow2);
        return g;
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("uso: banner <logo.png> <capturas> <saida>"); return 2; }
        string shots = args[1], outDir = args[2];
        Directory.CreateDirectory(outDir);
        var ui = new FontFamily("Segoe UI Variable Display, Segoe UI");

        // ------------------------------------------------------------ banner
        {
            var g = Background(1280, 640);
            var left = new StackPanel { Margin = new Thickness(72, 70, 0, 0), Width = 560, HorizontalAlignment = HorizontalAlignment.Left };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Image { Source = Load(args[0]), Width = 104, Height = 104, Margin = new Thickness(0, 0, 18, 0) });
            head.Children.Add(new TextBlock { Text = "TakaVox", FontFamily = ui, FontSize = 76, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(head);
            left.Children.Add(new TextBlock
            {
                Text = "Estúdio de voz com IA para Discord, jogos e lives", FontFamily = ui, FontSize = 30, FontWeight = FontWeights.SemiBold,
                Foreground = Hex("#E6E1FF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 22, 0, 0),
            });
            left.Children.Add(new TextBlock
            {
                Text = "Tira o barulho do teclado e da ventoinha, muda a sua voz e toca sons — sem tirar FPS do jogo.",
                FontFamily = ui, FontSize = 21, Foreground = Hex("#B9B2D9"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 14, 0, 0),
            });
            var pills = new WrapPanel { Margin = new Thickness(4, 30, 0, 0) };
            foreach (var p in new[] { "IA anti-ruído", "Voz com IA", "Sound pad", "Plugins VST", "Auto-Tune", "Temas" }) pills.Children.Add(Pill(p));
            left.Children.Add(pills);
            g.Children.Add(left);
            var shot = Shot(Load(System.IO.Path.Combine(shots, "painel.png")), 760, 16);
            var holder = new Canvas();
            Canvas.SetLeft(shot, 640);
            Canvas.SetTop(shot, 150);
            ((FrameworkElement)shot).RenderTransform = new RotateTransform(-3, 380, 250);
            holder.Children.Add(shot);
            g.Children.Add(holder);
            Save(g, 1280, 640, System.IO.Path.Combine(outDir, "banner.png"));
        }

        // ------------------------------------------------------------ mosaico de temas
        {
            var names = new[] { ("escuro", "Escuro"), ("oled", "OLED"), ("vidro", "Vidro"), ("claro", "Claro"), ("neon", "Neon Taka"), ("sakura", "Sakura") };
            var g = Background(1280, 760);
            var grid = new UniformGrid { Columns = 3, Rows = 2, Margin = new Thickness(36, 34, 36, 30) };
            foreach (var (key, label) in names)
            {
                string f = System.IO.Path.Combine(shots, "tema_" + key + "_painel.png");
                if (!File.Exists(f)) continue;
                var cell = new StackPanel { Margin = new Thickness(10) };
                cell.Children.Add(Shot(Load(f), 380, 10));
                cell.Children.Add(new TextBlock { Text = label, FontFamily = ui, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
                grid.Children.Add(cell);
            }
            g.Children.Add(grid);
            Save(g, 1280, 760, System.IO.Path.Combine(outDir, "temas.png"));
        }
        return 0;
    }
}
