using System.Runtime.InteropServices;

namespace Pagurian.Sdk;

// Win32 MessageBox for module UI (works headless in an unpackaged app;
// ContentDialog needs an owner window and is avoided deliberately).
public static class MessageBoxes
{
    [DllImport("user32.dll")]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    public static void Show(string text, string caption) =>
        MessageBoxW(IntPtr.Zero, text, caption, 0x00000040); // MB_ICONINFORMATION
}
