param([switch]$StartWithWindows)
$ErrorActionPreference = 'Stop'
$nivoRoot = Split-Path $PSScriptRoot -Parent
$nivoSource = Join-Path $nivoRoot 'artifacts/NIVO-win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $nivoSource 'NIVO.Local.exe'))) { throw 'Build the portable package first with scripts/build.ps1.' }
$nivoDestination = Join-Path $env:LOCALAPPDATA 'Programs/NIVO'
New-Item -ItemType Directory -Path $nivoDestination -Force | Out-Null
Copy-Item -Path (Join-Path $nivoSource '*') -Destination $nivoDestination -Recurse -Force
$nivoShell = New-Object -ComObject WScript.Shell
$nivoMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'NIVO.lnk'
$nivoShortcut = $nivoShell.CreateShortcut($nivoMenu)
$nivoShortcut.TargetPath = Join-Path $nivoDestination 'NIVO.Local.exe'
$nivoShortcut.WorkingDirectory = $nivoDestination
$nivoShortcut.Save()
if ($StartWithWindows) {
    $nivoStartup = Join-Path ([Environment]::GetFolderPath('Startup')) 'NIVO.lnk'
    Copy-Item -LiteralPath $nivoMenu -Destination $nivoStartup -Force
}
Write-Output ('Installed for the current user: ' + $nivoDestination)
