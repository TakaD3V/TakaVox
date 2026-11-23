# Como lançar uma versão nova

- Repositório: https://github.com/TakaD3V/TakaVox
- Site: https://takad3v.github.io/TakaVox/ (GitHub Pages, servido da pasta `docs/` da branch `main`)

O botão "Baixar" do README e do site sempre aponta para o instalador da última versão publicada, então não precisa mexer neles.

## Passo a passo

1. Mude o número da versão em `build.ps1` (`$version`) e conte as novidades no `CHANGELOG.md`.
2. Gere o instalador:

   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```

   Ele sai em `build\TakaVox-Setup-X.Y.Z.exe`.
3. Faça o commit e envie (`git push`).
4. Crie a versão (Release) com o instalador. Escolha um dos jeitos:
   - **Pelo site:** **Releases › Draft a new release**:
     - tag `vX.Y.Z`;
     - título `TakaVox X.Y.Z`;
     - cole as novidades;
     - arraste o instalador para "Attach binaries";
     - clique em **Publish release**.
   - **Pela linha de comando**, com o [GitHub CLI](https://cli.github.com/):

     ```powershell
     gh release create vX.Y.Z build\TakaVox-Setup-X.Y.Z.exe --title "TakaVox X.Y.Z" --notes-file notas.md
     ```

Coloque nas notas o SHA-256 do instalador, para quem quiser conferir o arquivo:

```powershell
(Get-FileHash build\TakaVox-Setup-X.Y.Z.exe -Algorithm SHA256).Hash
```

## Textos do repositório

**Descrição** (engrenagem ao lado de "About"):

```
Estúdio de voz com IA para Windows: tira o ruído do teclado e da ventoinha, muda a sua voz (até com IA), toca sons com teclas de atalho e roda plugins VST no Discord, jogos e lives — sem tirar FPS.
```

**Tópicos:**

```
windows discord voice-changer soundboard noise-suppression noise-cancellation deepfilternet ai vst3 vst-host auto-tune obs streaming gaming microphone audio wpf csharp valorant
```

**Imagem de capa:** em **Settings › General › Social preview › Edit › Upload an image**, envie `docs/banner.png`. Ela aparece quando alguém compartilha o link no Discord, no Twitter etc.

## O que nunca vai para o repositório

O `.gitignore` já deixa de fora:
- a pasta `build/`: o instalador vai na Release, não no código;
- os SDKs baixados;
- o VB-CABLE e os arquivos da voz com IA (Beatrice), que não podem ser redistribuídos;
- os áudios de teste.
