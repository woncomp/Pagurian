using Pagurian;
using Microsoft.UI.Reactor;

// Bridge mode: "Pagurian.exe post {shell_id} <cmd> [args...]" delivers a
// message to a shell of the running app over a named pipe, then exits —
// never starts WinUI and always exits 0 (callers like the Copilot preToolUse
// hook are fail-closed).
if (args is ["post", .. var postArgs])
{
    PostBridge.Run(postArgs);
    return;
}

ReactorApp.Run(_ =>
{
    ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;

    PagurianLog.Initialize();

    // Last-resort crash logging: XAML fail-fasts normally leave no trace in
    // the console or WER, so log before the process goes down.
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        PagurianLog.Host($"FATAL unhandled exception: {e.ExceptionObject}");
    Microsoft.UI.Xaml.Application.Current?.UnhandledException += (_, e) =>
        PagurianLog.Host($"FATAL XAML unhandled exception: {e.Exception}");

    ModuleLoader.LoadAll();
    TrayShells.LoadFromConfig(TrayConfig.Load());
    ShellMessageServer.Start();

    var tray = ReactorApp.OpenTrayIcon(new TrayIconSpec(
        Icon: WindowIcon.FromPath(AppAssets.ApplicationIconPath),
        Tooltip: "Pagurian",
        Key: WindowKey.Of("pagurian-tray"),
        IsVisible: true));

    var trayWindow = ReactorApp.OpenWindow(
        TaskbarTrayWindow.CreateSpec(),
        () => new TaskbarTrayWindow());

    TaskbarController.Start(trayWindow);

    tray.DoubleClick += (_, _) => SettingsWindow.OpenOrActivate(SettingsPage.Shells);

    tray.RightClick += (_, _) =>
    {
        var cmd = TaskbarInterop.ShowTrayMenu(TaskbarController.TrayWindowHwnd());
        if (cmd == TaskbarInterop.EditShellsCommandId)
        {
            SettingsWindow.OpenOrActivate(SettingsPage.Shells);
            return;
        }
        if (cmd == TaskbarInterop.SettingsCommandId)
        {
            SettingsWindow.OpenOrActivate(SettingsPage.General);
            return;
        }
        if (cmd != TaskbarInterop.QuitCommandId)
            return;

        // ReactorApp.Exit(0) only calls WinUI's Application.Exit(), which closes
        // the windows but leaves this process running, so finish the job once
        // the WinUI unwind has been kicked off.
        TaskbarController.Stop();
        ShellMessageServer.Stop();
        SettingsWindow.CloseIfOpen();
        TrayShells.ShutdownAll();
        ModuleLoader.ShutdownAll();
        tray.Close();
        ReactorApp.Exit(0);
        ShellNavigationDiagnostics.CompleteAllForExit();
        PagurianLog.FlushNavigationOnExit();
        Environment.Exit(0);
    };
});
