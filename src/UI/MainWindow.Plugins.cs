using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TakaVox.Plugins;

namespace TakaVox.UI
{
    /// <summary>Pagina "Plugins": plugins VST3/VST2 do PC direto na voz, sem DAW.</summary>
    public sealed partial class MainWindow
    {
        [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);

        PluginManager plugins;
        DispatcherTimer plugTimer;
        StackPanel chainList, libList, badList, folderList;
        TextBlock plugScanStatus, plugInfo, plugNotice, plugCpu;
        Border plugNoticeBox;
        TextBox plugSearch;
        CheckBox plugMaster;
        Button scanBtn, badToggle;
        bool refreshingPlugins, showBad;
        int shownLibVersion = -1;
        string shownLibKey = "";
        readonly Dictionary<PluginSlot, PluginWindow> paramWindows = new Dictionary<PluginSlot, PluginWindow>();

        void InitPlugins()
        {
            plugins = new PluginManager();
            engine.Plugins = plugins.Chain;
            plugins.Changed += RefreshPluginsPage;
            plugins.Notice += ShowPluginNotice;
            plugins.ParamsChanged += s => { if (paramWindows.TryGetValue(s, out var w)) w.RefreshValues(); };
            plugTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
            plugTimer.Tick += (s, e) =>
            {
                try { plugins.Tick(); }
                catch (Exception ex) { Log.Write("Plugins (tick): " + ex.Message); }
            };
        }

