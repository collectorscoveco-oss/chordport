using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using ChordPort.Api.Services;

namespace ChordPort.Api.Tests;

public sealed class UpdateCheckClientTests
{
    [Fact]
    public async Task Newer_stable_release_is_reported_with_a_validated_github_release_url()
    {
        var handler = new RecordingHandler(_ => Json("""{"tag_name":"v0.1.2","prerelease":false,"draft":false,"html_url":"https://github.com/collectorscoveco-oss/chordport/releases/tag/v0.1.2"}"""));
        var client = new UpdateCheckClient(new HttpClient(handler), new UpdateCheckOptions { CurrentVersion = "0.1.1", CacheDuration = TimeSpan.FromMinutes(5) });

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal("0.1.2", result.Version);
        Assert.Equal("https://github.com/collectorscoveco-oss/chordport/releases/tag/v0.1.2", result.ReleaseUrl);
        Assert.Equal("ok", result.Status);
        Assert.Equal("api.github.com", handler.Requests.Single().RequestUri!.Host);
        Assert.Contains("ChordPort", handler.Requests.Single().Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData("v0.1.1")]
    [InlineData("v0.1.0")]
    public async Task Equal_or_older_release_is_not_available(string tag)
    {
        var client = new UpdateCheckClient(new HttpClient(new RecordingHandler(_ => Json($"{{\"tag_name\":\"{tag}\",\"prerelease\":false,\"draft\":false,\"html_url\":\"https://github.com/collectorscoveco-oss/chordport/releases/tag/{tag}\"}}"))), new UpdateCheckOptions { CurrentVersion = "0.1.1" });

        var result = await client.CheckAsync(CancellationToken.None);

        Assert.False(result.Available);
        Assert.Null(result.ReleaseUrl);
    }

    [Fact]
    public async Task Malformed_release_and_network_failure_return_safe_status()
    {
        var malformed = new UpdateCheckClient(new HttpClient(new RecordingHandler(_ => Json("not json"))), new UpdateCheckOptions { CurrentVersion = "0.1.1" });
        var offline = new UpdateCheckClient(new HttpClient(new RecordingHandler(_ => throw new HttpRequestException("private details"))), new UpdateCheckOptions { CurrentVersion = "0.1.1" });

        var malformedResult = await malformed.CheckAsync(CancellationToken.None);
        var offlineResult = await offline.CheckAsync(CancellationToken.None);

        Assert.False(malformedResult.Available);
        Assert.Equal("unavailable", malformedResult.Status);
        Assert.Equal("unavailable", offlineResult.Status);
        Assert.DoesNotContain("private details", offlineResult.Message ?? "");
    }

    [Fact]
    public async Task Cached_result_does_not_make_a_second_request()
    {
        var now = DateTimeOffset.UnixEpoch;
        var handler = new RecordingHandler(_ => Json("""{"tag_name":"v0.1.2","prerelease":false,"draft":false,"html_url":"https://github.com/collectorscoveco-oss/chordport/releases/tag/v0.1.2"}"""));
        var client = new UpdateCheckClient(new HttpClient(handler), new UpdateCheckOptions { CurrentVersion = "0.1.1", CacheDuration = TimeSpan.FromMinutes(5) }, () => now);

        await client.CheckAsync(CancellationToken.None);
        await client.CheckAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://github.com/collectorscoveco-oss/chordport/releases/tag/v0.1.2", true)]
    [InlineData("https://github.com/collectorscoveco-oss/chordport/releases/latest", false)]
    [InlineData("https://evil.example/releases/tag/v0.1.2", false)]
    [InlineData("https://github.com/collectorscoveco-oss/chordport/releases/tag/v0.1.2?x=1", false)]
    public void Release_url_validation_accepts_only_this_projects_tag_pages(string value, bool expected)
        => Assert.Equal(expected, UpdateReleaseUrlValidator.IsValid(value));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(responder(request)); }
    }
}
