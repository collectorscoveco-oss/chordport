using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using ChordPort.Api.Domain;

namespace ChordPort.Api.Services;

public sealed record IndexerSearchHit(string ResultId, string Title, long? SizeBytes, DateTimeOffset? PublishedAt, int? Seeders);
public sealed record SearchCandidate(string Id, string IndexerId, string IndexerName, string ResultId, string Title, long? SizeBytes, DateTimeOffset? PublishedAt, int? Seeders, DateTimeOffset ExpiresAt);
public sealed record ManualSearchResult(string CandidateId, string Title, long? SizeBytes, DateTimeOffset? PublishedAt, int? Seeders, string Indexer);
public sealed record ManualSearchResponse(IReadOnlyList<ManualSearchResult> Results, string? Message);
public sealed record SubmitCandidateRequest(string CandidateId);
public sealed record ActivityResponse(string Id, string Title, string Indexer, string? SabJobId, string Status, string Message, DateTimeOffset SubmittedAt, DateTimeOffset? UpdatedAt);

public sealed class CandidateStore
{
    private const int Maximum = 200;
    private readonly ConcurrentDictionary<string, SearchCandidate> _items = new();
    private readonly TimeProvider _clock;
    public CandidateStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;
    public SearchCandidate Issue(string indexerId, string indexerName, IndexerSearchHit hit)
    {
        Prune();
        while (_items.Count >= Maximum && _items.OrderBy(pair => pair.Value.ExpiresAt).FirstOrDefault() is { Key: { } key }) _items.TryRemove(key, out _);
        var candidate = new SearchCandidate(Guid.NewGuid().ToString("N"), indexerId, indexerName, hit.ResultId, Trim(hit.Title, 300), hit.SizeBytes, hit.PublishedAt, hit.Seeders, _clock.GetUtcNow().AddMinutes(10));
        _items[candidate.Id] = candidate;
        return candidate;
    }
    public bool TryConsume(string? id, out SearchCandidate candidate)
    {
        candidate = default!;
        if (string.IsNullOrWhiteSpace(id) || !_items.TryRemove(id, out var found) || found.ExpiresAt <= _clock.GetUtcNow()) return false;
        candidate = found; return true;
    }
    private void Prune() { foreach (var item in _items.Where(pair => pair.Value.ExpiresAt <= _clock.GetUtcNow())) _items.TryRemove(item.Key, out _); }
    internal static string Trim(string value, int maximum) => value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}

