using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TakaVox.Dsp;
using TakaVox.Plugins;

namespace TakaVox.UI
{
    /// <summary>
    /// Voz com IA (Beatrice 2) na pagina do modificador: vozes com retrato, ajustes (tom, timbre, entonacao), estado ao vivo
    /// e a instalacao (o usuario baixa no site oficial, aceitando os termos, e o TakaVox importa da pasta Downloads).
    /// </summary>
    public sealed partial class MainWindow
    {
        readonly AiVoice aiVoice = new AiVoice();
        List<BeatriceModel> aiModels = new List<BeatriceModel>();
        string aiPlugin;
        StackPanel aiBody;
        TextBlock aiStatus;
        Button aiRetryBtn;
        DispatcherTimer aiTimer;
        double aiMsPerBlock;
        DateTime aiCpuAt = DateTime.MinValue;
        string aiStatusText = "";
        bool aiImporting;

        void InitAiVoice()
        {
            engine.Ai = aiVoice;
            aiVoice.SourceMidi = app.AiSrcPitch > 0 ? app.AiSrcPitch : 48f;
            aiVoice.Failed += reason =>
            {
                tray?.Balloon("Voz com IA parou", reason + ". Sua voz normal voltou; clique numa voz com IA para tentar de novo.");
                UpdateAiStatus();
            };
            ReloadAiModels();
            aiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            aiTimer.Tick += (s, e) => AiTick();
            aiTimer.Start();
        }

        void ReloadAiModels()
        {
            aiPlugin = Beatrice.FindPlugin();
            aiModels = BeatriceModel.LoadAll(Beatrice.ModelsDir);
        }

        void AiTick()
        {
            try
            {
                aiVoice.Update(P, aiModels, aiPlugin);
                if (aiVoice.SourceChanged)
                {
                    aiVoice.SourceChanged = false;
                    app.AiSrcPitch = aiVoice.SourceMidi;
                    MarkDirty();
                }
                if ((DateTime.Now - aiCpuAt).TotalSeconds >= 2)
                {
                    aiCpuAt = DateTime.Now;
                    double ms = aiVoice.ReadMsPerBlock();
                    if (ms > 0) aiMsPerBlock = ms;
                }
                if (currentPage == "voz") UpdateAiStatus();
                UpdateBackgroundTimers();
            }
            catch (Exception ex) { Log.Write("Voz com IA: " + ex.Message); }
        }

        void ShutdownAiVoice()
        {
            try { aiTimer?.Stop(); } catch { }
            try { aiVoice.Shutdown(); } catch (Exception ex) { Log.Write("Voz com IA (saída): " + ex.Message); }
        }

        // ================================================================== nomes amigaveis
        BeatriceVoice CurrentAiVoice()
        {
            var m = BeatriceModel.Find(aiModels, P.VcAiModel);
            if (m == null) return null;
            return m.Voices[Math.Max(0, Math.Min(P.VcAiVoice, m.Voices.Count - 1))];
        }

        static string AiName(BeatriceVoice v)
        {
            switch (v.Name)
            {
                case "つくよみちゃん (UTAU)": return "Tsukuyomi-chan (fofa)";
                case "つくよみちゃん (コーパス)": return "Tsukuyomi-chan (natural)";
                case "刻鳴時雨": return "Tokina Shigure";
            }
            if (v.Model.IsJvs) return v.Name.ToUpperInvariant() + (v.Feminine ? " (feminina)" : " (masculina)");
            return v.Name;
        }

        static string AiDesc(BeatriceVoice v)
        {
            switch (v.Name)
            {
                case "つくよみちゃん (UTAU)": return "Garota de anime, bem aguda e fofa";
                case "つくよみちゃん (コーパス)": return "Garota de anime, fala mais natural";
                case "刻鳴時雨": return "Garoto de anime, voz jovem";
                case "OLUNE": return "Voz masculina jovem, de cantor";
            }
            return (v.Feminine ? "Voz feminina" : "Voz masculina") + ", " + v.AverageHz.ToString("0") + " Hz";
        }

