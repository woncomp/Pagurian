namespace Pagurian.Sdk;

// Resolves a module's asset files relative to the module assembly's location
// (modules load from the modules folder, not from AppContext.BaseDirectory,
// so assets ship next to the module dll).
public static class ModuleAssets
{
    public static string Resolve(Type moduleType, string relativePath) =>
        Path.Combine(
            Path.GetDirectoryName(moduleType.Assembly.Location)!,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
}
