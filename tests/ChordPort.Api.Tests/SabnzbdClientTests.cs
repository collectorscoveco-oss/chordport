using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ChordPort.Api.Domain;
using ChordPort.Api.Services;
using Microsoft.AspNetCore.DataProtection;

namespace ChordPort.Api.Tests;

public sealed class SabnzbdClientTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("https://192.168.1.20:8080", true)]
    [InlineData("http://10.0.0.2", true)]
    [InlineData("http://172.20.0.5", true)]
    [InlineData("https://localhost:8080", true)]
    [InlineData("https://example.com", false)]
    [InlineData("https://8.8.8.8", false)]
    [InlineData("http://127.0.0.1:8080/evil", false)]
    [InlineData("http://user@127.0.0.1:8080", false)]
    [InlineData("http://127.0.0.1:8080?next=x", false)]
    public void Endpoint_validation_allows_only_local_or_private_roots(string raw, bool allowed)
    {
        var result = SabnzbdEndpointValidator.Validate(raw);
        Assert.Equal(allowed, result.IsValid);
    }

    [Fact]
    public async Task Test_connection_uses_only_the_version_api_form_and_does_not_leak_the_key()
    {
        var handler = new RecordingHandler(_ => Json("{\"version\":\"4.3.0\"}"));
        var client = new SabnzbdClient(new HttpClient(handler));
        var result = await client.TestConnectionAsync(new SabnzbdSettings(true, "http://127.0.0.1:8080", "music", "secret-key"), CancellationToken.None);

        Assert.True(result.Connected);
        Assert.Equal("Connected to SABnzbd 4.3.0.", result.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api", request.RequestUri!.AbsolutePath);
        Assert.Equal("mode=version&output=json&apikey=secret-key", request.RequestUri.Query.TrimStart('?'));
        Assert.DoesNotContain("secret-key", result.Message);
    }

    [Fact]
    public async Task Test_connection_malformed_or_unreachable_responses_are_safe()
    {
        var malformed = new SabnzbdClient(new HttpClient(new RecordingHandler(_ => Json("not-json"))));
        var offline = new SabnzbdClient(new HttpClient(new RecordingHandler(_ => throw new HttpRequestException("secret-key offline"))));
        var settings = new SabnzbdSettings(true, "http://127.0.0.1:8080", "music", "secret-key");

        var malformedResult = await malformed.TestConnectionAsync(settings, CancellationToken.None);
        var offlineResult = await offline.TestConnectionAsync(settings, CancellationToken.None);

        Assert.False(malformedResult.Connected);
        Assert.False(offlineResult.Connected);
        Assert.Equal("SABnzbd returned an unexpected response.", malformedResult.Message);
        Assert.Equal("Could not connect to SABnzbd. Check the local address and try again.", offlineResult.Message);
        Assert.DoesNotContain("secret-key", offlineResult.Message);
    }

    [Fact]
    public async Task Sab_client_does_not_follow_redirects_or_return_secrets()
    {
        const string sentinel = "sab-redirect-sentinel";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("http://attacker.invalid/steal") } });
        var result = await new SabnzbdClient(new HttpClient(handler)).AddFileAsync(new SabnzbdSettings(true, "http://127.0.0.1:8080", "music", sentinel), Encoding.UTF8.GetBytes("<nzb />"), "safe", CancellationToken.None);

        Assert.False(result.Submitted);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(sentinel, result.Message);
        Assert.DoesNotContain(sentinel, System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Hardened_handler_does_not_contact_redirect_target_or_expose_secrets(int redirectStatus)
    {
        const string sentinel = "sab-runtime-redirect-sentinel";
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var attacker = new TcpListener(IPAddress.Loopback, 0);
        source.Start(); attacker.Start();
        var attackerPort = ((IPEndPoint)attacker.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var connection = await source.AcceptTcpClientAsync();
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = new StringBuilder();
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) request.AppendLine(line);
            var redirect = $"HTTP/1.1 {redirectStatus} Redirect\r\nLocation: http://127.0.0.1:{attackerPort}/steal\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(redirect));
            return request.ToString();
        });
        using var http = new HttpClient(SabnzbdHttpHandlerFactory.Create());
        var port = ((IPEndPoint)source.LocalEndpoint).Port;
        var result = await new SabnzbdClient(http).AddFileAsync(new SabnzbdSettings(true, $"http://localhost:{port}", "music", sentinel), Encoding.UTF8.GetBytes("<nzb />"), "safe", CancellationToken.None);
        var request = await served;
        var attackerAccept = attacker.AcceptTcpClientAsync();
        var contacted = await Task.WhenAny(attackerAccept, Task.Delay(250));

        Assert.False(result.Submitted);
        Assert.Contains(sentinel, request);
        Assert.DoesNotContain(sentinel, result.Message);
        Assert.NotSame(attackerAccept, contacted);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.169.254", false)]
    public void Sab_connection_policy_allows_only_loopback_or_private_resolution(string address, bool allowed)
    {
        Assert.Equal(allowed, SabnzbdAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Fact]
    public void Protected_setting_preserves_an_empty_save_but_explicit_clear_removes_the_secret()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var secrets = new SabnzbdSecretProtector(provider);
        var protectedKey = secrets.Protect("secret-key");
        var existing = new StoredSabnzbdSettings(true, "http://127.0.0.1:8080", "music", protectedKey);

        var preserved = SabnzbdSettingsMerger.Merge(existing, new SabnzbdSettingsInput(true, "http://127.0.0.1:8080", "music", "", false));
        var cleared = SabnzbdSettingsMerger.Merge(existing, new SabnzbdSettingsInput(true, "http://127.0.0.1:8080", "music", "", true));

        Assert.Equal("secret-key", secrets.Unprotect(preserved.ProtectedApiKey!));
        Assert.Null(cleared.ProtectedApiKey);
        var response = SabnzbdSettingsMapper.ToResponse(preserved);
        Assert.True(response.HasApiKey);
        Assert.DoesNotContain("secret-key", System.Text.Json.JsonSerializer.Serialize(response));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
