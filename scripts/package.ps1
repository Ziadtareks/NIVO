$ErrorActionPreference = 'Stop'
$nivoRoot = Split-Path $PSScriptRoot -Parent
$nivoOutput = Join-Path $nivoRoot 'artifacts/NIVO-win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $nivoOutput 'NIVO.Local.exe'))) { throw 'Publish the application before packaging it' }
Push-Location $nivoRoot
try {
    Copy-Item -LiteralPath (Join-Path $nivoRoot 'README.md') -Destination $nivoOutput
    $nivoDocs = Join-Path $nivoOutput 'docs'
    New-Item -ItemType Directory -Path $nivoDocs -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $nivoRoot 'docs/FEATURES.md') -Destination $nivoDocs
    Copy-Item -LiteralPath (Join-Path $nivoRoot 'docs/THIRD_PARTY.md') -Destination $nivoDocs
    Copy-Item -LiteralPath (Join-Path $nivoRoot 'docs/VERIFICATION.md') -Destination $nivoDocs
    Copy-Item -LiteralPath (Join-Path $nivoRoot 'docs/REVIEW.md') -Destination $nivoDocs
    Copy-Item -LiteralPath (Join-Path $nivoRoot 'docs/PARITY.md') -Destination $nivoDocs
    Copy-Item -LiteralPath (Join-Path $nivoRoot 'docs/licenses') -Destination $nivoDocs -Recurse -Force
    # ZIP entry names use forward slashes, including when this script runs on Windows.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    function Write-NivoArchive($Base, $Files, $Destination) {
        $nivoStream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create)
        $nivoArchive = $null
        try {
            $nivoArchive = New-Object System.IO.Compression.ZipArchive $nivoStream,([System.IO.Compression.ZipArchiveMode]::Create),$false
            foreach ($nivoFile in $Files) {
                $nivoAbsolute = [System.IO.Path]::GetFullPath((Join-Path $Base $nivoFile))
                if (-not $nivoAbsolute.StartsWith(([System.IO.Path]::GetFullPath($Base).TrimEnd('\') + '\'), [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Archive source is outside its root' }
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($nivoArchive, $nivoAbsolute, $nivoFile.Replace('\','/'), [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { if ($null -ne $nivoArchive) { $nivoArchive.Dispose() }; $nivoStream.Dispose() }
    }
    foreach ($nivoLegacy in @('FEATURES.md', 'VERIFICATION.md', 'THIRD_PARTY.md')) {
        $nivoLegacyPath = Join-Path $nivoOutput $nivoLegacy
        if (Test-Path -LiteralPath $nivoLegacyPath -PathType Leaf) { Remove-Item -LiteralPath $nivoLegacyPath }
    }
    $nivoBinaryFiles = @(Get-ChildItem -LiteralPath $nivoOutput -Recurse -File | ForEach-Object { $_.FullName.Substring($nivoOutput.Length + 1) })
    Write-NivoArchive $nivoOutput $nivoBinaryFiles (Join-Path $nivoRoot 'artifacts/NIVO-win-x64.zip')
    $nivoSourceFiles = @(rg --files --hidden --no-ignore -g '!artifacts/**' -g '!**/bin/**' -g '!**/obj/**' -g '!.git/**' -g '!**/.env*' -g '!*.secret' -g '!*.pfx' -g '!*.p12' -g '!*.pem' -g '!*.key' -g '!*.log' -g '!*.user' -g '!*.suo' -g '!.vs/**' -g '!.idea/**' -g '!**/accounts/**' -g '!**/voice-sessions/**' -g '!**/data.json' -g '!**/auth.json' -g '!**/oauth_creds.json' -g '!**/*oauth-token*')
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate source files' }
    Write-NivoArchive $nivoRoot $nivoSourceFiles (Join-Path $nivoRoot 'artifacts/NIVO-source.zip')
    Write-Output ('Ready: ' + $nivoOutput)
} finally { Pop-Location }
