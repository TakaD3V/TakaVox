using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TakaVox.Dsp;

namespace TakaVox.UI
{
    /// <summary>
    /// Pagina "Modificador de voz": vozes prontas (garota de anime, robo, monstro...), vozes salvas e ajuste fino
    /// de tudo (tom, timbre, entonacao, vibrato, sopro, robo sintetizado, voz extra, filtros, eco, reverb...).
    /// </summary>
    public sealed partial class MainWindow
    {
        static readonly FontFamily EmojiFont = new FontFamily("Segoe UI Emoji, Segoe UI Symbol");
        readonly List<KeyValuePair<Border, string>> voiceTiles = new List<KeyValuePair<Border, string>>();
        readonly Dictionary<string, ChainParams> userVoices = new Dictionary<string, ChainParams>();
        WrapPanel userVoicePanel;
        TextBlock vcStatus, vcLive, dashVoiceNote;
        TextBox voiceNameBox;
        string voiceKey = "", vcLiveText = "";

        /// <summary>Nome da voz atual: "normal", o nome da voz pronta/salva, ou "personalizada".</summary>
        string VoiceLabel()
        {
            if (!P.VcOn) return "voz normal";
            if (P.VcAiOn)
            {
                foreach (var kv in userVoices) if (kv.Value.VcAiOn && kv.Value.SameVoice(P)) return "★ " + kv.Key;
                var av = CurrentAiVoice();
                return "IA: " + (av != null ? AiName(av) : "voz com IA");
            }
            var m = VoicePresets.Match(P);
            if (m != null) return m.Name;
            foreach (var kv in userVoices) if (kv.Value.SameVoice(P)) return "★ " + kv.Key;
            return VoicePresets.IsNeutral(P) ? "sem mudanças" : "voz personalizada";
        }

        string CurrentVoiceKey()
        {
            if (!P.VcOn) return "#normal";
            if (P.VcAiOn)
            {
                foreach (var kv in userVoices) if (kv.Value.VcAiOn && kv.Value.SameVoice(P)) return "★" + kv.Key;
                var av = CurrentAiVoice();
                return av != null ? "ai:" + av.Key : "";
            }
            var m = VoicePresets.Match(P);
            if (m != null) return m.Name;
            foreach (var kv in userVoices) if (kv.Value.SameVoice(P)) return "★" + kv.Key;
            return "";
        }

        FrameworkElement BuildVoicePage()
        {
            var root = new StackPanel();

            // ---------------------------------------------------------------- topo: liga/desliga, status, ouvir
            var head = Ui.Columns(Ui.Star(), Ui.Auto);
            head.Children.Add(Ui.At(Ui.V(
                Ui.T("Mude sua voz ao vivo", 17, Theme.Text, true),
                Ui.M(Ui.T("Vale para o Discord, os jogos e qualquer app que usa o microfone. O tom e o timbre (tamanho da garganta) mudam separados: " +
                          "por isso a garota de anime soa como garota, e não como esquilo.", 12.5, Theme.Sub, false, true), 0, 4, 0, 0)), 0));
            var sw = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 2, 0, 0), ToolTip = "Liga/desliga o modificador (atalho global: " + HotkeyVoice + ")" };
            sw.Checked += (s, e) => { if (!refreshing) SetVoiceOn(true); };
            sw.Unchecked += (s, e) => { if (!refreshing) SetVoiceOn(false); };
            binders.Add(new SwitchBinder(sw, () => P.VcOn));
            head.Children.Add(Ui.At(sw, 1));

            vcStatus = Ui.T("", 14, Theme.Good, true, true);
            vcLive = Ui.T("", 12.5, Theme.Sub, false, true);
            var mon = new CheckBox { Content = Ui.T("Ouvir minha voz", 13, Theme.Text), ToolTip = "Toca a voz modificada no seu fone para você testar. Use FONE: com caixa de som dá microfonia." };
            mon.Checked += (s, e) => { if (!refreshing) SetMonitorOn(true); };
            mon.Unchecked += (s, e) => { if (!refreshing) SetMonitorOn(false); };
            binders.Add(new SwitchBinder(mon, () => app.MonitorOn));
            var normalBtn = Ui.Btn("Voz normal", () => { VoicePresets.ResetVoice(P); P.VcOn = false; P.Touch(); Log.Write("Modificador de voz: voz normal"); OnParamChanged(); },
                null, "", "Desliga o modificador e zera todos os ajustes dele.");
            var tools = Ui.H(mon, Ui.M(normalBtn, 18, 0, 0, 0));
            var hotkey = Ui.T("", 12, Theme.Sub, false, true);
            binders.Add(new ActionBinder(() => hotkey.Text = "Atalho global: " + HotkeyVoice + " liga/desliga até dentro do jogo." +
                (AutoTune.Needed(P) ? "  Mudar tom/timbre soma ~38 ms de atraso (normal em modificadores de voz)." : "")));
            root.Children.Add(Ui.Card(Ui.V(head, Ui.M(vcStatus, 0, 14, 0, 0), Ui.M(vcLive, 0, 3, 0, 0),
                Ui.M(tools, 0, 12, 0, 0), Ui.M(hotkey, 0, 10, 0, 0))));

