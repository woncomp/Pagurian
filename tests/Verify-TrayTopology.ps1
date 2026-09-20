param([switch]$BuildOnly)
$ErrorActionPreference = "Stop"
$fixture = Join-Path $PSScriptRoot "Fixtures/TrayTopology"
dotnet build (Join-Path $fixture "TrayTopology.csproj") -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Tray topology fixture build failed." }
if ($BuildOnly) { return }
$output = Join-Path $fixture "bin/x64/Debug/net10.0-windows10.0.22621.0"
$stdout = Join-Path $output "smoke.stdout.log"
$stderr = Join-Path $output "smoke.stderr.log"
$process = Start-Process -FilePath (Join-Path $output "Pagurian.exe") -WorkingDirectory $output -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if (-not $process.WaitForExit(30000)) { $process.Kill(); throw "Tray topology fixture timed out." }
Get-Content $stdout
Get-Content $stderr
if ($process.ExitCode -ne 0 -or -not (Select-String -LiteralPath $stdout -SimpleMatch 'Tray topology passed:')) { throw "Tray topology fixture failed ($($process.ExitCode))." }
