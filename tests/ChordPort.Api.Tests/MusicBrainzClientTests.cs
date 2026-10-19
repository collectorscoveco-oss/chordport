using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using ChordPort.Api.Domain;
using ChordPort.Api.Services;

namespace ChordPort.Api.Tests;

public sealed class MusicBrainzClientTests
{
    [Fact]
    public async Task Search_songs_encodes_query_and_maps_stable_recording_id()
    {
        var handler = new FakeHttpMessageHandler(_ => Json("""
            {"recordings":[{"id":"recording-123","title":"A & B","length":215000,
            "artist-credit":[{"name":"Artist One"}],"releases":[{"title":"Album One","id":"release-456"}]}]}
            """));
        var client = CreateClient(handler);

        var result = await client.SearchAsync(SearchMode.Songs, "A & B", CancellationToken.None);

        Assert.Equal("MusicBrainz", result.Provider);
        Assert.Equal("ok", result.Status);
        var item = Assert.Single(result.Results);
        Assert.Equal("recording-123", item.Id);
        Assert.Equal("recording", item.EntityType);
        Assert.Equal("A & B", item.Title);
        Assert.Equal("Artist One", item.Artist);
        Assert.Equal("Album One", item.Album);
        Assert.Equal(215, item.DurationSeconds);
        Assert.Contains("query=A%20%26%20B", handler.Requests.Single().RequestUri!.Query);
        Assert.Contains("fmt=json", handler.Requests.Single().RequestUri!.Query);
        Assert.Contains("ChordPort", handler.Requests.Single().Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Search_artists_and_albums_preserves_entity_id_and_type()
    {
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("artist")
            ? Json("{" + "\"artists\":[{\"id\":\"artist-1\",\"name\":\"Artist One\"}]}" )
            : Json("{" + "\"release-groups\":[{\"id\":\"album-1\",\"title\":\"Album One\",\"artist-credit\":[{\"name\":\"Artist One\"}]}]}"));
        var client = CreateClient(handler);

        var artists = await client.SearchAsync(SearchMode.Artists, "Artist", CancellationToken.None);
        var albums = await client.SearchAsync(SearchMode.Albums, "Album", CancellationToken.None);

        Assert.Equal(("artist-1", "artist"), (artists.Results.Single().Id, artists.Results.Single().EntityType));
        Assert.Equal(("album-1", "release-group"), (albums.Results.Single().Id, albums.Results.Single().EntityType));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Search_http_failures_returns_safe_error_status(HttpStatusCode code)
    {
        var client = CreateClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(code)));
        var result = await client.SearchAsync(SearchMode.Songs, "test", CancellationToken.None);
        Assert.Equal("error", result.Status);
        Assert.Empty(result.Results);
        Assert.Equal("MusicBrainz could not be reached. Try again later.", result.Message);
    }

    [Fact]
    public async Task Search_malformed_response_returns_safe_error_status()
    {
        var client = CreateClient(new FakeHttpMessageHandler(_ => Json("not json")));
        var result = await client.SearchAsync(SearchMode.Songs, "test", CancellationToken.None);
        Assert.Equal("error", result.Status);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task Search_timeout_returns_safe_error_status()
    {
        var client = CreateClient(new FakeHttpMessageHandler(_ => throw new TaskCanceledException()));
        var result = await client.SearchAsync(SearchMode.Songs, "test", CancellationToken.None);
        Assert.Equal("error", result.Status);
        Assert.Empty(result.Results);
    }

    [Fact]
    public async Task Search_throttles_distinct_uncached_requests()
    {
        var handler = new FakeHttpMessageHandler(_ => Json("{\"recordings\":[]}"));
        var client = new MusicBrainzClient(new HttpClient(handler), new MusicBrainzOptions
        {
            BaseUrl = "https://metadata.example/ws/2", MinimumRequestInterval = TimeSpan.FromMilliseconds(40)
        });
        var started = DateTimeOffset.UtcNow;
        await client.SearchAsync(SearchMode.Songs, "first", CancellationToken.None);
        await client.SearchAsync(SearchMode.Songs, "second", CancellationToken.None);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(35));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Search_uses_cached_result_without_second_request()
    {
        var handler = new FakeHttpMessageHandler(_ => Json("{\"recordings\":[]}"));
        var client = CreateClient(handler);
        await client.SearchAsync(SearchMode.Songs, "cached", CancellationToken.None);
        await client.SearchAsync(SearchMode.Songs, "cached", CancellationToken.None);
        Assert.Single(handler.Requests);
    }

    private static MusicBrainzClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new MusicBrainzOptions { BaseUrl = "https://metadata.example/ws/2", MinimumRequestInterval = TimeSpan.Zero });

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