        // ================================================================== cartao
        FrameworkElement BuildAiVoiceCard()
        {
            aiBody = new StackPanel();
            FillAiCard();
            return Ui.Card(aiBody);
        }

        void FillAiCard()
        {
            aiBody.Children.Clear();
            voiceTiles.RemoveAll(t => t.Value.StartsWith("ai:"));
            aiStatus = null;
            aiStatusText = "";

            var badge = Ui.Pill("realista", Theme.OnAccent, Theme.Accent);
            badge.Margin = new Thickness(10, 0, 0, 0);
            aiBody.Children.Add(Ui.V(
                Ui.H(Ui.T("Vozes com IA", 15, Theme.Text, true), badge),
                Ui.M(Ui.T("Uma IA (Beatrice 2) troca a sua voz pela voz da personagem: soa como outra pessoa falando, não como efeito. " +
                          "Roda no processador, sem placa de vídeo (uns 25% de 1 núcleo) e soma ~40 ms de atraso.", 12.5, Theme.Sub, false, true), 0, 3, 0, 0)));

            if (aiPlugin == null || aiModels.Count == 0)
            {
                aiBody.Children.Add(BuildAiInstall());
                return;
            }

            var grid = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var m in aiModels.Where(m => !m.IsJvs))
                foreach (var v in m.Voices) grid.Children.Add(AiTile(v));
            aiBody.Children.Add(grid);

            var jvs = aiModels.FirstOrDefault(m => m.IsJvs);
            if (jvs != null)
            {
                var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 360 };
                box.Items.Add("Escolher uma das " + jvs.Voices.Count + " vozes…");
                var order = jvs.Voices.OrderByDescending(v => v.AveragePitch).ToList();
                foreach (var v in order) box.Items.Add(AiName(v) + " · " + v.AverageHz.ToString("0") + " Hz");
                box.SelectedIndex = 0;
                box.SelectionChanged += (s, e) =>
                {
                    if (refreshing || box.SelectedIndex <= 0) return;
                    ApplyAiVoice(order[box.SelectedIndex - 1]);
                };
                binders.Add(new ActionBinder(() =>
                {
                    var cur = P.VcOn && P.VcAiOn ? CurrentAiVoice() : null;
                    int i = cur != null && cur.Model == jvs ? order.IndexOf(cur) + 1 : 0;
                    if (box.SelectedIndex != i) box.SelectedIndex = i;
                }));
                aiBody.Children.Add(Ui.M(Row("Mais vozes (JVS)", "Vozes de pessoas reais do corpus JVS (japonês), da mais aguda para a mais grave. Só para uso não comercial.", box), 0, 0, 0, 4));
            }

            aiBody.Children.Add(AiSlider("Tom", "Ajuste fino. 0 = a IA casa o tom médio da sua voz com o da personagem (automático). + = mais agudo.",
                -12, 12, 0.5, () => P.VcAiPitch, v => P.VcAiPitch = (float)v, v => Math.Abs(v) < 0.01 ? "automático" : St(v)));
            aiBody.Children.Add(AiSlider("Timbre", "+ = garganta menor (mais infantil), − = maior (mais adulta). Não muda o tom.",
                -2, 2, 0.5, () => P.VcAiFormant, v => P.VcAiFormant = (float)v, v => Math.Abs(v) < 0.01 ? "original" : St(v)));
            aiBody.Children.Add(AiSlider("Entonação", "100% = acompanha a sua entonação. Mais = fala mais 'cantada' e expressiva (bom para anime). 0% = monótona.",
                0, 200, 5, () => P.VcAiInton, v => P.VcAiInton = (float)v, v => v < 1 ? "monótona" : v.ToString("0") + "%"));

            aiStatus = Ui.T("", 12.5, Theme.Sub, false, true);
            aiRetryBtn = Ui.Btn("Tentar de novo", () => { aiVoice.Retry(); AiTick(); UpdateAiStatus(); }, null, null, "Carrega a IA de novo.");
            aiRetryBtn.Visibility = Visibility.Collapsed;
            aiRetryBtn.Margin = new Thickness(12, 0, 0, 0);
            var statusRow = Ui.Columns(Ui.Star(), Ui.Auto);
            statusRow.Children.Add(Ui.At(aiStatus, 0));
            statusRow.Children.Add(Ui.At(aiRetryBtn, 1));
            aiBody.Children.Add(Ui.M(statusRow, 0, 10, 0, 0));

