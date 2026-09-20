namespace Pagurian;

// Shared physical-pixel placement for the injected tray and the shell
// editor's target preview. Keeping this calculation in one place guarantees
// that the preview marks the position where the real tray will appear.
static class TaskbarTrayPlacement
{
    internal readonly record struct Surface(
        TaskbarInterop.RECT ContentRect,
        TaskbarInterop.RECT ParentRect,
        bool IsHorizontal,
        double Scale,
        TrayEdge Edge = TrayEdge.Left)
    {
        public TaskbarInterop.RECT Place(double widthDip)
        {
            var winW = Math.Max(widthDip * Scale, 1);
            var winH = (TaskbarTrayWindow.WindowHeightDip -
                        2 * TaskbarTrayWindow.WindowInsetYDip) * Scale;

            double xPx, yPx;
            if (IsHorizontal)
            {
                // TODO(right-edge trays): anchoring at the content rect's
                // right end overlaps the notification area/clock on stock
                // Windows; the right anchor needs a measured safe inset
                // before TrayEdge.Right surfaces are materialized. Until then
                // the binding engine normalizes every tray to the left
                // surface, so this branch is unreachable.
                xPx = Edge == TrayEdge.Right
                    ? ContentRect.Right - winW - 8 * Scale
                    : ContentRect.Left + 8 * Scale;
                yPx = ContentRect.Top + (ContentRect.Height - winH) / 2;
            }
            else
            {
                xPx = ContentRect.Left + (ContentRect.Width - winW) / 2;
                yPx = ContentRect.Bottom - winH - 8 * Scale;
            }

            xPx = Math.Clamp(
                xPx,
                ContentRect.Left,
                Math.Max(ContentRect.Left, ContentRect.Right - winW));
            yPx = Math.Clamp(
                yPx,
                ContentRect.Top,
                Math.Max(ContentRect.Top, ContentRect.Bottom - winH));

            return new TaskbarInterop.RECT
            {
                Left = (int)xPx,
                Top = (int)yPx,
                Right = (int)(xPx + winW),
                Bottom = (int)(yPx + winH),
            };
        }
    }

    // The primary taskbar's left surface (the historical single-tray case).
    public static bool TryGetSurface(out Surface surface) =>
        TryGetSurface(TaskbarInterop.FindTaskbar(), TrayEdge.Left, out surface);

    public static bool TryGetSurface(nint taskbar, TrayEdge edge, out Surface surface)
    {
        if (!TaskbarInterop.TryGetTaskbarContentRect(taskbar, out var contentRect) ||
            !TaskbarInterop.TryGetTaskbarRect(taskbar, out var parentRect) ||
            contentRect.Width < 32 || contentRect.Height < 16)
        {
            surface = default;
            return false;
        }

        var horizontal = contentRect.Width >= contentRect.Height;
        var scale = (horizontal ? contentRect.Height : contentRect.Width) /
            TaskbarTrayWindow.WindowHeightDip;
        if (scale <= 0)
        {
            surface = default;
            return false;
        }

        surface = new Surface(contentRect, parentRect, horizontal, scale, edge);
        return true;
    }
}
