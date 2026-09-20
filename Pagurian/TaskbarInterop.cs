using System.Runtime.InteropServices;
using System.Text;

namespace Pagurian;

static class TaskbarInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(POINT p) =>
            p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? szDevice;
    }

    public readonly record struct DisplayMonitor(
        string DeviceName,
        RECT MonitorRect,
        RECT WorkRect,
        bool IsPrimary);

    public const int GWL_STYLE = -16;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CHILD = 0x40000000;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const int GA_PARENT = 1;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter,
        string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, int gaFlags);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, [Out] StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr GetWindowStyle(IntPtr h) => GetWindowLongPtr64(h, GWL_STYLE);
    public static void SetWindowStyle(IntPtr h, IntPtr s) => SetWindowLongPtr64(h, GWL_STYLE, s);

    public static bool TryGetWindowRect(IntPtr hwnd, out RECT rect)
    {
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect) &&
            rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            return true;
        }

        rect = default;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest,
        int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    private const uint SRCCOPY = 0x00CC0020;

    // Captures a physical-pixel screen region as RGBA bytes (row-major, top-down)
    // with a single BitBlt into a memory DC.
    //
    // Every read from the screen DC synchronizes with DWM composition and can
    // stall for ~one display frame each (measured here: ~17 ms idle, ~33 ms
    // under contention), so per-pixel GetPixel on the screen DC costs one frame
    // PER PIXEL — ~50 frames per sampling round, which starved the UI thread
    // (and, once the widget is parented into the taskbar, wedged the taskbar's
    // attached input queue: the whole taskbar stopped responding). One BitBlt
    // pays the frame cost once; reads from the memory DC are plain memory
    // accesses. Must be called off the UI thread.
    public static byte[]? CaptureScreenRegionPixels(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
            return null;

        IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            bmp = CreateCompatibleBitmap(screen, width, height);
            if (mem == IntPtr.Zero || bmp == IntPtr.Zero)
                return null;

            var old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, width, height, screen, x, y, SRCCOPY))
                return null;

            var rgba = new byte[width * height * 4];
            for (var py = 0; py < height; py++)
            for (var px = 0; px < width; px++)
            {
                var c = GetPixel(mem, px, py); // COLORREF 0x00BBGGRR, 0xFFFFFFFF on failure
                if (c == 0xFFFFFFFF)
                    return null;
                var i = (py * width + px) * 4;
                rgba[i] = (byte)(c & 0xFF);
                rgba[i + 1] = (byte)((c >> 8) & 0xFF);
                rgba[i + 2] = (byte)((c >> 16) & 0xFF);
                rgba[i + 3] = 255;
            }
            SelectObject(mem, old);
            return rgba;
        }
        finally
        {
            if (bmp != IntPtr.Zero)
                DeleteObject(bmp);
            if (mem != IntPtr.Zero)
                DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Robust average of a column slice [col0, col1) of a captured region:
    // pixels are sorted by luminance and only the middle 50% are averaged per
    // channel, so acrylic noise and text/icon glyph pixels (outliers) are
    // dropped. The returned color is always fully opaque; null on an
    // empty/invalid slice.
    public static Windows.UI.Color? TrimmedMeanColor(byte[] rgba, int width, int height, int col0, int col1)
    {
        col0 = Math.Clamp(col0, 0, width);
        col1 = Math.Clamp(col1, 0, width);
        if (height <= 0 || col1 - col0 <= 0)
            return null;

        var pixels = new System.Collections.Generic.List<(double Lum, byte R, byte G, byte B)>((col1 - col0) * height);
        for (var py = 0; py < height; py++)
        for (var px = col0; px < col1; px++)
        {
            var i = (py * width + px) * 4;
            byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            pixels.Add((0.299 * r + 0.587 * g + 0.114 * b, r, g, b));
        }

        pixels.Sort((a, b) => a.Lum.CompareTo(b.Lum));
        var from = pixels.Count / 4;
        var to = pixels.Count - from; // middle 50%: [from, to)
        long rSum = 0, gSum = 0, bSum = 0;
        for (var i = from; i < to; i++)
        {
            rSum += pixels[i].R;
            gSum += pixels[i].G;
            bSum += pixels[i].B;
        }
        var n = to - from;
        return Windows.UI.Color.FromArgb(255,
            (byte)(rSum / n), (byte)(gSum / n), (byte)(bSum / n));
    }

    public static IntPtr FindTaskbar() => FindWindowW("Shell_TrayWnd", null);

    // All taskbar windows: the primary Shell_TrayWnd plus every secondary
    // taskbar (Shell_SecondaryTrayWnd, present only while "show taskbar on
    // all displays" is on), each mapped to its owning monitor.
    public static IReadOnlyList<(nint Hwnd, nint Monitor)> FindAllTaskbars()
    {
        var taskbars = new List<(nint Hwnd, nint Monitor)>();
        var primary = FindTaskbar();
        if (primary != IntPtr.Zero)
            taskbars.Add((primary, MonitorFromWindow(primary, MONITOR_DEFAULTTONEAREST)));
        nint after = IntPtr.Zero;
        nint secondary;
        while ((secondary = FindWindowExW(IntPtr.Zero, after, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            taskbars.Add((secondary, MonitorFromWindow(secondary, MONITOR_DEFAULTTONEAREST)));
            after = secondary;
        }
        return taskbars;
    }

    // Monitor identity/geometry for an HMONITOR (from EnumDisplayMonitors or
    // MonitorFromWindow), exposed for the display topology layer.
    public static bool TryGetMonitorInfo(IntPtr monitor, out DisplayMonitor info) =>
        TryReadDisplayMonitor(monitor, out info);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_LBUTTON = 0x01;

    // Physical left-button state, for the native clock's pressed feedback.
    public static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    public const int QuitCommandId = 1;
    public const int SettingsCommandId = 2;
    public const int EditShellsCommandId = 3;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags,
        int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // Shows the regular Win32 context menu at
    // the cursor and returns the selected command id, or 0 when the menu was
    // dismissed. Blocks while the menu is open, like every classic tray app.
    public static int ShowTrayMenu(IntPtr owner)
    {
        var cursor = GetCursorPosition();
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return 0;
        try
        {
            AppendMenuW(menu, MF_STRING, (IntPtr)EditShellsCommandId, "Edit Shells…");
            AppendMenuW(menu, MF_STRING, (IntPtr)SettingsCommandId, "Settings…");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null!);
            AppendMenuW(menu, MF_STRING, (IntPtr)QuitCommandId, "Quit");
            // Required so the menu dismisses correctly when clicking elsewhere.
            SetForegroundWindow(owner);
            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
                cursor.X, cursor.Y, 0, owner, IntPtr.Zero);
            PostMessageW(owner, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL
            return cmd;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    private const uint ABM_GETTASKBARPOS = 0x00000005;

    public static bool TryGetTaskbarRect(out RECT rect)
    {
        var hwnd = FindWindowW("Shell_TrayWnd", null);
        if (TryGetTaskbarRect(hwnd, out rect))
            return true;

        // Fallback: official appbar API (works even when window enumeration
        // doesn't see Shell_TrayWnd). Rare enough that its blocking nature is
        // acceptable. Primary-only: no appbar message exists for secondaries.
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != IntPtr.Zero &&
            data.rc.Right > data.rc.Left && data.rc.Bottom > data.rc.Top)
        {
            rect = data.rc;
            return true;
        }

        rect = default;
        return false;
    }

    // The taskbar window's rect directly. GetWindowRect sends no message,
    // unlike SHAppBarMessage (a cross-process SendMessage to the taskbar that
    // can block the caller — and with our window parented into the taskbar, a
    // blocked UI thread wedges the taskbar's attached input queue).
    // GetWindowRect also reflects the actual on-screen position, e.g. during
    // the auto-hide slide.
    public static bool TryGetTaskbarRect(IntPtr taskbar, out RECT rect)
    {
        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out rect) &&
            rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            return true;
        }

        rect = default;
        return false;
    }

    // Windows 11's taskbar windows can include an empty strip above the actual
    // taskbar controls. Anchor to a visible control's cross-axis bounds rather
    // than treating that outer host rectangle as the taskbar surface.
    public static bool TryGetTaskbarContentRect(out RECT rect) =>
        TryGetTaskbarContentRect(FindTaskbar(), out rect);

    // Known classes of the taskbar's system-area controls: the notification
    // tray (primary taskbar), the clock, and the show-desktop button. A
    // right-edge tray anchors to the left of whichever is present. The
    // secondary taskbar's clock is a XAML island whose bridging window class
    // varies by build, so the geometry heuristic below covers whatever class
    // it actually uses (its signature lands in the log for confirmation).
    private static readonly string[] SystemAreaClassNames =
        ["TrayNotifyWnd", "TrayClockWClass", "TrayShowDesktopButtonWClass"];

    private static string? _loggedSystemAreaChildren;

    // Left physical-pixel boundary of the taskbar's system area — where a
    // right-edge tray's cells must end. Returns false when no system-area
    // control is found; the caller then keeps the content rect's right edge.
    public static bool TryGetTaskbarSystemAreaLeft(nint taskbar, in RECT contentRect, out int leftPx)
    {
        var content = contentRect;
        var knownMin = int.MaxValue;
        var rightAnchoredMin = int.MaxValue;
        var signature = new StringBuilder();
        EnumChildWindows(taskbar, (child, _) =>
        {
            if (!TryGetWindowRect(child, out var rect))
                return true;
            var name = new StringBuilder(256);
            var length = GetClassNameW(child, name, name.Capacity);
            var className = length > 0 ? name.ToString() : "";
            signature.Append(' ').Append(className)
                .Append('@').Append(rect.Left).Append(',').Append(rect.Top)
                .Append(',').Append(rect.Width).Append('x').Append(rect.Height);
            if (SystemAreaClassNames.Contains(className))
                knownMin = Math.Min(knownMin, rect.Left);
            // Fallback: children docked at the taskbar's right end and at
            // most half as wide as the taskbar — the system-area cluster.
            if (rect.Right >= content.Right - 8 && rect.Width <= content.Width / 2)
                rightAnchoredMin = Math.Min(rightAnchoredMin, rect.Left);
            return true;
        }, IntPtr.Zero);

        // One log line per taskbar structure change; the child list is short
        // and stable, so this stays bounded (Explorer restarts re-log).
        var signatureText = signature.ToString();
        if (signatureText != _loggedSystemAreaChildren)
        {
            _loggedSystemAreaChildren = signatureText;
            PagurianLog.Host($"taskbar {taskbar}: children[{signatureText}]");
        }

        if (knownMin != int.MaxValue)
        {
            leftPx = knownMin;
            return true;
        }
        if (rightAnchoredMin != int.MaxValue)
        {
            leftPx = rightAnchoredMin;
            return true;
        }
        leftPx = 0;
        return false;
    }

    public static bool TryGetTaskbarContentRect(IntPtr taskbar, out RECT rect)
    {
        if (!TryGetTaskbarRect(taskbar, out var shellRect))
        {
            rect = default;
            return false;
        }

        var horizontal = shellRect.Width >= shellRect.Height;
        foreach (var className in new[] { "MSTaskListWClass", "ReBarWindow32", "Start" })
        {
            var child = FindWindowExW(taskbar, IntPtr.Zero, className, null);
            if (!TryGetWindowRect(child, out var childRect))
                continue;

            if (horizontal &&
                childRect.Top >= shellRect.Top && childRect.Bottom <= shellRect.Bottom &&
                childRect.Height < shellRect.Height)
            {
                rect = new RECT
                {
                    Left = shellRect.Left,
                    Top = childRect.Top,
                    Right = shellRect.Right,
                    Bottom = childRect.Bottom,
                };
                return true;
            }

            if (!horizontal &&
                childRect.Left >= shellRect.Left && childRect.Right <= shellRect.Right &&
                childRect.Width < shellRect.Width)
            {
                rect = new RECT
                {
                    Left = childRect.Left,
                    Top = shellRect.Top,
                    Right = childRect.Right,
                    Bottom = shellRect.Bottom,
                };
                return true;
            }
        }

        rect = shellRect;
        return true;
    }

    public static POINT GetCursorPosition() =>
        GetCursorPos(out var p) ? p : default;

    // Resolves the display containing (or nearest to) a physical screen rect.
    public static bool TryGetDisplayMonitor(in RECT screenRect, out DisplayMonitor display)
    {
        var rect = screenRect;
        var monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        return TryReadDisplayMonitor(monitor, out display);
    }

    private static bool TryReadDisplayMonitor(IntPtr monitor, out DisplayMonitor display)
    {
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(),
            };
            if (GetMonitorInfoW(monitor, ref info) &&
                info.rcMonitor.Right > info.rcMonitor.Left &&
                info.rcMonitor.Bottom > info.rcMonitor.Top)
            {
                var deviceName = string.IsNullOrWhiteSpace(info.szDevice)
                    ? $"monitor-{monitor.ToInt64():X}"
                    : info.szDevice;
                display = new DisplayMonitor(
                    deviceName,
                    info.rcMonitor,
                    info.rcWork,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0);
                return true;
            }
        }

        display = default;
        return false;
    }

    // Returns the physical-pixel work area of the monitor nearest a physical
    // screen rectangle. Unlike the virtual-desktop metrics below, rcWork
    // excludes that monitor's taskbar and respects secondary monitors with
    // negative coordinates.
    public static bool TryGetMonitorWorkArea(in RECT screenRect, out RECT workArea)
    {
        if (TryGetDisplayMonitor(screenRect, out var display) &&
            display.WorkRect.Right > display.WorkRect.Left &&
            display.WorkRect.Bottom > display.WorkRect.Top)
        {
            workArea = display.WorkRect;
            return true;
        }

        workArea = default;
        return false;
    }

    // Physical-pixel rectangles for the whole virtual desktop and the
    // primary display. Secondary displays may extend the virtual desktop
    // into negative coordinates; the primary display starts at (0, 0).
    public static bool TryGetDesktopRects(out RECT virtualScreen, out RECT primaryScreen)
    {
        var virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        var primaryWidth = GetSystemMetrics(SM_CXSCREEN);
        var primaryHeight = GetSystemMetrics(SM_CYSCREEN);
        if (virtualWidth <= 0 || virtualHeight <= 0 ||
            primaryWidth <= 0 || primaryHeight <= 0)
        {
            virtualScreen = default;
            primaryScreen = default;
            return false;
        }

        var virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        virtualScreen = new RECT
        {
            Left = virtualLeft,
            Top = virtualTop,
            Right = virtualLeft + virtualWidth,
            Bottom = virtualTop + virtualHeight,
        };
        primaryScreen = new RECT
        {
            Left = 0,
            Top = 0,
            Right = primaryWidth,
            Bottom = primaryHeight,
        };
        return true;
    }

    public static void ShowMessage(string text, string caption) =>
        MessageBoxW(IntPtr.Zero, text, caption, 0x00000040); // MB_ICONINFORMATION
}
