using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Pagurian.Modules.Copilot;

// WinRT Clipboard requires a foreground window in some desktop hosts. Use the
// clicked element's own HWND instead, without activating this or any other window.
internal static class SessionClipboard
{
    internal static bool TryCopy(FrameworkElement? owner, string text, out string feedback)
    {
        try
        {
            var island = owner?.XamlRoot?.ContentIslandEnvironment;
            nint hwnd = island is null ? 0 : Microsoft.UI.Win32Interop.GetWindowFromWindowId(island.AppWindowId);
            return TryCopy(hwnd, text, out feedback);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            feedback = $"Copy failed ({ex.GetType().Name}). Try again.";
            return false;
        }
    }

    internal static bool TryCopy(nint owner, string text, out string feedback)
    {
        feedback = "Copy failed. Try again.";
        if (owner == 0 || !IsWindow(owner))
        {
            feedback = "Copy failed: the session window is unavailable.";
            return false;
        }
        nint memory = 0;
        bool opened = false, copied = false;
        try
        {
            // Allocate before EmptyClipboard so allocation failures preserve it.
            memory = GlobalAlloc(0x0042, checked((nuint)((text.Length + 1) * sizeof(char))));
            if (memory == 0) return Failed("allocate", out feedback);
            nint data = GlobalLock(memory);
            if (data == 0) return Failed("lock", out feedback);
            Marshal.Copy((text + '\0').ToCharArray(), 0, data, text.Length + 1);
            Marshal.SetLastPInvokeError(0);
            if (!GlobalUnlock(memory) && Marshal.GetLastPInvokeError() != 0)
                return Failed("unlock", out feedback);
            if (!OpenClipboard(owner)) return Failed("open (clipboard may be busy)", out feedback);
            opened = true;
            if (!EmptyClipboard()) return Failed("empty", out feedback);
            if (SetClipboardData(13 /* CF_UNICODETEXT */, memory) == 0)
                return Failed("write", out feedback);
            memory = 0; // System now owns the allocation.
            copied = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or OverflowException)
        {
            feedback = $"Copy failed ({ex.GetType().Name}). Try again.";
        }
        finally
        {
            if (opened && !CloseClipboard())
            {
                copied = false;
                Failed("close", out feedback);
            }
            if (memory != 0 && GlobalFree(memory) != 0)
            {
                copied = false;
                Failed("release", out feedback);
            }
        }
        if (copied) feedback = "Copied full session ID.";
        return copied;
    }

    private static bool Failed(string operation, out string feedback)
    {
        feedback = $"Copy failed: {operation} (Windows error {Marshal.GetLastPInvokeError()}). Try again.";
        return false;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint memory);
}
