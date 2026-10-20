using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Net;
using System.Text;
using ChordPort.Api.Services;

namespace ChordPort.Api.Tests;

public sealed class ManualAcquisitionTests
{
    [Fact]
    public async Task Search_uses_fixed_music_request_and_returns_only_opaque_candidate_inputs()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<rss><channel><item><title>Safe Album</title><guid>provider-id</guid></item></channel></rss>", Encoding.UTF8, "application/xml") });
        var hits = await new IndexerSearchClient(new HttpClient(handler)).SearchAsync(new IndexerConnection("Newznab", "https://news.example.test", "sentinel-key"), "3000,3010", "a & b", CancellationToken.None);
        var candidate = new CandidateStore().Issue("indexer", "Primary", Assert.Single(hits));
        Assert.Equal("t=search&cat=3000%2C3010&q=a%20%26%20b&apikey=sentinel-key", handler.Requests.Single().RequestUri!.Query.TrimStart('?'));
        Assert.NotEqual("provider-id", candidate.Id); Assert.DoesNotContain("sentinel-key", System.Text.Json.JsonSerializer.Serialize(candidate));
    }
    [Fact]
    public void Candidate_is_one_time_and_forged_id_is_rejected()
    {
        var store = new CandidateStore(); var candidate = store.Issue("indexer", "Primary", new IndexerSearchHit("provider", "Title", null, null, null));
        Assert.False(store.TryConsume("forged", out _)); Assert.True(store.TryConsume(candidate.Id, out var consumed)); Assert.Equal("provider", consumed.ResultId); Assert.False(store.TryConsume(candidate.Id, out _));
    }
    [Theory]
    [InlineData("<!DOCTYPE rss [<!ENTITY x 'bad'>]><rss><channel><item><title>&x;</title><guid>x</guid></item></channel></rss>")]
    [InlineData("<html><title>bad</title></html>")]
    public void Search_parser_rejects_dtd_and_unexpected_roots(string xml)
    {
        Assert.ThrowsAny<Exception>(() => IndexerSearchClient.ParseAtom(new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }
    [Fact]
    public async Task Grab_accepts_only_fixed_get_path_and_nzb_xml()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<nzb></nzb>", Encoding.UTF8, "application/xml") });
        var nzb = await new IndexerSearchClient(new HttpClient(handler)).GrabNzbAsync(new IndexerConnection("Torznab", "https://news.example.test", "sentinel-key"), "provider id", CancellationToken.None);
        Assert.NotNull(nzb); Assert.Equal("t=get&id=provider%20id&apikey=sentinel-key", handler.Requests.Single().RequestUri!.Query.TrimStart('?'));
    }
    private sealed class RecordingHandler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler { public List<HttpRequestMessage> Requests { get; } = []; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(response(request)); } }
}
