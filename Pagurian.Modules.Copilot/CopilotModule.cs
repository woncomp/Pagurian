using System.Runtime.InteropServices;
using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

[PagurianModule(DisplayName = "GitHub Copilot")]
public sealed class CopilotModule : PagurianModule
{
    public static CopilotModule Instance { get; private set; } = null!;

    internal CopilotUsageService Usage { get; } = new();

    public CopilotModule()
    {
        Instance = this;
    }

    public override void Startup() => Usage.Start(Log);

    public override void Shutdown() => Usage.Shutdown();

    // Asset path helper: the module loads from the modules folder, so its
    // assets resolve next to the module dll, not next to the host exe.
    public static string GitHubIconPath =>
        ModuleAssets.Resolve(typeof(CopilotModule), "Assets/icons8-github-64.png");

    public static string UsageIconPath =>
        ModuleAssets.Resolve(typeof(CopilotModule), "Assets/icons8-pulse-50.png");

    internal static string CopilotRuntimePath =>
        ModuleAssets.Resolve(
            typeof(CopilotModule),
            $"runtimes/{RuntimeRid}/native/copilot-runtime.exe");

    private static string RuntimeRid =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            var architecture => throw new PlatformNotSupportedException(
                $"GitHub Copilot SDK does not support {architecture} in Pagurian."),
        };
}
