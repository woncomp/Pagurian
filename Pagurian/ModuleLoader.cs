using System.Reflection;
using Pagurian.Sdk;

namespace Pagurian;

// Discovers module bundles from <exe>\modules and
// %LOCALAPPDATA%\Pagurian\modules. A bundle is an immediate child directory
// whose name matches its entry dll and .deps.json:
//
//   modules\Foo\Foo.dll
//   modules\Foo\Foo.deps.json
//
// Every bundle gets a private ModuleLoadContext for managed/native
// dependencies. Pagurian.Sdk, Reactor and WinUI contracts stay in the
// Default context so module components remain assignable to host types.
static class ModuleLoader
{
    private static readonly List<PagurianModule> _modules = new();
    private static readonly Dictionary<string, ShellAttribute> _catalog = new();
    private static readonly List<ShellAttribute> _kinds = new();
    private static readonly List<ModuleLoadContext> _loadContexts = new();

    public static IReadOnlyList<PagurianModule> Modules => _modules;
    public static IReadOnlyList<ShellAttribute> Kinds => _kinds;

    public static void LoadAll()
    {
        foreach (string root in ModuleDirs())
        {
            foreach (string bundle in FindBundles(root))
                TryLoadBundle(bundle);
        }

        foreach (PagurianModule module in _modules)
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
        foreach (PagurianModule module in _modules)
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
        _catalog.Clear();
        _kinds.Clear();
        // Contexts are intentionally non-collectible and live until process
        // exit. Keeping the references makes that lifetime explicit.
    }

