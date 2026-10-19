using System.Net;
using ChordPort.Api.Services;

namespace ChordPort.Api.Tests;

public sealed class LoopbackRequestPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("8.8.8.8", false)]
    public void Shutdown_policy_allows_only_a_loopback_remote_address(string address, bool allowed)
        => Assert.Equal(allowed, LoopbackRequestPolicy.IsLoopback(IPAddress.Parse(address)));

    [Fact]
    public void Shutdown_policy_rejects_an_unknown_remote_address()
        => Assert.False(LoopbackRequestPolicy.IsLoopback(null));
}
