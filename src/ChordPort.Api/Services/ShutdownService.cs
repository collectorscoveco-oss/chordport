using Microsoft.Extensions.Hosting;

namespace ChordPort.Api.Services;

public sealed class ShutdownService(IHostApplicationLifetime lifetime)
{
    private int _requested;

    public ShutdownResponse RequestStop()
    {
        if (Interlocked.Exchange(ref _requested, 1) == 0) lifetime.StopApplication();
        return new ShutdownResponse("ChordPort is stopping.");
    }
}

public sealed record ShutdownResponse(string Message);
