param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [switch]$BuildOnly
)
$ErrorActionPreference = "Stop"
$directory = Join-Path $PSScriptRoot "Fixtures\CopilotUsagePresentation"
$project = Join-Path $directory "CopilotUsagePresentation.csproj"
if (Get-Command mur -ErrorAction SilentlyContinue) {
    mur check $project -- -c $Configuration -p:Platform=$Platform
} else {
    dotnet build $project -c $Configuration -p:Platform=$Platform
}
if ($LASTEXITCODE -ne 0) { throw "Copilot Usage presentation fixture failed to build." }
if ($BuildOnly) { return }

# Isolated fake source and in-memory draft; no production Startup or settings IO.
& (Join-Path $directory "bin\$Platform\$Configuration\net10.0-windows10.0.22621.0\CopilotUsagePresentation.exe")
if ($LASTEXITCODE -ne 0) { throw "Copilot Usage presentation fixture failed: $LASTEXITCODE." }
