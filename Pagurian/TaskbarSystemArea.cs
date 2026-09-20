namespace Pagurian;

internal static class TaskbarSystemArea
{
    internal readonly record struct Candidate(uint ProcessId, bool Visible,
        string ClassName, TaskbarInterop.RECT Bounds);

    internal static bool IsValidBounds(TaskbarInterop.RECT candidate, TaskbarInterop.RECT content)
    {
        int overlap = Math.Min(candidate.Bottom, content.Bottom) - Math.Max(candidate.Top, content.Top);
        return content.Width > 0 && content.Height > 0 &&
            candidate.Width > 0 && candidate.Width <= content.Width / 2 &&
            candidate.Height > 0 && candidate.Height <= 2 * content.Height &&
            candidate.Left >= content.Left + content.Width / 2 &&
            candidate.Right <= content.Right &&
            overlap >= Math.Min(candidate.Height, content.Height) / 2.0;
    }

    internal static int? SelectNative(uint taskbarProcessId, TaskbarInterop.RECT content,
        IEnumerable<Candidate> candidates)
    {
        int? left = null;
        foreach (var candidate in candidates)
        {
            if (taskbarProcessId == 0 || candidate.ProcessId != taskbarProcessId || !candidate.Visible ||
                candidate.ClassName is not ("TrayNotifyWnd" or "TrayClockWClass") ||
                !IsValidBounds(candidate.Bounds, content))
                continue;
            left = left is { } previous ? Math.Min(previous, candidate.Bounds.Left) : candidate.Bounds.Left;
        }
        return left;
    }
}
