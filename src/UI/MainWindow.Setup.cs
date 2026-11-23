using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TakaVox.Audio;
using TakaVox.Interop;

namespace TakaVox.UI
{
    public sealed partial class MainWindow
    {
        ComboBox cbIn, cbOut, cbMon;
        Slider micLevel;
        TextBlock micLevelText, micFormatText, cableStatusText, defaultStatusText, outHintText;
        Button installBtn, makeDefaultBtn, removeCableBtn;
        bool installing;
        string prevPlayConsole, prevPlayComms;

        FrameworkElement BuildSetupPage()
        {
            var cols = Ui.Columns(Ui.Star(), Ui.Px(14), Ui.Star());
            var left = new StackPanel();
            var right = new StackPanel();
            cols.Children.Add(Ui.At(left, 0));
            cols.Children.Add(Ui.At(right, 2));

            // ---- microfone
            cbIn = new ComboBox();
            cbIn.SelectionChanged += (s, e) => OnInputSelected();
            micLevel = new Slider { Minimum = 0, Maximum = 100, ToolTip = "Ganho do próprio microfone no Windows (hardware). Seu M8 vai até +31 dB em 100%." };
            micLevel.ValueChanged += (s, e) =>
            {
                if (refreshing || !micLevel.IsMouseCaptureWithin && !micLevel.IsKeyboardFocusWithin && !micLevel.IsMouseOver) return;
                AudioDevices.SetVolumeScalar(engine.InputId, (float)(micLevel.Value / 100));
                UpdateMicLevelText();
            };
            micLevel.LostMouseCapture += (s, e) => Log.Change($"Volume do microfone no Windows → {micLevel.Value:0}%");
            micLevelText = Ui.T("", 12.5, Theme.Sub, true);
            micFormatText = Ui.T("", 11.5, Theme.Dim, false, true);
            var lvlRow = Ui.Columns(Ui.Star(), Ui.Px(120));
            lvlRow.Children.Add(Ui.At(micLevel, 0));
            micLevelText.HorizontalAlignment = HorizontalAlignment.Right;
            lvlRow.Children.Add(Ui.At(micLevelText, 1));
            left.Children.Add(Ui.Card(Ui.V(
                Ui.SectionTitle("Seu microfone (entrada)", "O microfone de verdade. O TakaVox escuta ele e trata a voz."),
                cbIn, Ui.M(micFormatText, 0, 6, 0, 0),
                Ui.M(Ui.T("Volume do microfone no Windows", 13, Theme.Text), 0, 16, 0, 4), lvlRow,
                Ui.M(Ui.T("Deixe em 100% para mics fracos (o TakaVox e o limitador cuidam do resto). Só baixe se a calibração avisar que está estourando.", 11.5, Theme.Dim, false, true), 0, 6, 0, 0),
                Ui.M(Ui.Btn("Atualizar lista", () => RefreshDeviceLists(), null, ""), 0, 14, 0, 0))));

            // ---- microfone virtual
            cableStatusText = Ui.T("", 13, Theme.Text, true, true);
            defaultStatusText = Ui.T("", 12.5, Theme.Sub, false, true);
            installBtn = Ui.Btn("Instalar microfone virtual (VB-CABLE)", InstallCable, "AccentButton", "");
            makeDefaultBtn = Ui.Btn("Tornar o TakaVox o mic padrão do Windows", () => { ConfigureVirtualMic(true); RefreshDeviceLists(); }, null, "");
            removeCableBtn = Ui.Btn("Remover VB-CABLE…", RemoveCable, null, "", "Abre o instalador oficial; clique em 'Remove Driver' para desinstalar.");
            cbOut = new ComboBox();
            cbOut.SelectionChanged += (s, e) => OnOutputSelected();
            outHintText = Ui.T("", 11.5, Theme.Warn, false, true);
            var keep = AppSwitch("Manter o TakaVox como microfone padrão",
                "Se outro programa (ex.: SteelSeries Sonar) trocar o microfone padrão, o TakaVox volta automaticamente.",
                () => app.KeepDefault, v => { app.KeepDefault = v; if (v) ConfigureVirtualMic(true); });
            var vbtns = new WrapPanel();
            foreach (var b in new[] { installBtn, makeDefaultBtn, removeCableBtn }) { b.Margin = new Thickness(0, 0, 10, 8); vbtns.Children.Add(b); }
            left.Children.Add(Ui.Card(Ui.V(
                Ui.SectionTitle("Microfone virtual (para Discord e jogos)", "O TakaVox entrega sua voz tratada no \"CABLE Input\" e os programas escutam o \"CABLE Output\"."),
                cableStatusText, Ui.M(defaultStatusText, 0, 4, 0, 10), vbtns, keep,
                Ui.M(Ui.T("Saída do TakaVox (onde a voz tratada é entregue)", 13, Theme.Text), 0, 12, 0, 4), cbOut, Ui.M(outHintText, 0, 4, 0, 0))));

            // ---- retorno
            cbMon = new ComboBox();
            cbMon.SelectionChanged += (s, e) => OnMonitorSelected();
            var monSwitch = AppSwitch("Ouvir minha voz (retorno no fone)", "Toca sua voz tratada no fone, com ~60 ms de atraso. Use fone, não caixa de som.",
                () => app.MonitorOn, v => SetMonitorOn(v));
            var monVol = new Slider { Minimum = 0, Maximum = 100, Value = app.MonitorVolume * 100 };
            monVol.ValueChanged += (s, e) => { if (!refreshing) SetMonitorVolume((float)(monVol.Value / 100)); };
            binders.Add(new ActionBinder(() => monVol.Value = app.MonitorVolume * 100));
            right.Children.Add(Ui.Card(Ui.V(
                Ui.SectionTitle("Ouvir minha voz em", "Fone onde tocam o retorno e os testes de gravação."),
                cbMon, Ui.M(monSwitch, 0, 12, 0, 0), Ui.M(Ui.T("Volume do retorno", 12.5, Theme.Sub), 0, 8, 0, 2), monVol)));

            // ---- Windows
            right.Children.Add(Ui.Card(Ui.V(
                Ui.SectionTitle("Windows", "Ajustes do sistema que deixam tudo redondo."),
                AppSwitch("Não abaixar o som do jogo/música quando alguém fala",
                    "Windows › Som › Comunicações. Por padrão o Windows abaixa 80% do resto do som durante chamadas.",
                    () => SystemTweaks.GetDucking() == 3,
                    v =>
                    {
                        if (v) SystemTweaks.SetDucking(3);
                        else SystemTweaks.SetDucking(int.TryParse(app.OrigDucking, out var o) && o != 3 ? o : (int?)null);
                        Log.Change("Windows › Comunicações: " + (v ? "não fazer nada" : "valor original"));
                    }),
                AppSwitch("Iniciar o TakaVox junto com o Windows",
                    "Recomendado: assim sua voz tratada está sempre pronta (ele fica na bandeja, perto do relógio).",
                    () => SystemTweaks.GetAutostart(),
                    v => { SystemTweaks.SetAutostart(v); Log.Change("Iniciar com o Windows: " + (v ? "ligado" : "desligado")); }),
                AppSwitch("Fechar no X = continuar ligado na bandeja", "Para sair de verdade use o ícone da bandeja › Sair.",
                    () => app.CloseToTray, v => app.CloseToTray = v),
                AppSwitch("Ao sair do TakaVox, voltar o mic padrão para o microfone real",
                    "Assim, se o TakaVox estiver fechado, o Discord/jogos usam seu mic normal em vez de ficar mudo.",
                    () => app.RestoreMicOnExit, v => app.RestoreMicOnExit = v),
                AppSwitch("Economia automática (recomendado)",
                    "Quando nenhum app (Discord, jogo, OBS) está usando o microfone virtual e a janela está fechada, ninguém está te ouvindo: " +
                    "a IA para e o jogo fica com o processador só para ele. Volta sozinho na hora em que um app abre o microfone.",
                    () => app.AutoRest, v => { app.AutoRest = v; UpdateRest(); }),
                Ui.M(Ui.Btn("Abrir painel de som do Windows", () => SystemTweaks.OpenSoundControlPanel(true), null, ""), 0, 10, 0, 0))));

            // ---- restaurar
            right.Children.Add(Ui.Card(Ui.V(
                Ui.SectionTitle("Restaurar", "Desfaz as mudanças do TakaVox no Windows (o histórico mostra tudo que foi feito)."),
                Ui.Btn("Restaurar tudo como era antes", RestoreEverything, "DangerButton", ""))));
            return cols;
        }

