using System.Threading;
using ChordPort.Api.Services;
using Microsoft.Extensions.Hosting;

namespace ChordPort.Api.Tests;

public sealed class ShutdownServiceTests
{
    [Fact]
    public void Request_stop_signals_the_host_once_and_returns_a_safe_response()
    {
        var lifetime = new RecordingLifetime();
        var shutdown = new ShutdownService(lifetime);

        var response = shutdown.RequestStop();
        var duplicate = shutdown.RequestStop();

        Assert.Equal("ChordPort is stopping.", response.Message);
        Assert.Equal("ChordPort is stopping.", duplicate.Message);
        Assert.Equal(1, lifetime.StopRequests);
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public int StopRequests { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequests++;
    }
}
