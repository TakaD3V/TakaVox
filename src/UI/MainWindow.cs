using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TakaVox.Audio;
using TakaVox.Dsp;
using TakaVox.Interop;

namespace TakaVox.UI
{
    public sealed partial class MainWindow : Window
    {
        readonly AudioEngine engine;
        readonly AppSettings app;
        readonly ChainParams P;
        readonly bool snapshotMode;
        readonly List<Binder> binders = new List<Binder>();
        bool refreshing, dirty, exiting, suppressKeepDefault;
        DateTime lastSave = DateTime.MinValue;

        readonly Dictionary<string, FrameworkElement> pages = new Dictionary<string, FrameworkElement>();
        readonly Dictionary<string, RadioButton> nav = new Dictionary<string, RadioButton>();
        string currentPage = "painel";
        Grid pageHost;
        TextBlock pageTitle, pageSubtitle;
        readonly DispatcherTimer fastTimer, slowTimer;
        TrayIcon tray;

        Button bypassBtn;
        ComboBox presetBox;
        Ellipse statusDot;
        TextBlock statusText, sideStatus;

        public MainWindow(AudioEngine engine, AppSettings app, bool snapshot)
        {
            this.engine = engine;
            this.app = app;
            P = engine.Params;
            snapshotMode = snapshot;

            Title = "TakaVox – Estúdio de Voz";
            Theme.Set(app.ThemeKey);
            Background = Theme.Bg;
            Foreground = Theme.Text;
            FontFamily = Theme.Ui;
            FontSize = 13;
            Width = app.WinWidth; Height = app.WinHeight;
            MinWidth = 1000; MinHeight = 720;
            if (!double.IsNaN(app.WinLeft) && !double.IsNaN(app.WinTop) &&
                app.WinLeft > SystemParameters.VirtualScreenLeft - 50 && app.WinTop > SystemParameters.VirtualScreenTop - 50 &&
                app.WinLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 200 &&
                app.WinTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 200)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = app.WinLeft; Top = app.WinTop;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;
            UpdateAppIcon();
            if (!snapshot) SetupChrome();

            InitPlugins();
            InitAiVoice();
            InitSounds();
            Content = BuildLayout();

            fastTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            fastTimer.Tick += (s, e) => FastTick();
            slowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            slowTimer.Tick += (s, e) => SlowTick();

            engine.DevicesChanged += () => Dispatcher.BeginInvoke(new Action(OnDevicesChanged));
            engine.DefaultDeviceChanged += (cap, role, id) => { if (cap) Dispatcher.BeginInvoke(new Action(OnDefaultCaptureChanged)); };
            engine.InputRelocated += (id, name) => Dispatcher.BeginInvoke(new Action(() =>
            {
                app.InputDeviceId = id; app.InputDeviceName = name; MarkDirty(); RefreshDeviceLists();
            }));
            Log.HistoryAdded += line => Dispatcher.BeginInvoke(new Action(() => AddHistoryLine(line, true)));

            StartAudio();
            if (!snapshotMode) FirstRunSetup();

            RefreshAll();
            ShowPage("painel");
            fastTimer.Start();
            slowTimer.Start();
            StartPlugins();
            InitIdleMode();
            InitAutoRest();

            if (!snapshotMode && Environment.GetEnvironmentVariable("TAKAVOX_NO_OUTPUT") != "1")
            {
                tray = new TrayIcon(this);
                tray.Update(P.Bypass, app.MonitorOn);
                tray.SetMuted(P.Mute);
            }
            Closing += OnClosing;
            SessionWatcher();
        }

        // ================================================================== layout

        UIElement BuildLayout()
        {
            var root = Ui.Columns(Ui.Px(214), Ui.Star());

            // ---- barra lateral
            var side = new Grid { Background = Theme.Side };
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var logoIcon = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(10),
                Background = new LinearGradientBrush(Theme.AccentC, Theme.GoodC, 45),
                Child = Ui.Icon("", 18, Brushes.White),
            };
            ((TextBlock)logoIcon.Child).HorizontalAlignment = HorizontalAlignment.Center;
            UIElement logoMark = (UIElement)AppIconImage(40) ?? logoIcon;
            var logo = Ui.H(logoMark, Ui.M(Ui.V(Ui.T("TakaVox", 19, Theme.Text, true), Ui.T("Estúdio de voz com IA", 11.5, Theme.Sub)), 12, 0, 0, 0));
            logo.Margin = new Thickness(20, 22, 10, 26);
            side.Children.Add(Ui.At(logo, 0, 0));

