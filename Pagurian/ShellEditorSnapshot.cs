namespace Pagurian;

internal sealed record ShellEditorSnapshot(
    string DeviceName,
    long Version,
    int PixelWidth,
    int PixelHeight,
    byte[] Bgra);

internal readonly record struct ShellEditorCapturedFrame(
    int PixelWidth,
    int PixelHeight,
    byte[] Bgra);

// Produces a deliberately low-detail, static desktop frame for one physical
// display. Capture and filtering are both safe to run away from the UI thread.
static class ShellEditorSnapshotService
{
    private const int DownsampleFactor = 6;
    private const int BlurRadius = 3;
    private const int BlurPasses = 3;
    private static long _nextVersion;

    public static ShellEditorCapturedFrame? Capture(
        TaskbarInterop.DisplayMonitor monitor)
    {
        var capture = TaskbarInterop.CaptureScreenRegionScaled(
            monitor.MonitorRect,
            DownsampleFactor);
        return capture is { } frame
            ? new ShellEditorCapturedFrame(frame.Width, frame.Height, frame.Bgra)
            : null;
    }

    public static ShellEditorSnapshot Blur(
        string deviceName,
        ShellEditorCapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var source = frame.Bgra;
        var scratch = new byte[source.Length];
        for (var pass = 0; pass < BlurPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlurHorizontal(
                source,
                scratch,
                frame.PixelWidth,
                frame.PixelHeight,
                BlurRadius);
            cancellationToken.ThrowIfCancellationRequested();
            BlurVertical(
                scratch,
                source,
                frame.PixelWidth,
                frame.PixelHeight,
                BlurRadius);
        }

        for (var i = 3; i < source.Length; i += 4)
            source[i] = 255;

        return new ShellEditorSnapshot(
            deviceName,
            Interlocked.Increment(ref _nextVersion),
            frame.PixelWidth,
            frame.PixelHeight,
            source);
    }

    private static void BlurHorizontal(
        byte[] source,
        byte[] destination,
        int width,
        int height,
        int radius)
    {
        var window = radius * 2 + 1;
        var sums = new int[4];
        for (var y = 0; y < height; y++)
        {
            Array.Clear(sums);
            for (var offset = -radius; offset <= radius; offset++)
                AddPixel(source, width, y, Math.Clamp(offset, 0, width - 1), sums, 1);

            for (var x = 0; x < width; x++)
            {
                var destinationIndex = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                    destination[destinationIndex + channel] = (byte)(sums[channel] / window);

                AddPixel(
                    source,
                    width,
                    y,
                    Math.Clamp(x - radius, 0, width - 1),
                    sums,
                    -1);
                AddPixel(
                    source,
                    width,
                    y,
                    Math.Clamp(x + radius + 1, 0, width - 1),
                    sums,
                    1);
            }
        }
    }

    private static void BlurVertical(
        byte[] source,
        byte[] destination,
        int width,
        int height,
        int radius)
    {
        var window = radius * 2 + 1;
        var sums = new int[4];
        for (var x = 0; x < width; x++)
        {
            Array.Clear(sums);
            for (var offset = -radius; offset <= radius; offset++)
                AddPixel(source, width, Math.Clamp(offset, 0, height - 1), x, sums, 1);

            for (var y = 0; y < height; y++)
            {
                var destinationIndex = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                    destination[destinationIndex + channel] = (byte)(sums[channel] / window);

                AddPixel(
                    source,
                    width,
                    Math.Clamp(y - radius, 0, height - 1),
                    x,
                    sums,
                    -1);
                AddPixel(
                    source,
                    width,
                    Math.Clamp(y + radius + 1, 0, height - 1),
                    x,
                    sums,
                    1);
            }
        }
    }

    private static void AddPixel(
        byte[] pixels,
        int width,
        int y,
        int x,
        int[] sums,
        int sign)
    {
        var index = (y * width + x) * 4;
        for (var channel = 0; channel < 4; channel++)
            sums[channel] += sign * pixels[index + channel];
    }
}
