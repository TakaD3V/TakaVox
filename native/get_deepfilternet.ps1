# Baixa o codigo-fonte oficial do DeepFilterNet 0.5.6 (com os modelos) para native\DeepFilterNet-0.5.6
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $here 'DeepFilterNet-0.5.6'
if (Test-Path (Join-Path $dest 'libDF')) { Write-Host 'DeepFilterNet ja esta presente.'; exit 0 }
$zip = Join-Path $env:TEMP 'DeepFilterNet-0.5.6.zip'
Write-Host 'Baixando DeepFilterNet 0.5.6 do GitHub oficial (~100 MB)...'
Invoke-WebRequest 'https://codeload.github.com/Rikorose/DeepFilterNet/zip/refs/tags/v0.5.6' -OutFile $zip -UseBasicParsing
Expand-Archive -Force $zip $here
Remove-Item $zip
Write-Host "OK -> $dest"