            var navStack = new StackPanel();
            AddNav(navStack, "painel", "", "Painel");
            AddNav(navStack, "efeitos", "", "Efeitos");
            AddNav(navStack, "voz", "", "Modificador de voz");
            AddNav(navStack, "sons", "\uE8D6", "Sound pad");
            AddNav(navStack, "plugins", "", "Plugins");
            AddNav(navStack, "teste", "", "Testar voz");
            AddNav(navStack, "temas", "\uE790", "Temas");
            AddNav(navStack, "config", "", "Configurar");
            AddNav(navStack, "guia", "", "Guia e histórico");
            side.Children.Add(Ui.At(navStack, 0, 1));

            sideStatus = Ui.T("", 11.5, Theme.Sub, false, true);
            var sideBottom = Ui.V(sideStatus);
            sideBottom.Margin = new Thickness(20, 10, 16, 18);
            side.Children.Add(Ui.At(sideBottom, 0, 2));
            root.Children.Add(Ui.At(side, 0));

            // ---- conteudo
            var content = new Grid { Margin = new Thickness(28, 18, 28, 0) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            pageTitle = Ui.T("", 24, Theme.Text, true);
            pageSubtitle = Ui.T("", 13, Theme.Sub);
            header.Children.Add(Ui.At(Ui.V(pageTitle, Ui.M(pageSubtitle, 0, 2, 0, 0)), 0));

            statusDot = new Ellipse { Width = 9, Height = 9, Fill = Theme.Dim, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            statusText = Ui.T("iniciando…", 12.5, Theme.Sub, true);
            var statusPill = new Border
            {
                CornerRadius = new CornerRadius(16), Background = Theme.Card, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 7, 14, 7), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.H(statusDot, statusText),
            };
            header.Children.Add(Ui.At(statusPill, 1));

            presetBox = new ComboBox { Width = 210, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Presets de voz prontos (e os que você salvar)" };
            presetBox.SelectionChanged += (s, e) => { if (!refreshing && presetBox.SelectedItem is string name) ApplyPreset(name); };
            header.Children.Add(Ui.At(presetBox, 2));

            bypassBtn = Ui.Btn("", () => SetBypass(!P.Bypass), null, null,
                "Liga/desliga todos os efeitos na hora (A/B). Desligado = sua voz crua, como era antes.");
            bypassBtn.MinWidth = 168;
            bypassBtn.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(Ui.At(BuildMuteButton(), 3));
            header.Children.Add(Ui.At(bypassBtn, 4));
            content.Children.Add(Ui.At(header, 0, 0));

            pageHost = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            content.Children.Add(Ui.At(pageHost, 0, 1));
            root.Children.Add(Ui.At(content, 1));

            AddPage("painel", BuildDashboard());
            AddPage("efeitos", Scroll(BuildEffects()));
            AddPage("voz", Scroll(BuildVoicePage()));
            AddPage("sons", Scroll(BuildSoundsPage()));
            AddPage("plugins", Scroll(BuildPluginsPage()));
            AddPage("teste", Scroll(BuildTestPage()));
            AddPage("temas", Scroll(BuildThemesPage()));
            AddPage("config", Scroll(BuildSetupPage()));
            AddPage("guia", Scroll(BuildGuidePage()));
            RefreshPresetList();

            outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outerGrid.Children.Add(Ui.At(BuildTitleBar(), 0, 0));
            outerGrid.Children.Add(Ui.At(root, 0, 1));
            return outerGrid;
        }

        static ScrollViewer Scroll(UIElement e) => new ScrollViewer
        {
            Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 10, 20), Focusable = false,
        };

