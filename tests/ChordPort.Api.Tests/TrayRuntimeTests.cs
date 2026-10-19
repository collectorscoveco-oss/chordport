using System;
using System.Collections.Generic;
using ChordPort.Tray;

namespace ChordPort.Api.Tests;

public sealed class TrayRuntimeTests
{
    [Fact]
    public void Second_launch_can_signal_an_open_event_that_was_published_before_mutex_acquisition()
    {
        if (!OperatingSystem.IsWindows()) return;
        var suffix = $"test-{Guid.NewGuid():N}";
        using var firstSignal = TrayInstanceCoordinator.CreateOpenSignal(suffix);

        Assert.True(TrayInstanceCoordinator.TrySignalExistingInstance(suffix));
        Assert.True(firstSignal.WaitOne(0));
    }

    [Fact]
    public void Health_failure_below_threshold_keeps_the_tray_controller_alive()
    {
        var tracker = new ConsecutiveHealthFailureTracker(threshold: 3);

        Assert.False(tracker.ShouldExitAfterHealthCheck(isHealthy: false));
        Assert.Equal(1, tracker.ConsecutiveFailures);
        Assert.False(tracker.ShouldExitAfterHealthCheck(isHealthy: false));
        Assert.Equal(2, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void Valid_identity_checked_health_resets_the_failure_counter()
    {
        var tracker = new ConsecutiveHealthFailureTracker(threshold: 3);
        tracker.ShouldExitAfterHealthCheck(isHealthy: false);
        tracker.ShouldExitAfterHealthCheck(isHealthy: false);

        Assert.False(tracker.ShouldExitAfterHealthCheck(isHealthy: true));
        Assert.Equal(0, tracker.ConsecutiveFailures);
        Assert.False(tracker.ShouldExitAfterHealthCheck(isHealthy: false));
    }

    [Fact]
    public void Health_failure_at_threshold_requests_tray_exit()
    {
        var tracker = new ConsecutiveHealthFailureTracker(threshold: 3);
        tracker.ShouldExitAfterHealthCheck(isHealthy: false);
        tracker.ShouldExitAfterHealthCheck(isHealthy: false);

        Assert.True(tracker.ShouldExitAfterHealthCheck(isHealthy: false));
    }

    [Fact]
    public void Cloned_icon_is_created_before_its_native_handle_is_destroyed()
    {
        var steps = new List<string>();
        var handle = new IntPtr(42);

        var clone = IconHandleLifetime.CloneAndDestroy(handle,
            original => { steps.Add($"clone:{original}"); return "managed clone"; },
            original => steps.Add($"destroy:{original}"));

        Assert.Equal("managed clone", clone);
        Assert.Equal(new[] { "clone:42", "destroy:42" }, steps);
    }

    [Fact]
    public void Native_icon_handle_is_destroyed_when_cloning_fails()
    {
        var destroyed = false;

        Assert.Throws<InvalidOperationException>(() => IconHandleLifetime.CloneAndDestroy<string>(new IntPtr(7),
            _ => throw new InvalidOperationException("clone failed"),
            _ => destroyed = true));

        Assert.True(destroyed);
    }
}
