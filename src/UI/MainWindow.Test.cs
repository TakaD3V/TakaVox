using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TakaVox.Audio;
using TakaVox.Dsp;

namespace TakaVox.UI
{
    public sealed partial class MainWindow
    {
        // calibracao
        Button calibBtn;
        TextBlock calibTitle, calibText, calibResult;
        Border calibBarFill;
        int calibStep;
        DateTime calibStart;
        int calibSilenceEnd;
        CalibrationData calibData;
        const double SilenceSecs = 3.0, SpeechSecs = 7.0;

        // gravacao A/B
        Button recBtn, playOrigBtn, playProcBtn, stopPlayBtn;
        TextBlock recInfo, playDevInfo;
        WaveformView wave;
        float[] recOrig, recProc;
        int recLen;
        bool recWatching, playing;

        FrameworkElement BuildTestPage()
        {
            var root = new StackPanel();

            // ---- calibracao
            calibBtn = Ui.Btn("Calibrar agora (10 s)", StartCalibration, "AccentButton", "\uE9D9");
            calibBtn.HorizontalAlignment = HorizontalAlignment.Left;
            calibTitle = Ui.T("Pronto para calibrar", 17, Theme.Text, true);
            calibText = Ui.T("Passo 1: 3 s em silêncio (mede o ruído do quarto).  Passo 2: 7 s falando normal (mede sua voz e o timbre do mic). Depois o TakaVox ajusta o ganho, o portão de voz e o equalizador sozinho.", 12.5, Theme.Sub, false, true);
            var autoEqSw = new CheckBox { IsChecked = app.AutoEq, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            autoEqSw.Checked += (s, e) => { app.AutoEq = true; MarkDirty(); };
            autoEqSw.Unchecked += (s, e) => { app.AutoEq = false; MarkDirty(); };
            var autoEqRow = Ui.H(autoEqSw, Ui.V(Ui.T("Ajustar o equalizador ao timbre do seu mic", 13, Theme.Text, true),
                Ui.T("Corrige voz abafada, embolada ou com som de 'copo': mede o seu mic e acerta as 5 faixas do EQ (você pode mexer depois).", 11.5, Theme.Sub, false, true)));
            autoEqRow.Margin = new Thickness(0, 12, 0, 0);
            calibBarFill = new Border { Background = Theme.Accent, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 14, 0, 14) };
            bar.Children.Add(new Border { Background = Theme.Card2, CornerRadius = new CornerRadius(3) });
            bar.Children.Add(calibBarFill);
            calibResult = Ui.T(app.Calibrated && app.CalibrationSummary.Length > 0 ? "Última calibração:\n" + app.CalibrationSummary : "", 13, Theme.Text, false, true);
            root.Children.Add(Ui.Card(Ui.V(
                Ui.SectionTitle("Calibração automática", "Faça uma vez (e de novo se trocar de mic, de lugar ou de posição)."),
                calibTitle, Ui.M(calibText, 0, 4, 0, 0), autoEqRow, bar, calibBtn, Ui.M(calibResult, 0, 14, 0, 0))));

            // ---- gravar e comparar
            recBtn = Ui.Btn("Gravar teste (até 10 s)", ToggleRecord, "AccentButton", "\uE7C8");
            playOrigBtn = Ui.Btn("Ouvir ORIGINAL", () => PlayTest(false), null, "\uE768", "Toca a gravação como seu mic capta, sem nada.");
            playProcBtn = Ui.Btn("Ouvir com TakaVox", () => PlayTest(true), null, "\uE768", "Toca a mesma gravação com os ajustes ATUAIS (mexa nos efeitos e ouça de novo).");
            stopPlayBtn = Ui.Btn("Parar", StopTest, null, "\uE71A");
            playOrigBtn.IsEnabled = playProcBtn.IsEnabled = stopPlayBtn.IsEnabled = false;
            var btns = new WrapPanel();
            foreach (var b in new[] { recBtn, playOrigBtn, playProcBtn, stopPlayBtn }) { b.Margin = new Thickness(0, 0, 10, 8); btns.Children.Add(b); }
            recInfo = Ui.T("Grave você falando como no Discord. Depois compare o antes e o depois no fone.", 12.5, Theme.Sub, false, true);
            playDevInfo = Ui.T("", 11.5, Theme.Dim, false, true);
            wave = new WaveformView { Height = 210, Margin = new Thickness(0, 12, 0, 0) };
            root.Children.Add(Ui.Card(Ui.V(
                Ui.SectionTitle("Gravar e comparar (antes × depois)", "A gravação fica só na memória do app – nada é salvo nem enviado."),
                btns, Ui.M(recInfo, 0, 4, 0, 0), Ui.M(playDevInfo, 0, 4, 0, 0), wave)));
            return root;
        }

