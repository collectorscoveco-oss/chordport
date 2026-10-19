using System.Text.Json;
using ChordPort.Api.Domain;

namespace ChordPort.Api.Services;

public sealed class MusicBrainzOptions
{
    public const string SectionName = "MusicBrainz";
    public string BaseUrl { get; set; } = "https://musicbrainz.org/ws/2";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MinimumRequestInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}

public interface IMusicMetadataProvider
{
    Task<MetadataSearchResponse> SearchAsync(SearchMode mode, string? query, CancellationToken cancellationToken);
}

public sealed class MusicBrainzClient : IMusicMetadataProvider
{
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, MetadataSearchResponse Response)> _cache = new();
    private readonly HttpClient _httpClient;
    private readonly MusicBrainzOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public MusicBrainzClient(
        HttpClient httpClient,
        MusicBrainzOptions options,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpClient = httpClient;
        _options = options;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<MetadataSearchResponse> SearchAsync(SearchMode mode, string? query, CancellationToken cancellationToken)
    {
        var term = (query ?? string.Empty).Trim();
        if (term.Length == 0) return new("MusicBrainz", "empty", [], "Enter a search term.");
        var cacheKey = $"{mode}:{term}";
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > _utcNow()) return cached.Response;
        }

        try
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                lock (_cache)
                {
                    if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > _utcNow()) return cached.Response;
                }
                var wait = _options.MinimumRequestInterval - (_utcNow() - _lastRequestAt);
                if (wait > TimeSpan.Zero) await _delayAsync(wait, cancellationToken);
                var path = mode switch { SearchMode.Songs => "recording", SearchMode.Artists => "artist", SearchMode.Albums => "release-group", _ => "recording" };
                if (!TryCreateSearchUri(_options.BaseUrl, path, term, out var uri)) return Failure();
                if (!_httpClient.DefaultRequestHeaders.UserAgent.Any()) _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ChordPort/0.1 (local metadata search)");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.Timeout);
                _lastRequestAt = _utcNow();
                using var response = await _httpClient.GetAsync(uri, timeout.Token);
                if (!response.IsSuccessStatusCode) return Failure();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                var result = Parse(mode, document.RootElement);
                lock (_cache) _cache[cacheKey] = (_utcNow().Add(_options.CacheDuration), result);
                return result;
            }
            finally { _requestGate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure(); }
        catch (HttpRequestException) { return Failure(); }
        catch (JsonException) { return Failure(); }
    }

    private static bool TryCreateSearchUri(string? baseUrl, string path, string term, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        var normalized = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri) ||
            baseUri is null ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(baseUri.Host)) return false;
        if (!Uri.TryCreate($"{normalized}/{path}?query={Uri.EscapeDataString(term)}&fmt=json", UriKind.Absolute, out var searchUri) || searchUri is null) return false;
        uri = searchUri;
        return true;
    }

    private static MetadataSearchResponse Parse(SearchMode mode, JsonElement root)
    {
        var property = mode switch { SearchMode.Songs => "recordings", SearchMode.Artists => "artists", SearchMode.Albums => "release-groups", _ => "recordings" };
        if (!root.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array) return new("MusicBrainz", "empty", [], null);
        var results = new List<MetadataSearchResult>();
        foreach (var item in entries.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var title = GetString(item, mode == SearchMode.Artists ? "name" : "title");
            var artist = Artist(item);
            var album = mode == SearchMode.Songs && item.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0 ? GetString(releases[0], "title") : string.Empty;
            var seconds = mode == SearchMode.Songs && item.TryGetProperty("length", out var length) && length.TryGetInt32(out var milliseconds) ? milliseconds / 1000 : 0;
            results.Add(new(id, mode == SearchMode.Songs ? "recording" : mode == SearchMode.Artists ? "artist" : "release-group", title, artist, album, seconds));
        }
        return new("MusicBrainz", results.Count == 0 ? "empty" : "ok", results, results.Count == 0 ? "No MusicBrainz matches found." : null);
    }

    private static string Artist(JsonElement item) => item.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array
        ? string.Join(", ", credits.EnumerateArray().Select(c => GetString(c, "name")).Where(name => !string.IsNullOrWhiteSpace(name))) : string.Empty;
    private static string GetString(JsonElement item, string property) => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static MetadataSearchResponse Failure() => new("MusicBrainz", "error", [], "MusicBrainz could not be reached. Try again later.");
}
