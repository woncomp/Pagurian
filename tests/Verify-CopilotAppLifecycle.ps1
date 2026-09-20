param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [string]$BundlePath
)
$ErrorActionPreference = "Stop"
$fixtureArgs = @()
if ($BundlePath) { $fixtureArgs = @("--bundle", (Resolve-Path $BundlePath).Path) }
dotnet run --project (Join-Path $PSScriptRoot "Fixtures\CopilotAppLifecycle\CopilotAppLifecycle.csproj") -c $Configuration -p:Platform=$Platform -- @fixtureArgs
if ($LASTEXITCODE -ne 0) { throw "Copilot App lifecycle adapter regression checks failed." }
