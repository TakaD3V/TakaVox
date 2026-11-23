using System;
using System.Windows;
using System.Windows.Controls;
using TakaVox.Dsp;

namespace TakaVox.UI
{
    public sealed partial class MainWindow
    {
        TextBox presetNameBox;
        TextBlock engStatus, tuneNoteText, toneStatus;
        string toneStatusKey = "";

        /// <summary>Atualiza os textos ao vivo da pagina Efeitos (motor da IA e nota do Auto-Tune).</summary>
        void FastTickEffects()
        {
            if (toneStatus != null)
            {
                var notches = engine.ToneNotches;
                string key = !P.ToneOn ? "off" : notches.Length == 0 ? "none" : "";
                if (key == "")
                {
                    var sb = new System.Text.StringBuilder();
                    double deepest = 0;
                    foreach (var nt in notches)
                    {
                        if (sb.Length > 0) sb.Append(" · ");
                        sb.Append(nt.Freq.ToString("0")).Append(" Hz");
                        deepest = Math.Min(deepest, nt.DepthDb);
                    }
                    key = $"Removendo agora: {sb} (cortes estreitos de até {-deepest:0} dB, antes da IA)";
                }
                if (key != toneStatusKey)
                {
                    toneStatusKey = key;
                    toneStatus.Text = key == "off" ? "Desligado." : key == "none" ? "Nenhum ruído tonal agora (ele procura sozinho nas suas pausas)." : key;
                    toneStatus.Foreground = key == "off" || key == "none" ? Theme.Sub : Theme.Good;
                }
            }
            if (engStatus != null)
            {
                bool loading = engine.ActiveEngine != P.NoiseEngine && engine.ActiveEngine >= 0 && P.NoiseEngine != 2;
                engStatus.Text = loading ? "⏳ Carregando o modelo de IA…" : "Ativo agora: " + engine.EngineName;
                engStatus.Foreground = loading ? Theme.Warn : Theme.Good;
            }
            if (tuneNoteText != null && P.TuneOn)
            {
                var t = engine.Tune;
                if (t.TargetMidi >= 0 && t.DetectedHz > 0)
                {
                    float c = t.CentsOff;
                    tuneNoteText.Text = $"🎵 {AutoTune.NoteName(t.TargetMidi)}   ({t.DetectedHz:0} Hz, {(c >= 0 ? "+" : "")}{c:0} cents → corrigido)";
                    tuneNoteText.Foreground = Math.Abs(c) < 15 ? Theme.Good : Theme.Warn;
                }
                else { tuneNoteText.Text = "🎵 –  (cante ou fale para detectar a nota)"; tuneNoteText.Foreground = Theme.Sub; }
            }
        }