            // ---------------------------------------------------------------- vozes com IA (Beatrice)
            root.Children.Add(BuildAiVoiceCard());

            // ---------------------------------------------------------------- vozes prontas
            var grid = new WrapPanel();
            grid.Children.Add(VoiceTile("\U0001F642", "Normal", "Sua voz, sem o modificador.", "#normal",
                () => { P.VcOn = false; P.Touch(); OnParamChanged(); }));
            foreach (var pr in VoicePresets.All)
            {
                var v = pr;
                grid.Children.Add(VoiceTile(v.Emoji, v.Name, v.Desc, v.Name, () => ApplyVoicePreset(v)));
            }
            root.Children.Add(Ui.Card(Ui.V(Ui.SectionTitle("Vozes prontas (modificador comum)", "Efeitos leves, sem IA (~1% de 1 núcleo): robô, monstro, rádio… Dá para ajustar tudo logo abaixo (vira uma voz personalizada)."), grid)));

            // ---------------------------------------------------------------- minhas vozes
            userVoicePanel = new WrapPanel();
            voiceNameBox = new TextBox { Width = 220, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Nome da sua voz" };
            var saveBtn = Ui.Btn("Salvar a voz atual", SaveUserVoice, "AccentButton", "", "Guarda os ajustes atuais do modificador com esse nome.");
            var saveRow = Ui.H(voiceNameBox, Ui.M(saveBtn, 10, 0, 0, 0));
            root.Children.Add(Ui.Card(Ui.V(Ui.SectionTitle("Minhas vozes", "Ajustou do seu jeito? Salve aqui. Botão direito numa voz salva para excluir."),
                userVoicePanel, Ui.M(saveRow, 0, 4, 0, 0))));
            RebuildUserVoices();

            // ---------------------------------------------------------------- ajuste fino
            var cols = Ui.Columns(Ui.Star(), Ui.Px(14), Ui.Star());
            var left = new StackPanel(); var right = new StackPanel();
            cols.Children.Add(Ui.At(left, 0)); cols.Children.Add(Ui.At(right, 2));
            root.Children.Add(cols);

            left.Children.Add(EffectCard("Tom e timbre", "O coração do modificador. Garota: tom +7 a +12 e timbre +15 a +30%. Homem: tom −3 a −6 e timbre −10%.", null, null,
                VSlider("Tom", "Altura da voz em semitons (st). 12 = uma oitava acima.", -12, 12, 0.5, () => P.VcPitch, v => P.VcPitch = (float)v, St),
                VSlider("Timbre (garganta)", "Formantes: + = garganta menor (voz feminina/infantil), − = garganta maior (voz masculina/gigante). Não muda o tom.",
                    -35, 60, 1, () => P.VcFormant, v => P.VcFormant = (float)v, v => (v > 0 ? "+" : "") + v.ToString("0") + "%"),
                VSlider("Entonação", "0% = sempre a mesma nota (robô). 100% = natural. Mais = fala mais 'cantada' e expressiva (bom para anime).",
                    0, 200, 5, () => P.VcIntonation, v => P.VcIntonation = (float)v, v => v < 1 ? "robô" : v.ToString("0") + "%"),
                VSlider("Vibrato", "Tremidinho no tom (voz de velhinho, fantasma, cantora).", 0, 150, 5, () => P.VcVibDepth, v => P.VcVibDepth = (float)v,
                    v => v < 1 ? "desligado" : v.ToString("0") + " cents"),
                VSlider("Velocidade do vibrato", "Quantas vezes por segundo o tom treme.", 2, 12, 0.5, () => P.VcVibRate, v => P.VcVibRate = (float)v, v => v.ToString("0.0") + " Hz")));

            left.Children.Add(EffectCard("Textura", "Ar na voz, sussurro e robô sintetizado (vocoder), mais graves e brilho.", null, null,
                VSlider("Sopro / sussurro", "Até 50%: soma ar à voz (voz soprosa, fofa). De 50% a 100%: a voz vira sussurro.", 0, 100, 1,
                    () => P.VcBreath, v => P.VcBreath = (float)v, Pct),
                VSlider("Robô sintetizado", "Troca as cordas vocais por um sintetizador (vocoder). 100% = só o robô.", 0, 100, 1,
                    () => P.VcSynth, v => P.VcSynth = (float)v, Pct),
                VSlider("Nota do robô", "Tom fixo do robô sintetizado.", 50, 400, 1, () => P.VcSynthHz, v => P.VcSynthHz = (float)v, v => v.ToString("0") + " Hz"),
                VSlider("Graves", "Corpo da voz modificada.", -12, 12, 0.5, () => P.VcLowDb, v => P.VcLowDb = (float)v, v => Db(v, true)),
                VSlider("Brilho", "Agudos da voz modificada.", -12, 12, 0.5, () => P.VcHighDb, v => P.VcHighDb = (float)v, v => Db(v, true))));

            left.Children.Add(EffectCard("Voz extra", "Uma segunda voz junto com a sua, em outra altura: monstro e demônio (uma oitava abaixo), dueto (acima).", null, null,
                VSlider("Volume", "0% = sem voz extra.", 0, 100, 1, () => P.VcExtraMix, v => P.VcExtraMix = (float)v, Pct),
                VSlider("Tom", "Altura da voz extra em relação à sua voz original.", -24, 24, 1, () => P.VcExtraPitch, v => P.VcExtraPitch = (float)v, St),
                VSlider("Timbre", "Garganta da voz extra.", -35, 60, 1, () => P.VcExtraFormant, v => P.VcExtraFormant = (float)v, v => (v > 0 ? "+" : "") + v.ToString("0") + "%")));

            var filterBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var f in VoiceFx.FilterNames) filterBox.Items.Add(f);
            filterBox.SelectionChanged += (s, e) =>
            {
                if (refreshing || filterBox.SelectedIndex < 0) return;
                P.VcFilter = filterBox.SelectedIndex;
                if (P.VcFilter > 0) P.VcOn = true;
                P.Touch(); OnParamChanged();
            };
            binders.Add(new ActionBinder(() => filterBox.SelectedIndex = P.VcFilter));
            right.Children.Add(EffectCard("Efeitos", "Rádio, telefone, distorção, robô metálico, alien…", null, null,
                Row("Filtro", "Simula o som passando por um aparelho.", filterBox),
                FxSlider("Distorção", "Saturação/rosnado. Pouco = voz rasgada; muito = alto-falante estourado.", 0, 100, 1, () => P.VcDrive, v => P.VcDrive = (float)v, Pct),
                FxSlider("Metálico", "Ressonância de lata/robô.", 0, 100, 1, () => P.VcMetal, v => P.VcMetal = (float)v, Pct),
                FxSlider("Nota do metálico", "Em que nota a lata ressoa.", 50, 500, 5, () => P.VcMetalHz, v => P.VcMetalHz = (float)v, v => v.ToString("0") + " Hz"),
                FxSlider("Modulador em anel", "Efeito alienígena/dalek: multiplica a voz por um tom.", 0, 100, 1, () => P.VcRingMix, v => P.VcRingMix = (float)v, Pct),
                FxSlider("Frequência do anel", "Baixa = robô áspero; alta = alien metálico.", 20, 1000, 5, () => P.VcRingHz, v => P.VcRingHz = (float)v, v => v.ToString("0") + " Hz"),
                FxSlider("Tremolo", "Volume tremendo (fantasma, velhinho, rádio ruim).", 0, 100, 1, () => P.VcTremDepth, v => P.VcTremDepth = (float)v, Pct),
                FxSlider("Velocidade do tremolo", "Quantas vezes por segundo o volume treme.", 1, 20, 0.5, () => P.VcTremRate, v => P.VcTremRate = (float)v, v => v.ToString("0.0") + " Hz")));

