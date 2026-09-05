using Microsoft.Win32;

namespace Pagurian;

// Host-level settings persisted at a fixed registry location
// (HKCU\Software\Pagurian). Today the only setting is ConfigDir: the folder
// holding Pagurian's configuration (currently exactly one file,
// config.json). The value is absent until the user picks a custom folder in
// the settings UI, so the default stays authoritative.
static class HostSettings
{
    private const string KeyPath = @"Software\Pagurian";
    private const string ConfigDirValue = "ConfigDir";

    public static string DefaultConfigDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pagurian");

    public static string ConfigDir
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                var dir = key?.GetValue(ConfigDirValue) as string;
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }
            catch (Exception ex)
            {
                PagurianLog.HostError("settings: failed to read ConfigDir from registry", ex);
            }
            return DefaultConfigDir;
        }
    }

    // Setting the default folder removes the value instead of writing it.
    public static void SetConfigDir(string dir)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (string.Equals(dir, DefaultConfigDir, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(ConfigDirValue, throwOnMissingValue: false);
            else
                key.SetValue(ConfigDirValue, dir, RegistryValueKind.String);
            PagurianLog.Host($"settings: ConfigDir = {dir}");
        }
        catch (Exception ex)
        {
            PagurianLog.HostError("settings: failed to write ConfigDir to registry", ex);
            throw;
        }
    }
}
