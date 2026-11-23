using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using TakaVox.Dsp;

namespace TakaVox.UI
{
    public sealed partial class MainWindow
    {
        LevelMeter inMeter, outMeter;
        TextBlock inDbText, outDbText, vadText, clipText, infoMic, infoOut, infoNoise, infoVoice;
        Ellipse vadLed;
        SpectrumEq spectrum;
        BarMeter barGate, barAgc, barDeess, barComp, barLim;
        Border cableBanner, calibBanner;
        CheckBox dashMonitor;
        TextBlock dashTuneNote;

        readonly Fft fft = new Fft(4096);
        readonly float[] tapBuf = new float[4096];
        readonly float[] specTmp = new float[2049];
        readonly float[] dispOut = new float[2049], dispIn = new float[2049];
        float inLevel = -120, outLevel = -120, inHold = -120, outHold = -120;
        DateTime inHoldT, outHoldT, clipUntil;
        int lastClips;
        float gGate, gAgc, gDeess, gComp, gLim;

        FrameworkElement BuildDashboard()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 210 });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- avisos
            cableBanner = Banner(Theme.Warn, "", "Falta instalar o microfone virtual",
                "Sem ele o Discord e os jogos não recebem sua voz tratada. É grátis e leva 10 segundos (o Windows vai pedir permissão).",
                Ui.Btn("Instalar agora", InstallCable, "AccentButton"));
            calibBanner = Banner(Theme.Accent, "", "Calibre o TakaVox para a sua voz (10 s)",
                "Ele mede o ruído do seu quarto e o volume da sua voz e ajusta tudo sozinho.",
                Ui.Btn("Calibrar agora", () => { ShowPage("teste"); StartCalibration(); }, "AccentButton"));
            var banners = Ui.V(cableBanner, calibBanner);
            grid.Children.Add(Ui.At(banners, 0, 0));

            // ---- medidores + espectro
            var mid = Ui.Columns(Ui.Px(206), Ui.Px(14), Ui.Star());
            inMeter = new LevelMeter { Width = 64, MinHeight = 120, ShowScale = true };
            outMeter = new LevelMeter { Width = 64, MinHeight = 120, ShowScale = true };
            inDbText = Ui.T("–∞", 13, Theme.Text, true); inDbText.HorizontalAlignment = HorizontalAlignment.Center;
            outDbText = Ui.T("–∞", 13, Theme.Text, true); outDbText.HorizontalAlignment = HorizontalAlignment.Center;
            var meters = new Grid();
            meters.ColumnDefinitions.Add(new ColumnDefinition());
            meters.ColumnDefinitions.Add(new ColumnDefinition());
            meters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            meters.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            meters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l1 = Ui.T("MIC CRU", 11, Theme.Sub, true); l1.HorizontalAlignment = HorizontalAlignment.Center; l1.ToolTip = "O som original do seu microfone, antes do TakaVox.";
            var l2 = Ui.T("SAÍDA", 11, Theme.Good, true); l2.HorizontalAlignment = HorizontalAlignment.Center; l2.ToolTip = "Sua voz tratada, do jeito que chega no Discord e nos jogos.";
            meters.Children.Add(Ui.At(Ui.M(l1, 18, 0, 0, 6), 0, 0));
            meters.Children.Add(Ui.At(Ui.M(l2, 18, 0, 0, 6), 1, 0));
            meters.Children.Add(Ui.At(inMeter, 0, 1));
            meters.Children.Add(Ui.At(outMeter, 1, 1));
            meters.Children.Add(Ui.At(Ui.M(inDbText, 18, 6, 0, 0), 0, 2));
            meters.Children.Add(Ui.At(Ui.M(outDbText, 18, 6, 0, 0), 1, 2));
            inMeter.HorizontalAlignment = outMeter.HorizontalAlignment = HorizontalAlignment.Center;

            vadLed = new Ellipse { Width = 10, Height = 10, Fill = Theme.Dim, Margin = new Thickness(0, 0, 8, 0) };
            vadText = Ui.T("Silêncio", 12.5, Theme.Sub, true);
            clipText = Ui.T("", 11.5, Theme.Bad, true, true);
            var meterPanel = new DockPanel();
            var vadRow = Ui.H(vadLed, vadText); vadRow.Margin = new Thickness(4, 10, 0, 0);
            vadRow.ToolTip = "Acende quando a IA detecta que você está falando (o portão de voz abre).";
            DockPanel.SetDock(vadRow, Dock.Bottom);
            DockPanel.SetDock(clipText, Dock.Bottom);
            meterPanel.Children.Add(vadRow);
            meterPanel.Children.Add(clipText);
            meterPanel.Children.Add(meters);
            var meterCard = Ui.Card(meterPanel, 14);
            meterCard.Margin = new Thickness(0);
            mid.Children.Add(Ui.At(meterCard, 0));

            spectrum = new SpectrumEq { Params = P };
            spectrum.EqChanged += () => OnParamChanged();
            var specHead = Ui.Columns(Ui.Star(), Ui.Auto);
            specHead.Children.Add(Ui.At(Ui.T("Espectro ao vivo + equalizador", 13.5, Theme.Text, true), 0));
            var legend = Ui.H(Dot(Theme.Sub), Ui.T("mic cru   ", 11.5, Theme.Sub), Dot(Theme.Good), Ui.T("com TakaVox   ", 11.5, Theme.Sub), Dot(Theme.AccentHi), Ui.T("curva do EQ", 11.5, Theme.Sub));
            specHead.Children.Add(Ui.At(legend, 1));
            var hint = Ui.T("Arraste os pontos 1–5 para moldar sua voz • roda do mouse = largura • duplo clique = zera", 11.5, Theme.Dim);
            var specPanel = new DockPanel();
            DockPanel.SetDock(specHead, Dock.Top);
            DockPanel.SetDock(hint, Dock.Bottom);
            specHead.Margin = new Thickness(0, 0, 0, 10);
            hint.Margin = new Thickness(0, 8, 0, 0);
            specPanel.Children.Add(specHead);
            specPanel.Children.Add(hint);
            specPanel.Children.Add(spectrum);
            var specCard = Ui.Card(specPanel, 14);
            specCard.Margin = new Thickness(0);
            mid.Children.Add(Ui.At(specCard, 2));
            mid.Margin = new Thickness(0, 0, 0, 14);
            grid.Children.Add(Ui.At(mid, 0, 1));

            // ---- botoes principais
            var knobs = new UniformGrid { Rows = 1, Columns = 5 };
            knobs.Children.Add(MakeKnob("Volume da voz", -12, 18, 2, v => (v >= 0 ? "+" : "") + v.ToString("0") + " dB",
                () => P.OutputGainDb, v => P.OutputGainDb = (float)v,
                "Deixa sua voz mais alta ou mais baixa no Discord/jogos. O limitador impede que estoure."));
            knobs.Children.Add(MakeKnob("Anti-ruído", 0, 100, 70, v => v.ToString("0") + "%", NoiseKnobGet, NoiseKnobSet,
                "Força da limpeza: até 70% = IA tirando ruído; acima disso também silencia tudo entre as falas (teclado, respiração)."));
            knobs.Children.Add(MakeKnob("Clareza", 0, 100, 50, v => v.ToString("0") + "%",
                () => Math.Max(0, Math.Min(100, P.Bands[3].GainDb / 7.0 * 100)),
                v => { P.Bands[3].GainDb = (float)(v / 100 * 7); P.Bands[4].GainDb = (float)(v / 100 * 6); P.Bands[3].On = P.Bands[4].On = true; P.DeessMaxDb = (float)(4 + v / 100 * 6); },
                "Realça a presença e o brilho da voz (fica mais nítida e 'na frente'). O de-esser segura os 'S' ardidos."));
            knobs.Children.Add(MakeKnob("Grave", -6, 9, 2, v => (v >= 0 ? "+" : "") + v.ToString("0.0") + " dB",
                () => P.Bands[0].GainDb, v => { P.Bands[0].GainDb = (float)v; P.Bands[0].On = true; },
                "Corpo da voz. Mais = voz de rádio/encorpada. Menos = voz mais fina e limpa."));
            knobs.Children.Add(MakeKnob("Compressão", 0, 100, 50, v => v.ToString("0") + "%",
                () => Math.Max(0, Math.Min(100, P.CompOn ? (P.CompRatio - 1) / 4.0 * 100 : 0)),
                v => { P.CompOn = v > 1; P.CompRatio = (float)(1 + v / 100 * 4); P.CompThresholdDb = (float)(-16 - v * 0.2); P.CompMakeupDb = (float)(v * 0.08); },
                "Deixa o volume da voz mais constante (sussurro e grito ficam parecidos). Estilo locutor/streamer."));

            dashMonitor = new CheckBox { Content = Ui.T("Ouvir minha voz", 13, Theme.Text), ToolTip = "Toca sua voz tratada no seu fone para você ouvir como os outros te escutam. Use FONE (com caixa de som dá eco)." };
            dashMonitor.Checked += (s, e) => { if (!refreshing) SetMonitorOn(true); };
            dashMonitor.Unchecked += (s, e) => { if (!refreshing) SetMonitorOn(false); };
            binders.Add(new SwitchBinder(dashMonitor, () => app.MonitorOn));
            var monVol = new Slider { Minimum = 0, Maximum = 100, Width = 150, Value = app.MonitorVolume * 100, ToolTip = "Volume do retorno no seu fone" };
            monVol.ValueChanged += (s, e) => { if (refreshing) return; SetMonitorVolume((float)(monVol.Value / 100)); };
            binders.Add(new ActionBinder(() => monVol.Value = app.MonitorVolume * 100));
            var resetBtn = Ui.Btn("Padrão", () => { BuiltInPresets.Apply("Padrão TakaVox", P); app.PresetName = "Padrão TakaVox"; RefreshPresetList(); OnParamChanged(); }, null, "",
                "Volta todos os efeitos para o padrão do TakaVox (mantém sua calibração).");
            var dashTune = new CheckBox { Content = Ui.T("Auto-Tune", 13, Theme.Text), ToolTip = "Liga/desliga o Auto-Tune (ajuste tom, escala e velocidade em Efeitos)." };
            dashTune.Checked += (s, e) => { if (refreshing) return; P.TuneOn = true; P.Touch(); OnParamChanged(); };
            dashTune.Unchecked += (s, e) => { if (refreshing) return; P.TuneOn = false; P.Touch(); OnParamChanged(); };
            binders.Add(new SwitchBinder(dashTune, () => P.TuneOn));
            dashTuneNote = Ui.T("", 12, Theme.Sub, true);
            var dashVoice = new CheckBox { Content = Ui.T("Modificador de voz", 13, Theme.Text), ToolTip = "Liga/desliga o modificador de voz (escolha a voz na página Modificador de voz). Atalho: " + HotkeyVoice };
            dashVoice.Checked += (s, e) => { if (!refreshing) SetVoiceOn(true); };
            dashVoice.Unchecked += (s, e) => { if (!refreshing) SetVoiceOn(false); };
            binders.Add(new SwitchBinder(dashVoice, () => P.VcOn));
            dashVoiceNote = Ui.T("", 12, Theme.AccentHi, true);
            var side = Ui.V(dashMonitor, Ui.M(Ui.H(Ui.Icon("", 13, Theme.Sub), Ui.M(monVol, 8, 0, 0, 0)), 0, 8, 0, 0),
                Ui.M(Ui.H(dashTune, Ui.M(dashTuneNote, 10, 0, 0, 0)), 0, 12, 0, 0),
                Ui.M(dashVoice, 0, 10, 0, 0), Ui.M(dashVoiceNote, 26, 2, 0, 0), Ui.M(resetBtn, 0, 12, 0, 0));
            side.VerticalAlignment = VerticalAlignment.Center;
            resetBtn.HorizontalAlignment = HorizontalAlignment.Left;
            var knobRow = Ui.Columns(Ui.Star(), Ui.Px(1), Ui.Px(220));
            knobRow.Children.Add(Ui.At(knobs, 0));
            knobRow.Children.Add(Ui.At(new Border { Background = Theme.Border, Margin = new Thickness(0, 10, 0, 10) }, 1));
            knobRow.Children.Add(Ui.At(Ui.M(side, 22, 0, 0, 0), 2));
            var knobCard = Ui.Card(knobRow, 12);
            grid.Children.Add(Ui.At(knobCard, 0, 2));

            // ---- dinamica + informacoes
            barGate = new BarMeter { Label = "Portão de voz", BarBrush = Theme.Good, Tip = "Quanto o TakaVox está abaixando o som quando você NÃO está falando (teclado, respiração, ruído)." };
            barAgc = new BarMeter { Label = "Nivelador", BarBrush = Theme.AccentHi, Tip = "Correção automática do volume da sua voz (+ aumenta, − diminui)." };
            barDeess = new BarMeter { Label = "De-esser", BarBrush = Theme.Warn, Tip = "Redução dos 'S' e 'X' ardidos." };
            barComp = new BarMeter { Label = "Compressor", BarBrush = Theme.Accent, Tip = "Quanto o compressor está segurando os picos da voz." };
            barLim = new BarMeter { Label = "Limitador", BarBrush = Theme.Bad, Tip = "Proteção final: impede que sua voz estoure/distorça quando você grita." };
            var bars = new UniformGrid { Rows = 1, Columns = 5 };
            foreach (var b in new[] { barGate, barAgc, barDeess, barComp, barLim }) { b.Margin = new Thickness(0, 0, 18, 0); bars.Children.Add(b); }
            infoMic = Ui.T("", 12, Theme.Sub); infoOut = Ui.T("", 12, Theme.Sub); infoNoise = Ui.T("", 12, Theme.Sub); infoVoice = Ui.T("", 12, Theme.Sub);
            var infos = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var t in new[] { infoMic, infoOut, infoNoise, infoVoice }) { t.Margin = new Thickness(0, 0, 14, 0); infos.Children.Add(t); }
            infoNoise.ToolTip = "Nível do ruído do ambiente quando você está calado: no mic cru → depois do TakaVox.";
            infoVoice.ToolTip = "Volume médio da sua voz: no mic cru → depois do TakaVox.";
            var dynCard = Ui.Card(Ui.V(bars, infos), 14);
            dynCard.Margin = new Thickness(0, 0, 0, 6);
            grid.Children.Add(Ui.At(dynCard, 0, 3));
            return grid;
        }

        static Ellipse Dot(Brush b) => new Ellipse { Width = 8, Height = 8, Fill = b, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

        Border Banner(Brush accent, string glyph, string title, string text, Button action)
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            g.Children.Add(Ui.At(Ui.M(Ui.Icon(glyph, 18, accent), 0, 0, 12, 0), 0));
            var tt = Ui.T(title, 13.5, Theme.Text, true);
            var tx = Ui.T(text, 12, Theme.Sub);
            tt.ToolTip = tx.ToolTip = text;
            g.Children.Add(Ui.At(Ui.V(tt, Ui.M(tx, 0, 1, 0, 0)), 1));
            action.Margin = new Thickness(12, 0, 0, 0);
            action.Padding = new Thickness(14, 6, 14, 6);
            action.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(Ui.At(action, 2));
            var src = accent as SolidColorBrush ?? Theme.Accent;
            return new Border
            {
                Background = Theme.Tint(src, 26), BorderBrush = Theme.Tint(src, 90), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 10, 8), Margin = new Thickness(0, 0, 0, 10),
                Child = g, Visibility = Visibility.Collapsed,
            };
        }

        FrameworkElement MakeKnob(string label, double min, double max, double def, Func<double, string> fmt, Func<double> get, Action<double> set, string tip)
        {
            var k = new Knob { Min = min, Max = max, Default = def, Label = label, Format = fmt, ToolTip = tip, Value = get(), HorizontalAlignment = HorizontalAlignment.Center };
            k.ValueChanged += v => { if (refreshing) return; set(v); P.Touch(); OnParamChanged(); };
            binders.Add(new KnobBinder(k, get));
            return k;
        }

        double NoiseKnobGet()
        {
            if (!P.NoiseOn) return 0;
            if (P.NoiseMix < 0.999f) return P.NoiseMix * 70;
            if (!P.GateOn) return 70;
            double r = -P.GateRangeDb;
            return Math.Max(70, Math.Min(100, 70 + (r - 30) / 30 * 30));
        }

        void NoiseKnobSet(double v)
        {
            if (v < 0.5) { P.NoiseOn = false; P.GateOn = false; return; }
            P.NoiseOn = true;
            P.GateOn = v >= 10;
            if (v <= 70) { P.NoiseMix = (float)(v / 70); P.GateRangeDb = (float)(-10 - v / 70 * 20); }
            else { P.NoiseMix = 1f; P.GateRangeDb = (float)(-30 - (v - 70) / 30 * 30); }
        }

        void UpdateDashboardStatus(bool capOk, bool outOk, bool cable)
        {
            cableBanner.Visibility = cable ? Visibility.Collapsed : Visibility.Visible;
            calibBanner.Visibility = !app.Calibrated && capOk ? Visibility.Visible : Visibility.Collapsed;
            infoMic.Text = "🎙  " + (string.IsNullOrEmpty(engine.InputName) ? "sem microfone" : engine.InputName) + (capOk ? "" : " (" + engine.CaptureStatus + ")");
            var mo = engine.MainOutput;
            infoOut.Text = "➜  " + (mo == null ? "saída não definida" : (app.OutputDeviceName ?? "") + (outOk ? "" : " (" + mo.Status + ")"));
            if (snapshotMode && Environment.GetEnvironmentVariable("TAKAVOX_DEMO") == "1")
            {
                // capturas do README: nomes genericos (nada do PC de quem gerou as imagens)
                infoMic.Text = "🎙  Microfone USB";
                infoOut.Text = "➜  CABLE Input (microfone virtual)";
            }
            var m = engine.Meters;
            infoNoise.Text = m.NoiseInDb > -119 ? $"Ruído: {m.NoiseInDb:0} dB → {Math.Max(-99, m.NoiseOutDb):0} dB" : "Ruído: medindo…";
            infoVoice.Text = m.SpeechInDb > -119 ? $"Voz: {m.SpeechInDb:0} dB → {m.SpeechOutDb:0} dB" : "Voz: fale algo…";
        }

        void FastTick()
        {
            // nada de desenho com a janela escondida/minimizada ou com um jogo em tela cheia na frente
            if (!ShouldRender(out float dt))
            {
                if (!IsVisible || otherFullscreen || WindowState == WindowState.Minimized) { engine.Meters.TakeInPeak(); engine.Meters.TakeOutPeak(); }
                // jogo em tela cheia na frente: a janela (aberta atras) acorda so 2x por segundo
                var slow = TimeSpan.FromMilliseconds(otherFullscreen && !TestBusy ? 500 : 33);
                if (fastTimer.Interval != slow) fastTimer.Interval = slow;
                FastTickTest();
                return;
            }
            if (fastTimer.Interval.TotalMilliseconds > 40) fastTimer.Interval = TimeSpan.FromMilliseconds(33);
            var m = engine.Meters;
            var now = DateTime.Now;
            float inPk = DspMath.LinToDb(m.TakeInPeak()), outPk = DspMath.LinToDb(m.TakeOutPeak());
            float fall = 42f * dt; // medidores caem ~42 dB/s, desenhe a 30 ou a 8 quadros/s
            inLevel = Math.Max(inPk, inLevel - fall);
            outLevel = Math.Max(outPk, outLevel - fall);
            if (inPk >= inHold) { inHold = inPk; inHoldT = now; } else if ((now - inHoldT).TotalSeconds > 1.5) inHold -= 21f * dt;
            if (outPk >= outHold) { outHold = outPk; outHoldT = now; } else if ((now - outHoldT).TotalSeconds > 1.5) outHold -= 21f * dt;

            if (m.Clips != lastClips) { lastClips = m.Clips; clipUntil = now.AddSeconds(3); }

            if (currentPage == "painel")
            {
                inMeter.Db = inLevel; inMeter.PeakDb = inHold; inMeter.InvalidateVisual();
                outMeter.Db = outLevel; outMeter.PeakDb = outHold; outMeter.InvalidateVisual();
                inDbText.Text = inLevel > -90 ? $"{inLevel:0} dB" : "–∞";
                outDbText.Text = outLevel > -90 ? $"{outLevel:0} dB" : "–∞";
                bool speaking = m.GateOpen && !P.Bypass;
                vadLed.Fill = speaking ? Theme.Good : Theme.Dim;
                vadText.Text = speaking ? "Falando" : "Silêncio";
                vadText.Foreground = speaking ? Theme.Good : Theme.Sub;
                clipText.Text = now < clipUntil ? "⚠ O mic está estourando! Afaste um pouco ou baixe o volume do mic em Configurar." : "";

                // espectro
                engine.TapOut.Snapshot(tapBuf); fft.PowerDb(tapBuf, specTmp); SmoothSpec(dispOut, specTmp);
                engine.TapIn.Snapshot(tapBuf); fft.PowerDb(tapBuf, specTmp); SmoothSpec(dispIn, specTmp);
                spectrum.OutDb = dispOut; spectrum.InDb = dispIn;
                spectrum.InvalidateVisual();

                gGate = Follow(gGate, -m.GateDb); gAgc = Follow2(gAgc, m.AgcDb); gDeess = Follow(gDeess, -m.DeessDb);
                gComp = Follow(gComp, -m.CompDb); gLim = Follow(gLim, -m.LimDb);
                barGate.Fraction = gGate / 40; barGate.ValueText = !P.GateOn ? "desligado" : m.GateOpen ? "aberto" : $"−{gGate:0} dB";
                barAgc.Fraction = Math.Abs(gAgc) / 12; barAgc.ValueText = !P.AgcOn ? "desligado" : $"{(gAgc >= 0 ? "+" : "−")}{Math.Abs(gAgc):0.0} dB";
                barDeess.Fraction = gDeess / 12; barDeess.ValueText = !P.DeessOn ? "desligado" : $"−{gDeess:0.0} dB";
                barComp.Fraction = gComp / 18; barComp.ValueText = !P.CompOn ? "desligado" : $"−{gComp:0.0} dB";
                barLim.Fraction = gLim / 12; barLim.ValueText = $"−{gLim:0.0} dB";
                foreach (var b in new[] { barGate, barAgc, barDeess, barComp, barLim }) b.InvalidateVisual();
                var tn = engine.Tune;
                dashTuneNote.Text = P.TuneOn ? (tn.TargetMidi >= 0 && tn.DetectedHz > 0 ? "🎵 " + AutoTune.NoteName(tn.TargetMidi) : "🎵 –") : "";
            }
            else if (currentPage == "efeitos") FastTickEffects();
            else if (currentPage == "voz") FastTickVoice();
            else if (currentPage == "sons") FastTickSounds();
            FastTickTest();
        }

        static float Follow(float disp, float v) => v > disp ? v : disp * 0.86f + v * 0.14f;
        static float Follow2(float disp, float v) => disp * 0.8f + v * 0.2f;

        static void SmoothSpec(float[] disp, float[] cur)
        {
            for (int k = 0; k < disp.Length; k++)
            {
                float c = cur[k];
                if (c > disp[k]) disp[k] = disp[k] * 0.35f + c * 0.65f;
                else disp[k] = Math.Max(c, disp[k] - 1.6f);
                if (disp[k] < -140) disp[k] = -140;
            }
        }

        void SetMonitorOn(bool on)
        {
            if (on && !snapshotMode && !ConfirmMonitorDevice(app.MonitorDeviceId)) { RefreshAll(); return; }
            app.MonitorOn = on;
            if (on) engine.Resting = false;
            engine.SetMonitor(app.MonitorDeviceId, on, app.MonitorVolume);
            Log.Write("Retorno (ouvir minha voz): " + (on ? "ligado" : "desligado"));
            tray?.Update(P.Bypass, app.MonitorOn);
            MarkDirty();
            RefreshAll();
        }

        void SetMonitorVolume(float v)
        {
            app.MonitorVolume = v;
            engine.SetMonitor(app.MonitorDeviceId, app.MonitorOn, v);
            MarkDirty();
        }

        public void ToggleMonitorFromTray() => SetMonitorOn(!app.MonitorOn);
        public void ToggleBypassFromTray() => SetBypass(!P.Bypass);
        public bool BypassState => P.Bypass;
        public bool MonitorState => app.MonitorOn;
    }
}
