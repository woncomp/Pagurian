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
# Force Process to retain its native handle so ExitCode remains available
# after the app tears down its WinUI dispatcher and exits.
$null = $process.Handle
if (-not $process.WaitForExit(30000)) {
    # Only the exact fixture process created above is eligible for termination.
    $process.Kill()
    throw "Navigation diagnostics smoke fixture timed out after 30 seconds."
}
$process.WaitForExit()
$process.Refresh()
$marker = "Configuration transition passed:"
$outputDeadline = [DateTime]::UtcNow.AddSeconds(2)
do {
    $standardOutput = Get-Content -LiteralPath $stdout
    if ($standardOutput | Select-String -SimpleMatch $marker) { break }
    Start-Sleep -Milliseconds 50
} while ([DateTime]::UtcNow -lt $outputDeadline)
$standardOutput
Get-Content -LiteralPath $stderr
if ($process.ExitCode -ne 0) {
    throw "Navigation diagnostics smoke fixture failed with exit code $($process.ExitCode)."
}
if (-not ($standardOutput | Select-String -SimpleMatch $marker)) {
    throw "Navigation diagnostics fixture exited before its final assertions completed."
}
