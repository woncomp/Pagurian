using System.Reflection;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;

namespace Pagurian;

// Compatibility bridge for Reactor 0.1.0-preview.12. Explorer can destroy a
// child HWND without raising WinUI Window.Closed. Public Close re-enters native
// destruction (AV); public Dispose writes SystemBackdrop on the dead surface
// (also AV). Replay the framework's normal two Closed handlers in their original
// order: mark the native surface gone and dispose the host, then unregister and
// dispose ReactorWindow. This does not close or mutate any live native window.
// Remove when Reactor exposes native-window-loss cleanup as a public API.
internal static class ReactorDestroyedWindow
{
    private static readonly FieldInfo HostClosed = typeof(ReactorHost).GetField(
        "_closedHandler", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("Reactor native-loss adapter requires _closedHandler");
    private static readonly MethodInfo WindowClosed = typeof(ReactorWindow).GetMethod(
        "OnNativeClosed", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException("Reactor native-loss adapter requires OnNativeClosed");

    internal static void CompleteClose(ReactorWindow window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow);
        if (TaskbarInterop.IsWindow(hwnd))
            throw new InvalidOperationException("Native-loss cleanup requires a destroyed HWND.");
        ((Delegate)HostClosed.GetValue(window.Host)!).DynamicInvoke(null, null);
        WindowClosed.Invoke(window, [null, null]);
    }
}