            right.Children.Add(EffectCard("Ambiente", "Eco e reverb (caverna, igreja, banheiro…). Ficam depois do compressor, como num estúdio.", null, null,
                FxSlider("Eco", "Volume das repetições.", 0, 100, 1, () => P.VcEchoMix, v => P.VcEchoMix = (float)v, Pct),
                FxSlider("Tempo do eco", "Distância entre as repetições.", 40, 1000, 10, () => P.VcEchoMs, v => P.VcEchoMs = (float)v, v => v.ToString("0") + " ms"),
                FxSlider("Repetições", "Quanto o eco demora para sumir.", 0, 85, 1, () => P.VcEchoFb, v => P.VcEchoFb = (float)v, v => v.ToString("0") + "%"),
                FxSlider("Reverb", "Volume do ambiente.", 0, 100, 1, () => P.VcReverbMix, v => P.VcReverbMix = (float)v, Pct),
                FxSlider("Tamanho do ambiente", "Pequeno = quarto/banheiro; grande = igreja/caverna.", 0, 100, 1, () => P.VcReverbSize, v => P.VcReverbSize = (float)v, v => v.ToString("0") + "%")));

            right.Children.Add(EffectCard("Dicas", "Para a voz chegar certinha do outro lado.", null, null,
                Ui.T("• No Discord: Configurações → Voz e vídeo → desligue a Supressão de ruído (Krisp) e o Cancelamento de eco. " +
                     "O TakaVox já limpa a voz, e o Krisp pode cortar sussurro, robô e efeitos.\n" +
                     "• Para ouvir como os outros te escutam, ligue 'Ouvir minha voz' com fone.\n" +
                     "• Garota de anime fica mais convincente se você falar um pouco mais agudo e animado: o modificador sobe o resto.\n" +
                     "• Leve: as vozes prontas usam ~1% de um núcleo do processador. As vozes com IA usam ~25% de um núcleo (uns 2% do PC) e nada da placa de vídeo: o FPS do jogo não muda.\n" +
                     "• Filtro, distorção, eco e reverb também valem por cima das vozes com IA.", 12.5, Theme.Sub, false, true)));