        CheckBox AppSwitch(string label, string tip, Func<bool> get, Action<bool> set)
        {
            var c = new CheckBox { Content = Ui.T(label, 13, Theme.Text, false, true), ToolTip = tip, Margin = new Thickness(0, 6, 0, 6) };
            RoutedEventHandler h = (s, e) =>
            {
                if (refreshing) return;
                try { set(c.IsChecked == true); }
                catch (Exception ex) { Log.Write("Ajuste '" + label + "': " + ex.Message); }
                MarkDirty();
                RefreshAll();
            };
            c.Checked += h; c.Unchecked += h;
            binders.Add(new SwitchBinder(c, get));
            return c;
        }

        void RefreshDeviceLists()
        {
            if (cbIn == null) return;
            refreshing = true;
            try
            {
                Fill(cbIn, AudioDevices.List(true), engine.InputId);
                Fill(cbOut, AudioDevices.List(false), app.OutputDeviceId);
                Fill(cbMon, AudioDevices.List(false), app.MonitorDeviceId);
                var v = AudioDevices.GetVolume(engine.InputId);
                if (v != null && !micLevel.IsMouseCaptureWithin) micLevel.Value = v.Scalar * 100;
            }
            finally { refreshing = false; }
            UpdateMicLevelText();
            UpdateCableStatus();
        }

