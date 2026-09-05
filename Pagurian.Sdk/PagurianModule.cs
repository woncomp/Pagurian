using System.Reflection;

namespace Pagurian.Sdk;

// Marks the single module class of a module assembly. The host discovers
// modules by reflecting each candidate dll for exactly one type carrying this
// attribute; the module's id is that type's FullName (there is no separate id
// member). An assembly with zero marked types is not a module; two or more
// marked types make the whole dll fail to load.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PagurianModuleAttribute : Attribute
{
    public string DisplayName { get; init; } = "";
}

// Base class of every Pagurian module. One module assembly = one subclass of
// this class, marked with [PagurianModule]. Keep Startup() light: heavy
// resources (samplers, hook files, listeners) belong in Shell.Startup, which
// runs only for shells the user actually configured into the tray.
public abstract class PagurianModule
{
    protected PagurianModule()
    {
        Id = GetType().FullName ?? GetType().Name;
        DisplayName = GetType().GetCustomAttribute<PagurianModuleAttribute>()?.DisplayName ?? "";
        Log = Logger.For(Id);
    }

    // Module id: the concrete class's FullName. Cached in the base
    // constructor so the attribute/type name is the single source of truth.
    public string Id { get; }

    public string DisplayName { get; }

    // Unified-log facade tagged with the module id. Replaced by the host with
    // an identically tagged instance after loading (same behavior; explicit
    // injection keeps the host in control of the sink).
    public Logger Log { get; internal set; }

    public virtual void Startup() { }

    public virtual void Shutdown() { }
}