        FrameworkElement BuildEffects()
        {
            var cols = Ui.Columns(Ui.Star(), Ui.Px(14), Ui.Star());
            var left = new StackPanel();
            var right = new StackPanel();
            cols.Children.Add(Ui.At(left, 0));
            cols.Children.Add(Ui.At(right, 2));

            // 1) Entrada
            var chBox = new ComboBox { Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var s in new[] { "Automático", "Só esquerdo", "Só direito", "Média dos dois" }) chBox.Items.Add(s);
            chBox.SelectionChanged += (s, e) => { if (refreshing || chBox.SelectedIndex < 0) return; P.ChannelMode = chBox.SelectedIndex; P.Touch(); OnParamChanged(); };
            binders.Add(new ActionBinder(() => chBox.SelectedIndex = P.ChannelMode));
            left.Children.Add(EffectCard("Entrada", "Primeiro ganho, antes de tudo. A calibração ajusta sozinha.", null, null,
                SliderRow("Ganho de entrada", "Aumenta o sinal do microfone antes dos efeitos. Seu mic é baixo: a calibração define o valor ideal.",
                    -12, 40, 0.5, () => P.InputGainDb, v => P.InputGainDb = (float)v, v => Db(v)),
                Row("Canal do mic", "Alguns mics só mandam som em um lado. 'Automático' detecta sozinho.", chBox)));

            // 2) Filtro de graves
            left.Children.Add(EffectCard("Filtro de graves", "Tira ronco, vibração da mesa, ar-condicionado e estalos de 'P'/'B'.",
                () => P.HpfOn, v => P.HpfOn = v,
                SliderRow("Corte abaixo de", "Frequências abaixo disso são removidas (24 dB/oitava). Voz masculina: 70–90 Hz. Feminina: 100–130 Hz.",
                    20, 300, 1, () => P.HpfFreq, v => P.HpfFreq = (float)v, v => v.ToString("0") + " Hz")));

            // 2b) Removedor de ventoinha/zumbido
            toneStatus = Ui.T("", 12, Theme.Sub, true, true);
            left.Children.Add(EffectCard("Removedor de ventoinha e zumbido",
                "Acha os 'apitos' fixos do ruído de fundo (ventoinha, ventilador, ar-condicionado, zumbido elétrico) nas suas pausas e corta cada um " +
                "com um filtro bem estreito antes da IA. Assim a IA não precisa 'cavar' sua voz no meio do barulho e a voz fica mais cheia, sem som de copo. " +
                "Sem atraso; se não houver ruído tonal, não faz nada.",
                () => P.ToneOn, v => P.ToneOn = v,
                toneStatus));

            // 3) IA
            var engBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var s in VoiceChain.EngineNames) engBox.Items.Add(s);
            var engHint = Ui.T("", 11.5, Theme.Sub, false, true);
            engStatus = Ui.T("", 11.5, Theme.Good, true, true);
            engBox.SelectionChanged += (s, e) =>
            {
                if (refreshing || engBox.SelectedIndex < 0) return;
                P.NoiseEngine = engBox.SelectedIndex; P.Touch();
                Log.Write("Motor de IA → " + VoiceChain.EngineNames[P.NoiseEngine]);
                OnParamChanged();
            };
            binders.Add(new ActionBinder(() => { engBox.SelectedIndex = P.NoiseEngine; engHint.Text = VoiceChain.EngineHints[P.NoiseEngine]; }));
            left.Children.Add(EffectCard("Redução de ruído por IA", "Rede neural que separa a sua voz de todo o resto: teclado, mouse, ventoinha, PC, rua, chiado.",
                () => P.NoiseOn, v => P.NoiseOn = v,
                Row("Modelo de IA", "Escolha o motor de IA. O recomendado já vem selecionado.", engBox),
                Ui.M(engHint, 170, 0, 0, 6),
                SliderRow("Intensidade", "100% = remove o ruído por completo. Mais baixo = deixa um pouco do ambiente (soa mais natural).",
                    0, 100, 1, () => P.NoiseMix * 100, v => P.NoiseMix = (float)(v / 100), v => v.ToString("0") + "%"),
                Switch("Limpeza extra (pós-filtro)", "Só nos modelos DeepFilterNet: limpa ainda mais o que sobra entre as palavras.", () => P.NoiseExtra, v => P.NoiseExtra = v),
                Ui.M(engStatus, 0, 4, 0, 0)));

