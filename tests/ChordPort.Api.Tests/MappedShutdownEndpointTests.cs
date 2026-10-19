using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace ChordPort.Api.Tests;

public sealed class MappedShutdownEndpointTests
{
    [Fact]
    public async Task Mapped_shutdown_route_returns_forbidden_for_a_non_loopback_remote_address()
    {
        using var factory = new WebApplicationFactory<Program>();

        var response = await SendShutdownAsync(factory, IPAddress.Parse("192.168.1.20"));

        Assert.Equal(StatusCodes.Status403Forbidden, response.Response.StatusCode);
    }

    [Fact]
    public async Task Mapped_shutdown_route_allows_a_loopback_remote_address()
    {
        using var factory = new WebApplicationFactory<Program>();

        var response = await SendShutdownAsync(factory, IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    private static Task<HttpContext> SendShutdownAsync(WebApplicationFactory<Program> factory, IPAddress address)
        => factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/api/system/shutdown";
            context.Connection.RemoteIpAddress = address;
        });

}
