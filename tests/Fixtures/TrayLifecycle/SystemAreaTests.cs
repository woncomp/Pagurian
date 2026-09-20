using Pagurian;

static class SystemAreaTests
{
    internal static void Run()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static TaskbarInterop.RECT Rect(int left, int top, int width, int height) =>
            new() { Left = left, Top = top, Right = left + width, Bottom = top + height };
        var bar = Rect(3440, 1945, 1440, 48);
        var clock = new TaskbarSystemArea.Candidate(19048, true, "TrayClockWClass", Rect(4797, 1945, 71, 48));
        foreach (int x in new[] { 4826, 4772 })
        {
            TaskbarSystemArea.Candidate[] candidates =
            [
                new(49560, true, "WinUIDesktopWin32WindowClass", Rect(x, 1947, 46, 44)),
                new(49560, true, "Microsoft.UI.Content.DesktopChildSiteBridge", Rect(x, 1947, 46, 44)),
                new(19048, true, "Windows.UI.Composition.DesktopWindowContentBridge", bar),
                new(49560, true, "TrayClockWClass", Rect(x, 1945, 46, 48)),
            ];
            Require(TaskbarSystemArea.SelectNative(19048, bar, candidates) == null, "self/foreign windows became an anchor");
            var left = TaskbarSystemArea.SelectNative(19048, bar, [.. candidates, clock]);
            Require(left == 4797, "clock boundary changed with own tray position");
            var surface = new TaskbarTrayPlacement.Surface(bar, bar, true, 1, TrayEdge.Right, left);
            Require(surface.Place(46).Left == 4743 && surface.Place(46).Right == 4789, "54px oscillation regression");
        }
        foreach (var invalid in new[]
        {
            clock with { Visible = false }, clock with { ProcessId = 0 },
            clock with { Bounds = bar }, clock with { Bounds = Rect(4797, 2000, 71, 48) },
            clock with { Bounds = Rect(4797, 1945, 0, 48) },
            clock with { Bounds = Rect(4797, 1945, 100, 48) },
            clock with { ClassName = "TrayShowDesktopButtonWClass" },
        })
            Require(TaskbarSystemArea.SelectNative(19048, bar, [invalid]) == null, "invalid native evidence accepted");
        var notify = clock with { ClassName = "TrayNotifyWnd", Bounds = Rect(4500, 1945, 380, 48) };
        Require(TaskbarSystemArea.SelectNative(19048, bar, [clock, notify]) == 4500, "primary notification cluster regressed");
        foreach (var scale in new[] { 1, 1.25, 1.5 })
        {
            var negative = Rect(-2560, 1410, 2560, (int)(48 * scale));
            var surface = new TaskbarTrayPlacement.Surface(negative, negative, true, scale, TrayEdge.Right, -83);
            Require(surface.Place(46).Right == (int)(-83 - 8 * scale), "DPI/negative-coordinate gap incorrect");
            Require(!(surface with { SystemAreaLeftPx = null }).CanPlace, "unknown boundary allowed right placement");
            Require((surface with { Edge = TrayEdge.Left, SystemAreaLeftPx = null }).CanPlace, "left placement requires clock");
            Require((surface with { IsHorizontal = false, SystemAreaLeftPx = null }).CanPlace, "vertical fold requires clock");
        }

        var raw = new TaskbarTrayPlacement.Surface(bar, bar, true, 1, TrayEdge.Right);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0, active = 0, maximum = 0;
        using var observer = new TaskbarSystemAreaObserver(context =>
        {
            int count = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, count);
            int call = Interlocked.Increment(ref calls);
            try
            {
                if (call == 1)
                {
                    entered.Set();
                    if (!release.Wait(3000)) throw new InvalidOperationException("probe fixture gate timeout");
                }
                return new(context.Geometry.ContentRect.Right - 83, "fixture");
            }
            finally { Interlocked.Decrement(ref active); }
        }, _ => { });
        Require(!observer.Observe(1, 19048, raw).CanPlace, "first query guessed a boundary");
        Require(entered.Wait(3000), "observer did not start");
        var changed = raw with { ContentRect = Rect(3440, 1945, 1400, 48) };
        Require(!observer.Observe(1, 19048, changed).CanPlace, "new geometry reused old evidence");
        release.Set();
        Require(SpinWait.SpinUntil(() => observer.Observe(1, 19048, changed).CanPlace, 3000), "new generation did not resolve");
        Require(observer.Observe(1, 19048, changed).SystemAreaLeftPx == 4757, "late result crossed generation");
        Require(maximum == 1, "observer overlapped probes");
        using var other = new TaskbarSystemAreaObserver(_ => new(4797, "fixture-other"), _ => { });
        other.Observe(2, 19048, raw);
        Require(SpinWait.SpinUntil(() => other.Observe(2, 19048, raw).CanPlace, 3000), "other taskbar did not resolve");
        for (int i = 0; i < 200; i++)
        {
            Require(observer.Observe(1, 19048, changed).SystemAreaLeftPx == 4757, "cross-taskbar cache contamination");
            Require(other.Observe(2, 19048, raw).SystemAreaLeftPx == 4797, "cross-taskbar cache contamination");
        }
        observer.Dispose();
        Require(!observer.Observe(1, 19048, changed).CanPlace, "disposed observer revived cache");

        using var closingEntered = new ManualResetEventSlim();
        using var closingRelease = new ManualResetEventSlim();
        using var closingCompleted = new ManualResetEventSlim();
        using var closing = new TaskbarSystemAreaObserver(_ =>
        {
            closingEntered.Set();
            if (!closingRelease.Wait(3000)) throw new InvalidOperationException("closing probe gate timeout");
            closingCompleted.Set();
            return new(4797, "fixture-late");
        }, _ => { });
        closing.Observe(3, 19048, raw);
        Require(closingEntered.Wait(3000), "closing probe did not start");
        closing.Dispose();
        closingRelease.Set();
        Require(closingCompleted.Wait(3000), "closing probe did not complete");
        Require(!closing.Observe(3, 19048, raw).CanPlace, "in-flight result revived closed observer");
    }
}
