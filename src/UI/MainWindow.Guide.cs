using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TakaVox.UI
{
    public sealed partial class MainWindow
    {
        StackPanel historyList;
        TextBlock aboutText;

        FrameworkElement BuildGuidePage()
        {
            var cols = Ui.Columns(Ui.Star(), Ui.Px(14), Ui.Star());
            var left = new StackPanel();
            var right = new StackPanel();
            cols.Children.Add(Ui.At(left, 0));
            cols.Children.Add(Ui.At(right, 2));

            left.Children.Add(Steps("Discord", "",
                "Configurações (engrenagem) › Voz e vídeo.",
                "Dispositivo de entrada: \"Padrão\" (ou \"CABLE Output (VB-Audio Virtual Cable)\"). Volume de entrada: 100%.",
                "Sensibilidade de entrada: deixe \"Determinar automaticamente\" ligado.",
                "Supressão de ruído: \"Nenhuma\" – o TakaVox já limpa com IA. (Se quiser uma camada extra contra teclado, teste o Krisp.)",
                "Controle automático de ganho: DESLIGADO (o TakaVox já nivela; o do Discord deixa a voz \"bombeando\").",
                "Cancelamento de eco: desligado se você usa fone; ligado só se usa caixa de som.",
                "Clique em \"Vamos verificar\" para se ouvir."));

            left.Children.Add(Steps("Valorant", "",
                "Configurações › Áudio › Chat de voz.",
                "Dispositivo de entrada: \"Padrão\" ou \"CABLE Output\". Volume de entrada: 100.",
                "Se usar \"falar para conversar\" (push-to-talk) tudo certo; o portão do TakaVox já corta o teclado.",
                "Feche e abra o jogo depois de instalar o microfone virtual."));

            left.Children.Add(Steps("Outros jogos, OBS e gravação", "",
                "Jogos (CS2, Fortnite, Roblox, GTA…) usam o microfone padrão do Windows – que agora é o TakaVox.",
                "Se o jogo tiver lista de microfones, escolha \"CABLE Output\".",
                "OBS: \"Captura de entrada de áudio\" › CABLE Output (não precisa de filtro de ruído no OBS).",
                "Se o TakaVox estiver fechado, o Windows volta para o seu mic normal sozinho (opção em Configurar)."));

            left.Children.Add(Steps("Dicas de microfone (fazem MUITA diferença)", "",
                "Distância: um palmo (10–15 cm) da boca.",
                "Fale um pouco de lado do mic para não estourar o \"P\" e o \"B\" (ou use espuma/pop filter).",
                "Deixe o mic longe do teclado e do PC, em cima de algo macio (evita batidas na mesa).",
                "Mudou o mic de lugar ou de posição? Refaça a calibração em \"Testar voz\"."));

            historyList = new StackPanel();
            foreach (var line in Log.ReadHistory()) AddHistoryLine(line, false);
            if (historyList.Children.Count == 0) historyList.Children.Add(Ui.T("Nada ainda.", 12.5, Theme.Dim));
            var histScroll = new ScrollViewer { Content = historyList, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            right.Children.Add(Ui.Card(Ui.V(
                Ui.SectionTitle("Histórico – tudo que o TakaVox mudou no seu PC", "Do mais novo para o mais antigo. Dá para desfazer em Configurar › Restaurar."),
                histScroll)));

            aboutText = Ui.T("", 12.5, Theme.Sub, false, true);
            binders.Add(new ActionBinder(UpdateAbout));
            right.Children.Add(Ui.Card(Ui.V(Ui.SectionTitle("Por dentro do TakaVox"), ChainDiagram(), Ui.M(aboutText, 0, 14, 0, 0))));
            return cols;
        }

        FrameworkElement Steps(string title, string glyph, params string[] steps)
        {
            var s = new StackPanel();
            s.Children.Add(Ui.H(Ui.M(Ui.Icon(glyph, 18, Theme.AccentHi), 0, 0, 10, 0), Ui.T(title, 15, Theme.Text, true)));
            for (int i = 0; i < steps.Length; i++)
            {
                var g = Ui.Columns(Ui.Px(28), Ui.Star());
                g.Margin = new Thickness(0, 9, 0, 0);
                var num = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Theme.Tint(Theme.Accent, 60),
                    VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                    Child = Ui.T((i + 1).ToString(), 11, Theme.Text, true),
                };
                ((TextBlock)num.Child).HorizontalAlignment = HorizontalAlignment.Center;
                g.Children.Add(Ui.At(num, 0));
                g.Children.Add(Ui.At(Ui.T(steps[i], 13, Theme.Text, false, true), 1));
                s.Children.Add(g);
            }
            return Ui.Card(s);
        }

        void AddHistoryLine(string line, bool newest)
        {
            if (historyList == null) return;
            if (historyList.Children.Count == 1 && historyList.Children[0] is TextBlock t0 && t0.Text == "Nada ainda.") historyList.Children.Clear();
            var tb = Ui.T(line, 12, Theme.Text, false, true);
            tb.Margin = new Thickness(0, 0, 0, 7);
            historyList.Children.Insert(0, tb); // mais novo primeiro
        }

        FrameworkElement ChainDiagram()
        {
            var wrap = new WrapPanel();
            string[] steps = { "Seu mic", "Ganho", "Filtro de graves", "IA anti-ruído", "Portão de voz", "Nivelador", "EQ 5 faixas", "De-esser", "Compressor", "Volume", "Limitador", "CABLE Input", "Discord / jogos" };
            for (int i = 0; i < steps.Length; i++)
            {
                bool end = i == 0 || i >= steps.Length - 2;
                var b = new Border
                {
                    CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 4, 6),
                    Background = end ? Theme.Tint(Theme.Good, 40) : Theme.Card2, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
                    Child = Ui.T(steps[i], 11.5, end ? Theme.Good : Theme.Text, true),
                };
                wrap.Children.Add(b);
                if (i < steps.Length - 1) wrap.Children.Add(Ui.M(Ui.T("›", 14, Theme.Dim), 0, 2, 4, 6));
            }
            return wrap;
        }

        void UpdateAbout()
        {
            if (aboutText == null) return;
            aboutText.Text =
                $"Atalhos (funcionam até dentro do jogo): mudo {HotkeyMute} • Auto-Tune {HotkeyTune} • efeitos liga/desliga {HotkeyBypass}\n" +
                $"Latência total estimada: ≈ {EstimateLatencyMs():0} ms (o Discord sozinho já usa mais que isso).\n" +
                $"IA anti-ruído: {engine.EngineName} (DeepFilterNet3: Hendrik Schröter, MIT/Apache-2.0 • RNNoise: Xiph.Org, BSD).\n" +
                "Microfone virtual: VB-CABLE (VB-Audio Software, doação opcional em vb-audio.com).\n" +
                $"Configurações: {AppSettings.ConfigPath}\n" +
                $"Registro técnico: {System.IO.Path.Combine(Log.Dir, "takavox.log")}\n" +
                $"Programa: {AppDomain.CurrentDomain.BaseDirectory}";
        }
    }
}
