using Pagurian.Sdk;

namespace Pagurian.Modules.Copilot;

[PagurianModule(DisplayName = "GitHub Copilot")]
public sealed class CopilotModule : PagurianModule
{
    // Asset path helper: the module loads from the modules folder, so its
    // assets resolve next to the module dll, not next to the host exe.
    public static string GitHubIconPath =>
        ModuleAssets.Resolve(typeof(CopilotModule), "Assets/icons8-github-64.png");
}
