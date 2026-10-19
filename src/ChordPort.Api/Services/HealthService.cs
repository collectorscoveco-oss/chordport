using ChordPort.Api.Domain;
namespace ChordPort.Api.Services;
public sealed class HealthService
{
    public HealthStatus Get() => new("healthy", "ChordPort", DateTimeOffset.UtcNow);
}
