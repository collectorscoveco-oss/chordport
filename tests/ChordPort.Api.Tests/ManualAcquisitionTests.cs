using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Net;
using System.Text;
using ChordPort.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

    [Fact]
    public async Task Manual_search_rejects_foreign_form_origin_before_contacting_indexers_but_accepts_same_origin()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<rss><channel><item><title>Safe Album</title><guid>provider-id</guid></item></channel></rss>", Encoding.UTF8, "application/xml") });
        var databasePath = Path.Combine(Path.GetTempPath(), $"chordport-{Guid.NewGuid():N}.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ChordPort", $"Data Source={databasePath}");
            builder.ConfigureServices(services => { services.RemoveAll<IndexerSearchClient>(); services.AddSingleton(new IndexerSearchClient(new HttpClient(handler))); });
        });
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/indexers", new { name = "Primary", type = "Newznab", serverUrl = "https://news.example.test", enabled = true, priority = 1, categories = "3000", apiKey = "indexer-sentinel", clearApiKey = false })).EnsureSuccessStatusCode();
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/manual-search?q=safe") { Content = new FormUrlEncodedContent([]) };
        foreign.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");

        var rejected = await client.SendAsync(foreign);

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Empty(handler.Requests);
        using var sameOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/manual-search?q=safe");
        sameOrigin.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        var accepted = await client.SendAsync(sameOrigin);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Foreign_origin_cannot_submit_a_candidate_to_sabnzbd()
    {
        var indexerHandler = new RecordingHandler(_ => throw new InvalidOperationException("Indexer must not be called."));
        var sabHandler = new RecordingHandler(_ => throw new InvalidOperationException("SABnzbd must not be called."));
        var candidates = new CandidateStore();
        var candidate = candidates.Issue("indexer", "Primary", new IndexerSearchHit("provider-id", "Safe Album", null, null, null));
        var databasePath = Path.Combine(Path.GetTempPath(), $"chordport-{Guid.NewGuid():N}.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ChordPort", $"Data Source={databasePath}");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<CandidateStore>(); services.AddSingleton(candidates);
                services.RemoveAll<IndexerSearchClient>(); services.AddSingleton(new IndexerSearchClient(new HttpClient(indexerHandler)));
                services.RemoveAll<SabnzbdClient>(); services.AddSingleton(new SabnzbdClient(new HttpClient(sabHandler)));
            });
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/manual-submit") { Content = JsonContent.Create(new { candidateId = candidate.Id }) };
        request.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(indexerHandler.Requests);
        Assert.Empty(sabHandler.Requests);
        Assert.True(candidates.TryConsume(candidate.Id, out _));
    }

    [Fact]
    public async Task Manual_search_issues_only_returned_candidates_when_multi_indexer_results_exceed_capacity()
    {
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchXml(request.RequestUri!.Host), Encoding.UTF8, "application/xml") });
        var candidates = new CandidateStore();
        var databasePath = Path.Combine(Path.GetTempPath(), $"chordport-{Guid.NewGuid():N}.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ChordPort", $"Data Source={databasePath}");
            builder.ConfigureServices(services => { services.RemoveAll<IndexerSearchClient>(); services.AddSingleton(new IndexerSearchClient(new HttpClient(handler))); services.RemoveAll<CandidateStore>(); services.AddSingleton(candidates); });
        });
        using var client = factory.CreateClient();
        for (var index = 0; index < 5; index++)
            (await client.PostAsJsonAsync("/api/indexers", new { name = $"Indexer {index}", type = "Newznab", serverUrl = $"https://indexer-{index}.example.test", enabled = true, priority = index, categories = "3000", apiKey = "indexer-sentinel", clearApiKey = false })).EnsureSuccessStatusCode();

        var response = await client.PostAsync("/api/manual-search?q=safe", null);
        var payload = await response.Content.ReadFromJsonAsync<ManualSearchResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        var results = payload!.Results;
        Assert.Equal(100, results.Count);
        Assert.All(results, result => Assert.True(candidates.TryConsume(result.CandidateId, out _)));
    }
    [Theory]
    [InlineData("<!DOCTYPE rss [<!ENTITY x 'bad'>]><rss><channel><item><title>&x;</title><guid>x</guid></item></channel></rss>")]
    [InlineData("<html><title>bad</title></html>")]
    public void Search_parser_rejects_dtd_and_unexpected_roots(string xml)
    {
        Assert.ThrowsAny<Exception>(() => IndexerSearchClient.ParseAtom(new MemoryStream(Encoding.UTF8.GetBytes(xml))));
    }
    [Theory]
    [InlineData("<!DOCTYPE rss [<!ENTITY x 'bad'>]><rss><channel><item><title>&x;</title><guid>x</guid></item></channel></rss>")]
    [InlineData("<html><title>bad</title></html>")]
    [InlineData("<not-an-atom-document />")]
    public async Task Manual_search_returns_a_safe_empty_response_for_malformed_indexer_documents(string body)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") });
        var databasePath = Path.Combine(Path.GetTempPath(), $"chordport-{Guid.NewGuid():N}.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ChordPort", $"Data Source={databasePath}");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IndexerSearchClient>();
                services.AddSingleton(new IndexerSearchClient(new HttpClient(handler)));
            });
        });
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/indexers", new { name = "Primary", type = "Newznab", serverUrl = "https://news.example.test", enabled = true, priority = 1, categories = "3000", apiKey = "indexer-sentinel", clearApiKey = false });
        created.EnsureSuccessStatusCode();

        var response = await client.PostAsync("/api/manual-search?q=safe", null);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"results\":[]", payload);
        Assert.Contains("No results found", payload);
        Assert.DoesNotContain("XmlException", payload);
        Assert.DoesNotContain("indexer-sentinel", payload);
    }

    [Fact]
    public async Task Grab_rejects_oversized_declared_content_without_opening_the_body()
    {
        var content = new ProbeContent(512 * 1024 + 1, Array.Empty<byte>());
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await new IndexerSearchClient(new HttpClient(handler)).GrabNzbAsync(new IndexerConnection("Newznab", "https://news.example.test", "sentinel-key"), "id", CancellationToken.None);

        Assert.Null(result);
        Assert.False(content.StreamOpened);
    }

    [Fact]
    public async Task Grab_rejects_chunked_body_past_limit_without_submitting_an_nzb()
    {
        var content = new ProbeContent(null, Array.Empty<byte>(), streamedLength: 512 * 1024 + 1);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var result = await new IndexerSearchClient(new HttpClient(handler)).GrabNzbAsync(new IndexerConnection("Newznab", "https://news.example.test", "sentinel-key"), "id", CancellationToken.None);

        Assert.Null(result);
        Assert.True(content.StreamOpened);
        Assert.True(content.StreamedBytesRead > 512 * 1024);
    }

    [Fact]
    public async Task Grab_accepts_only_fixed_get_path_and_nzb_xml()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<nzb></nzb>", Encoding.UTF8, "application/xml") });
        var nzb = await new IndexerSearchClient(new HttpClient(handler)).GrabNzbAsync(new IndexerConnection("Torznab", "https://news.example.test", "sentinel-key"), "provider id", CancellationToken.None);
        Assert.NotNull(nzb); Assert.Equal("t=get&id=provider%20id&apikey=sentinel-key", handler.Requests.Single().RequestUri!.Query.TrimStart('?'));
    }
    private sealed class RecordingHandler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler { public List<HttpRequestMessage> Requests { get; } = []; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(response(request)); } }
    private static string SearchXml(string prefix) => $"<rss><channel>{string.Concat(Enumerable.Range(0, 50).Select(index => $"<item><title>{prefix} {index}</title><guid>{prefix}-{index}</guid></item>"))}</channel></rss>";
    private sealed class ProbeContent : HttpContent
    {
        private readonly long? length;
        private readonly byte[] bytes;
        private readonly long? streamedLength;
        public bool StreamOpened { get; private set; }
        public long StreamedBytesRead { get; private set; }
        public ProbeContent(long? length, byte[] bytes, long? streamedLength = null)
        {
            this.length = length;
            this.bytes = bytes;
            this.streamedLength = streamedLength;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
        }
        protected override bool TryComputeLength(out long value) { value = length ?? 0; return length.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamOpened = true;
            return Task.FromResult<Stream>(streamedLength is long size
                ? new GeneratedStream(size, bytesRead => StreamedBytesRead += bytesRead)
                : new MemoryStream(bytes, writable: false));
        }
    }
    private sealed class GeneratedStream(long length, Action<int> recorded) : Stream
    {
        private long remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var read = (int)Math.Min(buffer.Length, remaining);
            buffer[..read].Fill((byte)'x');
            remaining -= read;
            recorded(read);
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
