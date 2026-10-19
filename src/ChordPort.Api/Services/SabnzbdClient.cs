using System.Net;
using System.Text.Json;
using ChordPort.Api.Domain;

namespace ChordPort.Api.Services;

public static class SabnzbdEndpointValidator
{
    public static ValidationResult Validate(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0))
            return new(false, "SABnzbd must use a local or private-network HTTP(S) server address without a path.");
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return new(true, null);
        if (!IPAddress.TryParse(uri.Host, out var address) || !IsLocalOrPrivate(address))
            return new(false, "SABnzbd must use a loopback or private-network address.");
        return new(true, null);
    }

    private static bool IsLocalOrPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }
}

public sealed class SabnzbdClient(HttpClient client)
{
    public async Task<SabnzbdTestResponse> TestConnectionAsync(SabnzbdSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = SabnzbdEndpointValidator.Validate(settings.ServerUrl);
        if (!endpoint.IsValid || string.IsNullOrWhiteSpace(settings.ApiKey)) return new(false, "SABnzbd settings need a valid local address and API key.");
        try
        {
            var uri = new UriBuilder(settings.ServerUrl.Trim()) { Path = "api", Query = $"mode=version&output=json&apikey={Uri.EscapeDataString(settings.ApiKey)}" }.Uri;
            using var response = await client.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, "SABnzbd did not accept the connection test.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.GetString()))
                return new(false, "SABnzbd returned an unexpected response.");
            return new(true, $"Connected to SABnzbd {version.GetString()}.");
        }
        catch (JsonException) { return new(false, "SABnzbd returned an unexpected response."); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        { return new(false, "Could not connect to SABnzbd. Check the local address and try again."); }
    }
}