            // 3b) Auto-Tune
            var keyBox = new ComboBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var n in AutoTune.NoteNames) keyBox.Items.Add(n);
            keyBox.SelectionChanged += (s, e) => { if (refreshing || keyBox.SelectedIndex < 0) return; P.TuneKey = keyBox.SelectedIndex; P.Touch(); OnParamChanged(); };
            var scaleBox = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var n in AutoTune.ScaleNames) scaleBox.Items.Add(n);
            scaleBox.SelectionChanged += (s, e) => { if (refreshing || scaleBox.SelectedIndex < 0) return; P.TuneScale = scaleBox.SelectedIndex; P.Touch(); OnParamChanged(); };
            binders.Add(new ActionBinder(() => { keyBox.SelectedIndex = P.TuneKey; scaleBox.SelectedIndex = P.TuneScale; }));
            tuneNoteText = Ui.T("Ligue e cante uma nota para ver a afinação aqui.", 13, Theme.Sub, true, true);
            left.Children.Add(EffectCard("Auto-Tune", "Afina sua voz na hora (estilo trap/T-Pain no 0% de velocidade, ou correção natural no 100%). Adiciona ~38 ms de atraso enquanto ligado.",
                () => P.TuneOn, v => P.TuneOn = v,
                Row("Tom", "A tonalidade da música (se não souber, use C com escala Cromática).", keyBox),
                Row("Escala", "Cromática = puxa para a nota mais próxima. Maior/Menor = só notas do tom.", scaleBox),
                SliderRow("Velocidade", "0 = efeito robô instantâneo (T-Pain). 100 = correção suave e natural.",
                    0, 100, 1, () => P.TuneSpeed, v => P.TuneSpeed = (float)v, v => v < 1 ? "robô" : v.ToString("0")),
                SliderRow("Intensidade", "Quanto da correção aplicar.", 0, 100, 1, () => P.TuneAmount, v => P.TuneAmount = (float)v, v => v.ToString("0") + "%"),
                Ui.M(tuneNoteText, 0, 6, 0, 0)));

            // 4) Portao
            var vadSwitch = Switch("Usar a IA para saber quando estou falando", "Recomendado: o portão só abre com VOZ, não com teclado/clique/batida.", () => P.GateUseVad, v => P.GateUseVad = v);
            left.Children.Add(EffectCard("Portão de voz", "Abaixa tudo quando você está calado. Acaba com teclado e respiração entre as falas.",
                () => P.GateOn, v => P.GateOn = v,
                vadSwitch,
                SliderRow("Sensibilidade da voz", "Quanto MENOR, mais fácil abrir (pega voz baixinha). Quanto MAIOR, mais rígido contra barulhos.",
                    5, 95, 1, () => P.GateVadThreshold * 100, v => P.GateVadThreshold = (float)(v / 100), v => v.ToString("0") + "%"),
                SliderRow("Nível mínimo", "Além da IA, a voz precisa passar desse volume. A calibração ajusta.",
                    -80, -10, 1, () => P.GateThresholdDb, v => P.GateThresholdDb = (float)v, v => Db(v)),
                SliderRow("Redução quando calado", "Quanto o som cai quando você não fala. −30 dB é natural; −60 dB é silêncio total.",
                    -80, 0, 1, () => P.GateRangeDb, v => P.GateRangeDb = (float)v, v => Db(v)),
                SliderRow("Segurar", "Tempo que o portão fica aberto depois que você para de falar (não corta o fim das palavras).",
                    0, 1000, 10, () => P.GateHoldMs, v => P.GateHoldMs = (float)v, v => v.ToString("0") + " ms"),
                SliderRow("Fechamento", "Velocidade com que o som abaixa depois do 'segurar'.",
                    10, 1000, 10, () => P.GateReleaseMs, v => P.GateReleaseMs = (float)v, v => v.ToString("0") + " ms")));

            // 5) Nivelador
            left.Children.Add(EffectCard("Nivelador automático", "Mantém sua voz sempre no mesmo volume, mesmo se você chegar perto ou longe do mic. Só aprende enquanto você fala.",
                () => P.AgcOn, v => P.AgcOn = v,
                SliderRow("Volume alvo", "Nível médio desejado da voz antes do compressor.",
                    -36, -8, 0.5, () => P.AgcTargetDb, v => P.AgcTargetDb = (float)v, v => Db(v)),
                SliderRow("Reforço máximo", "Quanto ele pode aumentar no máximo.",
                    0, 24, 0.5, () => P.AgcMaxBoostDb, v => P.AgcMaxBoostDb = (float)v, v => "+" + v.ToString("0.0") + " dB"),
                SliderRow("Corte máximo", "Quanto ele pode diminuir no máximo.",
                    0, 24, 0.5, () => P.AgcMaxCutDb, v => P.AgcMaxCutDb = (float)v, v => "−" + v.ToString("0.0") + " dB"),
                SliderRow("Velocidade", "Rapidez da correção. Muito rápido soa 'bombeando'.",
                    0.5, 20, 0.5, () => P.AgcSpeed, v => P.AgcSpeed = (float)v, v => v.ToString("0.0") + " dB/s")));

            // 6) EQ
            var eqItems = new System.Collections.Generic.List<UIElement>();
            for (int i = 0; i < 5; i++)
            {
                int b = i;
                var band = P.Bands[b];
                var head = Switch($"{b + 1}. {band.Name}  ({TypeName(band.Type)})", "Liga/desliga esta faixa do equalizador.", () => P.Bands[b].On, v => P.Bands[b].On = v);
                head.Margin = new Thickness(0, b == 0 ? 0 : 12, 0, 2);
                eqItems.Add(head);
                eqItems.Add(SliderRow("Ganho", "Quanto aumentar (+) ou cortar (−) nesta faixa.", -18, 18, 0.5,
                    () => P.Bands[b].GainDb, v => P.Bands[b].GainDb = (float)v, v => Db(v, true)));
                eqItems.Add(SliderRow("Frequência", "Centro da faixa.", Math.Log10(20), Math.Log10(20000), 0,
                    () => Math.Log10(P.Bands[b].Freq), v => P.Bands[b].Freq = (float)Math.Round(Math.Pow(10, v)), v => SpectrumEq.FormatHz(Math.Pow(10, v))));
                eqItems.Add(SliderRow("Largura (Q)", "Q baixo = faixa larga e suave. Q alto = faixa estreita e cirúrgica.", 0.2, 8, 0.01,
                    () => P.Bands[b].Q, v => P.Bands[b].Q = (float)v, v => v.ToString("0.00")));
            }
            eqItems.Insert(0, Ui.M(Ui.T("Dica: no Painel você arrasta os pontos direto no gráfico.", 12, Theme.Dim, false, true), 0, 0, 0, 10));
            right.Children.Add(EffectCard("Equalizador (5 faixas)", "Molda o timbre: corpo, tira o som 'embolado'/nasal, dá presença e brilho.",
                () => P.EqOn, v => P.EqOn = v, eqItems.ToArray()));

            // 7) De-esser
            right.Children.Add(EffectCard("De-esser", "Suaviza os 'S', 'X' e 'CH' ardidos sem deixar a voz abafada.",
                () => P.DeessOn, v => P.DeessOn = v,
                SliderRow("Frequência", "A partir de onde ele vigia o chiado. 5–7 kHz é o comum.",
                    3000, 12000, 100, () => P.DeessFreq, v => P.DeessFreq = (float)v, v => SpectrumEq.FormatHz(v)),
                SliderRow("Sensibilidade", "Mais perto de 0 = age menos. Mais negativo = age mais vezes.",
                    -30, 0, 0.5, () => P.DeessThresholdDb, v => P.DeessThresholdDb = (float)v, v => Db(v)),
                SliderRow("Redução máxima", "O máximo que ele abaixa os agudos num 'S'.",
                    0, 18, 0.5, () => P.DeessMaxDb, v => P.DeessMaxDb = (float)v, v => "−" + v.ToString("0.0") + " dB")));

            // 8) Compressor
            right.Children.Add(EffectCard("Compressor", "Deixa a voz encorpada e constante, como em rádio/stream.",
                () => P.CompOn, v => P.CompOn = v,
                SliderRow("Limiar", "Acima desse volume o compressor começa a agir.",
                    -60, 0, 0.5, () => P.CompThresholdDb, v => P.CompThresholdDb = (float)v, v => Db(v)),
                SliderRow("Razão", "3:1 = natural. 6:1 = bem comprimido.",
                    1, 20, 0.1, () => P.CompRatio, v => P.CompRatio = (float)v, v => v.ToString("0.0") + ":1"),
                SliderRow("Ataque", "Rapidez para segurar um pico.",
                    0.5, 100, 0.5, () => P.CompAttackMs, v => P.CompAttackMs = (float)v, v => v.ToString("0.0") + " ms"),
                SliderRow("Liberação", "Rapidez para soltar depois do pico.",
                    20, 1000, 5, () => P.CompReleaseMs, v => P.CompReleaseMs = (float)v, v => v.ToString("0") + " ms"),
                SliderRow("Joelho", "Transição suave para a compressão (mais natural).",
                    0, 20, 0.5, () => P.CompKneeDb, v => P.CompKneeDb = (float)v, v => v.ToString("0.0") + " dB"),
                SliderRow("Compensação", "Ganho depois do compressor para recuperar o volume.",
                    -12, 24, 0.5, () => P.CompMakeupDb, v => P.CompMakeupDb = (float)v, v => Db(v, true))));

            // 9) Saida
            right.Children.Add(EffectCard("Volume final e limitador", "O limitador é a trava de segurança: sua voz nunca passa do teto, então nunca estoura ou distorce no Discord.",
                null, null,
                SliderRow("Volume final", "Volume geral que vai para o Discord/jogos.",
                    -24, 24, 0.5, () => P.OutputGainDb, v => P.OutputGainDb = (float)v, v => Db(v, true)),
                SliderRow("Teto do limitador", "Nível máximo absoluto. −1 dB é o ideal.",
                    -12, 0, 0.1, () => P.LimiterCeilingDb, v => P.LimiterCeilingDb = (float)v, v => Db(v)),
                SliderRow("Liberação", "Rapidez com que o limitador solta.",
                    10, 500, 5, () => P.LimiterReleaseMs, v => P.LimiterReleaseMs = (float)v, v => v.ToString("0") + " ms")));

            // 10) Presets
            presetNameBox = new TextBox { Text = "Meu preset", Width = 200 };
            var saveBtn = Ui.Btn("Salvar ajustes atuais", SaveUserPreset, "AccentButton", "");
            var delBtn = Ui.Btn("Apagar preset selecionado", DeleteSelectedPreset, null, "");
            var resetBtn = Ui.Btn("Voltar tudo ao padrão", () => { BuiltInPresets.Apply("Padrão TakaVox", P); app.PresetName = "Padrão TakaVox"; RefreshPresetList(); OnParamChanged(); }, null, "");
            right.Children.Add(EffectCard("Presets", "Salve seus ajustes favoritos e troque rápido pelo menu lá em cima.", null, null,
                Ui.H(presetNameBox, Ui.M(saveBtn, 10, 0, 0, 0)),
                Ui.M(Ui.H(delBtn, Ui.M(resetBtn, 10, 0, 0, 0)), 0, 10, 0, 0)));
            return cols;
        }

        static string TypeName(FilterType t)
        {
            switch (t)
            {
                case FilterType.LowShelf: return "prateleira de graves";
                case FilterType.HighShelf: return "prateleira de agudos";
                case FilterType.HighPass: return "passa-altas";
                case FilterType.LowPass: return "passa-baixas";
                default: return "sino";
            }
        }

        static string Db(double v, bool sign = false) => (sign && v > 0 ? "+" : "") + v.ToString("0.0") + " dB";

        FrameworkElement EffectCard(string title, string desc, Func<bool> getOn, Action<bool> setOn, params UIElement[] body)
        {
            var head = Ui.Columns(Ui.Star(), Ui.Auto);
            head.Children.Add(Ui.At(Ui.V(Ui.T(title, 15, Theme.Text, true), Ui.M(Ui.T(desc, 12, Theme.Sub, false, true), 0, 3, 0, 0)), 0));
            var content = new StackPanel();
            if (getOn != null)
            {
                var sw = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 2, 0, 0), ToolTip = "Liga/desliga este efeito" };
                sw.Checked += (s, e) => { if (refreshing) return; setOn(true); P.Touch(); OnParamChanged(); };
                sw.Unchecked += (s, e) => { if (refreshing) return; setOn(false); P.Touch(); OnParamChanged(); };
                binders.Add(new SwitchBinder(sw, getOn));
                binders.Add(new ActionBinder(() => content.Opacity = getOn() ? 1 : 0.45));
                head.Children.Add(Ui.At(sw, 1));
            }
            var stack = Ui.V(head);
            content.Margin = new Thickness(0, 12, 0, 0);
            foreach (var b in body) content.Children.Add(b);
            stack.Children.Add(content);
            return Ui.Card(stack);
        }

        FrameworkElement Row(string label, string tip, UIElement control)
        {
            var g = Ui.Columns(Ui.Px(170), Ui.Star());
            g.Margin = new Thickness(0, 5, 0, 5);
            var l = Ui.T(label, 13, Theme.Text); l.ToolTip = tip;
            g.Children.Add(Ui.At(l, 0));
            g.Children.Add(Ui.At(control, 1));
            return g;
        }

        FrameworkElement SliderRow(string label, string tip, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt)
        {
            var g = Ui.Columns(Ui.Px(170), Ui.Star(), Ui.Px(82));
            g.Margin = new Thickness(0, 4, 0, 4);
            var l = Ui.T(label, 13, Theme.Text);
            l.ToolTip = tip;
            var s = new Slider { Minimum = min, Maximum = max, ToolTip = tip, Value = Math.Max(min, Math.Min(max, get())) };
            if (step > 0) { s.SmallChange = step; s.LargeChange = step * 5; s.TickFrequency = step; s.IsSnapToTickEnabled = true; }
            else { s.SmallChange = (max - min) / 100; s.LargeChange = (max - min) / 20; }
            var vt = Ui.T(fmt(get()), 12.5, Theme.Sub, true);
            vt.HorizontalAlignment = HorizontalAlignment.Right;
            s.ValueChanged += (o, e) =>
            {
                if (refreshing) return;
                set(s.Value);
                P.Touch();
                vt.Text = fmt(s.Value);
                OnParamChanged();
            };
            binders.Add(new SliderBinder(s, vt, get, fmt));
            g.Children.Add(Ui.At(l, 0));
            g.Children.Add(Ui.At(Ui.M(s, 8, 0, 8, 0), 1));
            g.Children.Add(Ui.At(vt, 2));
            return g;
        }

        CheckBox Switch(string label, string tip, Func<bool> get, Action<bool> set)
        {
            var c = new CheckBox { Content = Ui.T(label, 13, Theme.Text, false, true), ToolTip = tip, Margin = new Thickness(0, 6, 0, 6) };
            c.Checked += (s, e) => { if (refreshing) return; set(true); P.Touch(); OnParamChanged(); };
            c.Unchecked += (s, e) => { if (refreshing) return; set(false); P.Touch(); OnParamChanged(); };
            binders.Add(new SwitchBinder(c, get));
            return c;
        }

        void SaveUserPreset()
        {
            string name = AppSettings.SafeName(presetNameBox.Text ?? "");
            if (name.Length == 0) { MessageBox.Show("Digite um nome para o preset.", "TakaVox"); return; }
            AppSettings.SaveUserPreset(name, P);
            app.PresetName = "★ " + name;
            RefreshPresetList();
            Log.Write("Preset salvo: " + name);
            MarkDirty();
        }

        void DeleteSelectedPreset()
        {
            if (!(presetBox.SelectedItem is string sel) || !sel.StartsWith("★ "))
            {
                MessageBox.Show("Selecione no menu lá em cima um preset seu (com ★) para apagar.", "TakaVox");
                return;
            }
            if (MessageBox.Show($"Apagar o preset '{sel.Substring(2)}'?", "TakaVox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            AppSettings.DeleteUserPreset(sel.Substring(2));
            app.PresetName = "Padrão TakaVox";
            RefreshPresetList();
        }
    }
}
