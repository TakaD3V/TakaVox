using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TakaVox.Audio;

namespace TakaVox.UI
{
    /// <summary>
    /// Pagina "Sound pad": sons para o Discord, jogos e lives. Pads com emoji/cor/atalho, categorias, busca, arrastar e
    /// soltar arquivos (e reordenar pads), editor com trecho/volume/fade/loop/modo e atalhos globais que funcionam no jogo.
    /// </summary>
    public sealed partial class MainWindow
    {
        readonly SoundLibrary soundLib = new SoundLibrary();
        WrapPanel padGrid, padCats;
        TextBox padSearch;
        TextBlock padStatus, padEmpty, padPlaying;
        string padCat;                        // null = todas
        readonly Dictionary<int, Pad> pads = new Dictionary<int, Pad>();
        bool padImporting;
        DateTime lastSoundAt = DateTime.MinValue;
        Point padDragStart;
        Pad padDragging;
        const int HK_STOPSOUNDS = 0x1F00, HK_SOUND = 0x2000;
        public static string HotkeyStopSounds = "—";
        readonly HashSet<int> soundHotkeys = new HashSet<int>();

        sealed class Pad
        {
            public SoundItem Item;
            public Border Root, Bar;
            public bool Playing;
            public double Pos = -1;
        }

        static float DbToGain(float db) => (float)Math.Pow(10, db / 20.0);

        void InitSounds()
        {
            soundLib.Load();
            ApplySoundSettings();
            if (soundLib.Items.Count > 0) Log.Write($"Sound pad: {soundLib.Items.Count} som(ns).");
        }

        void ApplySoundSettings()
        {
            var b = engine.Sounds;
            b.ToMic = app.SoundToMic;
            b.MicGain = DbToGain(app.SoundMicDb);
            b.ToPhone = app.SoundToPhone;
            b.PhoneGain = DbToGain(app.SoundPhoneDb);
            b.WhileMuted = app.SoundWhileMuted;
            b.Exclusive = app.SoundExclusive;
            b.DuckDb = app.SoundDuckDb;
            if (!app.SoundToPhone && engine.SoundMonitor && b.Active == 0) engine.SetSoundMonitor(false);
        }

        // ================================================================== tocar
        void PlaySound(SoundItem it, bool preview = false)
        {
            var clip = soundLib.GetClip(it, out var err);
            if (clip == null)
            {
                Log.Write("Sound pad: não deu para tocar \"" + it.Name + "\": " + err);
                if (padStatus != null) { padStatus.Text = "⚠ \"" + it.Name + "\": " + err; padStatus.Foreground = Theme.Warn; }
                return;
            }
            // a saida do fone so abre quando precisa (e fecha sozinha 1 min depois do ultimo som)
            if ((preview || app.SoundToPhone) && !engine.SoundMonitor && !snapshotMode) engine.SetSoundMonitor(true);
            lastSoundAt = DateTime.Now;
            engine.Resting = false; // o som precisa da cadeia rodando
            engine.Sounds.Play(SoundLibrary.MakePlay(it, clip, preview));
        }

        void PlaySoundKey(int key)
        {
            var it = soundLib.Items.FirstOrDefault(i => i.Key == key);
            if (it != null) PlaySound(it);
        }

        public void StopAllSounds()
        {
            engine.Sounds.StopAll();
        }

        /// <summary>A cada ~0,7 s: fecha a saida do fone dos sons depois de 1 min parado e solta a memoria apos 15 min.</summary>
        void SlowTickSounds()
        {
            if (engine.Sounds.Active > 0) lastSoundAt = DateTime.Now;
            double idle = (DateTime.Now - lastSoundAt).TotalSeconds;
            if (engine.SoundMonitor && idle > 60 && !IsPlayingAny()) engine.SetSoundMonitor(false);
            if (idle > 900 && soundLib.CachedBytes > 0) soundLib.TrimMemory();
        }

        bool IsPlayingAny()
        {
            foreach (var k in engine.Sounds.SlotKey) if (k != 0) return true;
            return false;
        }

