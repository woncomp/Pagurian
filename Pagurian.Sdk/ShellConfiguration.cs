using System.Text.Json;
using Microsoft.UI.Reactor.Core;

namespace Pagurian.Sdk;

// A module-owned editor for one configured shell instance. The host mounts
// the component by type on the Shells page in Settings and owns the surrounding
// selection and shared draft, including Save, Revert, and close/discard behavior.
public abstract class ShellConfiguration : Component<ShellConfigurationProps>
{
    protected string InstanceId => Props.InstanceId;

    protected JsonElement? Settings => Props.Settings;

    protected IThemeService Theme => Props.Theme;

    protected Logger Log => Props.Log;

    // Configuration components publish a complete replacement settings
    // object. Clone it at the SDK boundary so the draft never depends on a
    // JsonDocument owned by module code.
    protected void SetSettings(JsonElement? settings) =>
        Props.SetSettings(settings is { } value ? value.Clone() : null);
}

public sealed record ShellConfigurationProps(
    string InstanceId,
    JsonElement? Settings,
    Action<JsonElement?> SetSettings,
    // The effective theme of the configuration surface hosting this component.
    IThemeService Theme,
    Logger Log);
