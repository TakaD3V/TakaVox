using System;
using System.Collections.Generic;
using IO = System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using TakaVox.Plugins;

namespace TakaVox.UI
{
    /// <summary>
    /// Janela de um plugin no estilo do FL Studio: barra fina escura com o nome, navegador de presets (◀ ▶) e botoes;
    /// dentro, a janela ORIGINAL do plugin (embutida, do jeito que o fabricante desenhou) ou, para plugins sem janela
    /// propria, um painel de knobs como o dos efeitos "Fruity" (nome em cima, valor embaixo).
    /// </summary>
    sealed class PluginWindow : Window
    {
        // cores do FL Studio (tema padrao), medidas numa captura de tela
        internal static readonly SolidColorBrush TitleBg = Fz(0x27, 0x24, 0x1E), TitleFg = Fz(0xD2, 0xCF, 0xC6), TitleDim = Fz(0x8A, 0x87, 0x7F),
            BodyBg = Fz(0x31, 0x37, 0x3B), CellBg = Fz(0x3C, 0x43, 0x46), LabelBg = Fz(0x45, 0x4D, 0x53), LabelFg = Fz(0xC5, 0xCD, 0xD2),
            ValueFg = Fz(0xE9, 0xED, 0xEF), HoverBg = Fz(0x3D, 0x39, 0x31), PresetBg = Fz(0x1C, 0x1A, 0x15), Edge = Fz(0x12, 0x10, 0x0C),
            Arc = Fz(0xA9, 0xD6, 0xD7);

        static SolidColorBrush Fz(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        const double TitleH = 26, MinW = 390;
        readonly PluginSlot slot;
        readonly PluginInstance inst;
        readonly DispatcherTimer timer;
        readonly Grid body = new Grid();
        readonly TextBlock presetText = new TextBlock();
        Border presetBox;
        Button prevBtn, nextBtn, maxBtn;
        EditorHost host;
        KnobPanel knobs;
        bool generic, resizable;
        int lastProgram = -2, lastProgramCount = -1;

        /// <summary>O usuario trocou de preset ou mexeu num knob (o app salva a cadeia).</summary>
        public event Action Edited;
        public bool ShowingOriginal => !generic;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern int SetThreadDpiHostingBehavior(int value);

        public PluginWindow(Window owner, PluginSlot s, bool preferGeneric)
        {
            slot = s; inst = s.Inst;
            Owner = owner;
            Title = s.Name + " – TakaVox";
            Icon = owner?.Icon;
            ShowInTaskbar = false;
            Background = BodyBg;
            Foreground = ValueFg;
            FontFamily = Theme.Ui;
            FontSize = 12.5;
            MinWidth = MinW;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanMinimize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = TitleH, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
            });
            SourceInitialized += (o, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                try { int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4); int square = 1; DwmSetWindowAttribute(h, 33, ref square, 4); } catch { }
            };

            var root = new DockPanel { LastChildFill = true };
            var bar = BuildTitleBar();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(body);
            Content = new Border { BorderBrush = Edge, BorderThickness = new Thickness(1), Child = root };

