$ErrorActionPreference = 'Stop'
$nivoRoot = Split-Path $PSScriptRoot -Parent
$nivoExe = Join-Path $nivoRoot 'artifacts/NIVO-win-x64/NIVO.Local.exe'
if (-not (Test-Path -LiteralPath $nivoExe)) {
    dotnet build (Join-Path $nivoRoot 'Nivo.slnx') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $nivoExe = Join-Path $nivoRoot 'src/Nivo.Desktop/bin/Release/net10.0-windows10.0.19041.0/NIVO.Local.exe'
}
Start-Process -FilePath $nivoExe -WindowStyle Hidden
