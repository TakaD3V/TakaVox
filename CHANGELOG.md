# Novidades

## 1.0.0 — primeira versão pública

### Voz
- IA anti-ruído **DeepFilterNet3** (e a versão LL, de baixa latência, recomendada): tira teclado, mouse, ventoinha, rua e chiado sem deixar a voz robótica. RNNoise como opção leve.
- **Removedor de ventoinha e zumbido**: acha os "apitos" fixos do PC e do ventilador e corta cada um antes da IA.
- Cadeia de estúdio: filtro de graves, portão de voz guiado pela IA, nivelador, EQ de 5 faixas arrastável, de-esser, compressor e limitador.
- **Calibração automática** (10 s), que também acerta o EQ para o timbre do seu mic, e **teste antes × depois**.
- **Auto-Tune** em tempo real.

### Modificador de voz
- 22 vozes prontas, com tom e timbre (formantes) separados: garota de anime, robô, monstro, demônio, rádio, telefone…
- **Vozes com IA** (Beatrice 2), baixadas pelo usuário no site oficial: Tsukuyomi-chan, Tokina Shigure, OLUNE e 100 vozes JVS.
  - O tom se ajusta sozinho à sua voz.
  - Roda numa thread própria, sem placa de vídeo.
  - Se falhar, a sua voz normal volta.

### Sound pad
- Pads com emoji, cor, categorias, busca, arrastar e soltar e reordenar.
- **Tecla de atalho por som, em 1 clique**, que funciona até dentro do jogo.
  - O teclado numérico e F1–F11 funcionam sozinhos.
  - **Teclas automáticas** coloca Num 1…Num 0 nos sons.
  - `Ctrl+Alt+P` para tudo.
- Trecho com forma de onda, volume, fades, loop e modos (recomeça, para, sobrepõe).
- Normalização de volume (LUFS) com teto de pico; o silêncio do começo de cada arquivo é tirado sozinho.
- Sons para quem te escuta e/ou para o seu fone. Dá para abaixar a sua voz enquanto um som toca e tocar com o mic no mudo.
- Lê MP3, WAV, M4A/AAC, WMA, FLAC e OGG pelo próprio Windows, com reamostragem de alta qualidade (>110 dB).

### Plugins
- Hospedeiro **VST3 e VST2** (64 bits) direto na voz, sem DAW.
- Janela no estilo do FL Studio, com o plugin original embutido ou um painel de knobs.
- Presets de fábrica e do usuário. Cada plugin é lido num processo separado, e um plugin que trava é desligado sem derrubar a voz.
- ~20 efeitos grátis inclusos (mda).

### Para jogar e para live
- **Economia automática**: sem nenhum app usando o microfone virtual, a IA para e o TakaVox usa 0,4% de um núcleo. Ele volta na hora em que o Discord, o jogo ou o OBS abre o microfone.
- Na bandeja: 0% de CPU na interface e ~60 MB de memória em uso.
- Threads de áudio no núcleo menos usado pelos jogos. Nada é desenhado com um jogo em tela cheia na frente.

### Visual
- Temas: Escuro, OLED, Vidro e Vidro claro (desfoque do Windows 11), Claro, Neon Taka, Grafite, Oceano e Sakura.
- O ícone muda quando o microfone está no mudo.
