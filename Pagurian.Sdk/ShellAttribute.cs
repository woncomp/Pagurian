namespace Pagurian.Sdk;

// Marks a Shell subclass as a shell kind offered by its module. The attribute
// instance itself IS the kind: the kind id is the annotated type's FullName
// (used in config.json), DisplayName and PreviewIcon feed the configuration
// UI, and CreateShell is the factory the host calls for each matching config
// entry.
//
// The default factory requires a public parameterless constructor; shared
// module state is typically fetched from the module singleton inside that
// constructor. Modules needing custom creation logic subclass this attribute
// and override CreateShell (which is why the class is not sealed).
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ShellAttribute : Attribute
{
    public string DisplayName { get; init; } = "";

    // Optional module-owned Reactor component shown when this shell kind is
    // selected in the host Shell editor. The loader validates that the
    // type is a concrete ShellConfiguration subclass.
    public Type? ConfigurationView { get; init; }

    // Module-assembly-relative path to the icon shown for this kind in the
    // configuration UI (e.g. "Assets/foo.png"); empty = the host falls back
    // to the Pagurian app icon.
    public string PreviewIcon { get; init; } = "";

    // Backfilled by the host's module loader at discovery time.
    internal Type ShellType { get; set; } = null!;

    // Absolute resolved PreviewIcon path, or null when unset / not found on
    // disk (callers then use the fallback icon).
    public string? PreviewIconPath
    {
        get
        {
            if (PreviewIcon.Length == 0)
                return null;
            var path = ModuleAssets.Resolve(ShellType, PreviewIcon);
            return File.Exists(path) ? path : null;
        }
    }

    public virtual Shell CreateShell() => (Shell)Activator.CreateInstance(ShellType)!;
}
