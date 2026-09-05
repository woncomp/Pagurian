# External Pagurian modules

Pagurian loads every module from a self-contained folder. The folder and
entry assembly names must match, and the module must ship its `.deps.json`:

```text
modules/
  Example.Module/
    Example.Module.dll
    Example.Module.deps.json
    Some.Private.Dependency.dll
    Assets/
```

The host supports modules beside `Pagurian.exe` and under
`%LOCALAPPDATA%\Pagurian\modules`. Loose DLLs directly under either modules
directory are ignored.

## Project contract

External modules target the same runtime and Reactor version as the host:

```xml
<PropertyGroup>
  <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
  <UseWinUI>true</UseWinUI>
  <EnableDynamicLoading>true</EnableDynamicLoading>
  <EnableMsixTooling>false</EnableMsixTooling>
  <AppxGeneratePriEnabled>false</AppxGeneratePriEnabled>
  <IncludeProjectPriFile>false</IncludeProjectPriFile>
  <Platforms>x64</Platforms>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Pagurian.Sdk" Version="[0.1.0-preview.1]">
    <ExcludeAssets>runtime</ExcludeAssets>
  </PackageReference>
  <PackageReference Include="Microsoft.UI.Reactor" Version="0.1.0-preview.12">
    <ExcludeAssets>runtime</ExcludeAssets>
  </PackageReference>
</ItemGroup>
```

`ExcludeAssets="runtime"` is required: the host owns the SDK and UI contract
assemblies. Do not put Pagurian.Sdk, Reactor, WinUI, Windows App SDK, WebView2
projection, or WinRT contract DLLs into a module folder.

Ship the module entry DLL, its `.deps.json`, private managed/native
dependencies, satellite resources, and assets. Each module runs in its own
non-collectible load context, so different modules may carry different
versions of the same private dependency. Framework and Pagurian UI contracts
remain shared with the host.

The loader requires exact Pagurian.Sdk and Microsoft.UI.Reactor package
versions. Incompatible bundles are rejected and reported in
`%LOCALAPPDATA%\Pagurian\pagurian.log`.

Run `tests\Verify-ModuleIsolation.ps1` to build two fixture modules carrying
different versions of the same private assembly and verify that both versions
operate in one Pagurian process.

## Local SDK package

A standalone repository can pin Pagurian as a Git submodule and generate a
local feed:

```powershell
git submodule update --init --recursive
dotnet pack vendor\Pagurian\Pagurian.Sdk\Pagurian.Sdk.csproj `
  -c Release -p:Platform=x64 -o artifacts\nuget
```

Reference `artifacts\nuget` from `NuGet.Config`, restore the external module,
then deploy its folder bundle under one of the supported modules directories.
A module is trusted in-process code; dependency isolation is not a security or
crash boundary.
