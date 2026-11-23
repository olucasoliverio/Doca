# Builds dist\Doca-Setup-<versão>.exe, an installer that runs on any Windows 10 2004+ / 11 x64.
# Self-contained: the .NET runtime goes inside, so the other PC needs nothing installed first.
# Needs Inno Setup 6 (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = 'Stop'

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 nao encontrado. Instale com: winget install JRSoftware.InnoSetup' }

$proj = Join-Path $PSScriptRoot 'Doca.csproj'
$version = ([xml](Get-Content $proj -Raw -Encoding utf8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $PSScriptRoot 'publish\installer'

Write-Host "Publicando Doca $version (self-contained)..."
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o Doca.' }

Write-Host 'Gerando o instalador...'
& $iscc /Q "/DMyAppVersion=$version" "/DSourceDir=$out" (Join-Path $PSScriptRoot 'installer\Doca.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha no Inno Setup.' }

$setup = Join-Path $PSScriptRoot "dist\Doca-Setup-$version.exe"
Write-Host ("Pronto: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