            // destaque da voz atual (recalculado a cada mudanca)
            binders.Add(new ActionBinder(RefreshVoiceState));
            return root;
        }

        static string St(double v) => (v > 0 ? "+" : "") + v.ToString(Math.Abs(v - Math.Round(v)) < 0.01 ? "0" : "0.0") + " st";
        static string Pct(double v) => v < 0.5 ? "desligado" : v.ToString("0") + "%";

        /// <summary>Slider do modificador comum (tom, timbre, textura, voz extra): mexer nele liga o modificador comum
        /// (para voce ouvir na hora) e sai da voz com IA, que tem os ajustes dela.</summary>
        FrameworkElement VSlider(string label, string tip, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt) =>
            SliderRow(label, tip, min, max, step, get, v => { set(v); P.VcOn = true; P.VcAiOn = false; }, fmt);

        /// <summary>Slider de efeito (filtro, distorcao, eco, reverb...): vale por cima da voz com IA tambem.</summary>
        FrameworkElement FxSlider(string label, string tip, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt) =>
            SliderRow(label, tip, min, max, step, get, v => { set(v); P.VcOn = true; }, fmt);

        Border VoiceTile(string emoji, string name, string tip, string key, Action click)
        {
            var em = new TextBlock { Text = emoji, FontFamily = EmojiFont, FontSize = 20, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center, Width = 26 };
            var nm = Ui.T(name, 12.5, Theme.Text, true, true);
            nm.Margin = new Thickness(6, 0, 0, 0);
            nm.Width = 92;
            var b = new Border
            {
                Width = 154, Height = 56, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10),
                Background = Theme.Card2, BorderBrush = Theme.Border, BorderThickness = new Thickness(1.5),
                Padding = new Thickness(12, 4, 8, 4), Cursor = Cursors.Hand, ToolTip = tip,
                Child = Ui.H(em, nm),
            };
            b.MouseLeftButtonUp += (s, e) =>
            {
                try { click(); }
                catch (Exception ex) { Log.Write("Erro na voz '" + name + "': " + ex.Message); }
            };
            b.MouseEnter += (s, e) => { if (voiceKey != key) b.BorderBrush = Theme.AccentHi; };
            b.MouseLeave += (s, e) => PaintTile(b, voiceKey == key);
            voiceTiles.Add(new KeyValuePair<Border, string>(b, key));
            PaintTile(b, false);
            return b;
        }

        static readonly SolidColorBrush TileSel = Theme.Tint(Theme.Accent, 0x40);

        static void PaintTile(Border b, bool sel)
        {
            b.BorderBrush = sel ? Theme.Accent : Theme.Border;
            b.Background = sel ? TileSel : Theme.Card2;
        }

        void ApplyVoicePreset(VoicePreset v)
        {
            v.ApplyTo(P);
            P.VcOn = true;
            P.Touch();
            app.VoicePreset = v.Name;
            Log.Write("Modificador de voz: " + v.Name);
            OnParamChanged();
        }

        void SetVoiceOn(bool on)
        {
            // primeira vez (tudo zerado): comeca na ultima voz pronta escolhida (garota de anime de inicio)
            if (on && VoicePresets.IsNeutral(P)) (VoicePresets.Find(app.VoicePreset) ?? VoicePresets.All[0]).ApplyTo(P);
            P.VcOn = on;
            P.Touch();
            Log.Write("Modificador de voz: " + (on ? "ligado – " + VoiceLabel() : "desligado"));
            OnParamChanged();
        }

        /// <summary>Atalho global / bandeja.</summary>
        public void ToggleVoice()
        {
            SetVoiceOn(!P.VcOn);
            tray?.Balloon(P.VcOn ? "Modificador de voz LIGADO" : "Modificador de voz desligado",
                P.VcOn ? VoiceLabel() + $" ({HotkeyVoice} volta ao normal)" : "Sua voz normal voltou.");
        }

        void RefreshVoiceState()
        {
            voiceKey = CurrentVoiceKey();
            foreach (var t in voiceTiles) PaintTile(t.Key, t.Value == voiceKey);
            string label = VoiceLabel();
            if (vcStatus != null)
            {
                vcStatus.Text = P.VcOn ? "Ligado: " + label : "Desligado: sua voz normal sai para o Discord e os jogos.";
                vcStatus.Foreground = P.VcOn ? Theme.Good : Theme.Sub;
            }
            if (dashVoiceNote != null) dashVoiceNote.Text = P.VcOn ? label : "";
            tray?.SetVoice(P.VcOn, label);
        }

        /// <summary>Tom ao vivo (so com a pagina aberta).</summary>
        void FastTickVoice()
        {
            if (vcLive == null) return;
            string s;
            var t = engine.Tune;
            if (!P.VcOn || P.VcAiOn) s = ""; // a voz com IA mostra o estado no cartao dela
            else if (P.Bypass) s = "⚠ Os efeitos estão desligados (botão no topo): o modificador também.";
            else if (!AutoTune.Needed(P)) s = "Esta voz não mexe no tom: só efeitos, sem atraso extra.";
            else if (t.DetectedHz > 0 && t.OutHz > 0) s = $"Sua voz agora: {t.DetectedHz:0} Hz  →  sai em {t.OutHz:0} Hz";
            else s = "Fale para ver o tom da sua voz e o da voz nova.";
            if (s != vcLiveText) { vcLiveText = s; vcLive.Text = s; }
        }

        void RebuildUserVoices()
        {
            if (userVoicePanel == null) return;
            userVoicePanel.Children.Clear();
            voiceTiles.RemoveAll(t => t.Value.StartsWith("★"));
            userVoices.Clear();
            foreach (var name in AppSettings.UserVoices())
            {
                var cp = new ChainParams();
                if (!AppSettings.LoadUserVoice(name, cp)) continue;
                userVoices[name] = cp;
                string n = name;
                var tile = VoiceTile("⭐", n, "Sua voz salva. Botão direito: excluir.", "★" + n, () =>
                {
                    if (!AppSettings.LoadUserVoice(n, P)) return;
                    P.VcOn = true; P.Touch();
                    Log.Write("Modificador de voz: ★ " + n);
                    OnParamChanged();
                });
                var menu = new ContextMenu();
                var del = new MenuItem { Header = "Excluir a voz \"" + n + "\"" };
                del.Click += (s, e) =>
                {
                    if (MessageBox.Show("Excluir a voz \"" + n + "\"?", "TakaVox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    AppSettings.DeleteUserVoice(n);
                    RebuildUserVoices();
                    RefreshVoiceState();
                };
                menu.Items.Add(del);
                tile.ContextMenu = menu;
                userVoicePanel.Children.Add(tile);
            }
            if (userVoicePanel.Children.Count == 0)
                userVoicePanel.Children.Add(Ui.M(Ui.T("Nenhuma voz salva ainda.", 12.5, Theme.Dim), 0, 0, 0, 10));
        }

        void SaveUserVoice()
        {
            string name = AppSettings.SafeName(voiceNameBox.Text ?? "");
            if (name.Length == 0) { MessageBox.Show("Digite um nome para a voz.", "TakaVox"); return; }
            AppSettings.SaveUserVoice(name, P);
            Log.Write("Voz salva: " + name);
            voiceNameBox.Text = "";
            RebuildUserVoices();
            RefreshVoiceState();
        }
    }
}
