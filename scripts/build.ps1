param([switch]$SelfContained, [switch]$DesktopAcceptance, [switch]$FullSelfTest)
$ErrorActionPreference = 'Stop'
$nivoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $nivoRoot
try {
    dotnet build tests/Nivo.FixtureCli/Nivo.FixtureCli.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed' }
    $nivoValidation = Join-Path $nivoRoot 'artifacts/build-validation'
    dotnet build src/Nivo.Desktop/Nivo.Desktop.csproj -c Release -o $nivoValidation
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
    $nivoExe = Join-Path $nivoValidation 'NIVO.Local.exe'
    # Separate WPF test processes avoid the legacy combined harness teardown hang.
    # The combined suite remains available explicitly; focused suites do not replace its additional coverage.
    $nivoSuites = @('--account-test', '--assistant-test', '--computer-test', '--motion-test')
    if ($FullSelfTest) { $nivoSuites += '--self-test' }
    foreach ($nivoSuite in $nivoSuites) {
        $nivoTest = Start-Process -FilePath $nivoExe -ArgumentList $nivoSuite -PassThru -WindowStyle Hidden
        try {
            if (-not $nivoTest.WaitForExit(60000)) {
                $nivoTest.Kill($true)
                throw "Validation $nivoSuite exceeded 60 seconds. See artifacts/test-lifecycle.log"
            }
            if ($nivoTest.ExitCode -ne 0) { throw "Validation $nivoSuite failed. See artifacts/test-failure.txt" }
            Write-Output "Passed: $nivoSuite"
        } finally { $nivoTest.Dispose() }
    }
    if ($DesktopAcceptance) {
        dotnet build tests/Nivo.WindowFixture/Nivo.WindowFixture.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Window fixture build failed' }
        $nivoNativeTest = Start-Process -FilePath $nivoExe -ArgumentList '--desktop-acceptance' -PassThru -WindowStyle Hidden
        if (-not $nivoNativeTest.WaitForExit(90000)) { $nivoNativeTest.Kill(); throw 'Native desktop tests exceeded 90 seconds' }
        if ($nivoNativeTest.ExitCode -ne 0) { throw 'Native desktop tests failed. See artifacts/desktop-failure.txt' }
    }
    $nivoOutput = Join-Path $nivoRoot 'artifacts/NIVO-win-x64'
    dotnet publish src/Nivo.Desktop/Nivo.Desktop.csproj -c Release -r win-x64 --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() -o $nivoOutput
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    & (Join-Path $PSScriptRoot 'package.ps1')
} finally { Pop-Location }
