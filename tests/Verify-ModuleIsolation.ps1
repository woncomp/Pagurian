param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$targetFramework = "net10.0-windows10.0.22621.0"
$hostOutput = Join-Path $repoRoot "Pagurian\bin\$Platform\$Configuration\$targetFramework"
$fixtureRoot = Join-Path $repoRoot "tests\Fixtures"

function Build-Fixture([string]$name) {
    dotnet build (Join-Path $fixtureRoot "$name\$name.csproj") `
        -c $Configuration -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture $name failed to build."
    }
}

function Get-FixtureEntry([string]$name) {
    $output = Join-Path $fixtureRoot "$name\bin\$Platform\$Configuration\$targetFramework"
    $bundleName = "Pagurian.Fixtures.$name"
    return Join-Path $output "$bundleName.dll"
}

dotnet build (Join-Path $repoRoot "Pagurian.sln") `
    -c $Configuration -p:Platform=$Platform
if ($LASTEXITCODE -ne 0) {
    throw "Pagurian failed to build."
}

Build-Fixture "ModuleA"
Build-Fixture "ModuleB"
Build-Fixture "ModuleHost"
$entries = @(
    Get-FixtureEntry "ModuleA"
    Get-FixtureEntry "ModuleB"
)

$default = [System.Runtime.Loader.AssemblyLoadContext]::Default
@(
    "WinRT.Runtime.dll",
    "Microsoft.Windows.SDK.NET.dll",
    "Reactor.Wrappers.Abstractions.dll",
    "Reactor.dll",
    "Pagurian.Sdk.dll"
) | ForEach-Object {
    $default.LoadFromAssemblyPath((Join-Path $hostOutput $_)) | Out-Null
}

$sdkAssembly = $default.Assemblies | Where-Object { $_.GetName().Name -eq "Pagurian.Sdk" }
$moduleBase = $sdkAssembly.GetType("Pagurian.Sdk.PagurianModule")
$hostAssembly = $default.LoadFromAssemblyPath((Get-FixtureEntry "ModuleHost"))
$contextType = $hostAssembly.GetType("Pagurian.ModuleLoadContext", $true)
$hostOwnedMethod = $contextType.GetMethod(
    "IsHostOwnedAssemblyFile",
    [Reflection.BindingFlags]"Static,NonPublic,Public")
$constructor = $contextType.GetConstructor(
    [Reflection.BindingFlags]"Instance,NonPublic,Public",
    $null,
    @([string]),
    $null)

$copilotBundle = Join-Path $hostOutput "modules\Pagurian.Modules.Copilot"
@(
    "GitHub.Copilot.SDK.dll",
    "Microsoft.Extensions.AI.Abstractions.dll",
    "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
    "Microsoft.Extensions.Logging.Abstractions.dll",
    "Assets\icons8-pulse-50.png",
    "runtimes\win-$Platform\native\copilot.exe",
    "runtimes\win-$Platform\native\copilot-runtime.exe",
    "runtimes\win-$Platform\native\runtime.node"
) | ForEach-Object {
    $path = Join-Path $copilotBundle $_
    if (!(Test-Path $path) -or (Get-Item $path).Length -eq 0) {
        throw "Copilot module bundle is missing required private asset: $_"
    }
}

$forbidden = Get-ChildItem $copilotBundle -Filter "*.dll" -File -Recurse |
    Where-Object {
        [object[]]$arguments = @([string]$_.FullName)
        $hostOwnedMethod.Invoke($null, $arguments)
    } |
    ForEach-Object { [IO.Path]::GetRelativePath($copilotBundle, $_.FullName) }
if ($forbidden.Count -ne 0) {
    throw "Copilot module bundle contains host-owned assemblies: $($forbidden -join ', ')"
}

$observed = @()
foreach ($entry in $entries) {
    [object[]]$constructorArguments = @([string]$entry)
    $context = $constructor.Invoke($constructorArguments)
    $assembly = $context.LoadFromAssemblyPath($entry)
    $moduleType = $assembly.GetTypes() |
        Where-Object { $_ -ne $moduleBase -and $moduleBase.IsAssignableFrom($_) }
    $module = [Activator]::CreateInstance($moduleType)
    $observed += $module.DependencyVersion
}

if ($observed.Count -ne 2 -or $observed[0] -ne "1.0.0" -or $observed[1] -ne "2.0.0") {
    throw "Dependency isolation failed. Observed versions: $($observed -join ', ')"
}

Write-Output "Module dependency isolation passed: $($observed -join ', ')"
Write-Output "Copilot private SDK/runtime bundle passed."