public sealed class IndexerSearchClient(HttpClient client)
{
    private const int MaxBytes = 512 * 1024;
    public async Task<IReadOnlyList<IndexerSearchHit>> SearchAsync(IndexerConnection connection, string categories, string query, CancellationToken token)
    {
        if (!IndexerClient.IsSupportedType(connection.Type) || !IndexerEndpointValidator.Validate(connection.ServerUrl).IsValid || string.IsNullOrWhiteSpace(connection.ApiKey) || string.IsNullOrWhiteSpace(query)) return [];
        try
        {
            var uri = ApiUri(connection.ServerUrl, $"t=search&cat={Uri.EscapeDataString(categories)}&q={Uri.EscapeDataString(query.Trim())}&apikey={Uri.EscapeDataString(connection.ApiKey)}");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || !IsXml(response.Content.Headers.ContentType?.MediaType)) return [];
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return ParseAtom(stream);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or FormatException or TaskCanceledException or OperationCanceledException) { return []; }
    }
    public async Task<byte[]?> GrabNzbAsync(IndexerConnection connection, string resultId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(resultId) || !IndexerClient.IsSupportedType(connection.Type) || !IndexerEndpointValidator.Validate(connection.ServerUrl).IsValid) return null;
        try
        {
            var uri = ApiUri(connection.ServerUrl, $"t=get&id={Uri.EscapeDataString(resultId)}&apikey={Uri.EscapeDataString(connection.ApiKey)}");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || !IsXml(response.Content.Headers.ContentType?.MediaType) || response.Content.Headers.ContentLength is > MaxBytes) return null;
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var bounded = new MemoryStream(response.Content.Headers.ContentLength is long length ? (int)length : 0);
            var buffer = new byte[81920];
            var total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, token);
                if (read == 0) break;
                if (read > MaxBytes - total) return null;
                await bounded.WriteAsync(buffer.AsMemory(0, read), token);
                total += read;
            }
            if (total == 0) return null;
            var bytes = bounded.ToArray(); if (!IsNzb(bytes)) return null; return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or TaskCanceledException or OperationCanceledException) { return null; }
    }
    private static Uri ApiUri(string root, string query) => new UriBuilder(root.Trim().TrimEnd('/')) { Path = "api", Query = query }.Uri;
    private static bool IsXml(string? mediaType) => mediaType is not null && (mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("application/x-nzb", StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<IndexerSearchHit> ParseAtom(Stream input)
    {
        using var bounded = new LimitedStream(input, MaxBytes);
        using var reader = XmlReader.Create(bounded, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = MaxBytes });
        var document = System.Xml.Linq.XDocument.Load(reader, System.Xml.Linq.LoadOptions.None);
        if (document.Root?.Name.LocalName is not ("rss" or "feed")) throw new XmlException("Unexpected search document.");
        return document.Descendants().Where(x => x.Name.LocalName is "item" or "entry").Select(item =>
        {
            var title = item.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value?.Trim() ?? "";
            var id = item.Elements().FirstOrDefault(x => x.Name.LocalName is "guid" or "id")?.Value?.Trim() ?? item.Descendants().FirstOrDefault(x => x.Name.LocalName == "attr" && (string?)x.Attribute("name") == "guid")?.Attribute("value")?.Value?.Trim() ?? "";
            var size = item.Descendants().FirstOrDefault(x => x.Name.LocalName == "attr" && (string?)x.Attribute("name") == "size")?.Attribute("value")?.Value;
            var seeders = item.Descendants().FirstOrDefault(x => x.Name.LocalName == "attr" && (string?)x.Attribute("name") == "seeders")?.Attribute("value")?.Value;
            var date = item.Elements().FirstOrDefault(x => x.Name.LocalName is "pubDate" or "published")?.Value;
            return new IndexerSearchHit(id, title, long.TryParse(size, out var parsedSize) ? parsedSize : null, DateTimeOffset.TryParse(date, out var parsedDate) ? parsedDate : null, int.TryParse(seeders, out var parsedSeeders) ? parsedSeeders : null);
        }).Where(hit => !string.IsNullOrWhiteSpace(hit.ResultId) && !string.IsNullOrWhiteSpace(hit.Title)).Take(50).ToArray();
    }
    private static bool IsNzb(byte[] bytes)
    {
        try { using var stream = new MemoryStream(bytes); using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }); while (reader.Read()) if (reader.NodeType == XmlNodeType.Element) return reader.LocalName == "nzb"; } catch (XmlException) { } return false;
    }
    private sealed class LimitedStream(Stream inner, int limit) : Stream
    { private int _left = limit; public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); } public override int Read(byte[] b,int o,int c) { var n=inner.Read(b,o,Math.Min(c,_left)); _left-=n; if(n==0&&_left==0) throw new XmlException("Response too large."); return n;} public override async ValueTask<int> ReadAsync(Memory<byte>b,CancellationToken t=default){var n=await inner.ReadAsync(b[..Math.Min(b.Length,_left)],t);_left-=n;if(n==0&&_left==0)throw new XmlException("Response too large.");return n;} public override void Flush()=>throw new NotSupportedException(); public override long Seek(long o,SeekOrigin w)=>throw new NotSupportedException(); public override void SetLength(long v)=>throw new NotSupportedException(); public override void Write(byte[]b,int o,int c)=>throw new NotSupportedException(); }
}
