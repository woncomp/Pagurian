namespace Pagurian.Sdk;

// Marks a Shell subclass as a shell kind offered by its module. The attribute
// instance itself IS the kind: the kind id is the annotated type's FullName
// (used in config.json), DisplayName is for a future configuration UI, and
// CreateShell is the factory the host calls for each matching config entry.
//
// The default factory requires a public parameterless constructor; shared
// module state is typically fetched from the module singleton inside that
// constructor. Modules needing custom creation logic subclass this attribute
// and override CreateShell (which is why the class is not sealed).
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class ShellAttribute : Attribute
{
    public string DisplayName { get; init; } = "";

    // Backfilled by the host's module loader at discovery time.
    internal Type ShellType { get; set; } = null!;

    public virtual Shell CreateShell() => (Shell)Activator.CreateInstance(ShellType)!;
}
