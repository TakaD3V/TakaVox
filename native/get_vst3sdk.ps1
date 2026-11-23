# Baixa o VST 3 SDK 3.8.1 oficial da Steinberg (licenca MIT) para native\vst3sdk:
# so as tres partes que o hospedeiro de plugins (tvhost.dll) usa: base, pluginterfaces e public.sdk.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $here 'vst3sdk'
$tag = 'v3.8.1_build_84'
$parts = [ordered]@{ 'base' = 'vst3_base'; 'pluginterfaces' = 'vst3_pluginterfaces'; 'public.sdk' = 'vst3_public_sdk' }
foreach ($name in $parts.Keys) {
    $target = Join-Path $dest $name
    if (Test-Path (Join-Path $target 'LICENSE.txt')) { Write-Host "$name ja esta presente."; continue }
    $zip = Join-Path $env:TEMP "$($parts[$name])-$tag.zip"
    $tmp = Join-Path $env:TEMP "$($parts[$name])-$tag"
    Write-Host "Baixando $name ($($parts[$name]) $tag) do GitHub oficial da Steinberg..."
    Invoke-WebRequest "https://codeload.github.com/steinbergmedia/$($parts[$name])/zip/refs/tags/$tag" -OutFile $zip -UseBasicParsing
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    Expand-Archive -Force $zip $tmp
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force $dest | Out-Null
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Move-Item $inner.FullName $target
    Remove-Item -Recurse -Force $tmp, $zip
}
Write-Host "OK -> $dest"
