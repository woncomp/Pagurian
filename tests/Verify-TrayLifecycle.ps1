param([switch]$Taskbar, [switch]$Right, [long]$SecondaryTaskbar, [switch]$Overflow, [switch]$Geometry, [switch]$BuildOnly, [switch]$NoBuild)
$ErrorActionPreference = "Stop"
$fixture = Join-Path $PSScriptRoot "Fixtures/TrayLifecycle"
if (-not $NoBuild) {
    dotnet build (Join-Path $fixture "TrayLifecycle.csproj") -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "Tray fixture build failed." }
}
if ($BuildOnly) { return }
$output = Join-Path $fixture "bin/x64/Debug/net10.0-windows10.0.22621.0"
$options = @{}
if ($Taskbar) { $options.ArgumentList = '--taskbar' }
if ($Right) { $options.ArgumentList = '--right' }
if ($SecondaryTaskbar) { $options.ArgumentList = "--secondary-taskbar $SecondaryTaskbar" }
if ($Overflow) { $options.ArgumentList = '--overflow' }
if ($Geometry) { $options.ArgumentList = '--geometry' }
if (($Taskbar -and ($Right -or $SecondaryTaskbar -or $Overflow -or $Geometry)) -or
    ($Right -and ($SecondaryTaskbar -or $Overflow -or $Geometry)) -or
    ($SecondaryTaskbar -and ($Overflow -or $Geometry)) -or ($Overflow -and $Geometry))
{ throw "Use -Taskbar for the existing left fixture, or one of -Right/-SecondaryTaskbar/-Overflow/-Geometry." }
$stdout = Join-Path $output "smoke.stdout.log"
$stderr = Join-Path $output "smoke.stderr.log"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $output "Pagurian.exe"
if ($options.ArgumentList) { $psi.Arguments = $options.ArgumentList }
$psi.WorkingDirectory = $output
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::Start($psi)
$outTask = $process.StandardOutput.ReadToEndAsync()
$errTask = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "Tray fixture timed out." }
$process.WaitForExit()
$outTask.Result | Set-Content $stdout
$errTask.Result | Set-Content $stderr
Get-Content $stdout
Get-Content $stderr
if ($process.ExitCode -ne 0 -or -not (Select-String -LiteralPath $stdout -SimpleMatch 'Tray lifecycle passed:')) { throw "Tray fixture failed ($($process.ExitCode))." }
