# Pagurian.Sdk

`Pagurian.Sdk` is the contract package for independently developed Pagurian modules.
It provides module, shell, cell, billboard, configuration, theme, logging, asset,
and message APIs. The package version and its Microsoft.UI.Reactor dependency
must exactly match the running Pagurian host.

Reference the package without deploying its runtime assembly:

```xml
<PackageReference Include="Pagurian.Sdk" Version="[0.1.0-preview.1]">
  <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
```

See the Pagurian repository's external-module documentation for the complete
project and bundle contract.
