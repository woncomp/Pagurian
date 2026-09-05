using System.Reflection;
using System.Runtime.Loader;

namespace Pagurian;

// One non-collectible load context per module bundle. Module-private managed
// and native dependencies resolve through the bundle's .deps.json while the
// UI/type contracts are always shared from the host's Default context.
sealed class ModuleLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedExactNames =
    [
        "Pagurian.Sdk",
        "Reactor",
        "Reactor.Wrappers.Abstractions",
        "WinRT.Runtime",
        "Microsoft.Windows.SDK.NET",
    ];

    private static readonly string[] SharedNamePrefixes =
    [
        "Microsoft.UI.",
        "Microsoft.WinUI",
        "Microsoft.Windows.",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.InteractiveExperiences.",
        "Microsoft.Security.Authentication.OAuth.",
        "Microsoft.Web.WebView2.",
        "System.Private.Windows.",
    ];

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _bundleRoot;
    private readonly string _bundleRootPrefix;

    public ModuleLoadContext(string entryAssemblyPath)
        : base($"Pagurian.Module:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: false)
    {
        var fullEntryPath = Path.GetFullPath(entryAssemblyPath);
        _bundleRoot = Path.GetDirectoryName(fullEntryPath)!;
        _bundleRootPrefix = Path.TrimEndingDirectorySeparator(_bundleRoot) + Path.DirectorySeparatorChar;
        _resolver = new AssemblyDependencyResolver(fullEntryPath);
    }

    public static bool IsHostOwnedAssemblyName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return SharedExactNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
               SharedNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHostOwnedAssemblyFile(string path) =>
        IsHostOwnedAssemblyName(Path.GetFileNameWithoutExtension(path));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsHostOwnedAssemblyName(assemblyName.Name))
        {
            // Returning the already loaded instance preserves type identity.
            // Returning null lets Default resolve a shared assembly that has
            // not been materialized yet; never consult the module resolver.
            return Default.Assemblies.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.GetName().Name,
                    assemblyName.Name,
                    StringComparison.OrdinalIgnoreCase));
        }

        string? resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolvedPath is null
            ? null
            : LoadFromAssemblyPath(ValidateBundlePath(resolvedPath));
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? resolvedPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return resolvedPath is null
            ? nint.Zero
            : LoadUnmanagedDllFromPath(ValidateBundlePath(resolvedPath));
    }

    private string ValidateBundlePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_bundleRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileLoadException(
                $"Module dependency '{fullPath}' resolves outside bundle '{_bundleRoot}'.");
        }

        return fullPath;
    }
}
