using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace TakaVox.UI
{
    /// <summary>Um tema: todas as cores do app (o resto e derivado destas).</summary>
    public sealed class ThemeDef
    {
        public string Key, Name, Desc;
        public bool Light;          // texto escuro em fundo claro (barra de titulo do Windows clara)
        public bool Glass;          // vidro: o fundo da janela e o desfoque do Windows 11 (acrilico)
        public Color Bg, Side, Card, Card2, Border, Text, Sub, Dim, Accent, AccentHi, Good, Warn, Bad;
        public Color BtnBg, BtnBorder, Field, FieldBorder, Popup, PopupBorder, Track, KnobOff, TitleFg, NavHover, NavSel,
            ItemSel, ItemHot, Scroll, Tip, TipBorder, Hover, DangerBg, DangerBorder, DangerFg,
            Well, Graph, WellText, WellSub, WellDim, WellLine;

        internal static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

        /// <summary>Calcula as cores secundarias a partir das principais (cada tema pode sobrescrever depois).</summary>
        public ThemeDef Derive()
        {
            var white = Colors.White; var black = Colors.Black;
            BtnBg = Lerp(Card2, Text, Light ? 0.0 : 0.035);
            if (Light) BtnBg = Card;
            BtnBorder = Lerp(Card2, Text, Light ? 0.13 : 0.09);
            Field = Light ? Card : Lerp(Card, Text, 0.035);
            FieldBorder = BtnBorder;
            Popup = Color.FromArgb(255, Card2.R, Card2.G, Card2.B);
            PopupBorder = Lerp(Popup, Text, Light ? 0.15 : 0.12);
            Track = Lerp(Card2, Text, Light ? 0.12 : 0.06);
            KnobOff = Light ? white : Lerp(Sub, Text, 0.4);
            TitleFg = Lerp(Sub, Text, 0.4);
            NavHover = Lerp(Side, Text, Light ? 0.05 : 0.035);
            NavSel = Lerp(Side, Accent, Light ? 0.12 : 0.14);
            ItemSel = Lerp(Popup, Accent, Light ? 0.14 : 0.2);
            ItemHot = Lerp(Popup, Accent, Light ? 0.22 : 0.3);
            Scroll = PopupBorder;
            Tip = Light ? Popup : Lerp(Popup, Text, 0.03);
            TipBorder = Lerp(Popup, Text, 0.15);
            Hover = Light ? black : white;
            DangerBg = Lerp(Color.FromArgb(255, Card.R, Card.G, Card.B), Bad, Light ? 0.1 : 0.15);
            DangerBorder = Lerp(Color.FromArgb(255, Card.R, Card.G, Card.B), Bad, Light ? 0.3 : 0.32);
            DangerFg = Light ? Lerp(Bad, black, 0.15) : Lerp(Bad, white, 0.3);
            // "pocos" escuros dos medidores e graficos (escuros ate nos temas claros: o espectro le melhor assim)
            if (Light) { Well = C("#1B1F27"); Graph = C("#20242D"); WellText = C("#E9ECF2"); WellSub = C("#A3AABA"); WellDim = C("#6F778A"); WellLine = C("#343A48"); }
            else { Well = Lerp(Color.FromArgb(255, Bg.R, Bg.G, Bg.B), black, 0.3); Graph = Lerp(Color.FromArgb(255, Bg.R, Bg.G, Bg.B), Color.FromArgb(255, Card.R, Card.G, Card.B), 0.4); WellText = Text; WellSub = Sub; WellDim = Dim; WellLine = Color.FromArgb(255, Track.R, Track.G, Track.B); }
            return this;
        }

        internal static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    }

    /// <summary>
    /// Temas do TakaVox (cores + estilos dos controles). Os pinceis sao "vivos": trocar o tema repinta o app na hora,
    /// sem reiniciar. Os estilos usam DynamicResource; o que e desenhado a mao (medidores, graficos) relê as cores
    /// quando <see cref="Version"/> muda.
    /// </summary>
    public static class Theme
    {
        static Color C(string hex) => ThemeDef.C(hex);

        // ------------------------------------------------------------------ temas
        public static readonly ThemeDef[] All = BuildThemes();

        static ThemeDef[] BuildThemes()
        {
            var list = new List<ThemeDef>();

            // padrao (as cores originais do app)
            var escuro = new ThemeDef
            {
                Key = "escuro", Name = "Escuro", Desc = "O visual padrão: grafite azulado com roxo.",
                Bg = C("#0D0F14"), Side = C("#11141B"), Card = C("#161A23"), Card2 = C("#1C2130"), Border = C("#252B39"),
                Text = C("#E9ECF2"), Sub = C("#8D95A8"), Dim = C("#5B6375"), Accent = C("#8B5CF6"), AccentHi = C("#A78BFA"),
                Good = C("#22D3A6"), Warn = C("#F5B544"), Bad = C("#F2545B"),
            }.Derive();
            escuro.BtnBg = C("#232838"); escuro.BtnBorder = C("#2F3648"); escuro.Field = C("#1E2330"); escuro.FieldBorder = C("#2F3648");
            escuro.Popup = C("#1B2030"); escuro.PopupBorder = C("#343B4F"); escuro.Track = C("#2A3142"); escuro.KnobOff = C("#AEB4C2");
            escuro.TitleFg = C("#AEB4C2"); escuro.NavHover = C("#191D28"); escuro.NavSel = C("#211C38"); escuro.ItemSel = C("#2B2452");
            escuro.ItemHot = C("#352C66"); escuro.Scroll = C("#343B4F"); escuro.Tip = C("#22283A"); escuro.TipBorder = C("#3A4258");
            escuro.DangerBg = C("#3A1D24"); escuro.DangerBorder = C("#5A2630"); escuro.DangerFg = C("#FF8A90");
            escuro.Well = C("#0A0C11"); escuro.Graph = C("#0F1219"); escuro.WellLine = C("#2A3142");
            list.Add(escuro);

            // OLED: preto puro (pixels apagados na tela OLED), bordas finas
            var oled = new ThemeDef
            {
                Key = "oled", Name = "OLED", Desc = "Preto puro: lindo e econômico em telas OLED.",
                Bg = C("#000000"), Side = C("#000000"), Card = C("#0A0A0C"), Card2 = C("#111114"), Border = C("#1C1C21"),
                Text = C("#F2F2F5"), Sub = C("#8E8E99"), Dim = C("#55555E"), Accent = C("#B266FF"), AccentHi = C("#CC99FF"),
                Good = C("#2BE0B0"), Warn = C("#FFC24D"), Bad = C("#FF5C66"),
            }.Derive();
            oled.Well = C("#000000"); oled.Graph = C("#050506"); oled.NavHover = C("#0E0E11"); oled.Popup = C("#0E0E11");
            list.Add(oled);

            // Vidro (estilo iOS): o desfoque do Windows aparece atras de paineis translucidos com bordas claras
            var vidro = new ThemeDef
            {
                Key = "vidro", Name = "Vidro", Light = false, Glass = true, Desc = "Estilo iOS: vidro fosco translúcido sobre o seu papel de parede.",
                Bg = C("#2E0B0D14"), Side = C("#40080A10"), Card = C("#1CFFFFFF"), Card2 = C("#14FFFFFF"), Border = C("#2EFFFFFF"),
                Text = C("#F7F8FB"), Sub = C("#C9CEDA"), Dim = C("#9399A8"), Accent = C("#BF5AF2"), AccentHi = C("#D58CF7"),
                Good = C("#30D9A9"), Warn = C("#FFC54D"), Bad = C("#FF6168"),
            }.Derive();
            vidro.BtnBg = C("#24FFFFFF"); vidro.BtnBorder = C("#33FFFFFF"); vidro.Field = C("#1FFFFFFF"); vidro.FieldBorder = C("#38FFFFFF");
            vidro.Popup = C("#F2262833"); vidro.PopupBorder = C("#45FFFFFF"); vidro.Track = C("#38FFFFFF"); vidro.KnobOff = C("#F0FFFFFF");
            vidro.NavHover = C("#14FFFFFF"); vidro.NavSel = C("#2BFFFFFF"); vidro.ItemSel = C("#5ABF5AF2"); vidro.ItemHot = C("#80BF5AF2");
            vidro.Scroll = C("#4DFFFFFF"); vidro.Tip = C("#F2262833"); vidro.TipBorder = C("#45FFFFFF");
            vidro.DangerBg = C("#40FF6168"); vidro.DangerBorder = C("#80FF6168"); vidro.DangerFg = C("#FFB3B7");
            vidro.Well = C("#6B000000"); vidro.Graph = C("#59000000"); vidro.WellLine = C("#40FFFFFF");
            list.Add(vidro);

            var vidroClaro = new ThemeDef
            {
                Key = "vidro-claro", Name = "Vidro claro", Light = true, Glass = true, Desc = "O vidro do iOS no modo claro: branco fosco e leve.",
                Bg = C("#40F4F5F9"), Side = C("#59FFFFFF"), Card = C("#8CFFFFFF"), Card2 = C("#6BFFFFFF"), Border = C("#1F000000"),
                Text = C("#111318"), Sub = C("#4B5262"), Dim = C("#7C8395"), Accent = C("#A23CD6"), AccentHi = C("#8A2BBF"),
                Good = C("#0F9F7C"), Warn = C("#B97D0A"), Bad = C("#D7343F"),
            }.Derive();
            vidroClaro.BtnBg = C("#A6FFFFFF"); vidroClaro.BtnBorder = C("#24000000"); vidroClaro.Field = C("#99FFFFFF"); vidroClaro.FieldBorder = C("#29000000");
            vidroClaro.Popup = C("#F7FBFBFD"); vidroClaro.PopupBorder = C("#29000000"); vidroClaro.Track = C("#29000000"); vidroClaro.KnobOff = C("#FFFFFFFF");
            vidroClaro.NavHover = C("#12000000"); vidroClaro.NavSel = C("#40FFFFFF"); vidroClaro.ItemSel = C("#33A23CD6"); vidroClaro.ItemHot = C("#4DA23CD6");
            vidroClaro.Scroll = C("#33000000"); vidroClaro.Tip = C("#F7FBFBFD"); vidroClaro.TipBorder = C("#29000000");
            vidroClaro.DangerBg = C("#33D7343F"); vidroClaro.DangerBorder = C("#66D7343F"); vidroClaro.DangerFg = C("#B3202C");
            vidroClaro.Well = C("#D91B1F27"); vidroClaro.Graph = C("#CC20242D");
            list.Add(vidroClaro);

            list.Add(new ThemeDef
            {
                Key = "claro", Name = "Claro", Light = true, Desc = "Branco limpo, para quem usa o Windows no modo claro.",
                Bg = C("#F3F4F8"), Side = C("#FFFFFF"), Card = C("#FFFFFF"), Card2 = C("#F5F6FA"), Border = C("#E1E4EC"),
                Text = C("#161922"), Sub = C("#5C6373"), Dim = C("#8C93A3"), Accent = C("#7C3AED"), AccentHi = C("#6D28D9"),
                Good = C("#0E9F7E"), Warn = C("#B97D0A"), Bad = C("#D93B43"),
            }.Derive());

            list.Add(new ThemeDef
            {
                Key = "neon", Name = "Neon Taka", Desc = "Roxo e rosa do logo do TakaVox, com brilho de neon.",
                Bg = C("#0C0812"), Side = C("#120B1A"), Card = C("#180F22"), Card2 = C("#1F142C"), Border = C("#2E1F40"),
                Text = C("#F3ECFA"), Sub = C("#A795BC"), Dim = C("#6B5B80"), Accent = C("#FF1F8F"), AccentHi = C("#FF6BB5"),
                Good = C("#2EE6B6"), Warn = C("#FFC04D"), Bad = C("#FF4D6A"),
            }.Derive());

            list.Add(new ThemeDef
            {
                Key = "grafite", Name = "Grafite", Desc = "Cinza de estúdio com detalhes azul-gelo, como os DAWs.",
                Bg = C("#2A2F33"), Side = C("#25292D"), Card = C("#33393E"), Card2 = C("#3B4247"), Border = C("#474F55"),
                Text = C("#E9EDEF"), Sub = C("#A6AFB5"), Dim = C("#737C82"), Accent = C("#7FC8C9"), AccentHi = C("#A9D6D7"),
                Good = C("#8FD694"), Warn = C("#F0C05A"), Bad = C("#F0686E"),
            }.Derive());

            list.Add(new ThemeDef
            {
                Key = "oceano", Name = "Oceano", Desc = "Azul profundo com ciano: calmo e moderno.",
                Bg = C("#07111C"), Side = C("#0A1624"), Card = C("#0E1C2D"), Card2 = C("#132438"), Border = C("#1D3149"),
                Text = C("#E6F0FA"), Sub = C("#8BA3BC"), Dim = C("#56708A"), Accent = C("#1FB6FF"), AccentHi = C("#6CD0FF"),
                Good = C("#29D8A6"), Warn = C("#F7B955"), Bad = C("#F2545B"),
            }.Derive());

            list.Add(new ThemeDef
            {
                Key = "sakura", Name = "Sakura", Desc = "Rosa cerejeira suave, clima de anime.",
                Bg = C("#140C11"), Side = C("#1A1016"), Card = C("#21141C"), Card2 = C("#291923"), Border = C("#3A2432"),
                Text = C("#FBEFF5"), Sub = C("#C29DB0"), Dim = C("#7E6170"), Accent = C("#FF7EB6"), AccentHi = C("#FFA8CF"),
                Good = C("#5EE2B8"), Warn = C("#FFC76B"), Bad = C("#FF5C72"),
            }.Derive());

            return list.ToArray();
        }

        public static ThemeDef Find(string key)
        {
            foreach (var t in All) if (string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        public static ThemeDef Current { get; private set; } = All[0];
        /// <summary>Muda a cada troca de tema (o que e desenhado a mao refaz os pinceis).</summary>
        public static int Version { get; private set; }
        /// <summary>Depois de trocar o tema (janela: fundo de vidro, barra de titulo, etc.).</summary>
        public static event Action Changed;

        // ------------------------------------------------------------------ pinceis vivos
        public static readonly SolidColorBrush Bg = M(), Side = M(), Card = M(), Card2 = M(), Border = M(),
            Text = M(), Sub = M(), Dim = M(), Accent = M(), AccentHi = M(), Good = M(), Warn = M(), Bad = M(),
            BtnBg = M(), BtnBorder = M(), Field = M(), FieldBorder = M(), Popup = M(), PopupBorder = M(), Track = M(), KnobOff = M(),
            TitleFg = M(), NavHover = M(), NavSel = M(), ItemSel = M(), ItemHot = M(), Scroll = M(), Tip = M(), TipBorder = M(),
            Hover = M(), DangerBg = M(), DangerBorder = M(), DangerFg = M(), Well = M(), Graph = M(), WellText = M(), WellSub = M(),
            WellDim = M(), WellLine = M(), OnAccent = M();

        public static Color BgC => Bg.Color;
        public static Color SideC => Side.Color;
        public static Color CardC => Card.Color;
        public static Color Card2C => Card2.Color;
        public static Color BorderC => Border.Color;
        public static Color TextC => Text.Color;
        public static Color SubC => Sub.Color;
        public static Color DimC => Dim.Color;
        public static Color AccentC => Accent.Color;
        public static Color AccentHiC => AccentHi.Color;
        public static Color GoodC => Good.Color;
        public static Color WarnC => Warn.Color;
        public static Color BadC => Bad.Color;

        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Display, Segoe UI");

        static SolidColorBrush M() => new SolidColorBrush();

        static Theme() { Fill(All[0]); }

        static readonly (string key, Func<ThemeDef, Color> get, SolidColorBrush brush)[] Map =
        {
            ("Th.Bg", t => t.Bg, Bg), ("Th.Side", t => t.Side, Side), ("Th.Card", t => t.Card, Card), ("Th.Card2", t => t.Card2, Card2),
            ("Th.Border", t => t.Border, Border), ("Th.Text", t => t.Text, Text), ("Th.Sub", t => t.Sub, Sub), ("Th.Dim", t => t.Dim, Dim),
            ("Th.Accent", t => t.Accent, Accent), ("Th.AccentHi", t => t.AccentHi, AccentHi), ("Th.Good", t => t.Good, Good),
            ("Th.Warn", t => t.Warn, Warn), ("Th.Bad", t => t.Bad, Bad), ("Th.BtnBg", t => t.BtnBg, BtnBg), ("Th.BtnBorder", t => t.BtnBorder, BtnBorder),
            ("Th.Field", t => t.Field, Field), ("Th.FieldBorder", t => t.FieldBorder, FieldBorder), ("Th.Popup", t => t.Popup, Popup),
            ("Th.PopupBorder", t => t.PopupBorder, PopupBorder), ("Th.Track", t => t.Track, Track), ("Th.KnobOff", t => t.KnobOff, KnobOff),
            ("Th.TitleFg", t => t.TitleFg, TitleFg), ("Th.NavHover", t => t.NavHover, NavHover), ("Th.NavSel", t => t.NavSel, NavSel),
            ("Th.ItemSel", t => t.ItemSel, ItemSel), ("Th.ItemHot", t => t.ItemHot, ItemHot), ("Th.Scroll", t => t.Scroll, Scroll),
            ("Th.Tip", t => t.Tip, Tip), ("Th.TipBorder", t => t.TipBorder, TipBorder), ("Th.Hover", t => t.Hover, Hover),
            ("Th.DangerBg", t => t.DangerBg, DangerBg), ("Th.DangerBorder", t => t.DangerBorder, DangerBorder), ("Th.DangerFg", t => t.DangerFg, DangerFg),
            ("Th.Well", t => t.Well, Well), ("Th.Graph", t => t.Graph, Graph), ("Th.WellText", t => t.WellText, WellText),
            ("Th.WellSub", t => t.WellSub, WellSub), ("Th.WellDim", t => t.WellDim, WellDim), ("Th.WellLine", t => t.WellLine, WellLine),
            ("Th.OnAccent", t => Colors.White, OnAccent),
        };

        static void Fill(ThemeDef t)
        {
            foreach (var m in Map) if (!m.brush.IsFrozen) m.brush.Color = m.get(t);
        }

        /// <summary>Pinceis vivos que alguem congelou (nao acompanham mais o tema). Vazio = tudo certo.</summary>
        public static string FrozenBrushes()
        {
            var l = new List<string>();
            foreach (var m in Map) if (m.brush.IsFrozen) l.Add(m.key);
            return string.Join(", ", l);
        }

        // pinceis derivados (cor do tema com transparencia) que acompanham a troca de tema
        static readonly List<(WeakReference<SolidColorBrush> target, SolidColorBrush src, byte alpha)> tints = new List<(WeakReference<SolidColorBrush>, SolidColorBrush, byte)>();

        static Color WithAlpha(Color c, byte a) => Color.FromArgb((byte)(a * c.A / 255), c.R, c.G, c.B);

        /// <summary>Um pincel com a cor de 'src' (pincel do tema) e transparencia 'alpha' que acompanha a troca de tema.</summary>
        public static SolidColorBrush Tint(SolidColorBrush src, byte alpha)
        {
            var b = new SolidColorBrush(WithAlpha(src.Color, alpha));
            if (tints.Count > 512) tints.RemoveAll(x => !x.target.TryGetTarget(out _));
            tints.Add((new WeakReference<SolidColorBrush>(b), src, alpha));
            return b;
        }

        /// <summary>Pincel fixo (congelado) de uma cor com transparencia: use so em desenhos refeitos a cada tema.</summary>
        public static SolidColorBrush Alpha(Color c, byte a) { var b = new SolidColorBrush(WithAlpha(c, a)); b.Freeze(); return b; }

        /// <summary>
        /// Cores dos estilos (DynamicResource). Os recursos do Application sao congelados pelo WPF no 1o uso, entao la vao
        /// COPIAS, trocadas a cada tema; os pinceis vivos (Theme.Bg etc.) ficam so no codigo.
        /// </summary>
        static void PublishResources(ResourceDictionary res)
        {
            if (res == null) return;
            foreach (var m in Map) res[m.key] = Frozen(m.get(Current));
            res["AccentBrush"] = Frozen(Current.Accent);
            res["TextBrush"] = Frozen(Current.Text);
            res["SubBrush"] = Frozen(Current.Sub);
        }

        static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>Troca o tema do app inteiro (na thread da interface).</summary>
        public static void Set(ThemeDef t)
        {
            if (t == null) t = All[0];
            Current = t;
            Fill(t);
            PublishResources(Application.Current?.Resources);
            for (int i = tints.Count - 1; i >= 0; i--)
            {
                var x = tints[i];
                if (!x.target.TryGetTarget(out var b)) { tints.RemoveAt(i); continue; }
                if (!b.IsFrozen) b.Color = WithAlpha(x.src.Color, x.alpha);
            }
            Version++;
            Changed?.Invoke();
        }

        public static void Set(string key) => Set(Find(key));

        public static void Apply(Application app)
        {
            PublishResources(app.Resources);
            var dict = (ResourceDictionary)XamlReader.Parse(Xaml);
            app.Resources.MergedDictionaries.Add(dict);
        }

        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <!-- Botao -->
  <Style TargetType='Button'>
    <Setter Property='Foreground' Value='{DynamicResource Th.Text}'/>
    <Setter Property='Background' Value='{DynamicResource Th.BtnBg}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource Th.BtnBorder}'/>
    <Setter Property='Padding' Value='14,8'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Grid>
            <Border x:Name='b' CornerRadius='8' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'/>
            <Border x:Name='h' CornerRadius='8' Background='{DynamicResource Th.Hover}' Opacity='0'/>
            <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='h' Property='Opacity' Value='0.07'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='h' Property='Opacity' Value='0.14'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='AccentButton' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Background' Value='{DynamicResource Th.Accent}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource Th.Accent}'/>
    <Setter Property='Foreground' Value='{DynamicResource Th.OnAccent}'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
  </Style>
  <Style x:Key='DangerButton' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Background' Value='{DynamicResource Th.DangerBg}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource Th.DangerBorder}'/>
    <Setter Property='Foreground' Value='{DynamicResource Th.DangerFg}'/>
  </Style>

  <!-- Botoes da barra de titulo -->
  <Style x:Key='TitleButton' TargetType='Button'>
    <Setter Property='Foreground' Value='{DynamicResource Th.TitleFg}'/>
    <Setter Property='Width' Value='46'/>
    <Setter Property='Height' Value='38'/>
    <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
    <Setter Property='FontSize' Value='10.5'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Grid>
            <Border Background='Transparent'/>
            <Border x:Name='h' Background='{DynamicResource Th.Hover}' Opacity='0'/>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='h' Property='Opacity' Value='0.13'/><Setter Property='Foreground' Value='{DynamicResource Th.Text}'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='h' Property='Opacity' Value='0.2'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='TitleClose' TargetType='Button' BasedOn='{StaticResource TitleButton}'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='Transparent'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#E81123'/><Setter Property='Foreground' Value='White'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Background' Value='#F1707A'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Navegacao lateral -->
  <Style x:Key='NavButton' TargetType='RadioButton'>
    <Setter Property='Foreground' Value='{DynamicResource Th.Sub}'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RadioButton'>
          <Grid Margin='10,2'>
            <Border x:Name='b' CornerRadius='8' Background='Transparent'/>
            <Border x:Name='bar' Width='3' CornerRadius='2' HorizontalAlignment='Left' Margin='0,9' Background='{DynamicResource Th.Accent}' Opacity='0'/>
            <ContentPresenter Margin='16,10,10,10' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{DynamicResource Th.NavHover}'/></Trigger>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='b' Property='Background' Value='{DynamicResource Th.NavSel}'/>
              <Setter TargetName='bar' Property='Opacity' Value='1'/>
              <Setter Property='Foreground' Value='{DynamicResource Th.Text}'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- CheckBox = interruptor -->
  <Style TargetType='CheckBox'>
    <Setter Property='Foreground' Value='{DynamicResource Th.Text}'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <Grid Background='Transparent'>
            <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
            <Grid Width='38' Height='20' VerticalAlignment='Center'>
              <Border x:Name='track' CornerRadius='10' Background='{DynamicResource Th.Track}'/>
              <Ellipse x:Name='knob' Width='14' Height='14' Fill='{DynamicResource Th.KnobOff}' HorizontalAlignment='Left' Margin='3,0,0,0'>
                <Ellipse.RenderTransform><TranslateTransform x:Name='slide' X='0'/></Ellipse.RenderTransform>
              </Ellipse>
            </Grid>
            <ContentPresenter Grid.Column='1' Margin='10,0,0,0' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='track' Property='Background' Value='{DynamicResource Th.Accent}'/>
              <Setter TargetName='knob' Property='Fill' Value='White'/>
              <Trigger.EnterActions>
                <BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='slide' Storyboard.TargetProperty='X' To='18' Duration='0:0:0.14'/></Storyboard></BeginStoryboard>
              </Trigger.EnterActions>
              <Trigger.ExitActions>
                <BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='slide' Storyboard.TargetProperty='X' To='0' Duration='0:0:0.14'/></Storyboard></BeginStoryboard>
              </Trigger.ExitActions>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Slider -->
  <Style x:Key='SliderFill' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'><Border Height='4' CornerRadius='2' Background='{DynamicResource Th.Accent}'/></ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='SliderEmpty' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='SliderThumb' TargetType='Thumb'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Grid Width='16' Height='16'>
            <Ellipse x:Name='e' Fill='White' Stroke='{DynamicResource Th.Accent}' StrokeThickness='3'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='e' Property='Fill' Value='#EDE7FF'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='Slider'>
    <Setter Property='MinHeight' Value='22'/>
    <Setter Property='IsMoveToPointEnabled' Value='True'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Slider'>
          <Grid VerticalAlignment='Center' Background='Transparent' Height='22'>
            <Border Height='4' CornerRadius='2' Background='{DynamicResource Th.Track}' VerticalAlignment='Center'/>
            <Track x:Name='PART_Track'>
              <Track.DecreaseRepeatButton><RepeatButton Command='{x:Static Slider.DecreaseLarge}' Style='{StaticResource SliderFill}'/></Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton><RepeatButton Command='{x:Static Slider.IncreaseLarge}' Style='{StaticResource SliderEmpty}'/></Track.IncreaseRepeatButton>
              <Track.Thumb><Thumb Style='{StaticResource SliderThumb}'/></Track.Thumb>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ComboBox -->
  <ControlTemplate x:Key='ComboToggle' TargetType='ToggleButton'>
    <Border x:Name='b' CornerRadius='8' Background='{DynamicResource Th.Field}' BorderBrush='{DynamicResource Th.FieldBorder}' BorderThickness='1'>
      <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0' Data='M0,0 L4,4 L8,0' Stroke='{DynamicResource Th.Sub}' StrokeThickness='1.6'/>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{DynamicResource Th.Accent}'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='{DynamicResource Th.Text}'/>
    <Setter Property='MinHeight' Value='34'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='MaxDropDownHeight' Value='360'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton Template='{StaticResource ComboToggle}' Focusable='False' ClickMode='Press'
                          IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'/>
            <ContentPresenter IsHitTestVisible='False' Margin='12,0,30,0' VerticalAlignment='Center' HorizontalAlignment='Left'
                              Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'/>
            <Popup IsOpen='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}' Placement='Bottom'
                   AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
              <Border Background='{DynamicResource Th.Popup}' BorderBrush='{DynamicResource Th.PopupBorder}' BorderThickness='1' CornerRadius='8' Margin='0,4,0,0'
                      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'
                      MaxHeight='{TemplateBinding MaxDropDownHeight}'>
                <ScrollViewer Margin='4' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'>
                  <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Contained'/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{DynamicResource Th.Text}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='b' Padding='10,7' CornerRadius='6' Background='Transparent'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsSelected' Value='True'><Setter TargetName='b' Property='Background' Value='{DynamicResource Th.ItemSel}'/></Trigger>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='b' Property='Background' Value='{DynamicResource Th.ItemHot}'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Barras de rolagem finas -->
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='8'/>
    <Setter Property='MinWidth' Value='8'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'><Border CornerRadius='4' Background='{DynamicResource Th.Scroll}' Margin='1'/></ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='Height' Value='8'/>
        <Setter Property='MinHeight' Value='8'/>
        <Setter Property='Template'>
          <Setter.Value>
            <ControlTemplate TargetType='ScrollBar'>
              <Track x:Name='PART_Track'>
                <Track.Thumb>
                  <Thumb>
                    <Thumb.Template>
                      <ControlTemplate TargetType='Thumb'><Border CornerRadius='4' Background='{DynamicResource Th.Scroll}' Margin='1'/></ControlTemplate>
                    </Thumb.Template>
                  </Thumb>
                </Track.Thumb>
              </Track>
            </ControlTemplate>
          </Setter.Value>
        </Setter>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType='ToolTip'>
    <Setter Property='Foreground' Value='{DynamicResource Th.Text}'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='MaxWidth' Value='380'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='{DynamicResource Th.Tip}' BorderBrush='{DynamicResource Th.TipBorder}' BorderThickness='1' CornerRadius='6' Padding='10,7'>
            <ContentPresenter>
              <ContentPresenter.Resources>
                <Style TargetType='TextBlock'><Setter Property='TextWrapping' Value='Wrap'/></Style>
              </ContentPresenter.Resources>
            </ContentPresenter>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='{DynamicResource Th.Text}'/>
    <Setter Property='Background' Value='{DynamicResource Th.Field}'/>
    <Setter Property='BorderBrush' Value='{DynamicResource Th.FieldBorder}'/>
    <Setter Property='CaretBrush' Value='{DynamicResource Th.Text}'/>
    <Setter Property='Padding' Value='8,6'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border CornerRadius='8' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";
    }
}