        // ================================================================== pagina
        FrameworkElement BuildSoundsPage()
        {
            var root = new StackPanel();

            // ---------------------------------------------------------------- controles gerais
            var stopBtn = Ui.Btn("Parar todos os sons", StopAllSounds, null, "", "Para tudo na hora (atalho global: " + HotkeyStopSounds + ").");
            padPlaying = Ui.T("", 12.5, Theme.Sub, false, true);
            var stopRow = Ui.Columns(Ui.Auto, Ui.Star());
            stopRow.Children.Add(Ui.At(stopBtn, 0));
            stopRow.Children.Add(Ui.At(Ui.M(padPlaying, 14, 0, 0, 0), 1));
            var stopHint = Ui.T("", 12, Theme.Sub, false, true);
            binders.Add(new ActionBinder(() => stopHint.Text = "Atalho para parar tudo: " + HotkeyStopSounds + ". Cada som pode ter o seu atalho (botão direito › Editar)."));

            var cols = Ui.Columns(Ui.Star(), Ui.Px(24), Ui.Star());
            var left = Ui.V(
                Switch("Tocar para quem te escuta (no microfone)", "Os sons vão junto com a sua voz para o Discord, os jogos e a live.",
                    () => app.SoundToMic, v => { app.SoundToMic = v; ApplySoundSettings(); }),
                SliderRow("Volume para os outros", "Volume geral dos sons no microfone.", -30, 6, 0.5,
                    () => app.SoundMicDb, v => { app.SoundMicDb = (float)v; ApplySoundSettings(); }, v => Db(v, true)),
                Switch("Ouvir no meu fone", "Você também escuta os sons (no fone/saída do retorno).",
                    () => app.SoundToPhone, v => { app.SoundToPhone = v; ApplySoundSettings(); }),
                SliderRow("Volume no meu fone", "Volume dos sons só para você.", -30, 6, 0.5,
                    () => app.SoundPhoneDb, v => { app.SoundPhoneDb = (float)v; ApplySoundSettings(); }, v => Db(v, true)));
            var right = Ui.V(
                Switch("Abaixar minha voz enquanto um som toca", "Ducking: a sua voz fica mais baixa durante os sons (bom para música e vinhetas).",
                    () => app.SoundDuckDb < -0.1f, v => { app.SoundDuckDb = v ? -12f : 0f; ApplySoundSettings(); }),
                SliderRow("Quanto abaixar", "Quanto a sua voz abaixa enquanto um som toca.", -30, -3, 1,
                    () => app.SoundDuckDb < -0.1f ? app.SoundDuckDb : -12f, v => { app.SoundDuckDb = (float)v; ApplySoundSettings(); }, v => Db(v, false)),
                Switch("Sons saem mesmo com o microfone no mudo", "Mudo cala só a sua voz; os sons continuam saindo.",
                    () => app.SoundWhileMuted, v => { app.SoundWhileMuted = v; ApplySoundSettings(); }),
                Switch("Um som por vez", "Tocar um som para o que estiver tocando.",
                    () => app.SoundExclusive, v => { app.SoundExclusive = v; ApplySoundSettings(); }));
            cols.Children.Add(Ui.At(left, 0));
            cols.Children.Add(Ui.At(right, 2));
            root.Children.Add(Ui.Card(Ui.V(
                Ui.T("Sound pad", 17, Theme.Text, true),
                Ui.M(Ui.T("Toque memes, vinhetas e músicas para quem está te ouvindo no Discord, nos jogos e na live, misturados com a sua voz. " +
                          "Os sons ficam prontos na memória: tocar não pesa no jogo.", 12.5, Theme.Sub, false, true), 0, 4, 0, 0),
                Ui.M(cols, 0, 12, 0, 0),
                Ui.M(stopRow, 0, 12, 0, 0),
                Ui.M(stopHint, 0, 8, 0, 0))));

            // ---------------------------------------------------------------- biblioteca
            var add = Ui.Btn("Adicionar sons…", AddSoundsDialog, "AccentButton", "", "MP3, WAV, M4A, AAC, WMA, FLAC, OGG…");
            var newCat = Ui.Btn("Nova categoria", NewCategory, null, "", "Separe os sons em abas (Memes, Música, Live…).");
            var autoKeys = Ui.Btn("Teclas automáticas", AutoKeys, null, "", "Coloca Num 1, Num 2… (teclado numérico) nos sons sem tecla desta aba.");
            var folder = Ui.Btn("Abrir a pasta", () =>
            {
                try { Directory.CreateDirectory(SoundLibrary.Dir); Process.Start("explorer.exe", "\"" + SoundLibrary.Dir + "\""); } catch { }
            }, null, "", "Onde o TakaVox guarda os sons.");
            padSearch = new TextBox { Width = 220, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Buscar som pelo nome" };
            var hint = Ui.T("Buscar…", 13, Theme.Dim);
            hint.IsHitTestVisible = false;
            hint.Margin = new Thickness(8, 0, 0, 0);
            var searchBox = new Grid();
            searchBox.Children.Add(padSearch);
            searchBox.Children.Add(hint);
            padSearch.TextChanged += (s, e) => { hint.Visibility = padSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RebuildPads(); };
            var tools = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto);
            tools.Children.Add(Ui.At(add, 0));
            tools.Children.Add(Ui.At(Ui.M(autoKeys, 10, 0, 0, 0), 1));
            tools.Children.Add(Ui.At(Ui.M(newCat, 10, 0, 0, 0), 2));
            tools.Children.Add(Ui.At(Ui.M(folder, 10, 0, 0, 0), 3));
            tools.Children.Add(Ui.At(searchBox, 5));

            padCats = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            padGrid = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            padEmpty = Ui.T("Arraste arquivos de áudio para cá (MP3, WAV, M4A, OGG, FLAC…) ou clique em \"Adicionar sons\". " +
                            "Clique num som para tocar; clique no ⌨ do som para escolher a tecla que toca ele (até dentro do jogo); botão direito para editar.", 13, Theme.Dim, false, true);
            padEmpty.Margin = new Thickness(0, 18, 0, 18);
            padStatus = Ui.T("", 12.5, Theme.Sub, false, true);
            var libCard = Ui.Card(Ui.V(tools, padCats, padEmpty, padGrid, Ui.M(padStatus, 0, 4, 0, 0)));
            libCard.AllowDrop = true;
            libCard.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : e.Data.GetDataPresent("TakaVoxPad") ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            libCard.Drop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportSounds(ExpandFiles(files));
            };
            root.Children.Add(libCard);

