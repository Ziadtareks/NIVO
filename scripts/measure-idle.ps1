param([int]$Seconds = 60, [string]$Executable = '', [string]$Report = 'artifacts/performance-idle.json')
$ErrorActionPreference = 'Stop'
$nivoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($Executable)) { $Executable = Join-Path $nivoRoot 'src/Nivo.Desktop/bin/Release/net10.0-windows10.0.19041.0/NIVO.Local.exe' }
if ($Seconds -lt 10 -or $Seconds -gt 600) { throw 'Choose a measurement between 10 and 600 seconds' }
$nivoQuit = Start-Process -FilePath $Executable -ArgumentList '--quit' -WindowStyle Hidden -PassThru
$nivoQuit.WaitForExit(5000) | Out-Null
$nivoApp = Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
try {
    $nivoApp.WaitForInputIdle(5000) | Out-Null
    Start-Sleep -Seconds 10
    $nivoApp.Refresh()
    if ($nivoApp.HasExited) { throw 'NIVO exited before sampling' }
    $nivoSamples = @()
    $nivoPreviousCpu = $nivoApp.TotalProcessorTime.TotalMilliseconds
    $nivoPreviousTime = [DateTime]::UtcNow
    for ($nivoIndex = 0; $nivoIndex -lt $Seconds; $nivoIndex++) {
        Start-Sleep -Seconds 1
        $nivoApp.Refresh()
        if ($nivoApp.HasExited) { throw 'NIVO exited while sampling' }
        $nivoNow = [DateTime]::UtcNow
        $nivoCpu = $nivoApp.TotalProcessorTime.TotalMilliseconds
        $nivoCorePercent = 100 * ($nivoCpu - $nivoPreviousCpu) / ($nivoNow - $nivoPreviousTime).TotalMilliseconds
        $nivoSamples += [PSCustomObject]@{ coreCpuPercent = $nivoCorePercent; systemCpuPercent = $nivoCorePercent / [Environment]::ProcessorCount; workingSetBytes = $nivoApp.WorkingSet64; privateBytes = $nivoApp.PrivateMemorySize64 }
        $nivoPreviousCpu = $nivoCpu; $nivoPreviousTime = $nivoNow
    }
    $nivoReport = [PSCustomObject]@{
        measuredUtc = [DateTime]::UtcNow.ToString('o')
        scope = 'Normal desktop idle observation, NIVO process only; not AI or conversion workload. Keep the device idle during sampling.'
        durationSeconds = $Seconds
        logicalProcessors = [Environment]::ProcessorCount
        assemblySha256 = (Get-FileHash ($Executable -replace '\.exe$','.dll') -Algorithm SHA256).Hash
        meanSystemCpuPercent = ($nivoSamples | Measure-Object systemCpuPercent -Average).Average
        maxSystemCpuPercent = ($nivoSamples | Measure-Object systemCpuPercent -Maximum).Maximum
        maxWorkingSetBytes = ($nivoSamples | Measure-Object workingSetBytes -Maximum).Maximum
        maxPrivateBytes = ($nivoSamples | Measure-Object privateBytes -Maximum).Maximum
        samples = $nivoSamples
    }
    $nivoReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $nivoRoot $Report) -Encoding UTF8
    Write-Output ('Mean CPU %: ' + $nivoReport.meanSystemCpuPercent)
    Write-Output ('Peak working set MiB: ' + ($nivoReport.maxWorkingSetBytes / 1MB))
} finally {
    $nivoQuit = Start-Process -FilePath $Executable -ArgumentList '--quit' -WindowStyle Hidden -PassThru
    $nivoQuit.WaitForExit(5000) | Out-Null
    $nivoApp.WaitForExit(5000) | Out-Null
}