        /// <summary>Abre os plugins salvos depois que a janela ja apareceu (alguns demoram um pouco para abrir).</summary>
        void StartPlugins()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try { plugins.Start(); }
                catch (Exception ex) { Log.Write("Plugins: " + ex); }
                plugTimer.Start();
                RefreshPluginsPage();
                if (currentPage == "plugins" && !plugins.ScannedThisSession) plugins.ScanAsync(false);
            }));
        }

        void OnPluginsPageShown()
        {
            if (plugins != null && plugins.Started && !plugins.ScannedThisSession) plugins.ScanAsync(false);
            RefreshPluginsPage();
        }

        void ShutdownPlugins()
        {
            try { plugTimer?.Stop(); } catch { }
            foreach (var w in paramWindows.Values.ToList()) try { w.Close(); } catch { }
            try { plugins?.Shutdown(); } catch (Exception ex) { Log.Write("Plugins (saída): " + ex.Message); }
        }

        void ShowPluginNotice(string msg)
        {
            if (plugNotice == null) return;
            plugNotice.Text = msg;
            plugNoticeBox.Visibility = Visibility.Visible;
            if (!IsVisible) tray?.Balloon("TakaVox – plugins", msg);
        }

        // ================================================================== layout

        FrameworkElement BuildPluginsPage()
        {
            var root = new StackPanel();

            plugNotice = Ui.T("", 12.5, Theme.Warn, true, true);
            var nb = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            nb.Children.Add(Ui.At(Ui.M(Ui.Icon("", 16, Theme.Warn), 0, 0, 12, 0), 0));
            nb.Children.Add(Ui.At(plugNotice, 1));
            nb.Children.Add(Ui.At(Ui.M(Ui.Btn("Entendi", () => plugNoticeBox.Visibility = Visibility.Collapsed), 12, 0, 0, 0), 2));
            plugNoticeBox = new Border
            {
                Background = Theme.Tint(Theme.Warn, 28), BorderBrush = Theme.Tint(Theme.Warn, 110), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10, 12, 10), Margin = new Thickness(0, 0, 0, 14),
                Child = nb, Visibility = Visibility.Collapsed,
            };
            root.Children.Add(plugNoticeBox);

            // ---- 1) cadeia
            plugMaster = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 2, 0, 0), ToolTip = "Liga/desliga todos os plugins de uma vez (A/B)" };
            plugMaster.Checked += (s, e) => { if (!refreshingPlugins) plugins.SetAllOn(true); };
            plugMaster.Unchecked += (s, e) => { if (!refreshingPlugins) plugins.SetAllOn(false); };
            var head = Ui.Columns(Ui.Star(), Ui.Auto);
            head.Children.Add(Ui.At(Ui.V(Ui.T("Sua cadeia de plugins", 15, Theme.Text, true),
                Ui.M(Ui.T("Use os mesmos plugins do FL Studio, Ableton, Reaper… direto na sua voz, sem nenhuma DAW aberta. " +
                          "Funciona com VST3 e VST2 de 64 bits. Eles entram nesta ordem depois dos efeitos do TakaVox e antes do limitador final, " +
                          "que continua protegendo contra estouro.", 12, Theme.Sub, false, true), 0, 3, 0, 0)), 0));
            head.Children.Add(Ui.At(plugMaster, 1));
            chainList = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            plugInfo = Ui.T("", 11.5, Theme.Sub, false, true);
            plugCpu = Ui.T("", 11.5, Theme.Sub, false, true);
            root.Children.Add(Ui.Card(Ui.V(head, chainList, Ui.M(plugInfo, 0, 10, 0, 0), plugCpu)));

            // ---- 2) biblioteca
            plugSearch = new TextBox { Width = 220, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Filtrar por nome, fabricante ou tipo" };
            plugSearch.TextChanged += (s, e) => RefreshLibraryList(true);
            scanBtn = Ui.Btn("Procurar plugins", () => plugins.ScanAsync(false), null, "", "Procura plugins novos ou atualizados nas pastas");
            scanBtn.VerticalAlignment = VerticalAlignment.Center;
            plugScanStatus = Ui.T("", 12, Theme.Sub, false, true);
            var libHead = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto);
            libHead.Children.Add(Ui.At(Ui.V(Ui.T("Plugins instalados no seu PC", 15, Theme.Text, true), Ui.M(plugScanStatus, 0, 3, 0, 0)), 0));
            libHead.Children.Add(Ui.At(Ui.M(Ui.V(Ui.T("Filtrar", 11, Theme.Dim), plugSearch), 14, 0, 12, 0), 1));
            libHead.Children.Add(Ui.At(scanBtn, 2));
            libList = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            badList = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            badToggle = Ui.Btn("", () => { showBad = !showBad; RefreshLibraryList(true); });
            badToggle.HorizontalAlignment = HorizontalAlignment.Left;
            badToggle.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(Ui.Card(Ui.V(libHead, libList, badToggle, badList)));

            // ---- 3) pastas
            folderList = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var addFolder = Ui.Btn("Adicionar pasta…", PickPluginFolder, null, "", "Escolha uma pasta com plugins .vst3 ou .dll");
            addFolder.HorizontalAlignment = HorizontalAlignment.Left;
            addFolder.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(Ui.Card(Ui.V(Ui.T("Pastas onde o TakaVox procura", 15, Theme.Text, true),
                Ui.M(Ui.T("As pastas padrão de VST3 e VST2 já estão incluídas (a mesma que o FL Studio usa para VST3). " +
                          "Se algum plugin foi instalado em outro lugar, adicione a pasta aqui.", 12, Theme.Sub, false, true), 0, 3, 0, 0),
                folderList, addFolder)));

            // ---- 4) dicas
            var tips = Ui.T(
                "O TakaVox já vem com cerca de 20 efeitos grátis do pacote mda (licença MIT): eco, reverb, voz de robô, rádio antigo, coro, flanger, wah… " +
                "Eles aparecem na lista acima. Para ir além, estes são ótimos e de graça:\n\n" +
                "• TDR Nova (Tokyo Dawn Records) — EQ dinâmico: segura sibilância e ressonâncias da voz.\n" +
                "• Valhalla Supermassive — reverbs e ecos gigantes, para efeitos.\n" +
                "• Kilohearts Essentials — pacote com dezenas de efeitos (chorus, distorção, pitch, delay…).\n" +
                "• MeldaProduction MFreeFXBundle — mais de 30 efeitos, com compressores e EQs.\n" +
                "• Graillon 3 Free (Auburn Sounds) — voz de robô e mudança de tom.\n\n" +
                "Baixe no site oficial de cada um, instale a versão VST3 64 bits e clique em \"Procurar plugins\". " +
                "Os efeitos que já vêm no FL Studio (Fruity…) são do próprio FL e só funcionam dentro dele; plugins VST de outras marcas funcionam aqui. " +
                "Dica: cada plugin com atraso (lookahead) aumenta a latência da voz — fique de olho no valor mostrado na cadeia.",
                12.5, Theme.Sub, false, true);
            root.Children.Add(Ui.Card(Ui.V(Ui.T("Plugins grátis que combinam com voz", 15, Theme.Text, true), Ui.M(tips, 0, 8, 0, 0))));
            return root;
        }

        // ================================================================== atualizar

        void RefreshPluginsPage()
        {
            if (plugins == null || chainList == null) return;
            refreshingPlugins = true;
            try
            {
                plugMaster.IsChecked = !plugins.Chain.Bypass;
                plugMaster.IsEnabled = plugins.Available && plugins.Slots.Count > 0;

                chainList.Children.Clear();
                var slots = plugins.Slots;
                if (!plugins.Started) chainList.Children.Add(Ui.T("Abrindo os plugins…", 13, Theme.Sub));
                else if (!plugins.Available) chainList.Children.Add(Ui.T("⚠ Suporte a plugins indisponível: " + plugins.UnavailableReason, 13, Theme.Warn, true, true));
                else if (slots.Count == 0)
                    chainList.Children.Add(EmptyChain());
                for (int i = 0; i < slots.Count; i++) chainList.Children.Add(ChainRow(slots[i], i, slots.Count));

                if (slots.Count > 0)
                {
                    double ms = plugins.Chain.LatencySamples * 1000.0 / Tvh.SampleRate;
                    plugInfo.Text = plugins.Chain.Bypass ? "Plugins desligados (a voz passa direto)."
                        : "Atraso somado dos plugins ligados: " + (ms < 0.05 ? "nenhum" : ms.ToString("0.#") + " ms") + ".";
                }
                else plugInfo.Text = "";

                plugScanStatus.Text = plugins.Scanning ? "⏳ " + plugins.ScanStatus
                    : plugins.ScanStatus.Length > 0 ? plugins.ScanStatus : LibrarySummary();
                scanBtn.IsEnabled = plugins.Available && plugins.Started && !plugins.Scanning;
                RefreshLibraryList(false);
                RefreshFolderList();
            }
            catch (Exception ex) { Log.Write("Plugins (tela): " + ex.Message); }
            finally { refreshingPlugins = false; }
        }

        void SlowTickPlugins()
        {
            if (plugins == null || plugCpu == null) return;
            double max = plugins.Chain.MaxMs;
            plugins.Chain.MaxMs = 0;
            if (plugins.Chain.Items.Length == 0 || plugins.Chain.Bypass) { plugCpu.Text = ""; return; }
            plugCpu.Text = $"Processador: os plugins usam até {max:0.0} ms de cada bloco de 10 ms" + (max > 5 ? " — muito! Remova algum plugin pesado para não picotar." : ".");
            plugCpu.Foreground = max > 5 ? Theme.Warn : Theme.Sub;
        }

        string LibrarySummary()
        {
            int ok = plugins.Library.Count(e => e.Usable);
            if (!plugins.Started) return "";
            if (plugins.Library.Count == 0) return "Clique em \"Procurar plugins\" para achar os plugins instalados.";
            return ok == 0 ? "Nenhum efeito encontrado ainda." : ok == 1 ? "1 efeito disponível." : ok + " efeitos disponíveis.";
        }

        UIElement EmptyChain()
        {
            return Ui.V(Ui.T("Nenhum plugin na cadeia ainda.", 13.5, Theme.Text, true),
                Ui.M(Ui.T("Escolha um efeito na lista \"Plugins instalados\" abaixo e clique em Adicionar (o TakaVox já vem com efeitos grátis: eco, reverb, voz de robô…). " +
                          "A janela dele abre na hora para você ajustar.", 12.5, Theme.Sub, false, true), 0, 3, 0, 0));
        }

        Button IconBtn(string glyph, string tip, Action click, bool enabled = true)
        {
            var b = new Button
            {
                Content = Ui.Icon(glyph, 12, null), ToolTip = tip, IsEnabled = enabled, Opacity = enabled ? 1 : 0.35,
                Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 7, 10, 7), VerticalAlignment = VerticalAlignment.Center,
            };
            b.Click += (s, e) =>
            {
                try { click(); }
                catch (Exception ex) { Log.Write("Plugins (botão): " + ex); MessageBox.Show(this, ex.Message, "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            return b;
        }

        UIElement ChainRow(PluginSlot s, int index, int count)
        {
            var row = Ui.Columns(Ui.Auto, Ui.Px(30), Ui.Star(), Ui.Auto);
            var sw = new CheckBox { IsChecked = s.Enabled && !s.Crashed, IsEnabled = s.Loaded && !s.Crashed, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Liga/desliga este plugin" };
            sw.Checked += (o, e) => { if (!refreshingPlugins) plugins.SetEnabled(s, true); };
            sw.Unchecked += (o, e) => { if (!refreshingPlugins) plugins.SetEnabled(s, false); };
            row.Children.Add(Ui.At(sw, 0));
            row.Children.Add(Ui.At(Ui.M(Ui.T((index + 1) + ".", 13, Theme.Dim, true), 10, 0, 0, 0), 1));

            var info = new List<string>();
            string desc = PluginManager.Describe(s.Path, s.Name);
            if (desc.Length > 0) info.Add(desc);
            else if (s.Vendor.Length > 0) info.Add(s.Vendor);
            info.Add(s.FormatName);
            if (s.Loaded && s.Inst.Latency > 0) info.Add($"atraso {s.Inst.Latency * 1000.0 / Tvh.SampleRate:0.#} ms");
            var name = Ui.V(Ui.T(s.Name, 13.5, s.Enabled && s.Loaded && !s.Crashed ? Theme.Text : Theme.Sub, true), Ui.T(string.Join(" · ", info), 11.5, Theme.Sub));
            if (s.Crashed) name.Children.Add(Ui.M(Ui.T("⚠ Travou e foi desligado. Clique em \"Abrir de novo\".", 11.5, Theme.Bad, true, true), 0, 2, 0, 0));
            else if (s.Error != null) name.Children.Add(Ui.M(Ui.T("⚠ " + s.Error, 11.5, Theme.Warn, false, true), 0, 2, 0, 0));
            row.Children.Add(Ui.At(name, 2));

            var btns = Ui.H();
            btns.VerticalAlignment = VerticalAlignment.Center;
            if (s.Loaded && !s.Crashed)
            {
                if (s.Inst.HasEditor)
                {
                    btns.Children.Add(Ui.Btn("Abrir", () => OpenPluginEditor(s), "AccentButton", "", "Abre a janela original do plugin, dentro de uma moldura no estilo do FL Studio"));
                    btns.Children.Add(Ui.M(Ui.Btn("Knobs", () => OpenParamsWindow(s), null, "", "Todos os parâmetros do plugin como knobs (estilo dos efeitos do FL Studio)"), 6, 0, 0, 0));
                }
                else
                    btns.Children.Add(Ui.Btn("Abrir", () => OpenParamsWindow(s), "AccentButton", "", "Abre os controles do plugin como knobs, no estilo dos efeitos do FL Studio"));
            }
            else
                btns.Children.Add(Ui.Btn("Abrir de novo", () =>
                {
                    Mouse.OverrideCursor = Cursors.Wait;
                    bool ok;
                    string err;
                    try { ok = plugins.Reload(s, out err); }
                    finally { Mouse.OverrideCursor = null; }
                    if (!ok) MessageBox.Show(this, $"'{s.Name}' não abriu:\n{err}", "TakaVox – plugins", MessageBoxButton.OK, MessageBoxImage.Warning);
                }, null, "", "Tenta abrir o plugin de novo, com os últimos ajustes salvos"));
            btns.Children.Add(IconBtn("", "Subir na cadeia (processa antes)", () => plugins.Move(s, -1), index > 0));
            btns.Children.Add(IconBtn("", "Descer na cadeia (processa depois)", () => plugins.Move(s, +1), index < count - 1));
            btns.Children.Add(IconBtn("", "Remover da cadeia", () => RemovePlugin(s)));
            row.Children.Add(Ui.At(btns, 3));

            return new Border
            {
                Background = Theme.Card2, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 10, 9), Margin = new Thickness(0, 0, 0, 8), Child = row,
            };
        }

        void RefreshLibraryList(bool force)
        {
            if (libList == null || plugins == null) return;
            string q = plugSearch?.Text.Trim() ?? "";
            string key = q + "|" + showBad + "|" + string.Join(",", plugins.Slots.Select(s => s.Key));
            if (!force && shownLibVersion == plugins.LibraryVersion && key == shownLibKey) return;
            shownLibVersion = plugins.LibraryVersion;
            shownLibKey = key;

            libList.Children.Clear();
            var usable = plugins.Library.Where(e => e.Usable).ToList();
            var shown = usable.Where(e => q.Length == 0 || e.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                          e.Vendor.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                          CategoryPt(e.Category).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            if (usable.Count == 0)
            {
                string msg = !plugins.Started ? "" : plugins.Scanning ? "Procurando…" :
                    "Nenhum efeito VST encontrado no seu PC ainda. Instale um plugin (veja as dicas no fim da página) e clique em \"Procurar plugins\".";
                if (msg.Length > 0) libList.Children.Add(Ui.T(msg, 12.5, Theme.Sub, false, true));
            }
            else if (shown.Count == 0) libList.Children.Add(Ui.T("Nada com esse nome.", 12.5, Theme.Sub));
            foreach (var e in shown.Take(300)) libList.Children.Add(LibRow(e));

            var bad = plugins.Library.Where(e => !e.Usable && !e.Hidden).ToList();
            badToggle.Visibility = bad.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            badToggle.Content = (showBad ? "Esconder" : "Mostrar") + $" os que não servem ({bad.Count}: instrumentos, 32 bits ou com erro)";
            badList.Visibility = showBad && bad.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            badList.Children.Clear();
            if (showBad)
                foreach (var e in bad.Take(200))
                {
                    string why = e.IsInstrument ? "instrumento (sintetizador): não processa voz" : e.Error;
                    var line = Ui.V(Ui.T(e.Name + "  ·  " + e.FormatName, 12.5, Theme.Sub, true), Ui.T(why + "  —  " + e.Path, 11, Theme.Dim, false, true));
                    line.Margin = new Thickness(0, 3, 0, 3);
                    badList.Children.Add(line);
                }
        }

        UIElement LibRow(PluginEntry e)
        {
            var g = Ui.Columns(Ui.Star(), Ui.Auto);
            g.Margin = new Thickness(0, 3, 0, 3);
            var info = new List<string>();
            string desc = PluginManager.Describe(e);
            if (desc.Length > 0) info.Add(desc);
            if (PluginManager.IsBundled(e.Path)) info.Add("grátis, vem com o TakaVox");
            else if (e.Vendor.Length > 0) info.Add(e.Vendor);
            info.Add(e.FormatName);
            string cat = desc.Length > 0 ? "" : CategoryPt(e.Category);
            if (cat.Length > 0) info.Add(cat);
            var label = Ui.V(Ui.T(e.Name, 13, Theme.Text, true), Ui.T(string.Join(" · ", info), 11.5, desc.Length > 0 ? Theme.AccentHi : Theme.Sub));
            label.ToolTip = e.Path;
            g.Children.Add(Ui.At(label, 0));
            bool inChain = plugins.Slots.Any(s => s.Key == e.Path.TrimEnd('\\', '/').ToLowerInvariant() + "|" + e.ClassIndex);
            var add = Ui.Btn(inChain ? "Adicionar outro" : "Adicionar", () => AddPluginFromLibrary(e), inChain ? null : "AccentButton", "",
                inChain ? "Já está na cadeia; adiciona mais uma cópia" : "Coloca este efeito no fim da cadeia e abre a janela dele");
            add.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(Ui.At(add, 1));
            return new Border { BorderBrush = Theme.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4, 0, 6), Child = g };
        }

        static string CategoryPt(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return "";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["EQ"] = "equalizador", ["Dynamics"] = "dinâmica", ["Reverb"] = "reverb", ["Delay"] = "delay", ["Distortion"] = "distorção",
                ["Modulation"] = "modulação", ["Pitch Shift"] = "tom/afinação", ["Analyzer"] = "analisador", ["Filter"] = "filtro",
                ["Restoration"] = "restauração", ["Spatial"] = "espacial", ["Surround"] = "surround", ["Tools"] = "ferramenta",
                ["Mastering"] = "masterização", ["Generator"] = "gerador", ["Network"] = "rede", ["Mono"] = "", ["Stereo"] = "", ["Fx"] = "",
            };
            var parts = cat.Split('|').Select(p => map.TryGetValue(p.Trim(), out var v) ? v : p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct();
            return string.Join(", ", parts);
        }

        void RefreshFolderList()
        {
            if (folderList == null) return;
            folderList.Children.Clear();
            foreach (var f in PluginManager.DefaultFolders())
            {
                bool exists = Directory.Exists(f);
                bool bundled = string.Equals(f, PluginManager.BundledFolder, StringComparison.OrdinalIgnoreCase);
                if (!exists && !f.EndsWith(@"Common Files\VST3", StringComparison.OrdinalIgnoreCase)) continue;
                string tag = bundled ? "efeitos grátis que vêm com o TakaVox"
                    : exists ? "padrão" : "padrão · ainda não existe (é criada quando você instala o primeiro VST3)";
                folderList.Children.Add(FolderRow(f, tag, false));
            }
            foreach (var f in plugins.UserFolders)
                folderList.Children.Add(FolderRow(f, Directory.Exists(f) ? "sua pasta" : "sua pasta · não encontrada", true));
        }

        UIElement FolderRow(string path, string tag, bool removable)
        {
            var g = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            g.Margin = new Thickness(0, 3, 0, 3);
            g.Children.Add(Ui.At(Ui.M(Ui.Icon("", 14, Theme.Sub), 0, 0, 10, 0), 0));
            g.Children.Add(Ui.At(Ui.V(Ui.T(path, 12.5, Theme.Text), Ui.T(tag, 11, Theme.Dim)), 1));
            if (removable) g.Children.Add(Ui.At(IconBtn("", "Tirar esta pasta da lista", () => { plugins.RemoveFolder(path); plugins.ScanAsync(false); }), 2));
            return g;
        }

        // ================================================================== acoes

        void PickPluginFolder()
        {
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Escolha a pasta onde estão os seus plugins VST (.vst3 ou .dll)", ShowNewFolderButton = false })
            {
                if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrEmpty(d.SelectedPath)) return;
                plugins.AddFolder(d.SelectedPath);
                plugins.ScanAsync(false);
            }
        }

        void AddPluginFromLibrary(PluginEntry e)
        {
            PluginSlot s;
            string err;
            Mouse.OverrideCursor = Cursors.Wait;
            try { s = plugins.Add(e, out err); }
            finally { Mouse.OverrideCursor = null; }
            if (s == null)
            {
                MessageBox.Show(this, $"Não deu para abrir '{e.Name}':\n{err}", "TakaVox – plugins", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (s.Inst.HasEditor) OpenPluginEditor(s);
            else OpenParamsWindow(s);
        }

        void RemovePlugin(PluginSlot s)
        {
            if (MessageBox.Show(this, $"Tirar '{s.Name}' da cadeia?\nOs ajustes que você fez nele serão perdidos.", "TakaVox – plugins",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (paramWindows.TryGetValue(s, out var w)) try { w.Close(); } catch { }
            plugins.Remove(s);
        }

        /// <summary>Janela original do plugin, numa moldura no estilo do FL Studio.</summary>
        void OpenPluginEditor(PluginSlot s) => OpenPluginWindow(s, false);

        /// <summary>Os parametros do plugin como knobs (estilo dos efeitos "Fruity" do FL Studio).</summary>
        void OpenParamsWindow(PluginSlot s) => OpenPluginWindow(s, true);

        void OpenPluginWindow(PluginSlot s, bool knobs)
        {
            if (!s.Loaded || s.Crashed) return;
            if (paramWindows.TryGetValue(s, out var existing))
            {
                // ja aberta: traz para frente (e troca entre janela original e knobs, se pediu o outro)
                if (existing.ShowingOriginal == knobs && s.Inst.HasEditor) existing.Close();
                else
                {
                    if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                    existing.Activate();
                    return;
                }
            }
            PluginWindow w;
            try { w = new PluginWindow(this, s, knobs); }
            catch (Exception ex) { Log.Write("Plugins (janela): " + ex); return; }
            paramWindows[s] = w;
            w.Closed += (o, e) =>
            {
                if (paramWindows.TryGetValue(s, out var cur) && cur == w) paramWindows.Remove(s);
                if (s.Inst != null && s.Inst.CheckCrashed())
                {
                    plugins.Chain.UpdateLatency();
                    ShowPluginNotice($"O plugin '{s.Name}' travou na janela dele e foi desligado. Os outros continuam funcionando.");
                    Log.Change($"Plugin '{s.Name}' travou na janela dele e foi desligado.");
                    RefreshPluginsPage();
                }
                plugins.SaveChain();
            };
            w.Open();
        }
    }
}
