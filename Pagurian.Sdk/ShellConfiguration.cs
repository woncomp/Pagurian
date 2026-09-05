using System.Text.Json;
using Microsoft.UI.Reactor.Core;

namespace Pagurian.Sdk;

// A module-owned editor for one configured shell instance. The host mounts
// the component by type in the Settings window and owns the surrounding
// selection, draft, Save, and Revert behavior.
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
    IThemeService Theme,
    Logger Log);
