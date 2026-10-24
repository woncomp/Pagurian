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
        TrayEdge Edge = TrayEdge.Left,
        int? SystemAreaLeftPx = null,
        TaskbarInterop.RECT? TaskContentRect = null)
    {
        internal bool CanPlace => !IsHorizontal ||
            TaskContentRect is { } content && TaskbarContent.IsValidBounds(content, ContentRect) &&
            (Edge != TrayEdge.Right ||
                SystemAreaLeftPx is { } left && left > ContentRect.Left && left <= ContentRect.Right);

        // Physical-pixel interval available to the injected tray. Both sides
        // keep the taskbar's 8-DIP anchor gap from system content.
        internal double AvailableWidthDip
        {
            get
            {
                if (!IsHorizontal || TaskContentRect is not { } content) return double.PositiveInfinity;
                double start, end;
                if (Edge == TrayEdge.Right)
                {
                    start = content.Right + 8 * Scale;
                    end = (SystemAreaLeftPx ?? ContentRect.Right) - 8 * Scale;
                }
                else
                {
                    start = ContentRect.Left + 8 * Scale;
                    end = content.Left - 8 * Scale;
                }
                return Math.Max(0, (end - start) / Scale);
            }
        }

        public TaskbarInterop.RECT Place(double widthDip)
        {
            // A left surface without content evidence can still supply its
            // cross-axis strip for background sampling; presentation callers
            // check CanPlace first. Right placement always needs the system
            // area, and content-bearing placement needs valid evidence.
            var sampleOnly = IsHorizontal && Edge == TrayEdge.Left && TaskContentRect == null;
            if (!CanPlace && !sampleOnly)
                throw new InvalidOperationException("Tray placement requires reliable taskbar-boundary evidence.");
            var right = IsHorizontal && Edge == TrayEdge.Right ? SystemAreaLeftPx!.Value : ContentRect.Right;
            var winW = Math.Max(widthDip * Scale, 1);
            if (IsHorizontal && TaskContentRect != null)
            {
                var availablePx = AvailableWidthDip * Scale;
                winW = Math.Min(winW, Math.Max(availablePx, 1));
            }
            var winH = (TaskbarTrayWindow.WindowHeightDip -
                        2 * TaskbarTrayWindow.WindowInsetYDip) * Scale;

            double xPx, yPx;
            if (IsHorizontal)
            {
                xPx = Edge == TrayEdge.Right
                    ? right - winW - 8 * Scale
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
                Math.Max(ContentRect.Left, right - winW));
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
