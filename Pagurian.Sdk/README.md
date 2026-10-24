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

## Cell simple mode

A cell model may implement `IShellCellSimpleMode` to offer a narrower
presentation when taskbar capacity is scarce. Implement it on the model passed
to `AddCell` (not on the `ShellCell` component): keep the boolean in model
state, make `SetSimpleMode` idempotent, and raise the model's existing
`Changed` event when the presentation actually changes so Reactor re-renders
and the host can re-measure. Models that do not implement the interface are
always Normal. The host owns when the mode is requested; the model owns what
Simple mode looks like.
