using System.Net;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.DataProtection;
using ChordPort.Api.Domain;

namespace ChordPort.Api.Services;

public sealed class IndexerSecretProtector(Microsoft.AspNetCore.DataProtection.IDataProtectionProvider provider)
{
    private readonly Microsoft.AspNetCore.DataProtection.IDataProtector _protector = provider.CreateProtector("ChordPort.Indexer.ApiKey.v1");
    public string Protect(string apiKey) => _protector.Protect(apiKey);
    public string Unprotect(string protectedApiKey) => _protector.Unprotect(protectedApiKey);
}

public sealed record IndexerInput(string Name, string Type, string ServerUrl, bool Enabled, int Priority, string? Categories, string? ApiKey, bool ClearApiKey);
public sealed record StoredIndexer(string Id, string Name, string Type, string ServerUrl, bool Enabled, int Priority, string? Categories, string? ProtectedApiKey, DateTimeOffset? LastTestedAt, string? LastTestStatus, string? LastTestMessage, string? CapabilitySummary);
public sealed record IndexerResponse(string Id, string Name, string Type, string ServerUrl, bool Enabled, int Priority, string? Categories, bool HasApiKey, DateTimeOffset? LastTestedAt, string? LastTestStatus, string? LastTestMessage, string? CapabilitySummary);
public sealed record IndexerConnection(string Type, string ServerUrl, string ApiKey);
public sealed record IndexerTestResponse(bool Connected, bool ProtocolConfirmed, string Message, string CapabilitySummary);

public static class IndexerSettingsMerger
{
    public static StoredIndexer Merge(StoredIndexer existing, IndexerInput next, string? protectedApiKey = null) => existing with
    {
        Name = next.Name.Trim(), Type = next.Type.Trim(), ServerUrl = next.ServerUrl.Trim().TrimEnd('/'), Enabled = next.Enabled,
        Priority = next.Priority, Categories = NormalizeCategories(next.Categories),
        ProtectedApiKey = next.ClearApiKey ? null : string.IsNullOrWhiteSpace(next.ApiKey) ? existing.ProtectedApiKey : protectedApiKey
    };
    public static string? NormalizeCategories(string? value) => string.IsNullOrWhiteSpace(value) ? null : string.Join(',', value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}
public static class IndexerMapper
{
    public static IndexerResponse ToResponse(StoredIndexer stored) => new(stored.Id, stored.Name, stored.Type, stored.ServerUrl, stored.Enabled, stored.Priority, stored.Categories, !string.IsNullOrWhiteSpace(stored.ProtectedApiKey), stored.LastTestedAt, stored.LastTestStatus, stored.LastTestMessage, stored.CapabilitySummary);
}

public static class IndexerEndpointValidator
{
    public static ValidationResult Validate(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0))
            return new(false, "Indexer must use a root HTTP(S) server address without credentials, a path, query, or fragment.");
        if (IPAddress.TryParse(uri.Host, out var address))
        {
            if (uri.Scheme == Uri.UriSchemeHttps && IndexerAddressPolicy.IsPublicHttpsAddress(address)) return new(true, null);
            if (uri.Scheme == Uri.UriSchemeHttp && IndexerAddressPolicy.IsUserLocalHttpAddress(address)) return new(true, null);
            return new(false, uri.Scheme == Uri.UriSchemeHttps ? "HTTPS indexers must use a public address." : "HTTP indexers must use a loopback or private-network address; remote indexers require HTTPS.");
        }
        if (uri.Scheme == Uri.UriSchemeHttps && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return new(true, null);
        if (uri.Scheme == Uri.UriSchemeHttp && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return new(true, null);
        return new(false, "HTTP indexers must use a loopback or private-network address; remote indexers require HTTPS.");
    }

}

public static class IndexerAddressPolicy
{
    public static bool IsUserLocalHttpAddress(IPAddress address)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }

    // HTTPS targets must be globally routable. Deny special-use space rather than attempting to maintain an allow-list of public networks.
    public static bool IsPublicHttpsAddress(IPAddress address)
    {
        address = Normalize(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return !IsSpecialIpv4(bytes);
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
        return !IsSpecialIpv6(address, bytes);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsSpecialIpv4(byte[] bytes) =>
        bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224 ||
        (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
        (bytes[0] == 169 && bytes[1] == 254) ||
        (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
        (bytes[0] == 192 && (bytes[1] == 0 || bytes[1] == 168 || (bytes[1] == 88 && bytes[2] == 99) || (bytes[1] == 0 && bytes[2] == 2))) ||
        (bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100))) ||
        (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);

    private static bool IsSpecialIpv6(IPAddress address, byte[] bytes) =>
        address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Loopback) ||
        bytes[0] == 0xff || // multicast
        (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) || // link-local
        (bytes[0] & 0xfe) == 0xfc || // unique-local
        (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0) || // deprecated site-local
        IsPrefix(bytes, 0x20, 0x01, 0x0d, 0xb8) || // documentation
        IsPrefix(bytes, 0x20, 0x01, 0x00, 0x02) || // benchmarking
        IsPrefix(bytes, 0x00, 0x64, 0xff, 0x9b) || // NAT64 well-known prefix
        IsPrefix(bytes, 0x01, 0x00, 0x00, 0x00) || // discard-only 100::/64
        IsPrefix(bytes, 0x20, 0x02) || // 6to4 can encode a non-public IPv4 destination
        IsPrefix(bytes, 0x20, 0x01, 0x00); // IETF special-purpose 2001:0::/24

    private static bool IsPrefix(byte[] bytes, params byte[] prefix) => bytes.AsSpan().StartsWith(prefix);
}

public static class IndexerHttpHandlerFactory
{
    public static HttpMessageHandler Create() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
            var scheme = context.InitialRequestMessage.RequestUri?.Scheme;
            if (addresses.Length == 0 || !addresses.All(address => IsAllowedConnectAddress(scheme, address)))
                throw new HttpRequestException("Unsafe indexer endpoint.");
            var socket = new System.Net.Sockets.Socket(addresses[0].AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), cancellationToken); return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    };
    private static bool IsAllowedConnectAddress(string? scheme, IPAddress address) => scheme switch
    {
        "https" => IndexerAddressPolicy.IsPublicHttpsAddress(address),
        "http" => IndexerAddressPolicy.IsUserLocalHttpAddress(address),
        _ => false
    };
}

public sealed class IndexerClient(HttpClient client)
{
    private const int MaxCapabilityBytes = 512 * 1024;
    public async Task<IndexerTestResponse> TestCapabilitiesAsync(IndexerConnection connection, CancellationToken cancellationToken)
    {
        if (!IsSupportedType(connection.Type) || !IndexerEndpointValidator.Validate(connection.ServerUrl).IsValid || string.IsNullOrWhiteSpace(connection.ApiKey))
            return new(false, false, "Indexer settings need a supported type, valid server address, and API key.", "");
        try
        {
            var uri = new UriBuilder(connection.ServerUrl.Trim().TrimEnd('/')) { Path = "api", Query = $"t=caps&apikey={Uri.EscapeDataString(connection.ApiKey)}" }.Uri;
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return new(false, false, "Indexer did not accept the capability test.", "");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var parsed = ParseCapabilities(stream, connection.Type);
            if (!parsed.ProtocolConfirmed) return new(false, false, "Indexer returned an unexpected capability response.", "");
            return new(true, true, "Indexer capability test succeeded.", parsed.Summary);
        }
        catch (XmlException) { return new(false, false, "Indexer returned an unexpected capability response.", ""); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException) { return new(false, false, "Could not connect to the indexer. Check the address and try again.", ""); }
    }
    private static (bool ProtocolConfirmed, string Summary) ParseCapabilities(Stream input, string expectedType)
    {
        using var bounded = new BoundedReadStream(input, MaxCapabilityBytes);
        using var reader = XmlReader.Create(bounded, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true, MaxCharactersInDocument = MaxCapabilityBytes });
        var protocol = false; var categories = new List<string>(); var rootSeen = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (!rootSeen)
            {
                rootSeen = true;
                if (reader.Depth != 0 || !string.Equals(reader.LocalName, "caps", StringComparison.Ordinal)) return (false, "");
                continue;
            }
            if (reader.Depth == 1 && reader.LocalName == "server" && string.Equals(reader.GetAttribute("type"), expectedType, StringComparison.OrdinalIgnoreCase)) protocol = true;
            if (reader.Name is "category" or "subcat")
            {
                var id = reader.GetAttribute("id"); var name = reader.GetAttribute("name");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name) && (name.Contains("music", StringComparison.OrdinalIgnoreCase) || categories.Count > 0)) categories.Add($"{id} {name}");
            }
        }
        return (protocol, string.Join(", ", categories.Distinct().Take(12)) is { Length: > 0 } summary ? summary : "No music categories reported.");
    }
    public static bool IsSupportedType(string? type) => type is "Newznab" or "Torznab";

    private sealed class BoundedReadStream(Stream inner, int maximum) : Stream
    {
        private int _remaining = maximum;
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var value = inner.Read(buffer, offset, Math.Min(count, _remaining)); _remaining -= value; if (value == 0 && _remaining == 0) throw new XmlException("Capability response was too large."); return value; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { var value = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], token); _remaining -= value; if (value == 0 && _remaining == 0) throw new XmlException("Capability response was too large."); return value; }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin w) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