            var more = Ui.Btn("Adicionar vozes (.zip)…", () => ImportAiFiles(true), null, "", "Instala outros modelos do Beatrice (zip com o arquivo .toml e os .bin).");
            var site = Ui.Btn("Site do Beatrice", () => OpenUrl(Beatrice.SiteUrl), null, "", "prj-beatrice.com: o plugin, as vozes oficiais e a lista de vozes da comunidade.");
            var terms = Ui.Btn("Termos das vozes", ShowAiTerms, null, "", "O que pode e o que não pode fazer com cada voz.");
            aiBody.Children.Add(Ui.M(Ui.H(more, Ui.M(site, 10, 0, 0, 0), Ui.M(terms, 10, 0, 0, 0)), 0, 12, 0, 0));
            aiBody.Children.Add(Ui.M(Ui.T("Termos: com a voz das personagens não vale atacar ou criticar pessoas, fazer campanha política ou religiosa, " +
                                           "nem publicar conteúdo pesado sem aviso de idade. Usar a voz como se fosse a personagem pede crédito. As vozes JVS são só para uso não comercial.",
                                           11.5, Theme.Dim, false, true), 0, 10, 0, 0));
            UpdateAiStatus();
        }

        FrameworkElement AiSlider(string label, string tip, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt) =>
            SliderRow(label, tip, min, max, step, get, v =>
            {
                set(v);
                if (!P.VcAiOn) { VoicePresets.ResetVoice(P); P.VcAiOn = true; set(v); } // mexer aqui liga a voz com IA
                P.VcOn = true;
            }, fmt);

        Border AiTile(BeatriceVoice v)
        {
            string key = "ai:" + v.Key;
            var pic = Portrait(v, 46);
            var nm = Ui.T(AiName(v), 13, Theme.Text, true);
            var ds = Ui.T(AiDesc(v), 11.5, Theme.Sub, false, true);
            var txt = Ui.V(nm, Ui.M(ds, 0, 2, 0, 0));
            txt.Margin = new Thickness(10, 0, 0, 0);
            txt.Width = 156;
            txt.VerticalAlignment = VerticalAlignment.Center;
            var b = new Border
            {
                Width = 236, Height = 70, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12),
                Background = Theme.Card2, BorderBrush = Theme.Border, BorderThickness = new Thickness(1.5),
                Padding = new Thickness(10, 6, 8, 6), Cursor = Cursors.Hand,
                ToolTip = AiName(v) + "\n" + AiDesc(v) + $"\nTom médio da voz: {v.AverageHz:0} Hz",
                Child = Ui.H(pic, txt),
            };
            b.MouseLeftButtonUp += (s, e) =>
            {
                try { ApplyAiVoice(v); }
                catch (Exception ex) { Log.Write("Erro na voz com IA '" + v.Name + "': " + ex.Message); }
            };
            b.MouseEnter += (s, e) => { if (voiceKey != key) b.BorderBrush = Theme.AccentHi; };
            b.MouseLeave += (s, e) => PaintTile(b, voiceKey == key);
            voiceTiles.Add(new KeyValuePair<Border, string>(b, key));
            PaintTile(b, false);
            return b;
        }

        /// <summary>Retrato redondo (o rosto fica no terco de cima das imagens dos modelos) ou a inicial.</summary>
        static FrameworkElement Portrait(BeatriceVoice v, double size)
        {
            if (v.Portrait != null)
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(v.Portrait);
                    bi.DecodePixelWidth = (int)(size * 4);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    var brush = new ImageBrush(bi) { Stretch = Stretch.UniformToFill, Viewbox = new Rect(0.24, 0.04, 0.62, 0.62), ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
                    brush.Freeze();
                    return new Ellipse { Width = size, Height = size, Fill = brush, Stroke = Theme.Border, StrokeThickness = 1 };
                }
                catch { }
            }
            var t = Ui.T((v.Name.Length > 0 ? v.Name.Substring(0, 1) : "?").ToUpperInvariant(), size * 0.42, Theme.Text, true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Theme.Tint(Theme.Accent, 0x50), Child = t };
        }

        void ApplyAiVoice(BeatriceVoice v)
        {
            // so a IA: os ajustes do modificador comum voltam ao neutro (os efeitos podem ser postos de novo por cima)
            VoicePresets.ResetVoice(P);
            P.VcOn = true;
            P.VcAiOn = true;
            P.VcAiModel = v.Model.Code;
            P.VcAiVoice = v.Index;
            P.Touch();
            if (aiVoice.State == "parou" || aiVoice.State == "erro") aiVoice.Retry();
            Log.Write("Modificador de voz: IA – " + AiName(v));
            OnParamChanged();
            AiTick();
        }

        void UpdateAiStatus()
        {
            if (aiStatus == null) return;
            string s;
            Brush fg = Theme.Sub;
            bool retry = false;
            var v = CurrentAiVoice();
            switch (aiVoice.State)
            {
                case "pronta":
                    if (!engine.AiActive) { s = "Ligando a IA…"; break; }
                    double cpu = aiMsPerBlock / 10.0 * 100;
                    s = $"✓ Ligada: {(v != null ? AiName(v) : "voz com IA")}.  Seu tom médio: {Beatrice.MidiToHz(aiVoice.SourceMidi):0} Hz → " +
                        $"{(v != null ? Beatrice.MidiToHz(v.AveragePitch + P.VcAiPitch) : 0):0} Hz.  " +
                        (cpu > 0 ? $"Usa {cpu:0}% de 1 núcleo, " : "") + "atraso +37 ms.";
                    if (aiVoice.MeterFrames < 60) s += "  (Fale um pouco: o TakaVox está aprendendo o tom da sua voz.)";
                    fg = Theme.Good;
                    break;
                case "carregando": s = "Carregando a IA… (leva ~1 s)"; break;
                case "parou": s = "⚠ " + (aiVoice.Error ?? "A IA parou") + ". Sua voz normal voltou."; fg = Theme.Warn; retry = true; break;
                case "erro": s = "⚠ " + (aiVoice.Error ?? "erro"); fg = Theme.Warn; retry = true; break;
                case "sem modelo":
                case "sem plugin": s = "⚠ " + (aiVoice.Error ?? "falta instalar"); fg = Theme.Warn; break;
                default:
                    s = P.VcOn && !P.VcAiOn ? "Desligada: o modificador comum está ativo. Clique numa voz com IA para usar." : "Clique numa voz para usar.";
                    break;
            }
            if (s != aiStatusText)
            {
                aiStatusText = s;
                aiStatus.Text = s;
                aiStatus.Foreground = fg;
            }
            if (aiRetryBtn != null) aiRetryBtn.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================================================================== instalacao
        FrameworkElement BuildAiInstall()
        {
            bool hasPlugin = aiPlugin != null, hasModel = aiModels.Count > 0;
            string missing = !hasPlugin && !hasModel ? "o plugin Beatrice e as vozes" : !hasPlugin ? "o plugin Beatrice" : "as vozes (modelo)";
            var steps = Ui.T("Falta instalar " + missing + " (uma vez só, ~60 MB). Os arquivos vêm do site oficial, onde você aceita os termos de uso das vozes:\n" +
                             "1. Abra o site e baixe o \"Beatrice 2 para Windows\" e o \"Modelo oficial 1\" (Tsukuyomi-chan, Tokina Shigure e OLUNE).\n" +
                             "2. Volte aqui e clique em \"Importar os arquivos baixados\": o TakaVox acha os zips na sua pasta Downloads.",
                             12.5, Theme.Text, false, true);
            var site = Ui.Btn("Abrir o site do Beatrice", () => OpenUrl(Beatrice.SiteUrl), "AccentButton", "", "prj-beatrice.com");
            var imp = Ui.Btn("Importar os arquivos baixados", () => ImportAiFiles(false), null, "", "Procura beatrice*.zip na pasta Downloads.");
            var pick = Ui.Btn("Escolher arquivos…", () => ImportAiFiles(true), null, null, "Escolha os .zip na mão.");
            aiStatus = Ui.T("", 12.5, Theme.Sub, false, true);
            return Ui.V(Ui.M(steps, 0, 12, 0, 0), Ui.M(Ui.H(site, Ui.M(imp, 10, 0, 0, 0), Ui.M(pick, 10, 0, 0, 0)), 0, 12, 0, 0), Ui.M(aiStatus, 0, 10, 0, 0));
        }

        void ImportAiFiles(bool choose)
        {
            if (aiImporting) return;
            List<string> zips;
            if (choose)
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Arquivos do Beatrice (plugin e vozes)", Filter = "Arquivos zip (*.zip)|*.zip", Multiselect = true,
                    InitialDirectory = Beatrice.DownloadsDir,
                };
                if (dlg.ShowDialog(this) != true) return;
                zips = dlg.FileNames.ToList();
            }
            else
            {
                zips = Beatrice.FindDownloads();
                if (zips.Count == 0)
                {
                    MessageBox.Show("Não achei nenhum arquivo beatrice*.zip na pasta Downloads.\n\nBaixe no site oficial (botão \"Abrir o site do Beatrice\") " +
                                    "ou use \"Escolher arquivos…\".", "TakaVox", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            aiImporting = true;
            if (aiStatus != null) { aiStatus.Text = "Instalando…"; aiStatus.Foreground = Theme.Sub; }
            Task.Run(() =>
            {
                string msg; int plugs = 0, models = 0;
                try { msg = Beatrice.Import(zips, out plugs, out models); }
                catch (Exception ex) { msg = ex.Message; }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    aiImporting = false;
                    Log.Write("Voz com IA: instalado " + (msg.Length > 0 ? msg : "nada"));
                    ReloadAiModels();
                    FillAiCard();
                    RefreshVoiceState();
                    if (plugs + models == 0)
                        MessageBox.Show("Esses arquivos não têm o plugin nem vozes do Beatrice.\n\n" + msg, "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning);
                    else if (aiPlugin != null && aiModels.Count > 0 && aiStatus != null)
                    {
                        aiStatusText = "";
                        aiStatus.Text = "✓ Instalado: " + msg + ". Clique numa voz para usar.";
                        aiStatus.Foreground = Theme.Good;
                    }
                }));
            });
        }

        void ShowAiTerms()
        {
            var lines = new List<string>
            {
                "Vozes do modelo oficial 1 do Beatrice (você aceitou os termos no site ao baixar):",
                "",
                "• Use o modelo só para converter a sua voz. Não repasse os arquivos (.bin, imagens) para ninguém.",
                "• Tsukuyomi-chan: com a voz dela não vale atacar ou criticar pessoas, pedir apoio ou oposição a política, religião ou ideologia, " +
                "publicar conteúdo pesado sem aviso de idade nem liberar o áudio para outros usarem como material. Se usar como a voz da personagem, dê o crédito " +
                "(\"voz: Tsukuyomi-chan, personagem de material gratuito\").",
                "• Tokina Shigure: siga os termos da personagem (sem política/religião; crédito se usar como a voz dela).",
                "• OLUNE: uso livre.",
                "• Vozes JVS (pessoas reais do corpus JVS): só uso não comercial.",
                "",
                "Os termos completos estão no README de cada modelo, na pasta das vozes.",
            };
            var r = MessageBox.Show(string.Join("\n", lines) + "\n\nAbrir a pasta das vozes?", "TakaVox – termos das vozes com IA", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes)
                try { Directory.CreateDirectory(Beatrice.ModelsDir); Process.Start("explorer.exe", "\"" + Beatrice.ModelsDir + "\""); } catch { }
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Abrir site: " + ex.Message); }
        }
    }
}
