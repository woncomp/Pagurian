param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [switch]$BuildOnly,
    [switch]$Capture,
    [string]$CompassAssembly
)
$ErrorActionPreference = "Stop"
$fixtureDirectory = Join-Path $PSScriptRoot "Fixtures\BillboardLifecycle"
dotnet build (Join-Path $fixtureDirectory "BillboardLifecycle.csproj") -c $Configuration -p:Platform=$Platform
if ($LASTEXITCODE -ne 0) { throw "Billboard lifecycle fixture failed to build." }
if ($BuildOnly) { return }

# Isolated windows only; no tray injection, user configuration or user log.
$output = Join-Path $fixtureDirectory "bin\$Platform\$Configuration\net10.0-windows10.0.22621.0"
$stdout = Join-Path $output "smoke.stdout.log"
$stderr = Join-Path $output "smoke.stderr.log"
$runOptions = @{}
$fixtureArguments = @()
if ($Capture) { $fixtureArguments += '--capture' }
if ($CompassAssembly) {
    $resolvedCompass = (Resolve-Path -LiteralPath $CompassAssembly).Path
    $fixtureArguments += '--compass', ('"' + $resolvedCompass + '"')
}
if ($fixtureArguments.Count) { $runOptions.ArgumentList = $fixtureArguments }
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $output "BillboardLifecycle.exe"
if ($runOptions.ArgumentList) { $psi.Arguments = ($runOptions.ArgumentList -join ' ') }
$psi.WorkingDirectory = $output
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::Start($psi)
$outTask = $process.StandardOutput.ReadToEndAsync()
$errTask = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(45000)) {
    $process.Kill()
    throw "Billboard lifecycle fixture timed out."
}
$process.WaitForExit()
$outTask.Result | Set-Content -LiteralPath $stdout
$errTask.Result | Set-Content -LiteralPath $stderr
Get-Content -LiteralPath $stdout
Get-Content -LiteralPath $stderr
if ($process.ExitCode -ne 0) { throw "Billboard lifecycle fixture failed: $($process.ExitCode)." }
if (-not (Select-String -LiteralPath $stdout -SimpleMatch "Billboard lifecycle passed:")) {
    throw "Billboard lifecycle fixture exited before completing assertions."
}
