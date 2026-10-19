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

public sealed class MusicBrainzClient(HttpClient httpClient, MusicBrainzOptions options) : IMusicMetadataProvider
{
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset ExpiresAt, MetadataSearchResponse Response)> _cache = new();
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public async Task<MetadataSearchResponse> SearchAsync(SearchMode mode, string? query, CancellationToken cancellationToken)
    {
        var term = (query ?? string.Empty).Trim();
        if (term.Length == 0) return new("MusicBrainz", "empty", [], "Enter a search term.");
        var cacheKey = $"{mode}:{term}";
        lock (_cache)
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Response;
        }

        try
        {
            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                lock (_cache)
                {
                    if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Response;
                }
                var wait = options.MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
                var path = mode switch { SearchMode.Songs => "recording", SearchMode.Artists => "artist", SearchMode.Albums => "release-group", _ => "recording" };
                var baseUrl = options.BaseUrl.TrimEnd('/');
                var uri = new Uri($"{baseUrl}/{path}?query={Uri.EscapeDataString(term)}&fmt=json");
                if (!httpClient.DefaultRequestHeaders.UserAgent.Any()) httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ChordPort/0.1 (local metadata search)");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);
                using var response = await httpClient.GetAsync(uri, timeout.Token);
                _lastRequestAt = DateTimeOffset.UtcNow;
                if (!response.IsSuccessStatusCode) return Failure();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                var result = Parse(mode, document.RootElement);
                lock (_cache) _cache[cacheKey] = (DateTimeOffset.UtcNow.Add(options.CacheDuration), result);
                return result;
            }
            finally { _requestGate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure(); }
        catch (HttpRequestException) { return Failure(); }
        catch (JsonException) { return Failure(); }
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
