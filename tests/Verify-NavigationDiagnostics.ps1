param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
$fixtureDirectory = Join-Path $PSScriptRoot "Fixtures\NavigationDiagnostics"
$fixture = Join-Path $fixtureDirectory "NavigationDiagnostics.csproj"
dotnet build $fixture -c $Configuration -p:Platform=$Platform
if ($LASTEXITCODE -ne 0) {
    throw "Navigation diagnostics smoke fixture failed to build."
}
if ($BuildOnly) { return }

# Requires an interactive Windows desktop. Opens one small nonactivating test
# window for about 11 seconds; no host startup, user configuration, or user log.
$output = Join-Path $fixtureDirectory "bin\$Platform\$Configuration\net10.0-windows10.0.22621.0"
$executable = Join-Path $output "NavigationDiagnostics.exe"
$stdout = Join-Path $output "smoke.stdout.log"
$stderr = Join-Path $output "smoke.stderr.log"
$process = Start-Process -FilePath $executable -WorkingDirectory $output `
    -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if (-not $process.WaitForExit(30000)) {
    # Only the exact fixture process created above is eligible for termination.
    $process.Kill()
    throw "Navigation diagnostics smoke fixture timed out after 30 seconds."
}
$standardOutput = Get-Content -LiteralPath $stdout
$standardOutput
Get-Content -LiteralPath $stderr
if ($process.ExitCode -ne 0) {
    throw "Navigation diagnostics smoke fixture failed with exit code $($process.ExitCode)."
}
if (-not ($standardOutput | Select-String -SimpleMatch "Live navigation diagnostics passed:")) {
    throw "Navigation diagnostics fixture exited before its final assertions completed."
}
