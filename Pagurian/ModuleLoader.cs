using System.Reflection;
using Pagurian.Sdk;

namespace Pagurian;

// Discovers and loads module assemblies from <exe>\modules and
// %LOCALAPPDATA%\Pagurian\modules, then builds the shell-kind catalog
// (shell class FullName -> [Shell] attribute instance) that TrayShells
// instantiates from config.
//
// Loading uses the Default AssemblyLoadContext (Assembly.LoadFrom) on
// purpose: modules return Reactor/WinUI types that the host consumes, so both
// sides must share one type universe (same TFM, same Reactor version).
//
// Validation is strict and per-dll; a failing dll never stops the host:
//  - 0 types with [PagurianModule]  -> not a module assembly, skipped;
//  - 2+ types with [PagurianModule] -> the dll fails to load;
//  - the marked type must derive from PagurianModule;
//  - [Shell] types must derive from Shell, kind ids unique within the dll;
//  - module ids (FullName) unique across all loaded modules.
static class ModuleLoader
{
    private static readonly List<PagurianModule> _modules = new();
    private static readonly Dictionary<string, ShellAttribute> _catalog = new();
    private static readonly List<ShellAttribute> _kinds = new();

    // The loaded modules and the shell-kind catalog in discovery order (the
    // settings UI builds its module pool from these).
    public static IReadOnlyList<PagurianModule> Modules => _modules;
    public static IReadOnlyList<ShellAttribute> Kinds => _kinds;

    public static void LoadAll()
    {
        foreach (var dir in ModuleDirs())
        {
            string[] dlls;
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    PagurianLog.Host($"modules: created {dir}");
                }
                dlls = Directory.GetFiles(dir, "*.dll");
            }
            catch (Exception ex)
            {
                PagurianLog.HostError($"modules: cannot scan {dir}", ex);
                continue;
            }
            foreach (var dll in dlls)
                TryLoad(dll);
        }

        foreach (var module in _modules)
        {
            try
            {
                module.Startup();
                PagurianLog.Host($"modules: started {module.Id}");
            }
            catch (Exception ex)
            {
                PagurianLog.HostError($"modules: {module.Id} Startup failed", ex);
            }
        }
    }

    public static bool TryGetKind(string shellType, out ShellAttribute kind) =>
        _catalog.TryGetValue(shellType, out kind!);

    public static void ShutdownAll()
    {
        foreach (var module in _modules)
        {
            try
            {
                module.Shutdown();
            }
            catch (Exception ex)
            {
                PagurianLog.HostError($"modules: {module.Id} Shutdown failed", ex);
            }
        }
        _modules.Clear();
    }

    private static string ExeDir()
    {
        // Single-file self-extract publish points AppContext.BaseDirectory at
        // the temp extraction dir; the modules folder lives next to the real
        // exe. Under `dotnet run` ProcessPath is dotnet.exe, so only trust it
        // when it is our own apphost.
        var exe = Environment.ProcessPath;
        if (exe != null && string.Equals(
                Path.GetFileNameWithoutExtension(exe),
                AppDomain.CurrentDomain.FriendlyName,
                StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(exe)!;
        return AppContext.BaseDirectory;
    }

    private static IEnumerable<string> ModuleDirs()
    {
        yield return Path.Combine(ExeDir(), "modules");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pagurian", "modules");
    }

    private static void TryLoad(string path)
    {
        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(path);
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"modules: failed to load {Path.GetFileName(path)}", ex);
            return;
        }

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            PagurianLog.HostError($"modules: type scan failed for {Path.GetFileName(path)}", ex);
            return;
        }

        var moduleTypes = types
            .Where(t => t.GetCustomAttribute<PagurianModuleAttribute>() != null)
            .ToList();
        if (moduleTypes.Count == 0)
            return; // not a module assembly (e.g. a dependency dll)
        if (moduleTypes.Count > 1)
        {
            PagurianLog.HostError(
                $"modules: {Path.GetFileName(path)} marks {moduleTypes.Count} types with " +
                $"[PagurianModule] ({string.Join(", ", moduleTypes.Select(t => t.FullName))}); " +
                "exactly one is allowed — dll rejected");
            return;
        }

        var moduleType = moduleTypes[0];
        if (!typeof(PagurianModule).IsAssignableFrom(moduleType))
        {
            PagurianLog.HostError(
                $"modules: {moduleType.FullName} is marked [PagurianModule] but does not " +
                "derive from PagurianModule — dll rejected");
            return;
        }

        var kinds = new List<ShellAttribute>();
        var kindIds = new HashSet<string>();
        foreach (var type in types)
        {
            var attr = type.GetCustomAttribute<ShellAttribute>();
            if (attr == null)
                continue;
            if (!typeof(Shell).IsAssignableFrom(type))
            {
                PagurianLog.HostError(
                    $"modules: {type.FullName} is marked [Shell] but does not derive from " +
                    "Shell — dll rejected");
                return;
            }
            if (attr.ConfigurationView is { } configurationView &&
                (!typeof(ShellConfiguration).IsAssignableFrom(configurationView) ||
                 configurationView.IsAbstract ||
                 configurationView.ContainsGenericParameters))
            {
                PagurianLog.HostError(
                    $"modules: {type.FullName} names invalid configuration view " +
                    $"{configurationView.FullName}; it must be a concrete " +
                    $"{nameof(ShellConfiguration)} subclass — dll rejected");
                return;
            }
            if (!kindIds.Add(type.FullName!))
            {
                PagurianLog.HostError(
                    $"modules: duplicate shell kind id {type.FullName} in " +
                    $"{Path.GetFileName(path)} — dll rejected");
                return;
            }
            attr.ShellType = type;
            kinds.Add(attr);
        }

        if (_modules.Any(m => m.Id == moduleType.FullName))
        {
            PagurianLog.HostError(
                $"modules: duplicate module id {moduleType.FullName} " +
                $"({Path.GetFileName(path)}) — dll rejected");
            return;
        }

        PagurianModule module;
        try
        {
            module = (PagurianModule)Activator.CreateInstance(moduleType)!;
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"modules: cannot instantiate {moduleType.FullName}", ex);
            return;
        }

        module.Log = Logger.For(module.Id);
        _modules.Add(module);
        foreach (var kind in kinds)
        {
            _catalog[kind.ShellType.FullName!] = kind;
            _kinds.Add(kind);
        }
        PagurianLog.Host(
            $"modules: loaded {module.Id} with {kinds.Count} shell kind(s) from " +
            Path.GetFileName(path));
    }
}
