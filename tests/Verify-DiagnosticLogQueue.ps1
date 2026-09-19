param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$fixture = Join-Path $PSScriptRoot "Fixtures\DiagnosticLogQueue\DiagnosticLogQueue.csproj"
dotnet run --project $fixture -c $Configuration -p:Platform=$Platform
if ($LASTEXITCODE -ne 0) {
    throw "Navigation diagnostic queue regression checks failed."
}