        static void Fill(ComboBox cb, List<DeviceInfo> list, string selectedId)
        {
            cb.Items.Clear();
            DeviceInfo sel = null;
            foreach (var d in list) { cb.Items.Add(d); if (d.Id == selectedId) sel = d; }
            cb.SelectedItem = sel;
        }

        void UpdateMicLevelText()
        {
            var v = AudioDevices.GetVolume(engine.InputId);
            micLevelText.Text = v == null ? "—" : $"{v.Scalar * 100:0}%  ({(v.Db >= 0 ? "+" : "")}{v.Db:0.#} dB){(v.Mute ? "  MUDO" : "")}";
            micFormatText.Text = engine.CaptureRunning ? $"Funcionando • {engine.CaptureFormat} • IA: {engine.EngineName}" : "Status: " + engine.CaptureStatus;
        }

        void UpdateCableStatus()
        {
            if (cableStatusText == null) return;
            var cabOut = AudioDevices.FindCableOutput();
            bool has = cabOut != null;
            if (installing) cableStatusText.Text = "⏳ Instalando o microfone virtual… aceite os avisos do Windows.";
            else cableStatusText.Text = has ? "✔ Instalado: " + cabOut.Name : "✖ Microfone virtual ainda não instalado";
            cableStatusText.Foreground = has ? Theme.Good : Theme.Warn;
            installBtn.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            removeCableBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            makeDefaultBtn.IsEnabled = has;
            if (has)
            {
                bool def = SystemTweaks.IsVirtualMicDefault();
                string cur = AudioDevices.GetNameById(AudioDevices.GetDefaultId(true, ERole.Console)) ?? "nenhum";
                defaultStatusText.Text = def ? "✔ O TakaVox é o microfone padrão do Windows (Discord em \"Padrão\" e jogos já recebem sua voz tratada)."
                                             : "Microfone padrão atual: " + cur + ". Clique em \"Tornar o TakaVox o mic padrão\".";
                defaultStatusText.Foreground = def ? Theme.Sub : Theme.Warn;
                makeDefaultBtn.Visibility = def ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                defaultStatusText.Text = "Clique em instalar. É o driver oficial e gratuito da VB-Audio (o mesmo que streamers usam).";
                defaultStatusText.Foreground = Theme.Sub;
                makeDefaultBtn.Visibility = Visibility.Collapsed;
            }
            var outName = AudioDevices.GetNameById(app.OutputDeviceId) ?? "";
            outHintText.Text = string.IsNullOrEmpty(app.OutputDeviceId) ? "Escolha \"CABLE Input (VB-Audio Virtual Cable)\"." :
                outName.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) < 0 ? "Atenção: o normal é \"CABLE Input\". Com outra saída o Discord não recebe sua voz." : "";
        }

        void SlowTickSetup()
        {
            if (!micLevel.IsMouseCaptureWithin)
            {
                var v = AudioDevices.GetVolume(engine.InputId);
                if (v != null) { refreshing = true; micLevel.Value = v.Scalar * 100; refreshing = false; }
            }
            UpdateMicLevelText();
            UpdateCableStatus();
        }

