# Componentes de terceiros

O TakaVox inclui (dentro do instalador) os seguintes componentes. Os textos completos das licenças vão na pasta `licencas` do app instalado.

## DeepFilterNet 0.5.6 (modelos DeepFilterNet3 e DeepFilterNet3 LL)
- Autor: Hendrik Schröter. Repositório: https://github.com/Rikorose/DeepFilterNet
- Licença: MIT **ou** Apache-2.0, à escolha do usuário.
- Uso: compilado em `takavox_df.dll` (com os modelos ONNX embutidos e o runtime `tract`) por meio da ponte em `native/takavox_df`.

## RNNoise 0.2
- Autor: Xiph.Org Foundation / Jean-Marc Valin. Repositório: https://github.com/xiph/rnnoise
- Licença: BSD-3-Clause.
- Uso: compilado em `rnnoise_avx2.dll` / `rnnoise_sse2.dll` (motor de IA leve).

## VST 3 SDK 3.8.1 (Steinberg)
- Autor: Steinberg Media Technologies GmbH. Repositório: https://github.com/steinbergmedia/vst3sdk
- Licença: MIT (texto em `licencas/VST3-SDK-LICENSE.txt`).
- Uso: as partes `pluginterfaces`, `base` e `public.sdk/source/vst/hosting` são compiladas em `tvhost.dll`, o hospedeiro de plugins do TakaVox.
- VST é marca registrada da Steinberg Media Technologies GmbH. O suporte a VST2 usa só a interface binária pública dos plugins (o SDK do VST2 não é incluído).

## mda-vst3 (efeitos gratuitos incluídos)
- Autor: Paul Kellett (mda), versão VST3 dos exemplos do VST 3 SDK. Fonte: `public.sdk/samples/vst/mda-vst3` do SDK.
- Licença: MIT (texto em `licencas/mda-vst3-LICENSE.txt`).
- Uso: compilado em `plugins/mda-vst3.vst3`, que aparece na página Plugins do app.

## Freeverb (afinações do reverb do modificador de voz)
- Autor: Jezar at Dreampoint. Licença: domínio público.
- Uso: o reverb do modificador de voz (`src/Dsp/VoiceFx.cs`) é código próprio, mas usa os tamanhos clássicos das linhas de atraso do Freeverb.

## Media Foundation (Windows)
- Parte do próprio Windows. O sound pad usa o Media Foundation (em `tvhost.dll`) para ler MP3, WAV, M4A/AAC, WMA e FLAC. Nada é incluído no instalador.

## Beatrice 2 (NÃO incluído)
- Autor: Project Beatrice. Site: https://prj-beatrice.com/ — código do VST: MIT; motor de inferência: fechado.
- As vozes oficiais têm termos próprios (Tsukuyomi-chan © Rei Yumesaki; 刻鳴時雨, de 瓶詰め; OLUNE, de Leo Carvalho) e **não permitem redistribuir** os arquivos. As vozes JVS seguem os termos do corpus JVS (uso não comercial).
- Uso: o usuário baixa o plugin e as vozes no site oficial, aceitando os termos, e o TakaVox importa os zips para `%APPDATA%\TakaVox\ia`.

## VB-CABLE (NÃO incluído)
- Autor: Vincent Burel / VB-Audio Software. Site: https://vb-audio.com/Cable/
- Licença: donationware. A licença **não permite** embutir o pacote no instalador de outro programa sem autorização do autor, por isso o TakaVox **não** o inclui.
- Uso: quando o usuário pede, o app baixa o pacote oficial e sem modificações de `download.vb-audio.com`, confere a assinatura digital e abre o instalador oficial.
- Se você usa, considere apoiar o autor: https://vb-audio.com/Services/support.htm
