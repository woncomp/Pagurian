using Pagurian.Sdk;

namespace Pagurian.Modules.Metrics;

[PagurianModule(DisplayName = "System Metrics")]
public sealed class MetricsModule : PagurianModule
{
    // Module singleton: the shells fetch the shared tracker through it (the
    // default [Shell] factory requires parameterless constructors).
    public static MetricsModule Instance { get; private set; } = null!;

    private int _activeShells;

    public MetricsModule()
    {
        Instance = this;
    }

    // The sampler is a heavy resource shared by both shells: it starts when
    // the first configured shell starts and stops when the last one shuts
    // down (reference counting — each shell works standalone).
    internal void ShellStarted()
    {
        if (++_activeShells == 1)
            SystemMetricsTracker.Start();
    }

    internal void ShellStopped()
    {
        if (--_activeShells == 0)
            SystemMetricsTracker.Stop();
    }
}