            root.Children.Add(Ui.Card(Ui.V(
                Ui.T("Dicas", 15, Theme.Text, true),
                Ui.M(Ui.T("• No Discord, os sons chegam como a sua voz: deixe a Supressão de ruído (Krisp) DESLIGADA, senão ele corta música e efeitos.\n" +
                          "• Os sons ficam com o mesmo volume entre si (normalização por LUFS). Ajuste fino em Editar › Volume.\n" +
                          "• O silêncio do começo de cada arquivo é tirado sozinho: o som sai na hora que você aperta.\n" +
                          "• Para live (OBS): use o \"CABLE Output\" como microfone no OBS. Os sons vão junto com a sua voz.", 12.5, Theme.Sub, false, true), 0, 6, 0, 0))));

            RebuildPads();
            return root;
        }

        IEnumerable<string> ExpandFiles(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    IEnumerable<string> inner;
                    try { inner = Directory.GetFiles(p, "*.*", SearchOption.AllDirectories).Where(SoundLibrary.IsAudioFile).Take(500).ToList(); }
                    catch { continue; }
                    foreach (var f in inner) yield return f;
                }
                else yield return p;
            }
        }

        // ================================================================== pads
        /// <summary>Os sons que aparecem agora (aba e busca), na ordem da tela.</summary>
        List<SoundItem> VisibleSounds()
        {
            string q = Fold(padSearch?.Text ?? "");
            return soundLib.Items.Where(it => (padCat == null || (it.Category ?? "") == padCat) && (q.Length == 0 || Fold(it.Name).Contains(q))).ToList();
        }

        void RebuildPads()
        {
            if (padGrid == null) return;
            padGrid.Children.Clear();
            pads.Clear();
            // categorias
            padCats.Children.Clear();
            int total = soundLib.Items.Count;
            padCats.Children.Add(CatChip(null, "Todos", total));
            foreach (var c in soundLib.Categories) padCats.Children.Add(CatChip(c, c, soundLib.Items.Count(i => i.Category == c)));
            if (soundLib.Items.Any(i => string.IsNullOrEmpty(i.Category)) && soundLib.Categories.Count > 0)
                padCats.Children.Add(CatChip("", "Sem categoria", soundLib.Items.Count(i => string.IsNullOrEmpty(i.Category))));
            padCats.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var it in VisibleSounds())
            {
                var p = MakePad(it);
                pads[it.Key] = p;
                padGrid.Children.Add(p.Root);
            }
            padEmpty.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (padStatus != null && !padImporting)
            {
                long mb = soundLib.CachedBytes >> 20;
                padStatus.Text = total == 0 ? "" : $"{total} som(ns)" + (pads.Count != total ? $" · mostrando {pads.Count}" : "") + " · clique para tocar · botão direito: editar";
                padStatus.Foreground = Theme.Sub;
            }
        }

        static string Fold(string s)
        {
            var d = (s ?? "").ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(d.Length);
            foreach (char c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString();
        }

        Border CatChip(string cat, string label, int count)
        {
            bool sel = padCat == cat;
            var b = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Background = sel ? Theme.Accent : Theme.Card2, BorderBrush = sel ? Theme.Accent : Theme.Border, BorderThickness = new Thickness(1),
                Child = Ui.T(label + "  " + count, 12.5, sel ? Theme.OnAccent : Theme.Text, sel),
            };
            b.MouseLeftButtonUp += (s, e) => { padCat = cat; RebuildPads(); };
            b.AllowDrop = cat != null;
            b.DragOver += (s, e) => { e.Effects = cat != null && e.Data.GetDataPresent("TakaVoxPad") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            b.Drop += (s, e) =>
            {
                if (cat == null || !(e.Data.GetData("TakaVoxPad") is SoundItem it)) return;
                it.Category = cat;
                soundLib.Save();
                RebuildPads();
                e.Handled = true;
            };
            if (!string.IsNullOrEmpty(cat))
            {
                var menu = new ContextMenu();
                var ren = new MenuItem { Header = "Renomear categoria…" };
                ren.Click += (s, e) =>
                {
                    string n = Prompt("Renomear categoria", "Novo nome:", cat);
                    if (string.IsNullOrWhiteSpace(n) || n == cat) return;
                    n = n.Trim();
                    int i = soundLib.Categories.IndexOf(cat);
                    if (i >= 0) soundLib.Categories[i] = n;
                    foreach (var it in soundLib.Items) if (it.Category == cat) it.Category = n;
                    soundLib.Categories.RemoveAll(c => c == cat);
                    if (!soundLib.Categories.Contains(n)) soundLib.Categories.Insert(Math.Max(0, Math.Min(i, soundLib.Categories.Count)), n);
                    padCat = n;
                    soundLib.Save();
                    RebuildPads();
                };
                var del = new MenuItem { Header = "Excluir categoria (os sons ficam)" };
                del.Click += (s, e) =>
                {
                    soundLib.Categories.Remove(cat);
                    foreach (var it in soundLib.Items) if (it.Category == cat) it.Category = "";
                    padCat = null;
                    soundLib.Save();
                    RebuildPads();
                };
                menu.Items.Add(ren);
                menu.Items.Add(del);
                b.ContextMenu = menu;
            }
            return b;
        }

        Pad MakePad(SoundItem it)
        {
            var color = SoundEditor.ParseColor(it.Color);
            var colorBrush = new SolidColorBrush(color); colorBrush.Freeze();
            var tint = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)); tint.Freeze();
            var emoji = new TextBlock { Text = string.IsNullOrEmpty(it.Emoji) ? "🔊" : it.Emoji, FontFamily = EmojiFont, FontSize = 22, VerticalAlignment = VerticalAlignment.Top };
            var name = Ui.T(it.Name, 13, Theme.Text, true, true);
            name.MaxHeight = 36;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Top;
            name.Margin = new Thickness(8, 1, 0, 0);
            var top = Ui.Columns(Ui.Auto, Ui.Star());
            top.Children.Add(Ui.At(emoji, 0));
            top.Children.Add(Ui.At(name, 1));
            string infoText = it.Problem != null ? "⚠ " + it.Problem : Len(it.PlayLength) + (it.Loop ? " · loop" : "") + (it.Mode == 1 ? " · liga/desliga" : it.Mode == 2 ? " · sobrepõe" : "");
            var info = Ui.T(infoText, 11.5, it.Problem != null ? Theme.Warn : Theme.Sub);
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(Ui.At(info, 0));
            bool hasKey = !string.IsNullOrEmpty(it.Hotkey);
            var hk = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 1), Cursor = Cursors.Hand,
                Background = hasKey ? (Brush)tint : Brushes.Transparent, BorderBrush = hasKey ? Brushes.Transparent : Theme.Border, BorderThickness = new Thickness(1),
                Child = Ui.T(hasKey ? "⌨ " + HotkeyText.Display(it.Hotkey) : "⌨ tecla", 11, hasKey ? (it.HotkeyProblem != null ? Theme.Warn : Theme.Text) : Theme.Dim, hasKey),
                ToolTip = hasKey ? (it.HotkeyProblem != null ? "A tecla não funciona: " + it.HotkeyProblem + ". Clique para trocar." : "Tecla deste som (funciona até dentro do jogo). Clique para trocar.")
                                 : "Escolher uma tecla para tocar este som (funciona até dentro do jogo).",
            };
            hk.MouseLeftButtonDown += (s, e) => e.Handled = true; // nao toca o som
            hk.MouseLeftButtonUp += (s, e) => { e.Handled = true; padDragging = null; AssignKey(it); };
            bottom.Children.Add(Ui.At(hk, 1));
            bottom.VerticalAlignment = VerticalAlignment.Bottom;
            var content = new Grid { Margin = new Thickness(14, 10, 10, 10) };
            content.Children.Add(top);
            content.Children.Add(bottom);
            var strip = new Border { Width = 4, HorizontalAlignment = HorizontalAlignment.Left, Background = colorBrush, CornerRadius = new CornerRadius(9, 0, 0, 9) };
            var bar = new Border { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Width = 0, Background = colorBrush, Margin = new Thickness(4, 0, 0, 1), CornerRadius = new CornerRadius(1.5) };
            var grid = new Grid();
            grid.Children.Add(strip);
            grid.Children.Add(content);
            grid.Children.Add(bar);
            var b = new Border
            {
                Width = 184, Height = 92, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10),
                Background = Theme.Card2, BorderBrush = Theme.Border, BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, Child = grid,
                ToolTip = it.Name + (string.IsNullOrEmpty(it.Category) ? "" : "\nCategoria: " + it.Category) +
                          $"\nDuração: {Len(it.PlayLength)} · volume medido {(it.Lufs > -70 ? it.Lufs.ToString("0") + " LUFS" : "—")}" +
                          (string.IsNullOrEmpty(it.Hotkey) ? "" : "\nAtalho: " + HotkeyText.Display(it.Hotkey) + (it.HotkeyProblem != null ? " (" + it.HotkeyProblem + ")" : "")) +
                          "\nClique: tocar · Botão direito: editar · Arraste para reordenar",
            };
            var pad = new Pad { Item = it, Root = b, Bar = bar };
            b.MouseLeftButtonDown += (s, e) => { padDragStart = e.GetPosition(padGrid); padDragging = pad; };
            b.MouseMove += (s, e) =>
            {
                if (padDragging != pad || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(padGrid);
                if (Math.Abs(p.X - padDragStart.X) < 8 && Math.Abs(p.Y - padDragStart.Y) < 8) return;
                padDragging = null;
                try { DragDrop.DoDragDrop(b, new DataObject("TakaVoxPad", it), DragDropEffects.Move); } catch { }
            };
            b.MouseLeftButtonUp += (s, e) =>
            {
                if (padDragging != pad) return;
                padDragging = null;
                PlaySound(it);
            };
            b.AllowDrop = true;
            b.DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent("TakaVoxPad") || e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            b.Drop += (s, e) =>
            {
                if (e.Data.GetData("TakaVoxPad") is SoundItem moving && moving != it)
                {
                    soundLib.Items.Remove(moving);
                    int at = soundLib.Items.IndexOf(it);
                    bool after = e.GetPosition(b).X > b.ActualWidth / 2;
                    soundLib.Items.Insert(Math.Max(0, Math.Min(soundLib.Items.Count, at + (after ? 1 : 0))), moving);
                    if (padCat != null) moving.Category = padCat;
                    soundLib.Save();
                    RebuildPads();
                    e.Handled = true;
                }
            };
            b.MouseEnter += (s, e) => { if (!pad.Playing) b.BorderBrush = Theme.AccentHi; };
            b.MouseLeave += (s, e) => { if (!pad.Playing) b.BorderBrush = Theme.Border; };
            b.ContextMenu = PadMenu(it);
            return pad;
        }

        static string Len(double s) => s < 60 ? s.ToString("0.0", CultureInfo.CurrentCulture) + " s" : TimeSpan.FromSeconds(s).ToString(@"m\:ss");

        ContextMenu PadMenu(SoundItem it)
        {
            var m = new ContextMenu();
            var play = new MenuItem { Header = "Tocar" };
            play.Click += (s, e) => PlaySound(it);
            var prev = new MenuItem { Header = "Ouvir só eu (prévia)" };
            prev.Click += (s, e) => PlaySound(it, true);
            var edit = new MenuItem { Header = "Editar… (trecho, volume, fade, loop)" };
            edit.Click += (s, e) => EditSound(it);
            var key = new MenuItem { Header = string.IsNullOrEmpty(it.Hotkey) ? "Definir tecla…" : "Trocar a tecla (" + HotkeyText.Display(it.Hotkey) + ")…" };
            key.Click += (s, e) => AssignKey(it);
            var unkey = new MenuItem { Header = "Tirar a tecla", Visibility = string.IsNullOrEmpty(it.Hotkey) ? Visibility.Collapsed : Visibility.Visible };
            unkey.Click += (s, e) => { it.Hotkey = ""; soundLib.Save(); RegisterSoundHotkeys(); RebuildPads(); };
            var move = new MenuItem { Header = "Mover para" };
            var none = new MenuItem { Header = "Sem categoria", IsChecked = string.IsNullOrEmpty(it.Category) };
            none.Click += (s, e) => { it.Category = ""; soundLib.Save(); RebuildPads(); };
            move.Items.Add(none);
            foreach (var c in soundLib.Categories)
            {
                string cc = c;
                var mi = new MenuItem { Header = c, IsChecked = it.Category == c };
                mi.Click += (s, e) => { it.Category = cc; soundLib.Save(); RebuildPads(); };
                move.Items.Add(mi);
            }
            var del = new MenuItem { Header = "Excluir" };
            del.Click += (s, e) =>
            {
                if (MessageBox.Show("Excluir o som \"" + it.Name + "\"?", "TakaVox", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                DeleteSound(it);
            };
            m.Items.Add(play);
            m.Items.Add(prev);
            m.Items.Add(new Separator());
            m.Items.Add(edit);
            m.Items.Add(key);
            m.Items.Add(unkey);
            m.Items.Add(move);
            m.Items.Add(new Separator());
            m.Items.Add(del);
            return m;
        }

        void DeleteSound(SoundItem it)
        {
            engine.Sounds.Stop(it.Key);
            engine.Sounds.Stop(-it.Key);
            soundLib.Remove(it);
            soundLib.Save();
            RegisterSoundHotkeys();
            RebuildPads();
            Log.Write("Sound pad: excluído \"" + it.Name + "\".");
        }

        void EditSound(SoundItem it)
        {
            var ed = new SoundEditor(this, it, soundLib, engine.Sounds, soundLib.Categories, CheckSoundHotkey);
            SuspendSoundHotkeys(); // senao apertar uma tecla de outro som toca ele em vez de gravar
            try { ed.ShowDialog(); }
            finally { RegisterSoundHotkeys(); }
            if (ed.Deleted) { DeleteSound(it); return; }
            if (!ed.Saved) return;
            if (!string.IsNullOrEmpty(it.Category) && !soundLib.Categories.Contains(it.Category)) soundLib.Categories.Add(it.Category);
            soundLib.Save();
            RegisterSoundHotkeys();
            RebuildPads();
        }

        /// <summary>Anima os pads tocando (so com a pagina aberta).</summary>
        void FastTickSounds()
        {
            if (padPlaying == null) return;
            var slots = engine.Sounds.SlotKey;
            var pos = engine.Sounds.SlotPos;
            int playing = 0;
            string names = "";
            foreach (var p in pads.Values)
            {
                double best = -1;
                for (int i = 0; i < slots.Length; i++) if (slots[i] == p.Item.Key) best = Math.Max(best, pos[i]);
                bool on = best >= 0;
                if (on) { playing++; if (names.Length < 80) names += (names.Length > 0 ? ", " : "") + p.Item.Name; }
                if (on != p.Playing)
                {
                    p.Playing = on;
                    p.Root.BorderBrush = on ? new SolidColorBrush(SoundEditor.ParseColor(p.Item.Color)) : Theme.Border;
                }
                double w = on ? Math.Max(0, (p.Root.ActualWidth - 8) * best) : 0;
                if (Math.Abs(w - p.Bar.Width) > 0.5) p.Bar.Width = w;
            }
            string t = playing == 0 ? "" : "▶ Tocando: " + names;
            if (padPlaying.Text != t) padPlaying.Text = t;
        }

        // ================================================================== importar
        void AddSoundsDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Adicionar sons ao sound pad", Multiselect = true,
                Filter = "Áudio|" + string.Join(";", SoundLibrary.Extensions.Select(e => "*" + e)) + "|Todos os arquivos|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            ImportSounds(dlg.FileNames);
        }

        void ImportSounds(IEnumerable<string> files)
        {
            if (padImporting) { MessageBox.Show("Espere terminar a importação atual.", "TakaVox"); return; }
            var list = files.Where(f => File.Exists(f)).Distinct().ToList();
            var audio = list.Where(SoundLibrary.IsAudioFile).ToList();
            if (audio.Count == 0)
            {
                if (list.Count > 0) MessageBox.Show("Esses arquivos não são de áudio. Use MP3, WAV, M4A, AAC, WMA, FLAC ou OGG.", "TakaVox", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            padImporting = true;
            string cat = padCat ?? "";
            padStatus.Foreground = Theme.Sub;
            padStatus.Text = $"Importando 1 de {audio.Count}…";
            var t = new Thread(() =>
            {
                var done = new List<SoundItem>();
                var errors = new List<string>();
                for (int i = 0; i < audio.Count; i++)
                {
                    int n = i + 1;
                    Dispatcher.BeginInvoke(new Action(() => padStatus.Text = $"Importando {n} de {audio.Count}: {Path.GetFileName(audio[n - 1])}…"));
                    var it = SoundLibrary.ImportFile(audio[i], cat, out var err);
                    if (it != null) done.Add(it);
                    else errors.Add(Path.GetFileName(audio[i]) + ": " + err);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var it in done)
                    {
                        it.Key = soundLib.NextKey();
                        soundLib.Items.Add(it);
                    }
                    if (cat.Length > 0 && !soundLib.Categories.Contains(cat)) soundLib.Categories.Add(cat);
                    soundLib.Save();
                    padImporting = false;
                    RebuildPads();
                    RegisterSoundHotkeys();
                    padStatus.Text = $"✓ {done.Count} som(ns) adicionado(s)." + (errors.Count > 0 ? $" {errors.Count} com problema." : "");
                    padStatus.Foreground = errors.Count > 0 ? Theme.Warn : Theme.Good;
                    Log.Write($"Sound pad: {done.Count} som(ns) importado(s)" + (errors.Count > 0 ? "; problemas: " + string.Join(" | ", errors) : "."));
                    if (errors.Count > 0)
                        MessageBox.Show("Alguns arquivos não entraram:\n\n" + string.Join("\n", errors.Take(12)) + (errors.Count > 12 ? "\n…" : ""),
                            "TakaVox", MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "TakaVox importando sons" };
            t.Start();
        }

        void NewCategory()
        {
            string n = Prompt("Nova categoria", "Nome da categoria (ex.: Memes, Música, Live):", "");
            if (string.IsNullOrWhiteSpace(n)) return;
            n = n.Trim();
            if (!soundLib.Categories.Contains(n)) soundLib.Categories.Add(n);
            padCat = n;
            soundLib.Save();
            RebuildPads();
        }

        /// <summary>Pergunta um texto curto (janela pequena no tema do app).</summary>
        string Prompt(string title, string label, string initial)
        {
            var w = new Window
            {
                Title = title, Owner = this, Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = Theme.Bg, Foreground = Theme.Text, FontFamily = Theme.Ui,
            };
            var box = new TextBox { Text = initial ?? "", MaxLength = 40, Margin = new Thickness(0, 8, 0, 0) };
            string result = null;
            var ok = Ui.Btn("OK", () => { result = box.Text; w.Close(); }, "AccentButton");
            ok.IsDefault = true;
            var cancel = Ui.Btn("Cancelar", w.Close);
            cancel.IsCancel = true;
            var btns = Ui.H(ok, Ui.M(cancel, 8, 0, 0, 0));
            btns.HorizontalAlignment = HorizontalAlignment.Right;
            btns.Margin = new Thickness(0, 14, 0, 0);
            w.Content = Ui.V(Ui.T(label, 13, Theme.Text, false, true), box, btns);
            ((FrameworkElement)w.Content).Margin = new Thickness(18);
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            w.ShowDialog();
            return result;
        }

        // ================================================================== teclas dos sons
        /// <summary>"Aperte a tecla": escolhe a tecla que toca o som (se outro som usava, ela troca de dono).</summary>
        void AssignKey(SoundItem it)
        {
            var dlg = new KeyCaptureDialog(this, it.Name, it.Hotkey, hk => CheckKeyFor(hk, it.Key));
            SuspendSoundHotkeys();
            try { dlg.ShowDialog(); }
            finally { RegisterSoundHotkeys(); }
            if (dlg.Result == null) return;
            string hk2 = dlg.Result;
            if (hk2.Length > 0)
                foreach (var o in soundLib.Items)
                    if (o != it && string.Equals(o.Hotkey, hk2, StringComparison.OrdinalIgnoreCase)) o.Hotkey = "";
            it.Hotkey = hk2;
            soundLib.Save();
            RegisterSoundHotkeys();
            RebuildPads();
            Log.Write("Sound pad: tecla de \"" + it.Name + "\" = " + (hk2.Length > 0 ? HotkeyText.Display(hk2) : "nenhuma") +
                      (it.HotkeyProblem != null ? " (" + it.HotkeyProblem + ")" : ""));
            if (padStatus != null)
            {
                padStatus.Text = hk2.Length == 0 ? "\"" + it.Name + "\" ficou sem tecla." :
                    it.HotkeyProblem != null ? "⚠ A tecla de \"" + it.Name + "\" não funciona: " + it.HotkeyProblem :
                    "✓ " + HotkeyText.Display(hk2) + " toca \"" + it.Name + "\" (até dentro do jogo).";
                padStatus.Foreground = it.HotkeyProblem != null ? Theme.Warn : Theme.Good;
            }
        }

        /// <summary>Coloca as teclas do teclado numerico (Num 1..9, Num 0) nos sons sem tecla da aba atual.</summary>
        void AutoKeys()
        {
            var keys = new[] { "NumPad1", "NumPad2", "NumPad3", "NumPad4", "NumPad5", "NumPad6", "NumPad7", "NumPad8", "NumPad9", "NumPad0" };
            var used = new HashSet<string>(soundLib.Items.Select(i => i.Hotkey).Where(h => !string.IsNullOrEmpty(h)), StringComparer.OrdinalIgnoreCase);
            var free = keys.Where(k => !used.Contains(k)).ToList();
            var targets = VisibleSounds().Where(i => string.IsNullOrEmpty(i.Hotkey)).ToList();
            if (soundLib.Items.Count == 0) { MessageBox.Show("Adicione alguns sons primeiro.", "TakaVox"); return; }
            if (targets.Count == 0) { MessageBox.Show("Todos os sons desta aba já têm tecla.", "TakaVox", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (free.Count == 0) { MessageBox.Show("As teclas Num 0 a Num 9 já estão todas em uso.", "TakaVox", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            int n = Math.Min(targets.Count, free.Count);
            var lines = Enumerable.Range(0, n).Select(i => HotkeyText.Display(free[i]) + "  →  " + targets[i].Name);
            if (MessageBox.Show("Colocar estas teclas do teclado numérico?\n\n" + string.Join("\n", lines) +
                                "\n\nFuncionam com o Num Lock ligado, até dentro do jogo. Dá para trocar depois clicando no ⌨ do som.",
                                "Teclas automáticas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            for (int i = 0; i < n; i++) targets[i].Hotkey = free[i];
            soundLib.Save();
            RegisterSoundHotkeys();
            RebuildPads();
            int bad = targets.Take(n).Count(t => t.HotkeyProblem != null);
            padStatus.Text = $"✓ {n - bad} som(ns) ganharam tecla do teclado numérico (Num Lock ligado)." + (bad > 0 ? $" {bad} tecla(s) já eram usadas por outro programa." : "");
            padStatus.Foreground = bad > 0 ? Theme.Warn : Theme.Good;
            Log.Write($"Sound pad: teclas automáticas em {n} som(ns).");
        }

        /// <summary>A tecla serve para este som? (outro som pode ceder a tecla; outro programa ou atalho do TakaVox nao)</summary>
        KeyCheck CheckKeyFor(string hotkey, int key)
        {
            var r = new KeyCheck();
            if (!HotkeyText.Parse(hotkey, out uint mods, out uint vk, out _)) { r.Message = "Tecla inválida."; return r; }
            string disp = HotkeyText.Display(hotkey);
            foreach (var used in new[] { HotkeyMute, HotkeyTune, HotkeyBypass, HotkeyVoice, HotkeyStopSounds })
                if (string.Equals(used, disp, StringComparison.OrdinalIgnoreCase)) { r.Message = "Essa já é uma tecla do TakaVox (" + used + "). Escolha outra."; return r; }
            var other = soundLib.Items.FirstOrDefault(i => i.Key != key && string.Equals(i.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase));
            if (other != null)
            {
                r.Ok = true;
                r.TakenFrom = other.Name;
                r.Message = "Essa tecla é do som “" + other.Name + "”. Salvar passa ela para este som.";
                return r;
            }
            if (hwndSource != null && !snapshotMode)
            {
                const int probe = 0x1F01;
                if (!RegisterHotKey(hwndSource.Handle, probe, mods | HotkeyText.MOD_NOREPEAT, vk))
                {
                    r.Message = "Outro programa já usa essa tecla. Escolha outra.";
                    return r;
                }
                UnregisterHotKey(hwndSource.Handle, probe);
            }
            r.Ok = true;
            r.Message = "✓ Livre. Aperte Enter ou clique em Salvar.";
            return r;
        }

        /// <summary>Solta as teclas dos sons (enquanto o usuario grava uma tecla nova).</summary>
        void SuspendSoundHotkeys()
        {
            if (hwndSource == null) return;
            foreach (var id in soundHotkeys) UnregisterHotKey(hwndSource.Handle, id);
            soundHotkeys.Clear();
        }

        // ================================================================== atalhos
        /// <summary>Registra os atalhos globais dos sons (sem repetir, sem brigar com outros programas).</summary>
        void RegisterSoundHotkeys()
        {
            if (snapshotMode || hwndSource == null) return;
            IntPtr hwnd = hwndSource.Handle;
            foreach (var id in soundHotkeys) UnregisterHotKey(hwnd, id);
            soundHotkeys.Clear();
            var seen = new Dictionary<string, SoundItem>(StringComparer.OrdinalIgnoreCase);
            int ok = 0;
            foreach (var it in soundLib.Items)
            {
                it.HotkeyProblem = null;
                if (string.IsNullOrEmpty(it.Hotkey)) continue;
                if (!HotkeyText.Parse(it.Hotkey, out uint mods, out uint vk, out _)) { it.HotkeyProblem = "atalho inválido"; continue; }
                if (seen.TryGetValue(it.Hotkey, out var other)) { it.HotkeyProblem = "o mesmo de \"" + other.Name + "\""; continue; }
                int id = HK_SOUND + it.Key;
                if (id > 0xBFFF) { it.HotkeyProblem = "sons demais com atalho"; continue; }
                if (RegisterHotKey(hwnd, id, mods | HotkeyText.MOD_NOREPEAT, vk)) { soundHotkeys.Add(id); seen[it.Hotkey] = it; ok++; }
                else it.HotkeyProblem = "ocupado por outro programa (ou por um atalho do TakaVox)";
            }
            if (ok > 0) Log.Write($"Sound pad: {ok} atalho(s) de som ativo(s).");
        }

        /// <summary>Para o editor: o atalho serve? (null = ok, senao o motivo)</summary>
        string CheckSoundHotkey(string hotkey, int key)
        {
            if (!HotkeyText.Parse(hotkey, out uint mods, out uint vk, out _)) return "Use Ctrl, Alt ou Shift junto com a tecla (ou uma tecla do teclado numérico).";
            var other = soundLib.Items.FirstOrDefault(i => i.Key != key && string.Equals(i.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase));
            if (other != null) return "O som \"" + other.Name + "\" já usa esse atalho.";
            string disp = HotkeyText.Display(hotkey);
            foreach (var used in new[] { HotkeyMute, HotkeyTune, HotkeyBypass, HotkeyVoice, HotkeyStopSounds })
                if (string.Equals(used, disp, StringComparison.OrdinalIgnoreCase)) return "Esse atalho já é do TakaVox (" + used + ").";
            if (hwndSource == null || snapshotMode) return null;
            var mine = soundLib.Items.FirstOrDefault(i => i.Key == key);
            if (mine != null && string.Equals(mine.Hotkey, hotkey, StringComparison.OrdinalIgnoreCase) && soundHotkeys.Contains(HK_SOUND + key)) return null;
            const int probe = 0x1F01;
            if (!RegisterHotKey(hwndSource.Handle, probe, mods | HotkeyText.MOD_NOREPEAT, vk)) return "Outro programa já usa esse atalho. Escolha outro.";
            UnregisterHotKey(hwndSource.Handle, probe);
            return null;
        }
    }
}
