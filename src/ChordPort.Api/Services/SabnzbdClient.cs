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
        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                return new(false, "SABnzbd must use a loopback or private-network address.");
        }
        else if (!SabnzbdAddressPolicy.IsAllowed(address))
            return new(false, "SABnzbd must use a loopback or private-network address.");
        return new(true, null);
    }
}

public static class SabnzbdAddressPolicy
{
    public static bool IsAllowed(IPAddress address) => IndexerAddressPolicy.IsUserLocalHttpAddress(address);
}

public static class SabnzbdHttpHandlerFactory
{
    public static HttpMessageHandler Create() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length == 0 || !addresses.All(SabnzbdAddressPolicy.IsAllowed))
                throw new HttpRequestException("Unsafe SABnzbd endpoint.");
            var address = addresses[0];
            var socket = new System.Net.Sockets.Socket(address.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken); return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    };
}

public sealed class SabnzbdClient(HttpClient client)
{
    private const int MaxJsonBytes = 512 * 1024;
    public async Task<SabnzbdSubmitResponse> AddFileAsync(SabnzbdSettings settings, byte[] nzb, string title, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || !SabnzbdEndpointValidator.Validate(settings.ServerUrl).IsValid || string.IsNullOrWhiteSpace(settings.ApiKey) || nzb.Length is 0 or > 512 * 1024) return new(false, null, "SABnzbd settings are unavailable.");
        try
        {
            var uri = new UriBuilder(settings.ServerUrl.Trim()) { Path = "api", Query = $"mode=addfile&output=json&apikey={Uri.EscapeDataString(settings.ApiKey)}&cat={Uri.EscapeDataString(settings.Category)}" }.Uri;
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(nzb), "nzbfile", SanitizeFileName(title) + ".nzb");
            using var response = await client.PostAsync(uri, form, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, null, "SABnzbd did not accept the NZB.");
            using var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            var id = root.TryGetProperty("nzo_ids", out var ids) && ids.ValueKind == JsonValueKind.Array ? ids.EnumerateArray().FirstOrDefault().GetString() : null;
            return !string.IsNullOrWhiteSpace(id) ? new(true, id, "Submitted to SABnzbd.") : new(false, null, "SABnzbd returned an unexpected response.");
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(false, null, "SABnzbd returned an unexpected response."); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { return new(false, null, "Could not submit to SABnzbd."); }
    }
    public async Task<IReadOnlyDictionary<string, string>> GetKnownJobStatusesAsync(SabnzbdSettings settings, IReadOnlyCollection<string> knownIds, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, string>(); if (!settings.Enabled || knownIds.Count == 0 || !SabnzbdEndpointValidator.Validate(settings.ServerUrl).IsValid || string.IsNullOrWhiteSpace(settings.ApiKey)) return found;
        foreach (var mode in new[] { "queue", "history" }) try
        {
            var uri = new UriBuilder(settings.ServerUrl.Trim()) { Path = "api", Query = $"mode={mode}&output=json&apikey={Uri.EscapeDataString(settings.ApiKey)}" }.Uri;
            using var response = await client.GetAsync(uri, cancellationToken); if (!response.IsSuccessStatusCode) continue;
            using var document = await ReadJsonAsync(response, cancellationToken);
            Scan(document.RootElement, knownIds, found);
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or TaskCanceledException or OperationCanceledException) { }
        return found;
    }
    private static void Scan(JsonElement value, IReadOnlyCollection<string> known, IDictionary<string,string> found)
    { if (value.ValueKind == JsonValueKind.Object) { var id = value.TryGetProperty("nzo_id", out var nzo) ? nzo.GetString() : value.TryGetProperty("nzo_ids", out var ids) && ids.ValueKind == JsonValueKind.String ? ids.GetString() : null; if (!string.IsNullOrWhiteSpace(id) && known.Contains(id)) { var status = value.TryGetProperty("status", out var s) ? s.GetString() : value.TryGetProperty("state", out var st) ? st.GetString() : "queued"; found[id] = MapStatus(status); } foreach (var property in value.EnumerateObject()) Scan(property.Value, known, found); } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Scan(item, known, found); }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxJsonBytes) throw new JsonException("Response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var bounded = new BoundedReadStream(stream, MaxJsonBytes);
        return await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken);
    }
    private static string MapStatus(string? status) => status?.ToLowerInvariant() switch { var s when s?.Contains("fail") == true => "failed", var s when s?.Contains("complete") == true => "completed", var s when s?.Contains("download") == true => "downloading", _ => "queued" };
    private static string SanitizeFileName(string title) => string.Concat(title.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.')).Trim()[..Math.Min(120, Math.Max(1, string.Concat(title.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.')).Trim().Length))];

    public async Task<SabnzbdTestResponse> TestConnectionAsync(SabnzbdSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = SabnzbdEndpointValidator.Validate(settings.ServerUrl);
        if (!endpoint.IsValid || string.IsNullOrWhiteSpace(settings.ApiKey)) return new(false, "SABnzbd settings need a valid local address and API key.");
        try
        {
            var uri = new UriBuilder(settings.ServerUrl.Trim()) { Path = "api", Query = $"mode=version&output=json&apikey={Uri.EscapeDataString(settings.ApiKey)}" }.Uri;
            using var response = await client.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, "SABnzbd did not accept the connection test.");
            using var document = await ReadJsonAsync(response, cancellationToken);
            if (!document.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.GetString()))
                return new(false, "SABnzbd returned an unexpected response.");
            return new(true, $"Connected to SABnzbd {version.GetString()}.");
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(false, "SABnzbd returned an unexpected response."); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        { return new(false, "Could not connect to SABnzbd. Check the local address and try again."); }
    }
    private sealed class BoundedReadStream(Stream inner, int maximum) : Stream
    {
        private int read;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var value = inner.Read(buffer[..Math.Min(buffer.Length, maximum - read + 1)]);
            read += value; if (read > maximum) throw new IOException("Response too large."); return value;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var value = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximum - read + 1)], cancellationToken);
            read += value; if (read > maximum) throw new IOException("Response too large."); return value;
        }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
