param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    [switch]$BuildOnly,
    # Explicit opt-in: the real copy-button round trip replaces clipboard contents.
    [switch]$Clipboard
)
$ErrorActionPreference = "Stop"
$directory = Join-Path $PSScriptRoot "Fixtures\CopilotSessionPresentation"
$project = Join-Path $directory "CopilotSessionPresentation.csproj"
if (Get-Command mur -ErrorAction SilentlyContinue) {
    mur check $project -- -c $Configuration -p:Platform=$Platform
} else {
    dotnet build $project -c $Configuration -p:Platform=$Platform
}
if ($LASTEXITCODE -ne 0) { throw "Copilot presentation fixture failed to build." }
if ($BuildOnly) { return }

# A foreground executable keeps output on the caller's console. No active app,
# user config, hook files, log redirection, or publish folders are touched.
$exe = Join-Path $directory "bin\$Platform\$Configuration\net10.0-windows10.0.22621.0\CopilotSessionPresentation.exe"
$fixtureArgs = @()
if ($Clipboard) { $fixtureArgs += "--clipboard" }
& $exe @fixtureArgs
if ($LASTEXITCODE -ne 0) { throw "Copilot presentation fixture failed: $LASTEXITCODE." }