        void OnInputSelected()
        {
            if (refreshing || !(cbIn.SelectedItem is DeviceInfo d)) return;
            if (d.Name.IndexOf("CABLE Output", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MessageBox.Show("Esse é o microfone virtual do próprio TakaVox – usar ele como entrada criaria um loop.\nEscolha o seu microfone de verdade (ex.: M8).", "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshDeviceLists();
                return;
            }
            if (AudioDevices.IsVirtualName(d.Name) &&
                MessageBox.Show($"\"{d.Name}\" parece ser um dispositivo virtual, não um microfone de verdade. Usar mesmo assim?", "TakaVox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                RefreshDeviceLists();
                return;
            }
            app.InputDeviceId = d.Id; app.InputDeviceName = d.Name;
            engine.SetInput(d.Id, d.Name);
            Log.Change("Microfone de entrada → " + d.Name);
            MarkDirty();
        }

        void OnOutputSelected()
        {
            if (refreshing || !(cbOut.SelectedItem is DeviceInfo d)) return;
            app.OutputDeviceId = d.Id; app.OutputDeviceName = d.Name;
            engine.SetOutput(d.Id);
            Log.Change("Saída do TakaVox → " + d.Name);
            MarkDirty();
            UpdateCableStatus();
        }

        void OnMonitorSelected()
        {
            if (refreshing || !(cbMon.SelectedItem is DeviceInfo d)) return;
            app.MonitorDeviceId = d.Id; app.MonitorDeviceName = d.Name;
            engine.SetMonitor(d.Id, app.MonitorOn, app.MonitorVolume);
            Log.Write("Retorno → " + d.Name);
            MarkDirty();
        }

        // ------------------------------------------------------------ VB-CABLE

        /// <summary>Garante o pacote oficial do VB-CABLE (baixa do site da VB-Audio se precisar) e chama "next" com o caminho do setup.</summary>
        void WithVbCableSetup(Action<string> next)
        {
            installing = true;
            UpdateCableStatus();
            Task.Run(() =>
            {
                if (File.Exists(SystemTweaks.VbCableSetupPath) && Signature.IsTrusted(SystemTweaks.VbCableSetupPath, "BUREL"))
                    return SystemTweaks.VbCableSetupPath;
                return SystemTweaks.DownloadVbCable(msg => Dispatcher.BeginInvoke(new Action(() => cableStatusText.Text = "⏳ " + msg)));
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (t.IsFaulted)
                {
                    installing = false;
                    UpdateCableStatus();
                    string why = t.Exception?.GetBaseException().Message ?? "erro desconhecido";
                    Log.Write("VB-CABLE: " + why);
                    MessageBox.Show("Não consegui preparar o VB-CABLE:\n" + why + "\n\nVocê também pode baixar manualmente em www.vb-cable.com e depois clicar em \"Tornar o TakaVox o mic padrão\".",
                        "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                next(t.Result);
            })));
        }

        void InstallCable()
        {
            if (AudioDevices.FindCableInput() != null) { ConfigureVirtualMic(true); return; }
            if (MessageBox.Show("O TakaVox usa o VB-CABLE como microfone virtual (driver gratuito da VB-Audio – www.vb-cable.com, donationware).\n\n" +
                                "Vou baixar o pacote OFICIAL direto do site da VB-Audio (~1,3 MB), conferir a assinatura digital e abrir o instalador deles:\n\n" +
                                "1) O Windows pergunta se permite alterações → clique SIM\n" +
                                "2) Na janela do VB-CABLE, clique em \"Install Driver\"\n" +
                                "3) Se aparecer \"Deseja instalar este software de dispositivo?\" → INSTALAR\n" +
                                "4) Feche a janela do VB-CABLE quando ele disser que terminou\n\n" +
                                "Continuar?", "Instalar microfone virtual", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            prevPlayConsole = AudioDevices.GetDefaultId(false, ERole.Console);
            prevPlayComms = AudioDevices.GetDefaultId(false, ERole.Communications);
            WithVbCableSetup(setup =>
            {
                System.Diagnostics.Process p;
                try { p = SystemTweaks.LaunchVbCableInstaller(); }
                catch (Win32Exception)
                {
                    installing = false; UpdateCableStatus();
                    MessageBox.Show("Instalação cancelada (a permissão do Windows foi negada).", "TakaVox");
                    return;
                }
                Log.Change("Instalador oficial do VB-CABLE aberto (pedido pelo usuário).");
                cableStatusText.Text = "⏳ Na janela do VB-CABLE, clique em \"Install Driver\" e depois feche-a.";
                Task.Run(() => { try { p?.WaitForExit(600000); } catch { } })
                    .ContinueWith(_ => Dispatcher.BeginInvoke(new Action(AfterCableInstall)));
            });
        }

        void AfterCableInstall()
        {
            int tries = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) =>
            {
                tries++;
                var cabIn = AudioDevices.FindCableInput();
                if (cabIn != null)
                {
                    t.Stop();
                    installing = false;
                    // o Windows as vezes troca o som padrao para o cabo novo: devolve para o que era
                    suppressKeepDefault = true;
                    if (AudioDevices.GetDefaultId(false, ERole.Console) == cabIn.Id && AudioDevices.IsActive(prevPlayConsole))
                    {
                        AudioDevices.SetDefault(prevPlayConsole, ERole.Console, ERole.Multimedia);
                        Log.Change("O Windows tinha trocado a saída de som para o CABLE; voltei para " + AudioDevices.GetNameById(prevPlayConsole));
                    }
                    if (AudioDevices.GetDefaultId(false, ERole.Communications) == cabIn.Id && AudioDevices.IsActive(prevPlayComms))
                        AudioDevices.SetDefault(prevPlayComms, ERole.Communications);
                    suppressKeepDefault = false;
                    Log.Change("Microfone virtual VB-CABLE instalado e ativo.");
                    ConfigureVirtualMic(true);
                    RefreshDeviceLists();
                    MessageBox.Show("Pronto! O microfone virtual está instalado e o TakaVox já é o seu microfone padrão.\n\n" +
                                    "Feche e abra de novo o Discord e os jogos para eles enxergarem o novo microfone.\n" +
                                    "No Discord: Configurações › Voz e vídeo › Dispositivo de entrada: \"Padrão\" ou \"CABLE Output\".",
                                    "TakaVox", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (tries > 25)
                {
                    t.Stop();
                    installing = false;
                    UpdateCableStatus();
                    MessageBox.Show("O instalador terminou mas o Windows ainda não ativou o microfone virtual.\n" +
                                    "Se você clicou em instalar, reinicie o PC – o TakaVox configura o resto sozinho quando abrir.",
                                    "TakaVox", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            t.Start();
        }

        void RemoveCable()
        {
            if (MessageBox.Show("Vou abrir o instalador oficial do VB-CABLE. Para remover, clique em \"Remove Driver\" e depois reinicie o PC.\n\nSem o VB-CABLE o Discord/jogos deixam de receber a voz tratada. Continuar?",
                    "TakaVox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            WithVbCableSetup(setup =>
            {
                installing = false;
                UpdateCableStatus();
                try
                {
                    SystemTweaks.LaunchVbCableInstaller();
                    Log.Change("Instalador do VB-CABLE aberto para remoção (pedido pelo usuário).");
                }
                catch (Win32Exception) { }
            });
        }

        // ------------------------------------------------------------ restaurar

        void RestoreEverything()
        {
            string orig = AudioDevices.GetNameById(app.OrigCaptureConsole) ?? "(o anterior)";
            if (MessageBox.Show($"Isso desfaz as mudanças do TakaVox no Windows:\n\n" +
                                $"• Microfone padrão volta para: {orig}\n" +
                                "• Ajuste de comunicações (abaixar som) volta ao original\n" +
                                "• Volume do microfone volta ao original\n" +
                                "• O TakaVox deixa de iniciar com o Windows\n\n" +
                                "O VB-CABLE continua instalado (remova em \"Remover VB-CABLE\" se quiser). Continuar?",
                    "Restaurar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            suppressKeepDefault = true;
            app.KeepDefault = false;
            var roles = new[] { (app.OrigCaptureConsole, ERole.Console), (app.OrigCaptureMultimedia, ERole.Multimedia), (app.OrigCaptureComms, ERole.Communications) };
            foreach (var (id, role) in roles)
            {
                string target = AudioDevices.IsActive(id) ? id : engine.InputId;
                if (AudioDevices.IsActive(target)) AudioDevices.SetDefault(target, role);
            }
            try { SystemTweaks.SetDucking(int.TryParse(app.OrigDucking, out var dv) ? dv : (int?)null); } catch { }
            if (app.OrigMicVolume >= 0 && AudioDevices.IsActive(app.OrigMicId)) AudioDevices.SetVolumeScalar(app.OrigMicId, app.OrigMicVolume);
            try { SystemTweaks.SetAutostart(false); } catch { }
            Log.Change("Restaurado: microfone padrão, ajuste de comunicações, volume do mic e inicialização voltaram ao que eram antes do TakaVox.");
            suppressKeepDefault = false;
            MarkDirty();
            RefreshAll();
            RefreshDeviceLists();
            MessageBox.Show("Pronto, tudo voltou como era antes.\nPara usar o TakaVox de novo: Configurar › \"Tornar o TakaVox o mic padrão\".", "TakaVox");
        }
    }
}
