param([switch]$BuildOnly)
$ErrorActionPreference = "Stop"
$fixture = Join-Path $PSScriptRoot "Fixtures/TrayTopology"
dotnet build (Join-Path $fixture "TrayTopology.csproj") -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Tray topology fixture build failed." }
if ($BuildOnly) { return }
$output = Join-Path $fixture "bin/x64/Debug/net10.0-windows10.0.22621.0"
$stdout = Join-Path $output "smoke.stdout.log"
$stderr = Join-Path $output "smoke.stderr.log"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $output "Pagurian.exe"
$psi.WorkingDirectory = $output
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::Start($psi)
$outTask = $process.StandardOutput.ReadToEndAsync()
$errTask = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(30000)) { $process.Kill(); throw "Tray topology fixture timed out." }
$process.WaitForExit()
$outTask.Result | Set-Content $stdout
$errTask.Result | Set-Content $stderr
Get-Content $stdout
Get-Content $stderr
if ($process.ExitCode -ne 0 -or -not (Select-String -LiteralPath $stdout -SimpleMatch 'Tray topology passed:')) { throw "Tray topology fixture failed ($($process.ExitCode))." }