        // ------------------------------------------------------------ calibracao

        void StartCalibration()
        {
            if (calibStep != 0) return;
            if (!engine.CaptureRunning)
            {
                MessageBox.Show("O microfone não está funcionando agora (" + engine.CaptureStatus + ").\nConfira em Configurar › Microfone.", "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (engine.Rec.Recording) engine.Rec.Stop();
            calibData = new CalibrationData();
            engine.Calib = calibData;
            calibStep = 1;
            calibStart = DateTime.Now;
            calibBtn.IsEnabled = false;
            calibTitle.Text = "1/2 – Fique em SILÊNCIO…";
            calibTitle.Foreground = Theme.Warn;
            calibText.Text = "Não fale nada por 3 segundos. Pode deixar PC, ventilador e ar ligados como sempre.";
            calibResult.Text = "";
        }

        /// <summary>Calibracao, gravacao ou teste tocando: a interface precisa continuar acordada.</summary>
        bool TestBusy => calibStep != 0 || recWatching || playing;

        void FastTickTest()
        {
            if (calibStep != 0)
            {
                double t = (DateTime.Now - calibStart).TotalSeconds;
                double total = calibStep == 1 ? SilenceSecs : SpeechSecs;
                double frac = Math.Min(1, t / total);
                var parent = calibBarFill.Parent as FrameworkElement;
                double fullW = parent?.ActualWidth ?? 0;
                calibBarFill.Width = fullW * ((calibStep == 1 ? 0 : 0.3) + frac * (calibStep == 1 ? 0.3 : 0.7));
                if (calibStep == 1 && t >= SilenceSecs)
                {
                    calibSilenceEnd = calibData.Count;
                    calibStep = 2;
                    calibStart = DateTime.Now;
                    calibTitle.Text = "2/2 – Agora FALE normalmente…";
                    calibTitle.Foreground = Theme.Good;
                    calibText.Text = "Fale como você fala no Discord, na distância de sempre. Ex.: \"Bora jogar, mano! Testando o TakaVox: um, dois, três, quatro, cinco…\"";
                }
                else if (calibStep == 2 && t >= SpeechSecs) FinishCalibration();
            }

            if (recWatching)
            {
                if (engine.Rec.Recording) recInfo.Text = $"● Gravando… {engine.Rec.Seconds:0.0} s (fale normalmente; clique em Parar quando terminar)";
                else { recWatching = false; FinishRecording(); }
            }
            if (playing && wave != null)
            {
                wave.Progress = engine.PlaybackProgress;
                wave.InvalidateVisual();
            }
        }

        void FinishCalibration()
        {
            engine.Calib = null;
            calibStep = 0;
            calibBtn.IsEnabled = true;
            calibBarFill.Width = (calibBarFill.Parent as FrameworkElement)?.ActualWidth ?? 0;
            var d = calibData;
            int n = d.Count, s = calibSilenceEnd;

            var sil = new List<float>();
            for (int i = Math.Min(10, s); i < s; i++) sil.Add(d.RmsDb[i]);
            if (sil.Count < 60)
            {
                CalibFail("Não deu para medir o silêncio (o microfone travou?). Tente de novo.");
                return;
            }
            sil.Sort();
            float noise = sil[sil.Count / 2];

            double sumPow = 0; int cnt = 0; float peak = -120;
            for (int i = s + 30; i < n; i++)
            {
                if (d.PeakDb[i] > peak) peak = d.PeakDb[i];
                if (d.Vad[i] > 0.6f && d.RmsDb[i] > noise + 6) { sumPow += Math.Pow(10, d.RmsDb[i] / 10); cnt++; }
            }
            if (cnt < 60)
            {
                CalibFail("Não consegui ouvir sua voz direito (preciso de pelo menos ~1 segundo de fala). Fale mais perto do microfone e tente de novo.");
                return;
            }
            float speech = (float)(10 * Math.Log10(sumPow / cnt));
            float snr = speech - noise;

            float gain = DspMath.Clamp(-23f - speech, -6f, 36f);
            float maxByPeak = -3f - peak;
            if (gain > maxByPeak) gain = Math.Max(-6f, maxByPeak);
            gain = (float)Math.Round(gain * 2) / 2f;
            float thr;
            if (P.NoiseEngine != 2)
            {
                // com DeepFilterNet o ruido some antes do portao: o nivel so precisa ficar abaixo da voz
                thr = Math.Min(-55f, speech + gain - 22f);
                thr = DspMath.Clamp((float)Math.Round(thr), -72f, -40f);
            }
            else
            {
                thr = noise + gain + 6f;
                thr = Math.Min(thr, speech + gain - 15f);
                thr = DspMath.Clamp((float)Math.Round(thr), -72f, -30f);
            }

            // EQ automatica: mede o timbre do mic na fala e acerta as 5 faixas do equalizador
            VoiceEq.Result eq = null;
            if (app.AutoEq)
            {
                try
                {
                    eq = VoiceEq.Analyze(d.Audio, Math.Min(d.AudioLength, n * CalibrationData.BlockLen), d.RmsDb, d.Vad, n, s, noise);
                    if (eq.Ok) VoiceEq.Apply(eq, P);
                }
                catch (Exception ex) { Log.Write("EQ automática: " + ex); eq = null; }
            }

            P.InputGainDb = gain;
            P.GateThresholdDb = thr;
            P.Touch();
            app.Calibrated = true;

            string level = speech < -45 ? "muito baixa" : speech < -32 ? "baixa" : speech < -18 ? "boa" : "alta";
            string quality = snr >= 35 ? "excelente" : snr >= 25 ? "boa" : snr >= 15 ? "razoável – a IA vai trabalhar bastante" : "ruim – aproxime o mic da boca";
            var lines = new List<string>
            {
                $"✔ Ruído do ambiente: {noise:0} dBFS",
                $"✔ Sua voz no mic cru: {speech:0} dBFS ({level})",
                $"✔ Voz/ruído: {snr:0} dB ({quality})",
                $"➜ Ganho de entrada: {(gain >= 0 ? "+" : "")}{gain:0.0} dB",
                $"➜ Portão de voz: {thr:0} dB",
            };
            // ruido tonal (ventoinha/zumbido) no silencio da calibracao
            try
            {
                int silStart = 10 * CalibrationData.BlockLen, silLen = Math.Max(0, (s - 12) * CalibrationData.BlockLen);
                var tones = ToneRemover.DetectTones(d.Audio, silStart, silLen, out double fund, out bool electric);
                if (tones.Count > 0)
                {
                    var fl = new List<string>();
                    for (int i = 0; i < tones.Count && i < 4; i++) fl.Add(tones[i].f.ToString("0") + " Hz");
                    string what = electric ? "zumbido elétrico" : "ventoinha/motor";
                    lines.Add($"⚠ Ruído de {what} no fundo ({string.Join(", ", fl)}): " +
                              (P.ToneOn ? "o 'Removedor de ventoinha' já tira esses apitos antes da IA." : "ligue o 'Removedor de ventoinha' em Efeitos."));
                    Log.Write($"Calibração: ruído tonal de {what}, fundamental ~{fund:0.0} Hz: {string.Join(", ", fl)}");
                }
            }
            catch (Exception ex) { Log.Write("Calibração (ruído tonal): " + ex.Message); }
            if (eq != null && eq.Ok)
            {
                lines.Add("➜ Equalizador: " + VoiceEq.Summary(eq));
                foreach (var note in eq.Notes) lines.Add("    • " + note);
                if (eq.Notes.Count == 0) lines.Add("    • o timbre do seu mic já está equilibrado: só um toque de presença e brilho");
            }
            else if (eq != null) lines.Add("• Equalizador mantido (" + eq.Error + "). Fale mais durante a calibração para ele se ajustar.");
            if (peak > -1f)
            {
                lines.Add($"⚠ Seu mic chegou a estourar (pico {peak:0.0} dBFS). Fale um pouco mais longe ou baixe o volume do mic em Configurar.");
            }
            else if (speech < -45) lines.Add("Dica: fale a uns 10–15 cm do mic, com a boca apontada para ele.");
            string summary = string.Join("\n", lines);
            app.CalibrationSummary = $"{DateTime.Now:dd/MM HH:mm}\n" + summary;
            calibTitle.Text = "Calibração concluída ✔";
            calibTitle.Foreground = Theme.Good;
            calibText.Text = "Pronto! Agora grave um teste aqui embaixo e compare o antes e o depois.";
            calibResult.Text = summary;
            Log.Change($"Calibração automática: ganho de entrada {gain:+0.0;-0.0} dB, portão {thr:0} dB (ruído {noise:0} dBFS, voz {speech:0} dBFS, voz/ruído {snr:0} dB).");
            if (eq != null && eq.Ok)
            {
                Log.Change("EQ automática pela calibração: " + VoiceEq.Summary(eq) + (eq.Notes.Count > 0 ? " (" + string.Join("; ", eq.Notes) + ")" : ""));
                var curve = new System.Text.StringBuilder("EQ automática – desvio medido por banda (dB): ");
                for (int i = 0; i < VoiceEq.Centers.Length; i++)
                    curve.Append(VoiceEq.Centers[i]).Append('=').Append(double.IsNaN(eq.Deviation[i]) ? "?" : eq.Deviation[i].ToString("+0.0;-0.0")).Append(' ');
                Log.Write(curve.ToString() + $"({eq.Frames} quadros de fala)");
            }
            MarkDirty();
            RefreshAll();
        }

        void CalibFail(string msg)
        {
            calibTitle.Text = "Não deu certo desta vez";
            calibTitle.Foreground = Theme.Bad;
            calibText.Text = msg;
        }

        // ------------------------------------------------------------ gravar / comparar

        void ToggleRecord()
        {
            if (calibStep != 0) return;
            if (engine.Rec.Recording) { engine.Rec.Stop(); return; }
            if (!engine.CaptureRunning)
            {
                MessageBox.Show("O microfone não está funcionando agora (" + engine.CaptureStatus + ").", "TakaVox");
                return;
            }
            StopTest();
            engine.Rec.Start();
            recWatching = true;
            recBtn.Content = Ui.H(Ui.M(Ui.Icon("\uE71A", 14, System.Windows.Media.Brushes.White), 0, 0, 8, 0), Ui.T("Parar gravação", 13, System.Windows.Media.Brushes.White));
            playOrigBtn.IsEnabled = playProcBtn.IsEnabled = false;
        }

        void FinishRecording()
        {
            recBtn.Content = Ui.H(Ui.M(Ui.Icon("\uE7C8", 14, System.Windows.Media.Brushes.White), 0, 0, 8, 0), Ui.T("Gravar de novo", 13, System.Windows.Media.Brushes.White));
            recLen = engine.Rec.Length;
            if (recLen < 4800) { recInfo.Text = "Gravação muito curta. Tente de novo."; return; }
            recOrig = new float[recLen];
            Array.Copy(engine.Rec.Buffer, recOrig, recLen);
            recProc = engine.ProcessOffline(recOrig, recLen);
            wave.A = recOrig; wave.B = recProc; wave.Length = recLen; wave.Progress = -1;
            wave.InvalidateVisual();
            recInfo.Text = $"Gravado: {recLen / 48000.0:0.0} s. Compare no fone: \"Ouvir ORIGINAL\" × \"Ouvir com TakaVox\". Mexa nos efeitos e ouça de novo!";
            playOrigBtn.IsEnabled = playProcBtn.IsEnabled = true;
        }

        void PlayTest(bool processed)
        {
            if (recOrig == null) return;
            if (processed)
            {
                recProc = engine.ProcessOffline(recOrig, recLen); // reprocessa com os ajustes atuais
                wave.B = recProc; wave.InvalidateVisual();
            }
            engine.Play(processed ? recProc : recOrig, recLen);
            playing = true;
            stopPlayBtn.IsEnabled = true;
            string dev = AudioDevices.GetNameById(engine.MonitorId) ?? "saída padrão";
            playDevInfo.Text = "Tocando em: " + dev + " (troque em Configurar › Ouvir minha voz em)";
        }

        void StopTest()
        {
            engine.StopPlayback();
            playing = false;
            if (wave != null) { wave.Progress = -1; wave.InvalidateVisual(); }
            if (stopPlayBtn != null) stopPlayBtn.IsEnabled = false;
        }

        void SlowTickTest()
        {
            if (playing && !engine.IsPlaying) StopTest();
        }
    }
}
