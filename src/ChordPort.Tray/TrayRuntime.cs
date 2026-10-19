using System;
using System.Runtime.Versioning;
using System.Threading;

namespace ChordPort.Tray;

internal static class TrayInstanceCoordinator
{
    [SupportedOSPlatform("windows")]
    internal static EventWaitHandle CreateOpenSignal(string userSuffix)
        => new(false, EventResetMode.AutoReset, $"Local\\ChordPort.Tray.Open.{userSuffix}");

    [SupportedOSPlatform("windows")]
    internal static bool TrySignalExistingInstance(string userSuffix)
    {
        try
        {
            using var open = EventWaitHandle.OpenExisting($"Local\\ChordPort.Tray.Open.{userSuffix}");
            return open.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}

internal sealed class ConsecutiveHealthFailureTracker
{
    private readonly int _threshold;

    internal ConsecutiveHealthFailureTracker(int threshold)
    {
        if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
    }

    internal int ConsecutiveFailures { get; private set; }

    internal bool ShouldExitAfterHealthCheck(bool isHealthy)
    {
        if (isHealthy)
        {
            ConsecutiveFailures = 0;
            return false;
        }

        ConsecutiveFailures++;
        return ConsecutiveFailures >= _threshold;
    }
}

internal static class IconHandleLifetime
{
    internal static T CloneAndDestroy<T>(IntPtr handle, Func<IntPtr, T> clone, Action<IntPtr> destroy)
    {
        try
        {
            return clone(handle);
        }
        finally
        {
            destroy(handle);
        }
    }
}
