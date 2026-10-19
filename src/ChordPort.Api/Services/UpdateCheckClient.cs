using System.Net.Http.Headers;
using System.Text.Json;

namespace ChordPort.Api.Services;

public sealed class UpdateCheckOptions
{
    public string CurrentVersion { get; init; } = "0.1.1";
    public TimeSpan CacheDuration { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed record UpdateCheckResponse(string CurrentVersion, bool Available, string? Version, string? ReleaseUrl, string Status, string? Message);

public static class UpdateReleaseUrlValidator
{
    public static bool IsValid(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath.StartsWith("/collectorscoveco-oss/chordport/releases/tag/", StringComparison.Ordinal)
        && uri.AbsolutePath.Length > "/collectorscoveco-oss/chordport/releases/tag/".Length;
}

public sealed class UpdateCheckClient
{
    private static readonly Uri ReleasesEndpoint = new("https://api.github.com/repos/collectorscoveco-oss/chordport/releases/latest");
    private readonly HttpClient _client;
    private readonly UpdateCheckOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateCheckResponse? _cached;
    private DateTimeOffset _cachedAt;

    public UpdateCheckClient(HttpClient client, UpdateCheckOptions options, Func<DateTimeOffset>? now = null)
    {
        _client = client; _options = options; _now = now ?? (() => DateTimeOffset.UtcNow);
        if (!_client.DefaultRequestHeaders.UserAgent.Any()) _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChordPort", options.CurrentVersion));
    }

    public async Task<UpdateCheckResponse> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && _now() - _cachedAt < _options.CacheDuration) return _cached;
            UpdateCheckResponse result;
            try
            {
                using var response = await _client.GetAsync(ReleasesEndpoint, cancellationToken);
                if (!response.IsSuccessStatusCode) result = SafeUnavailable();
                else
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    var root = document.RootElement;
                    var tag = root.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
                    var url = root.TryGetProperty("html_url", out var urlValue) ? urlValue.GetString() : null;
                    var prerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
                    var draft = root.TryGetProperty("draft", out var draftValue) && draftValue.ValueKind == JsonValueKind.True;
                    result = !prerelease && !draft && TryStableVersion(tag, out var version) && UpdateReleaseUrlValidator.IsValid(url) &&
                        TryStableVersion(_options.CurrentVersion, out var current) && version > current
                        ? new(_options.CurrentVersion, true, version.ToString(3), url, "ok", null)
                        : new(_options.CurrentVersion, false, null, null, "ok", null);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
            { result = SafeUnavailable(); }
            _cached = result; _cachedAt = _now(); return result;
        }
        finally { _gate.Release(); }
    }

    private UpdateCheckResponse SafeUnavailable() => new(_options.CurrentVersion, false, null, null, "unavailable", "Update information is unavailable right now.");
    private static bool TryStableVersion(string? text, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim().TrimStart('v', 'V');
        if (value.Count(c => c == '.') != 2 || !Version.TryParse(value, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0) return false;
        version = parsed;
        return true;
    }
}