    private static IEnumerable<string> FindBundles(string root)
    {
        string[] bundles;
        try
        {
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                PagurianLog.Host($"modules: created {root}");
            }

            string[] looseDlls = Directory.GetFiles(root, "*.dll", SearchOption.TopDirectoryOnly);
            if (looseDlls.Length > 0)
            {
                PagurianLog.HostError(
                    $"modules: ignored {looseDlls.Length} loose dll(s) in {root}; " +
                    "modules must use <name>\\<name>.dll bundles");
            }

            bundles = Directory.GetDirectories(root)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"modules: cannot scan {root}", ex);
            yield break;
        }

        foreach (string bundle in bundles)
            yield return bundle;
    }

    private static string ExeDir()
    {
        // Single-file self-extract publish points AppContext.BaseDirectory at
        // the temp extraction dir; the modules folder lives next to the real
        // exe. Under `dotnet run` ProcessPath is dotnet.exe, so only trust it
        // when it is our own apphost.
        string? exe = Environment.ProcessPath;
        if (exe != null && string.Equals(
                Path.GetFileNameWithoutExtension(exe),
                AppDomain.CurrentDomain.FriendlyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(exe)!;
        }

        return AppContext.BaseDirectory;
    }

    private static IEnumerable<string> ModuleDirs()
    {
        yield return Path.Combine(ExeDir(), "modules");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pagurian", "modules");
    }

    private static void TryLoadBundle(string bundlePath)
    {
        string bundleName = Path.GetFileName(Path.TrimEndingDirectorySeparator(bundlePath));
        string entryPath = Path.Combine(bundlePath, $"{bundleName}.dll");
        string depsPath = Path.Combine(bundlePath, $"{bundleName}.deps.json");

        if (!File.Exists(entryPath))
        {
            PagurianLog.HostError(
                $"modules: bundle {bundleName} is missing entry assembly {bundleName}.dll");
            return;
        }

        if (!File.Exists(depsPath))
        {
            PagurianLog.HostError(
                $"modules: bundle {bundleName} is missing {bundleName}.deps.json");
            return;
        }

        if (!ValidateBundleFiles(bundleName, bundlePath))
            return;

        var loadContext = new ModuleLoadContext(entryPath);
        Assembly assembly;
        try
        {
            assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(entryPath));
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"modules: failed to load bundle {bundleName}", ex);
            return;
        }

        if (!ValidateCompatibility(bundleName, assembly))
            return;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            string loaderErrors = string.Join(
                Environment.NewLine,
                ex.LoaderExceptions.Where(error => error != null).Select(error => error!.Message));
            PagurianLog.HostError(
                $"modules: type scan failed for {bundleName}; loader errors: {loaderErrors}",
                ex);
            return;
        }

        List<Type> moduleTypes = types
            .Where(type => type.GetCustomAttribute<PagurianModuleAttribute>() != null)
            .ToList();
        if (moduleTypes.Count != 1)
        {
            PagurianLog.HostError(
                $"modules: {bundleName} marks {moduleTypes.Count} types with " +
                $"[PagurianModule] ({string.Join(", ", moduleTypes.Select(type => type.FullName))}); " +
                "exactly one is required — bundle rejected");
            return;
        }

        Type moduleType = moduleTypes[0];
        if (!typeof(PagurianModule).IsAssignableFrom(moduleType))
        {
            PagurianLog.HostError(
                $"modules: {moduleType.FullName} is marked [PagurianModule] but does not " +
                "derive from PagurianModule — bundle rejected");
            return;
        }

        var kinds = new List<ShellAttribute>();
        var kindIds = new HashSet<string>();
        foreach (Type type in types)
        {
            ShellAttribute? attribute = type.GetCustomAttribute<ShellAttribute>();
            if (attribute == null)
                continue;

            if (!typeof(Shell).IsAssignableFrom(type))
            {
                PagurianLog.HostError(
                    $"modules: {type.FullName} is marked [Shell] but does not derive from " +
                    "Shell — bundle rejected");
                return;
            }

            if (attribute.ConfigurationView is { } configurationView &&
                (!typeof(ShellConfiguration).IsAssignableFrom(configurationView) ||
                 configurationView.IsAbstract ||
                 configurationView.ContainsGenericParameters))
            {
                PagurianLog.HostError(
                    $"modules: {type.FullName} names invalid configuration view " +
                    $"{configurationView.FullName}; it must be a concrete " +
                    $"{nameof(ShellConfiguration)} subclass — bundle rejected");
                return;
            }

            if (!kindIds.Add(type.FullName!))
            {
                PagurianLog.HostError(
                    $"modules: duplicate shell kind id {type.FullName} in " +
                    $"{bundleName} — bundle rejected");
                return;
            }

            attribute.ShellType = type;
            kinds.Add(attribute);
        }

        if (_modules.Any(module => module.Id == moduleType.FullName))
        {
            PagurianLog.HostError(
                $"modules: duplicate module id {moduleType.FullName} ({bundleName}) — bundle rejected");
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
        _loadContexts.Add(loadContext);
        _modules.Add(module);
        foreach (ShellAttribute kind in kinds)
        {
            _catalog[kind.ShellType.FullName!] = kind;
            _kinds.Add(kind);
        }

        PagurianLog.Host(
            $"modules: loaded {module.Id} with {kinds.Count} shell kind(s) from " +
            $"{bundleName} in {loadContext.Name}");
    }

    private static bool ValidateBundleFiles(string bundleName, string bundlePath)
    {
        try
        {
            string[] forbiddenFiles = Directory
                .GetFiles(bundlePath, "*.dll", SearchOption.AllDirectories)
                .Where(ModuleLoadContext.IsHostOwnedAssemblyFile)
                .Select(path => Path.GetRelativePath(bundlePath, path))
                .ToArray();
            if (forbiddenFiles.Length == 0)
                return true;

            PagurianLog.HostError(
                $"modules: bundle {bundleName} contains host-owned assemblies: " +
                string.Join(", ", forbiddenFiles));
            return false;
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"modules: cannot inspect bundle {bundleName}", ex);
            return false;
        }
    }

    private static bool ValidateCompatibility(string bundleName, Assembly assembly)
    {
        try
        {
            string? sdkVersion = ModuleMetadata(assembly, "PagurianSdkVersion");
            string? reactorVersion = ModuleMetadata(assembly, "PagurianReactorVersion");
            string hostSdkVersion = HostSdkVersion();
            string hostReactorVersion = HostReactorVersion();

            if (!string.Equals(sdkVersion, hostSdkVersion, StringComparison.OrdinalIgnoreCase))
            {
                PagurianLog.HostError(
                    $"modules: bundle {bundleName} requires Pagurian.Sdk " +
                    $"{sdkVersion ?? "<missing>"}; host provides {hostSdkVersion}");
                return false;
            }

            if (!string.Equals(reactorVersion, hostReactorVersion, StringComparison.OrdinalIgnoreCase))
            {
                PagurianLog.HostError(
                    $"modules: bundle {bundleName} requires Microsoft.UI.Reactor " +
                    $"{reactorVersion ?? "<missing>"}; host provides {hostReactorVersion}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            PagurianLog.HostError($"modules: cannot validate {bundleName} compatibility metadata", ex);
            return false;
        }
    }

    private static string? ModuleMetadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key == key)
            .Select(attribute => attribute.Value)
            .SingleOrDefault();

    private static string HostSdkVersion()
    {
        string informationalVersion = typeof(PagurianModule).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "";
        int metadata = informationalVersion.IndexOf('+');
        return metadata < 0 ? informationalVersion : informationalVersion[..metadata];
    }

    private static string HostReactorVersion() =>
        typeof(PagurianModule).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ReactorPackageVersion")
            .Value ?? "";
}
