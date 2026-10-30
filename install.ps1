# Installs (or updates) Doca in %LOCALAPPDATA%\Programs\Doca, separate from the source/build folder.
#   .\install.ps1              publish + install + start
#   .\install.ps1 -Startup     also start Doca with Windows
#   .\install.ps1 -Uninstall   remove Doca (taskbar is restored on exit)
param([switch]$Startup, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$dest      = Join-Path $env:LOCALAPPDATA 'Programs\Doca'
$exe       = Join-Path $dest 'Doca.exe'
$runKey    = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Doca.lnk'

# Close any running Doca cleanly (it restores the taskbar and screen space on exit).
$running = Get-Process Doca -ErrorAction SilentlyContinue | Where-Object { $_.Path }
if ($running) {
    Write-Host 'Fechando o Doca em execucao...'
    & $running[0].Path --quit
    for ($i = 0; $i -lt 40 -and (Get-Process Doca -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    Get-Process Doca -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($Uninstall) {
    Remove-ItemProperty $runKey -Name Doca -ErrorAction SilentlyContinue
    Remove-Item $startMenu -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Doca removido. Suas preferencias continuam em %APPDATA%\Doca.'
    return
}

Write-Host "Publicando em $dest ..."
dotnet publish (Join-Path $PSScriptRoot 'Doca.csproj') -c Release -r win-x64 --self-contained false -o $dest --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o Doca.' }

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.Description = 'Dock e barra de menu estilo macOS'
$lnk.Save()

# Point "start with Windows" at the installed copy (keep it on if it already was).
if ($Startup -or (Get-ItemProperty $runKey -Name Doca -ErrorAction SilentlyContinue)) {
    Set-ItemProperty $runKey -Name Doca -Value "`"$exe`""
    Write-Host 'Iniciar com o Windows: ativado.'
}

Start-Process $exe
Write-Host "Doca instalado e iniciado: $exe"
