# Instala o TakaVox na pasta de programas do usuario (sem precisar de administrador)
# e cria atalhos no Menu Iniciar e na Area de Trabalho.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'build\app'
$dst = Join-Path $env:LOCALAPPDATA 'Programs\TakaVox'

# fecha o TakaVox se estiver aberto (para poder substituir o .exe)
Get-Process -Name TakaVox -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $_.HasExited) { $_.Kill() } }

New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$src\*" $dst -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$targets = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'TakaVox.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'TakaVox.lnk')
)
foreach ($lnkPath in $targets) {
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = Join-Path $dst 'TakaVox.exe'
    $lnk.WorkingDirectory = $dst
    $lnk.IconLocation = (Join-Path $dst 'TakaVox.exe') + ',0'
    $lnk.Description = 'TakaVox - estudio de voz (microfone com IA anti-ruido)'
    $lnk.Save()
}
Write-Host "Instalado em: $dst"