        void AddNav(StackPanel host, string key, string glyph, string text)
        {
            var rb = new RadioButton
            {
                GroupName = "nav", Style = (Style)Application.Current.Resources["NavButton"],
                Content = Ui.H(Ui.M(Ui.Icon(glyph, 16, null), 0, 0, 12, 0), Ui.T(text, 14, null)),
            };
            foreach (TextBlock t in ((StackPanel)rb.Content).Children)
                t.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = rb });
            rb.Checked += (s, e) => ShowPage(key);
            nav[key] = rb;
            host.Children.Add(rb);
        }

        void AddPage(string key, FrameworkElement el)
        {
            el.Visibility = Visibility.Collapsed;
            pages[key] = el;
            pageHost.Children.Add(el);
        }

        void ShowPage(string key)
        {
            currentPage = key;
            foreach (var kv in pages) kv.Value.Visibility = kv.Key == key ? Visibility.Visible : Visibility.Collapsed;
            if (nav.TryGetValue(key, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
            switch (key)
            {
                case "painel": pageTitle.Text = "Painel"; pageSubtitle.Text = "Sua voz ao vivo"; break;
                case "efeitos": pageTitle.Text = "Efeitos"; pageSubtitle.Text = "Cada etapa, com ajuste fino"; break;
                case "voz": pageTitle.Text = "Modificador"; pageSubtitle.Text = "Vozes ao vivo, leves"; break;
                case "sons": pageTitle.Text = "Sound pad"; pageSubtitle.Text = "Sons para o Discord, jogos e lives"; break;
                case "plugins": pageTitle.Text = "Plugins"; pageSubtitle.Text = "VST3 e VST2 sem precisar de DAW"; break;
                case "teste": pageTitle.Text = "Testar voz"; pageSubtitle.Text = "Calibração e antes × depois"; break;
                case "temas": pageTitle.Text = "Temas"; pageSubtitle.Text = "OLED, vidro e mais"; break;
                case "config": pageTitle.Text = "Configurar"; pageSubtitle.Text = "Mic, mic virtual e Windows"; break;
                case "guia": pageTitle.Text = "Guia e histórico"; pageSubtitle.Text = "Discord, jogos e o que mudou"; break;
            }
            if (key == "config") RefreshDeviceLists();
            if (key == "plugins") OnPluginsPageShown();
            if (key == "voz") AiTick();
        }

        // ================================================================== estado / parametros

        void RefreshAll()
        {
            refreshing = true;
            try { foreach (var b in binders) b.Refresh(); }
            finally { refreshing = false; }
            UpdateBypassButton();
            spectrum?.InvalidateVisual();
        }

        void OnParamChanged()
        {
            MarkDirty();
            if (!refreshing) RefreshAll();
            AiTick(); // a voz com IA aplica tom/timbre/voz na hora
        }

        void MarkDirty() { dirty = true; }

        void SaveNow()
        {
            if (snapshotMode) return;
            if (WindowState == WindowState.Normal && IsVisible)
            {
                app.WinLeft = Left; app.WinTop = Top; app.WinWidth = Width; app.WinHeight = Height;
            }
            AppSettings.Save(app, P);
            dirty = false;
            lastSave = DateTime.Now;
        }

        void SetBypass(bool bypass)
        {
            P.Bypass = bypass;
            UpdateBypassButton();
            tray?.Update(P.Bypass, app.MonitorOn);
        }

        void UpdateBypassButton()
        {
            if (bypassBtn == null) return;
            bool on = !P.Bypass;
            bypassBtn.Content = Ui.H(Ui.M(Ui.Icon(on ? "" : "", 13, Brushes.White), 0, 0, 8, 0),
                Ui.T(on ? "EFEITOS LIGADOS" : "EFEITOS DESLIGADOS", 12.5, Brushes.White, true));
            bypassBtn.Background = on ? Theme.Accent : new SolidColorBrush(Color.FromRgb(0x5A, 0x26, 0x30));
            bypassBtn.BorderBrush = bypassBtn.Background;
        }

        void RefreshPresetList()
        {
            if (presetBox == null) return;
            refreshing = true;
            try
            {
                presetBox.Items.Clear();
                foreach (var n in BuiltInPresets.Names) presetBox.Items.Add(n);
                foreach (var n in AppSettings.UserPresets()) presetBox.Items.Add("★ " + n);
                presetBox.SelectedItem = app.PresetName;
                if (presetBox.SelectedItem == null && presetBox.Items.Count > 0 && Array.IndexOf(BuiltInPresets.Names, app.PresetName) < 0)
                    presetBox.SelectedItem = null;
            }
            finally { refreshing = false; }
        }

        void ApplyPreset(string name)
        {
            if (name.StartsWith("★ "))
            {
                if (!AppSettings.LoadUserPreset(name.Substring(2), P)) return;
            }
            else BuiltInPresets.Apply(name, P);
            app.PresetName = name;
            Log.Write("Preset aplicado: " + name);
            OnParamChanged();
        }

        // ================================================================== ciclo de vida

        void StartAudio()
        {
            var mic = (!string.IsNullOrEmpty(app.InputDeviceId) && AudioDevices.IsActive(app.InputDeviceId))
                ? new DeviceInfo { Id = app.InputDeviceId, Name = AudioDevices.GetNameById(app.InputDeviceId) ?? app.InputDeviceName, IsCapture = true }
                : AudioDevices.GuessPhysicalMic(app.InputDeviceName);
            if (mic != null) { app.InputDeviceId = mic.Id; app.InputDeviceName = mic.Name; }
            engine.SetInput(app.InputDeviceId, app.InputDeviceName);

            string outId = app.OutputDeviceId;
            if (string.IsNullOrEmpty(outId) || !AudioDevices.IsActive(outId))
            {
                var byName = AudioDevices.FindByName(false, app.OutputDeviceName);
                var cab = AudioDevices.FindCableInput();
                var pick = byName ?? cab;
                outId = pick?.Id ?? "";
                if (pick != null) app.OutputDeviceName = pick.Name;
            }
            app.OutputDeviceId = outId;
            // TAKAVOX_NO_OUTPUT=1: medicoes automaticas, sem escrever no microfone virtual de verdade
            if (!snapshotMode && Environment.GetEnvironmentVariable("TAKAVOX_NO_OUTPUT") != "1") engine.SetOutput(outId);

            string mon = app.MonitorDeviceId;
            if (string.IsNullOrEmpty(mon) || !AudioDevices.IsActive(mon))
            {
                var byName = AudioDevices.FindByName(false, app.MonitorDeviceName);
                mon = byName?.Id ?? AudioDevices.GetDefaultId(false, ERole.Console) ?? "";
                app.MonitorDeviceName = AudioDevices.GetNameById(mon) ?? "";
            }
            app.MonitorDeviceId = mon;
            engine.SetMonitor(mon, app.MonitorOn && !snapshotMode, app.MonitorVolume);
        }

        void FirstRunSetup()
        {
            if (!app.OriginalsSaved)
            {
                app.OrigCaptureConsole = AudioDevices.GetDefaultId(true, ERole.Console) ?? "";
                app.OrigCaptureMultimedia = AudioDevices.GetDefaultId(true, ERole.Multimedia) ?? "";
                app.OrigCaptureComms = AudioDevices.GetDefaultId(true, ERole.Communications) ?? "";
                var d = SystemTweaks.GetDucking();
                app.OrigDucking = d.HasValue ? d.Value.ToString() : "";
                app.OrigMicId = app.InputDeviceId;
                app.OrigMicVolume = AudioDevices.GetVolume(app.InputDeviceId)?.Scalar ?? -1f;
                app.OriginalsSaved = true;
                Log.Change("Primeira execução: estado original do Windows guardado (para poder restaurar).");

                try
                {
                    SystemTweaks.SetDucking(3);
                    Log.Change("Windows › Comunicações: 'reduzir o volume de outros sons' → Não fazer nada (o jogo não abaixa quando alguém fala).");
                }
                catch (Exception ex) { Log.Write("ducking: " + ex.Message); }
                try
                {
                    SystemTweaks.SetAutostart(true);
                    Log.Change("TakaVox configurado para iniciar junto com o Windows (fica na bandeja, perto do relógio).");
                }
                catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
                SaveNow();
            }
            else SystemTweaks.RefreshAutostartPath();
            ConfigureVirtualMic(false);
        }

        /// <summary>Quando o VB-CABLE existe: usa como saida, deixa no volume certo e vira o mic padrao.</summary>
        void ConfigureVirtualMic(bool userAction)
        {
            var cabIn = AudioDevices.FindCableInput();
            var cabOut = AudioDevices.FindCableOutput();
            if (cabIn == null || cabOut == null) return;

            if (string.IsNullOrEmpty(app.OutputDeviceId) || !AudioDevices.IsActive(app.OutputDeviceId))
            {
                app.OutputDeviceId = cabIn.Id; app.OutputDeviceName = cabIn.Name;
                engine.SetOutput(cabIn.Id);
                Log.Change($"Saída do TakaVox → {cabIn.Name}");
                MarkDirty();
            }
            foreach (var id in new[] { cabIn.Id, cabOut.Id })
            {
                var v = AudioDevices.GetVolume(id);
                if (v != null && (v.Scalar < 0.999f || v.Mute))
                {
                    AudioDevices.SetVolumeScalar(id, 1f); AudioDevices.SetMute(id, false);
                    Log.Change($"Volume de '{AudioDevices.GetNameById(id)}' → 100% (sem mudo)");
                }
            }
            if ((app.KeepDefault || userAction) && !SystemTweaks.IsVirtualMicDefault())
            {
                suppressKeepDefault = true;
                if (SystemTweaks.MakeVirtualMicDefault(out var name))
                    Log.Change($"Microfone padrão do Windows (todos os usos) → {name}");
                suppressKeepDefault = false;
            }
            else if (userAction && SystemTweaks.IsVirtualMicDefault())
                Log.Change($"Microfone padrão do Windows (todos os usos): {cabOut.Name} – Discord/jogos recebem a voz do TakaVox.");
            RefreshDeviceLists();
        }

        int keepDefaultFixes;
        DateTime keepDefaultWindow = DateTime.MinValue;
        DispatcherTimer keepDefaultTimer;

        void OnDefaultCaptureChanged()
        {
            RefreshDeviceLists();
            if (exiting || suppressKeepDefault || !app.KeepDefault || snapshotMode) return;
            if (keepDefaultTimer == null)
            {
                keepDefaultTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                keepDefaultTimer.Tick += (s, e) =>
                {
                    keepDefaultTimer.Stop();
                    if (exiting || suppressKeepDefault || !app.KeepDefault) return;
                    if (AudioDevices.FindCableOutput() == null || SystemTweaks.IsVirtualMicDefault()) return;
                    if (DateTime.Now - keepDefaultWindow > TimeSpan.FromMinutes(10)) { keepDefaultWindow = DateTime.Now; keepDefaultFixes = 0; }
                    if (++keepDefaultFixes > 4)
                    {
                        Log.Change("Outro programa fica trocando o microfone padrão; o TakaVox parou de insistir. Veja Configurar.");
                        return;
                    }
                    suppressKeepDefault = true;
                    string other = AudioDevices.GetNameById(AudioDevices.GetDefaultId(true, ERole.Console)) ?? "outro dispositivo";
                    if (SystemTweaks.MakeVirtualMicDefault(out var name))
                        Log.Change($"'{other}' tinha virado o mic padrão; o TakaVox voltou para {name}.");
                    suppressKeepDefault = false;
                    RefreshDeviceLists();
                };
            }
            keepDefaultTimer.Stop();
            keepDefaultTimer.Start();
        }

        DispatcherTimer devicesDebounce;
        bool cableCached, cableCheckDirty = true;
        DateTime cableCheckAt;

        void OnDevicesChanged()
        {
            cableCheckDirty = true;
            BindListenDevice();
            if (devicesDebounce == null)
            {
                devicesDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                devicesDebounce.Tick += (s, e) =>
                {
                    devicesDebounce.Stop();
                    if (!snapshotMode && AudioDevices.FindCableInput() != null &&
                        (string.IsNullOrEmpty(app.OutputDeviceId) || !AudioDevices.IsActive(app.OutputDeviceId)))
                        ConfigureVirtualMic(false);
                    RefreshDeviceLists();
                };
            }
            devicesDebounce.Stop();
            devicesDebounce.Start();
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (exiting || snapshotMode) return;
            if (app.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                SaveNow();
                if (!app.TrayTipShown)
                {
                    tray?.Balloon("O TakaVox continua ligado", "Sua voz segue sendo tratada. Clique no ícone perto do relógio para abrir ou sair.");
                    app.TrayTipShown = true; MarkDirty();
                }
                return;
            }
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() => ExitApp(true)));
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
            Focus();
        }

        /// <summary>Sai do app. restoreMic: devolve o mic padrao do Windows para o microfone real.</summary>
        public void ExitApp(bool restoreMic)
        {
            if (exiting) return;
            exiting = true;
            try
            {
                fastTimer.Stop(); slowTimer.Stop();
                SaveNow();
                ShutdownAiVoice();
                ShutdownPlugins();
                try { listen?.Dispose(); } catch { }
                if (restoreMic && app.RestoreMicOnExit && SystemTweaks.IsVirtualMicDefault() && AudioDevices.IsActive(engine.InputId))
                {
                    AudioDevices.SetDefault(engine.InputId, ERole.Console, ERole.Multimedia, ERole.Communications);
                    Log.Change($"TakaVox fechado: microfone padrão voltou para {engine.InputName}.");
                }
            }
            catch (Exception ex) { Log.Write("Saída: " + ex.Message); }
            try { tray?.Dispose(); } catch { }
            try { engine.Dispose(); } catch { }
            Application.Current.Shutdown();
        }

        void SessionWatcher()
        {
            Application.Current.SessionEnding += (s, e) =>
            {
                // Desligando/saindo do Windows: se o app inicia com o Windows, mantem o mic virtual como padrao.
                bool keep = SystemTweaks.GetAutostart();
                ExitApp(!keep);
            };
        }

        // ================================================================== timers

        void SlowTick()
        {
            try
            {
                UpdateStatus();
                LogAudioStats();
                HowlCheck();
                SlowTickTest();
                SlowTickSounds();
                UpdateRest();
                if (currentPage == "config") SlowTickSetup();
                if (currentPage == "plugins") SlowTickPlugins();
                UpdateBackgroundTimers();
                if (dirty && (DateTime.Now - lastSave).TotalSeconds > 2) SaveNow();
            }
            catch (Exception ex) { Log.Write("SlowTick: " + ex.Message); }
        }

        void UpdateStatus()
        {
            bool capOk = engine.CaptureRunning;
            var mo = engine.MainOutput;
            bool outOk = mo != null && mo.Running;
            // procurar o cabo lista todos os dispositivos do Windows: so a cada 10 s (ou quando algo e plugado/tirado)
            if (cableCheckDirty || (DateTime.Now - cableCheckAt).TotalSeconds > (uiIdle ? 60 : 10))
            {
                cableCached = AudioDevices.FindCableOutput() != null;
                cableCheckAt = DateTime.Now;
                cableCheckDirty = false;
            }
            bool cable = cableCached;
            // capturas do README (TAKAVOX_DEMO=1): o app de demonstracao nao abre o microfone virtual de verdade
            if (snapshotMode && Environment.GetEnvironmentVariable("TAKAVOX_DEMO") == "1") { capOk = true; outOk = true; cable = true; }
            Brush dot; string text;
            if (!capOk) { dot = Theme.Bad; text = "Mic: " + engine.CaptureStatus; }
            else if (!cable) { dot = Theme.Warn; text = "Falta o microfone virtual"; }
            else if (!outOk) { dot = Theme.Warn; text = "Saída: " + (mo?.Status ?? "não escolhida"); }
            else if (P.Bypass) { dot = Theme.Warn; text = "Ativo – efeitos desligados"; }
            else { dot = Theme.Good; text = "Ativo – voz tratada"; }
            statusDot.Fill = dot;
            statusText.Text = text;
            UpdateUsage();
            sideStatus.Text = $"IA: {engine.EngineName}\nLatência ≈ {EstimateLatencyMs():0} ms" + (usageText.Length > 0 ? "\n" + usageText : "") +
                (P.VcOn ? "\nVoz: " + VoiceLabel() : "");
            UpdateDashboardStatus(capOk, outOk, cable);
            tray?.SetTooltip("TakaVox – " + text);
        }

        DateTime lastStatsLog = DateTime.Now;
        string lastStats = "";

        /// <summary>A cada 20 s registra no log se houve qualquer falha de audio (para diagnostico).</summary>
        void LogAudioStats()
        {
            if ((DateTime.Now - lastStatsLog).TotalSeconds < 20) return;
            lastStatsLog = DateTime.Now;
            var f = engine.MainFifo;
            var mo = engine.MainOutput;
            string key = $"{mo?.SilenceInserted ?? 0}/{f.ShortReads}/{f.Underruns}/{f.Resyncs}/{engine.CaptureGlitches}";
            if (key == lastStats) return;
            lastStats = key;
            Log.Write($"Áudio: silêncio-inserido={mo?.SilenceInserted ?? 0} leituras-curtas={f.ShortReads} fila-vazia={f.Underruns} resync={f.Resyncs} " +
                      $"descontinuidades-mic={engine.CaptureGlitches} fila={f.FillMs:0.0}ms deriva={(f.Ratio - 1) * 1e6:0}ppm buffer-saída={mo?.BufferMs ?? 0:0}ms");
        }

        double EstimateLatencyMs()
        {
            double chain = engine.ChainLatencySamples / 48.0;
            double outBuf = engine.MainOutput?.BufferMs ?? 20;
            return 5 + chain + engine.MainFifo.TargetFill / 48.0 + outBuf * 0.75;
        }

        // ================================================================== snapshot (testes visuais)

        /// <summary>Fundo das capturas: nos temas de vidro, um "papel de parede" (o vidro de verdade e do Windows).</summary>
        static void SnapshotBackground(DrawingContext dc, double w, double h)
        {
            if (Theme.Current.Glass)
            {
                var wall = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                wall.GradientStops.Add(new GradientStop(Color.FromRgb(0x4B, 0x24, 0x70), 0));
                wall.GradientStops.Add(new GradientStop(Color.FromRgb(0x1F, 0x4E, 0xA8), 0.55));
                wall.GradientStops.Add(new GradientStop(Color.FromRgb(0xC2, 0x4E, 0x88), 1));
                dc.DrawRectangle(wall, null, new Rect(0, 0, w, h));
                // o acrilico escurece (ou clareia) e desfoca o fundo
                dc.DrawRectangle(new SolidColorBrush(Theme.Current.Light ? Color.FromArgb(0x99, 0xF3, 0xF4, 0xF8) : Color.FromArgb(0x99, 0x12, 0x14, 0x1C)), null, new Rect(0, 0, w, h));
            }
            dc.DrawRectangle(Theme.Bg, null, new Rect(0, 0, w, h));
        }

        void SnapshotPage(string key, string path)
        {
            ShowPage(key);
            UpdateLayout();
            FastTick();
            UpdateLayout();
            var el = (FrameworkElement)Content;
            var rtb = new RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) SnapshotBackground(dc, el.ActualWidth, el.ActualHeight);
            rtb.Render(dv);
            rtb.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        public void SaveSnapshots(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (var key in new[] { "painel", "efeitos", "voz", "sons", "plugins", "teste", "temas", "config", "guia" })
                SnapshotPage(key, System.IO.Path.Combine(dir, key + ".png"));
            // cada tema (TAKAVOX_SNAPSHOT_THEMES=1): painel e efeitos em todos
            if (Environment.GetEnvironmentVariable("TAKAVOX_SNAPSHOT_THEMES") == "1")
            {
                string start = Theme.Current.Key;
                foreach (var t in Theme.All)
                {
                    SetTheme(t.Key);
                    foreach (var key in new[] { "painel", "efeitos", "temas" })
                        SnapshotPage(key, System.IO.Path.Combine(dir, "tema_" + t.Key + "_" + key + ".png"));
                }
                SetTheme(start);
            }
            // pagina do modificador inteira (ela rola)
            if (pages.TryGetValue("voz", out var vp) && vp is ScrollViewer sv && sv.Content is FrameworkElement inner)
            {
                ShowPage("voz");
                UpdateLayout();
                double w = Math.Max(400, sv.ActualWidth - 10);
                inner.Measure(new Size(w, double.PositiveInfinity));
                inner.Arrange(new Rect(0, 0, w, inner.DesiredSize.Height));
                var rtb = new RenderTargetBitmap((int)w, (int)inner.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) SnapshotBackground(dc, w, inner.DesiredSize.Height);
                rtb.Render(dv);
                rtb.Render(inner);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(System.IO.Path.Combine(dir, "voz_inteira.png"))) enc.Save(fs);
            }
        }
    }
}
