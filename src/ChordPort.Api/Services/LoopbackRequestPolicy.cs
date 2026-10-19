using System.Net;

namespace ChordPort.Api.Services;

public static class LoopbackRequestPolicy
{
    public static bool IsLoopback(IPAddress? remoteAddress) => remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
}
