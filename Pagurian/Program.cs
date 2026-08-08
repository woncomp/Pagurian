using Pagurian;
using Microsoft.UI.Reactor;

// Bridge mode: invoked by a Copilot CLI hook as "Pagurian.exe --hook <event>"
// (see CopilotHookInstaller). Reads the event payload from stdin and forwards
// it to the running app over a named pipe, then exits — never starts WinUI
// and always exits 0 (preToolUse hooks are fail-closed).
if (args is ["--hook", var hookEvent])
{
    CopilotHookBridge.Run(hookEvent);
    return;
}

ReactorApp.Run(_ =>
{
    ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;

    // Copilot hook feature: register the CLI hooks and start listening for
    // bridge events before any window opens.
    CopilotHookInstaller.Install();
    CopilotSessionTracker.Start();
    SystemMetricsTracker.Start();

    var tray = ReactorApp.OpenTrayIcon(new TrayIconSpec(
        Icon: WindowIcon.FromPath(AppAssets.IconPath),
        Tooltip: "Pagurian",
        Key: WindowKey.Of("pagurian-tray"),
        IsVisible: true));

    var iconWindow = ReactorApp.OpenWindow(
        TaskbarIconWindow.CreateSpec(),
        () => new TaskbarIconWindow());

    TaskbarController.Start(iconWindow);

    tray.RightClick += (_, _) =>
    {
        if (TaskbarInterop.ShowTrayMenu(TaskbarController.IconWindowHwnd()) != TaskbarInterop.QuitCommandId)
            return;

        // ReactorApp.Exit(0) only calls WinUI's Application.Exit(), which closes
        // the windows but leaves this process running, so finish the job once
        // the WinUI unwind has been kicked off.
        TaskbarController.Stop();
        SystemMetricsTracker.Stop();
        CopilotSessionTracker.Stop();
        CopilotHookInstaller.Uninstall();
        tray.Close();
        ReactorApp.Exit(0);
        Environment.Exit(0);
    };
});
