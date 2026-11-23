# Compila o TakaVox (C# / WPF / .NET Framework 4.8), a IA (Rust) e o instalador.
# Uso:  powershell -ExecutionPolicy Bypass -File build.ps1
# Requer: Visual Studio Build Tools (C# Roslyn + C/C++ MSVC) e, so para recompilar a IA, Rust (rustup).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = '1.0.0'
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) { $csc = (Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$build = Join-Path $root 'build'
$out = Join-Path $build 'app'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out, "$build\native" | Out-Null

# 1) icones (da arte em assets/): som saindo e microfone mudo, com o mesmo recorte
$ico = Join-Path $build 'TakaVox.ico'
$icoMudo = Join-Path $build 'TakaVox-mudo.ico'
& $csc /nologo /target:exe /platform:x64 /optimize+ /out:"$build\makeicon.exe" /r:System.Drawing.dll "$root\tools\MakeIcon.cs"
if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar o gerador de icone' }
& "$build\makeicon.exe" "$root\assets\icone-som.jpg" $ico "$root\assets\icone-mudo.jpg" $icoMudo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'falha ao gerar os icones' }

# 2) IAs nativas (so compila se ainda nao existirem)
if (-not (Test-Path "$build\native\rnnoise_avx2.dll")) {
    cmd /c "`"$root\native\build_rnnoise.cmd`" `"$root\native\rnnoise-0.2`" `"$build\native`""
    if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar o RNNoise' }
}
if (-not (Test-Path "$build\native\takavox_df.dll")) {
    $env:CARGO_TARGET_DIR = Join-Path $env:TEMP 'takavox_cargo'   # caminho curto (limite de 260 caracteres do linker)
    cmd /c "`"$root\native\build_df.cmd`""
    if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar a IA DeepFilterNet (precisa do Rust: https://rustup.rs)' }
    Copy-Item "$env:CARGO_TARGET_DIR\release\takavox_df.dll" "$build\native\" -Force
}

$tvh = "$build\native\tvhost.dll"
$tvhSrc = Get-ChildItem "$root\native\tvhost" -Filter *.cpp | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not (Test-Path $tvh) -or $tvhSrc.LastWriteTime -gt (Get-Item $tvh).LastWriteTime) {
    cmd /c "`"$root\native\build_tvhost.cmd`" `"$build\native`""
    if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar o hospedeiro de plugins (tvhost.dll)' }
}
$mda = "$build\native\mda-vst3.vst3"
if (-not (Test-Path $mda)) {
    cmd /c "`"$root\native\build_mda.cmd`" `"$build\native`""
    if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar os efeitos gratuitos mda-vst3' }
}

# 3) app
$refs = @(
    "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll",
    "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll"
) | ForEach-Object { "/r:$_" }
$src = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /platform:x64 /langversion:latest /optimize+ /unsafe /warn:3 /nowarn:0420 `
    /win32icon:"$ico" /win32manifest:"$root\src\app.manifest" /out:"$out\TakaVox.exe" @refs @src
if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar o TakaVox' }

# 4) arquivos de apoio (o VB-CABLE NAO vai junto: a licenca da VB-Audio nao permite embutir; o app baixa o oficial)
Copy-Item "$build\native\rnnoise_avx2.dll", "$build\native\rnnoise_sse2.dll", "$build\native\takavox_df.dll", "$build\native\tvhost.dll" $out -Force
Copy-Item $ico, $icoMudo $out -Force
New-Item -ItemType Directory -Force "$out\licencas" | Out-Null
Copy-Item "$root\native\rnnoise-0.2\COPYING" "$out\licencas\RNNoise-LICENSE.txt" -Force
Copy-Item "$root\native\DeepFilterNet-0.5.6\LICENSE-MIT" "$out\licencas\DeepFilterNet-LICENSE-MIT.txt" -Force
Copy-Item "$root\native\DeepFilterNet-0.5.6\LICENSE-APACHE" "$out\licencas\DeepFilterNet-LICENSE-APACHE.txt" -Force
Copy-Item "$root\native\vst3sdk\public.sdk\LICENSE.txt" "$out\licencas\VST3-SDK-LICENSE.txt" -Force
New-Item -ItemType Directory -Force "$out\plugins" | Out-Null
Copy-Item $mda "$out\plugins\mda-vst3.vst3" -Force
Copy-Item "$root\native\mda-LICENSE.txt" "$out\licencas\mda-vst3-LICENSE.txt" -Force
if (Test-Path "$root\LICENSE") { Copy-Item "$root\LICENSE" "$out\licencas\TakaVox-LICENSE.txt" -Force }
if (Test-Path "$root\THIRD_PARTY_NOTICES.md") { Copy-Item "$root\THIRD_PARTY_NOTICES.md" "$out\licencas\" -Force }

# 5) instalador (um .exe com tudo dentro)
$payload = Join-Path $build 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($out, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$setup = Join-Path $build "TakaVox-Setup-$version.exe"
$srefs = @(
    "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll", "$fw\Microsoft.CSharp.dll", "$fw\System.Core.dll"
) | ForEach-Object { "/r:$_" }
& $csc /nologo /target:winexe /platform:x64 /langversion:latest /optimize+ /warn:3 `
    /win32icon:"$ico" /win32manifest:"$root\src\app.manifest" /resource:"$payload,payload.zip" /resource:"$ico,app.ico" `
    /out:"$setup" @srefs "$root\tools\Setup\Setup.cs" "$root\src\UI\Theme.cs"
if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar o instalador' }
Write-Host "OK -> app: $out"
Write-Host "OK -> instalador: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