            generic = preferGeneric || !inst.HasEditor;
            ShowBody();
            UpdatePresetText();

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (o, e) => Tick();
            Loaded += (o, e) => timer.Start();
            Closed += (o, e) => { timer.Stop(); host?.Dispose(); host = null; };
            StateChanged += (o, e) => { if (maxBtn != null) maxBtn.Content = WindowState == WindowState.Maximized ? "" : ""; };
        }

        /// <summary>Mostra a janela. Em modo DPI misto: plugins VST2 antigos sao ampliados pelo Windows em telas com zoom.</summary>
        public void Open()
        {
            int old = -1;
            try { old = SetThreadDpiHostingBehavior(1); } catch { }
            try
            {
                new WindowInteropHelper(this).EnsureHandle();
                ApplyEditorSize();
                Show();
            }
            finally { if (old >= 0) try { SetThreadDpiHostingBehavior(old); } catch { } }
        }

        // ================================================================== barra de titulo
        FrameworkElement BuildTitleBar()
        {
            var g = new Grid { Height = TitleH, Background = TitleBg };
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

            var tri = new Path { Data = Geometry.Parse("M0,0 L5,4 L0,8 Z"), Fill = TitleDim, Width = 5, Height = 8, Stretch = Stretch.None };
            var menuBtn = Flat(tri, "Opções do plugin", 22);
            menuBtn.Click += (o, e) => ShowMenu(menuBtn);
            g.Children.Add(Ui.At(menuBtn, 0));
            var gear = Flat(Ui.Icon("", 13, TitleDim), "Opções do plugin", 22);
            gear.Click += (o, e) => ShowMenu(gear);
            g.Children.Add(Ui.At(gear, 1));

            var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 10, 1), FontSize = 13 };
            title.Inlines.Add(new System.Windows.Documents.Run(slot.Name) { Foreground = TitleFg });
            title.Inlines.Add(new System.Windows.Documents.Run(" (TakaVox)") { Foreground = TitleDim });
            title.ToolTip = slot.Name + (slot.Vendor.Length > 0 ? " · " + slot.Vendor : "") + " · " + slot.FormatName;
            g.Children.Add(Ui.At(title, 2));

            // presets de fabrica: nome (clique = lista) e setas
            presetText.Foreground = TitleFg;
            presetText.FontSize = 12.5;
            presetText.VerticalAlignment = VerticalAlignment.Center;
            presetText.MaxWidth = 170;
            presetText.TextTrimming = TextTrimming.CharacterEllipsis;
            var presetBtn = Flat(presetText, "Presets de fábrica do plugin (clique para ver a lista)", double.NaN);
            presetBtn.Padding = new Thickness(8, 0, 6, 0);
            presetBtn.Click += (o, e) => ShowPresetMenu();
            prevBtn = Flat(new Path { Data = Geometry.Parse("M5,0 L0,4.5 L5,9 Z"), Fill = TitleFg, Stretch = Stretch.None }, "Preset anterior", 16);
            nextBtn = Flat(new Path { Data = Geometry.Parse("M0,0 L5,4.5 L0,9 Z"), Fill = TitleFg, Stretch = Stretch.None }, "Próximo preset", 16);
            prevBtn.Click += (o, e) => StepProgram(-1);
            nextBtn.Click += (o, e) => StepProgram(+1);
            var ps = new StackPanel { Orientation = Orientation.Horizontal };
            ps.Children.Add(presetBtn); ps.Children.Add(prevBtn); ps.Children.Add(nextBtn);
            presetBox = new Border { Background = PresetBg, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 3, 8, 3), Padding = new Thickness(0, 0, 4, 0), Child = ps };
            g.Children.Add(Ui.At(presetBox, 3));

            var min = Flat(Ui.Icon("", 10, TitleFg), "Minimizar", 30);
            min.Click += (o, e) => WindowState = WindowState.Minimized;
            maxBtn = Flat("", "Maximizar", 30);
            maxBtn.FontFamily = Theme.Icons; maxBtn.FontSize = 10; maxBtn.Foreground = TitleFg;
            maxBtn.Click += (o, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            var close = Flat(Ui.Icon("", 10, TitleFg), "Fechar", 30);
            close.Click += (o, e) => Close();
            g.Children.Add(Ui.At(min, 4));
            g.Children.Add(Ui.At(maxBtn, 5));
            g.Children.Add(Ui.At(close, 6));
            foreach (UIElement c in g.Children) WindowChrome.SetIsHitTestVisibleInChrome(c, true);
            WindowChrome.SetIsHitTestVisibleInChrome(title, false);
            return g;
        }

        /// <summary>Botao "chapado" da barra (fundo so no mouse em cima), como os do FL.</summary>
        static Button Flat(object content, string tip, double width)
        {
            var b = new Button { Content = content, ToolTip = tip, Focusable = false, Cursor = Cursors.Arrow, Padding = new Thickness(0) };
            if (!double.IsNaN(width)) b.Width = width;
            b.Template = FlatTemplate;
            return b;
        }

        static readonly ControlTemplate FlatTemplate = (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Border x:Name='b' Background='Transparent' Padding='{TemplateBinding Padding}'>
    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#3D3931'/></Trigger>
    <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Background' Value='#4A453B'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>");

        // ================================================================== menus (escuros, como os do FL)
        static readonly ResourceDictionary MenuStyles = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Menu' TargetType='ContextMenu'>
    <Setter Property='Foreground' Value='#E2DFD6'/>
    <Setter Property='FontSize' Value='12.5'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ContextMenu'>
          <Border Background='#2B2822' BorderBrush='#12100C' BorderThickness='1' Padding='0,3'>
            <ScrollViewer VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' MaxHeight='{TemplateBinding MaxHeight}'>
              <ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle'/>
            </ScrollViewer>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='MenuItem'>
    <Setter Property='Foreground' Value='#E2DFD6'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='MenuItem'>
          <Border x:Name='bd' Background='Transparent' Padding='6,4,16,4'>
            <Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width='20'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
              <TextBlock x:Name='chk' Text='&#xE73E;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='11' Foreground='#A9D6D7' VerticalAlignment='Center' Visibility='Hidden'/>
              <ContentPresenter Grid.Column='1' ContentSource='Header' RecognizesAccessKey='False' VerticalAlignment='Center'/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='bd' Property='Background' Value='#46413A'/></Trigger>
            <Trigger Property='IsChecked' Value='True'><Setter TargetName='chk' Property='Visibility' Value='Visible'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
    <Setter Property='Template'>
      <Setter.Value><ControlTemplate TargetType='Separator'><Border Height='1' Margin='8,3' Background='#423D35'/></ControlTemplate></Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>");

        static ContextMenu NewMenu(UIElement anchor)
        {
            var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, MaxHeight = 460 };
            m.Resources.MergedDictionaries.Add(MenuStyles);
            m.Style = (Style)MenuStyles["Menu"];
            return m;
        }

        static MenuItem Item(string text, Action click, bool? check = null, bool enabled = true)
        {
            var it = new MenuItem { Header = text, IsEnabled = enabled };
            if (check.HasValue) { it.IsCheckable = true; it.IsChecked = check.Value; }
            if (click != null) it.Click += (o, e) => click();
            return it;
        }

        void ShowMenu(UIElement anchor)
        {
            var m = NewMenu(anchor);
            if (inst.HasEditor)
            {
                m.Items.Add(Item("Janela original do plugin", () => SetGeneric(false), !generic));
                m.Items.Add(Item("Controles em knobs (TakaVox)", () => SetGeneric(true), generic));
                m.Items.Add(new Separator());
            }
            if (generic && knobs != null && knobs.Count > 0) m.Items.Add(Item("Voltar todos os knobs ao padrão", () => { knobs.ResetAll(); Edited?.Invoke(); }));
            m.Items.Add(Item("Sempre na frente", () => Topmost = !Topmost, Topmost));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Fechar", Close));
            m.IsOpen = true;
        }

        // ================================================================== presets: de fabrica (programas) + os seus
        // Os seus = o estado completo do plugin num arquivo (%APPDATA%\TakaVox\presets\<plugin>\nome.tvpreset).
        string currentUser;          // preset seu carregado/salvo por ultimo (null = de fabrica ou ajustes soltos)
        int userCount = -1;

        static string SafeName(string n)
        {
            foreach (var c in IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return n.Trim().TrimEnd('.');
        }

        string PresetDir => IO.Path.Combine(Log.Dir, "presets", SafeName(slot.Name));

        string[] UserPresets()
        {
            try
            {
                if (!IO.Directory.Exists(PresetDir)) return new string[0];
                return IO.Directory.GetFiles(PresetDir, "*.tvpreset").Select(IO.Path.GetFileNameWithoutExtension)
                    .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            catch { return new string[0]; }
        }

        void ShowPresetMenu()
        {
            var m = NewMenu(presetBox);
            int n = inst.ProgramCount, cur = currentUser == null ? inst.CurrentProgram : -1;
            for (int i = 0; i < n && i < 1024; i++)
            {
                int k = i;
                m.Items.Add(Item($"{i + 1}.  {inst.ProgramName(i)}", () => SetProgram(k), i == cur));
            }
            var mine = UserPresets();
            if (n > 0 && mine.Length > 0) m.Items.Add(new Separator());
            foreach (var u in mine)
            {
                string name = u;
                m.Items.Add(Item("★  " + u, () => LoadUserPreset(name), name == currentUser));
            }
            if (n == 0 && mine.Length == 0) m.Items.Add(Item("Nenhum preset ainda (este plugin não traz de fábrica)", null, null, false));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Salvar preset…", SaveUserPreset));
            if (mine.Length > 0)
            {
                m.Items.Add(Item("Abrir a pasta dos seus presets", () =>
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", "\"" + PresetDir + "\""); } catch { }
                }));
                if (currentUser != null)
                {
                    string del = currentUser;
                    m.Items.Add(Item("Excluir o preset \"" + del + "\"", () => DeleteUserPreset(del)));
                }
            }
            m.IsOpen = true;
        }

        /// <summary>◀ ▶: passa pelos presets de fabrica e depois pelos seus.</summary>
        void StepProgram(int d)
        {
            int n = inst.ProgramCount;
            var mine = UserPresets();
            int total = n + mine.Length;
            if (total == 0) return;
            int pos = currentUser != null ? n + Array.IndexOf(mine, currentUser) : inst.CurrentProgram;
            if (pos < 0 || pos >= total) pos = d > 0 ? -1 : 0;
            int next = ((pos + d) % total + total) % total;
            if (next < n) SetProgram(next);
            else LoadUserPreset(mine[next - n]);
        }

        void SetProgram(int i)
        {
            if (!inst.SetProgram(i)) return;
            currentUser = null;
            lastProgram = -2;
            UpdatePresetText();
            knobs?.RefreshValues();
            Edited?.Invoke();
        }

        void LoadUserPreset(string name)
        {
            try
            {
                var b = IO.File.ReadAllBytes(IO.Path.Combine(PresetDir, name + ".tvpreset"));
                if (!inst.SetState(b)) { Note("Não deu para carregar o preset \"" + name + "\" neste plugin."); return; }
                currentUser = name;
                lastProgram = -2;
                UpdatePresetText();
                knobs?.RefreshValues();
                Edited?.Invoke();
            }
            catch (Exception ex) { Note("Não deu para abrir o preset: " + ex.Message); }
        }

        void SaveUserPreset()
        {
            string name = Prompt(this, "Salvar preset", "Nome do preset de " + slot.Name + ":", currentUser ?? "");
            if (name == null) return;
            name = SafeName(name);
            if (name.Length == 0) return;
            var st = inst.GetState();
            if (st == null) { Note("Este plugin não deixou salvar os ajustes dele."); return; }
            try
            {
                IO.Directory.CreateDirectory(PresetDir);
                IO.File.WriteAllBytes(IO.Path.Combine(PresetDir, name + ".tvpreset"), st);
                currentUser = name;
                lastProgram = -2;
                UpdatePresetText();
            }
            catch (Exception ex) { Note("Não deu para salvar: " + ex.Message); }
        }

        void DeleteUserPreset(string name)
        {
            if (MessageBox.Show(this, "Excluir o preset \"" + name + "\"?", "TakaVox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { IO.File.Delete(IO.Path.Combine(PresetDir, name + ".tvpreset")); } catch { }
            if (currentUser == name) currentUser = null;
            lastProgram = -2;
            UpdatePresetText();
        }

        void Note(string msg) => MessageBox.Show(this, msg, "TakaVox", MessageBoxButton.OK, MessageBoxImage.Information);

        void UpdatePresetText()
        {
            int n = inst.ProgramCount, cur = n > 0 ? inst.CurrentProgram : -1;
            int u = lastProgram == -2 || userCount < 0 ? UserPresets().Length : userCount;
            if (cur == lastProgram && n == lastProgramCount && u == userCount) return;
            lastProgram = cur; lastProgramCount = n; userCount = u;
            string name = currentUser ?? (cur >= 0 ? inst.ProgramName(cur) : "");
            presetText.Text = name.Length > 0 ? name : "Presets";
            presetText.Foreground = name.Length > 0 ? TitleFg : TitleDim;
            prevBtn.IsEnabled = nextBtn.IsEnabled = n + u > 1;
        }

        /// <summary>Caixinha escura pedindo um texto (null = cancelou).</summary>
        static string Prompt(Window owner, string title, string label, string initial)
        {
            var box = new TextBox { Text = initial, MinWidth = 280, Margin = new Thickness(0, 6, 0, 12) };
            var ok = new Button { Content = "Salvar", IsDefault = true, MinWidth = 90, Style = (Style)Application.Current.TryFindResource("AccentButton") };
            var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
            panel.Children.Add(new TextBlock { Text = label, Foreground = TitleFg });
            panel.Children.Add(box);
            panel.Children.Add(btns);
            var w = new Window
            {
                Title = title, Owner = owner, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = TitleBg,
                ShowInTaskbar = false, FontFamily = Theme.Ui, FontSize = 13,
            };
            w.SourceInitialized += (o, e) => { try { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, 4); } catch { } };
            ok.Click += (o, e) => w.DialogResult = true;
            w.Loaded += (o, e) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? box.Text : null;
        }

        // ================================================================== corpo: janela original ou knobs
        void SetGeneric(bool g)
        {
            if (g == generic) return;
            generic = g;
            ShowBody();
            ApplyEditorSize();
        }

        void ShowBody()
        {
            body.Children.Clear();
            if (!generic)
            {
                host = new EditorHost(inst) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
                host.PluginResized += (w, h) => ApplyEditorSize();
                host.Built += () => ApplyEditorSize();
                host.CreateFailed += () =>
                {
                    // nao abriu a janela original (ou travou): fica com os knobs
                    if (inst.CheckCrashed()) { Close(); return; }
                    generic = true;
                    ShowBody();
                    ApplyEditorSize();
                };
                body.Children.Add(host);
            }
            else
            {
                if (host != null) { host.Dispose(); host = null; }
                if (knobs == null)
                {
                    knobs = new KnobPanel(inst);
                    knobs.Edited += () => Edited?.Invoke();
                }
                body.Children.Add(knobs.Root);
            }
        }

        double Scale()
        {
            try { return VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { return 1.0; }
        }

        /// <summary>Ajusta a moldura ao tamanho da janela do plugin (ou ao painel de knobs).</summary>
        void ApplyEditorSize()
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            if (generic || host == null || !host.Ok)
            {
                resizable = false;
                ResizeMode = ResizeMode.CanMinimize;
                chrome.ResizeBorderThickness = new Thickness(0);
                maxBtn.Visibility = Visibility.Collapsed;
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                SizeToContent = SizeToContent.WidthAndHeight;
                return;
            }
            double s = Scale();
            double w = host.PxW / s, h = host.PxH / s;
            resizable = inst.EditorResizable;
            maxBtn.Visibility = resizable ? Visibility.Visible : Visibility.Collapsed;
            if (resizable)
            {
                ResizeMode = ResizeMode.CanResize;
                chrome.ResizeBorderThickness = new Thickness(5);
                host.HorizontalAlignment = HorizontalAlignment.Stretch;
                host.VerticalAlignment = VerticalAlignment.Stretch;
                host.Width = double.NaN; host.Height = double.NaN;
                if (WindowState == WindowState.Normal)
                {
                    SizeToContent = SizeToContent.Manual;
                    double ww = Math.Max(MinW, w + 2), hh = h + TitleH + 2;
                    if (Math.Abs(ActualWidth - ww) > 1.5 || Math.Abs(ActualHeight - hh) > 1.5) { Width = ww; Height = hh; }
                }
            }
            else
            {
                ResizeMode = ResizeMode.CanMinimize;
                chrome.ResizeBorderThickness = new Thickness(0);
                host.HorizontalAlignment = HorizontalAlignment.Center;
                host.VerticalAlignment = VerticalAlignment.Top;
                host.Width = w; host.Height = h;
                SizeToContent = SizeToContent.WidthAndHeight;
            }
        }

        void Tick()
        {
            if (slot.Inst != inst || !inst.Alive || inst.CheckCrashed()) { Close(); return; }
            if (!IsVisible || WindowState == WindowState.Minimized) return;
            if (generic) knobs?.RefreshValues();
            UpdatePresetText();
        }

        /// <summary>O plugin mudou os proprios parametros (preset, automacao): atualiza os knobs.</summary>
        public void RefreshValues()
        {
            knobs?.RefreshValues();
            lastProgram = -2;
            UpdatePresetText();
        }
    }

    // ====================================================================== janela original embutida
    /// <summary>Hospeda a janela nativa do plugin dentro da moldura WPF (o plugin desenha numa janela filha).</summary>
    sealed class EditorHost : HwndHost
    {
        readonly PluginInstance inst;
        public int PxW = 400, PxH = 300;
        public bool Ok { get; private set; }
        public event Action<int, int> PluginResized;
        public event Action Built, CreateFailed;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hinst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WM_TVH_RESIZE = 0x8000 + 0x51;

        public EditorHost(PluginInstance i) { inst = i; }

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            IntPtr h = IntPtr.Zero;
            try { h = inst.CreateEditorIn(parent.Handle, out PxW, out PxH); } catch { h = IntPtr.Zero; }
            if (h != IntPtr.Zero)
            {
                Ok = true;
                Dispatcher.BeginInvoke(new Action(() => Built?.Invoke()));
                return new HandleRef(this, h);
            }
            // nao abriu: uma janela vazia so para o WPF; a moldura troca para os knobs
            h = CreateWindowEx(0, "STATIC", "", WS_CHILD | WS_VISIBLE, 0, 0, 10, 10, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Dispatcher.BeginInvoke(new Action(() => CreateFailed?.Invoke()));
            return new HandleRef(this, h);
        }

        protected override void DestroyWindowCore(HandleRef hwnd) { DestroyWindow(hwnd.Handle); }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TVH_RESIZE)
            {
                PxW = wParam.ToInt32(); PxH = lParam.ToInt32();
                PluginResized?.Invoke(PxW, PxH);
                handled = true;
                return IntPtr.Zero;
            }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }
    }

    // ====================================================================== painel de knobs (plugins sem janela)
    /// <summary>Todos os parametros como knobs, no layout dos efeitos "Fruity" do FL: nome em cima, valor embaixo.</summary>
    sealed class KnobPanel
    {
        sealed class Cell { public int Index; public double Default; public FlKnob Knob; public TextBlock Value; }

        public readonly FrameworkElement Root;
        public int Count => cells.Count;
        public event Action Edited;
        readonly PluginInstance inst;
        readonly List<Cell> cells = new List<Cell>();
        const int MaxParams = 256;

        public KnobPanel(PluginInstance inst)
        {
            this.inst = inst;
            var list = new List<PluginInstance.ParamInfo>();
            int n = inst.ParamCount;
            for (int i = 0; i < n && list.Count < MaxParams; i++)
            {
                var p = inst.GetParam(i);
                if (p != null && !p.IsBypass) list.Add(p);
            }
            int cols = list.Count <= 3 ? Math.Max(1, list.Count) : list.Count <= 12 ? 3 : 4;
            var grid = new UniformGrid { Columns = cols, Margin = new Thickness(4) };
            foreach (var p in list) grid.Children.Add(MakeCell(p));
            FrameworkElement content = grid;
            if (list.Count == 0)
                content = new TextBlock
                {
                    Text = "Este plugin não mostra parâmetros que dê para ajustar por aqui.", Foreground = PluginWindow.LabelFg,
                    Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
                };
            var sv = new ScrollViewer
            {
                Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 640,
            };
            Root = new Border { Background = PluginWindow.BodyBg, Child = sv };
        }

        FrameworkElement MakeCell(PluginInstance.ParamInfo p)
        {
            var c = new Cell { Index = p.Index, Default = p.Default };
            string name = p.Name.Length > 0 ? p.Name : "Parâmetro " + (p.Index + 1);
            c.Knob = new FlKnob
            {
                Value = p.Value, Default = p.Default, Steps = p.Steps, Width = 40, Height = 40,
                Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = name + "\nArraste para cima/baixo (Ctrl = ajuste fino) ou use a roda do mouse. Duplo clique: valor padrão.",
            };
            c.Value = new TextBlock
            {
                Text = p.Display, Foreground = PluginWindow.ValueFg, FontSize = 13, Margin = new Thickness(2, 5, 6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            c.Knob.ValueChanged += v =>
            {
                inst.SetParam(c.Index, v);
                var q = inst.GetParam(c.Index);
                if (q != null) c.Value.Text = q.Display;
                Edited?.Invoke();
            };
            var label = new Border
            {
                Background = PluginWindow.LabelBg, CornerRadius = new CornerRadius(3), Height = 18, Margin = new Thickness(0, 5, 6, 0),
                Child = new TextBlock
                {
                    Text = name.ToUpperInvariant(), Foreground = PluginWindow.LabelFg, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(4, 0, 4, 0),
                },
                ToolTip = name,
            };
            var right = new StackPanel();
            right.Children.Add(label);
            right.Children.Add(c.Value);
            var g = new Grid { Width = 168, Height = 52 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(Ui.At(c.Knob, 0));
            g.Children.Add(Ui.At(right, 1));
            cells.Add(c);
            return new Border { Background = PluginWindow.CellBg, CornerRadius = new CornerRadius(3), Margin = new Thickness(2), Child = g };
        }

        /// <summary>Traz os valores atuais do plugin (mudancas pela janela dele, preset ou automacao).</summary>
        public void RefreshValues()
        {
            if (!inst.Alive) return;
            foreach (var c in cells)
            {
                if (c.Knob.Dragging) continue;
                var p = inst.GetParam(c.Index);
                if (p == null) continue;
                c.Knob.SetQuiet(p.Value);
                if (c.Value.Text != p.Display) c.Value.Text = p.Display;
            }
        }

        public void ResetAll()
        {
            foreach (var c in cells) if (c.Default >= 0) inst.SetParam(c.Index, c.Default);
            RefreshValues();
        }
    }

    /// <summary>Knob no estilo do FL: corpo escuro, ponteiro claro e arco azul-gelo mostrando o valor (7h30 a 4h30).</summary>
    sealed class FlKnob : FrameworkElement
    {
        public double Value, Default = -1;
        public int Steps;
        public bool Dragging { get; private set; }
        public event Action<double> ValueChanged;
        double startY, startV;

        static readonly Pen Track = MakePen(Color.FromRgb(0x26, 0x2C, 0x30), 3, PenLineCap.Flat);
        static readonly Pen ArcPen = MakePen(Color.FromRgb(0xA9, 0xD6, 0xD7), 3, PenLineCap.Flat);
        static readonly Pen Pointer = MakePen(Color.FromRgb(0xDF, 0xF4, 0xF4), 2, PenLineCap.Round);
        static readonly Pen BodyEdge = MakePen(Color.FromRgb(0x1A, 0x1F, 0x22), 1, PenLineCap.Flat);
        static readonly Brush BodyFill = MakeBody();

        static Pen MakePen(Color c, double t, PenLineCap cap)
        {
            var p = new Pen(new SolidColorBrush(c), t) { StartLineCap = cap, EndLineCap = cap };
            p.Freeze();
            return p;
        }

        static Brush MakeBody()
        {
            var b = new LinearGradientBrush(Color.FromRgb(0x4E, 0x58, 0x5E), Color.FromRgb(0x2A, 0x31, 0x35), 90);
            b.Freeze();
            return b;
        }

        public FlKnob() { Cursor = Cursors.SizeNS; Focusable = false; }

        static Point Pt(double cx, double cy, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        }

        static Geometry Arc(double cx, double cy, double r, double a0, double a1)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(Pt(cx, cy, r, a0), false, false);
                c.ArcTo(Pt(cx, cy, r, a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight, cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 2;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            double ra = r - 0.5, rb = r - 5;
            dc.DrawGeometry(null, Track, Arc(cx, cy, ra, -135, 135));
            double ang = -135 + 270 * Math.Max(0, Math.Min(1, Value));
            if (ang > -134) dc.DrawGeometry(null, ArcPen, Arc(cx, cy, ra, -135, ang));
            dc.DrawEllipse(BodyFill, BodyEdge, new Point(cx, cy), rb, rb);
            dc.DrawLine(Pointer, Pt(cx, cy, rb * 0.42, ang), Pt(cx, cy, rb * 0.9, ang));
        }

        void Set(double v)
        {
            v = Math.Max(0, Math.Min(1, v));
            if (Steps > 0) v = Math.Round(v * Steps) / Steps;
            if (Math.Abs(v - Value) < 1e-9) return;
            Value = v;
            InvalidateVisual();
            ValueChanged?.Invoke(v);
        }

        /// <summary>Atualiza sem avisar (valor vindo do plugin).</summary>
        public void SetQuiet(double v)
        {
            if (Math.Abs(v - Value) < 1e-6) return;
            Value = v;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && Default >= 0) { Set(Default); e.Handled = true; return; }
            startY = e.GetPosition(this).Y;
            startV = Value;
            Dragging = CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!Dragging) return;
            double dy = startY - e.GetPosition(this).Y;
            double sens = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ? 1200 : 220;
            Set(startV + dy / sens);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (Dragging) { Dragging = false; ReleaseMouseCapture(); }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e) { Dragging = false; }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            double step = Steps > 0 ? 1.0 / Steps : (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 0.002 : 0.01;
            Set(Value + Math.Sign(e.Delta) * step);
            e.Handled = true;
        }
    }
}
