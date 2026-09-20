param([switch]$Taskbar, [switch]$Right, [long]$SecondaryTaskbar, [switch]$BuildOnly, [switch]$NoBuild)
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
if ($Taskbar -and ($Right -or $SecondaryTaskbar)) { throw "Use -Taskbar for the existing left fixture, or -Right/-SecondaryTaskbar for right placement." }
$stdout = Join-Path $output "smoke.stdout.log"
$stderr = Join-Path $output "smoke.stderr.log"
$process = Start-Process @options -FilePath (Join-Path $output "Pagurian.exe") -WorkingDirectory $output -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "Tray fixture timed out." }
Get-Content $stdout
Get-Content $stderr
if ($process.ExitCode -ne 0 -or -not (Select-String -LiteralPath $stdout -SimpleMatch 'Tray lifecycle passed:')) { throw "Tray fixture failed ($($process.ExitCode))." }
