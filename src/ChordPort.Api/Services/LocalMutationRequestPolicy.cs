using Microsoft.AspNetCore.Http;

namespace ChordPort.Api.Services;

/// <summary>
/// Rejects browser cross-site unsafe API requests before endpoint binding reads their body.
/// An explicit Origin must exactly match the UI request origin, so localhost and 127.0.0.1
/// remain supported only when the page and API use the same host. Requests with neither
/// browser Origin nor Fetch Metadata are permitted for local non-browser clients; a web
/// form cannot suppress those browser-controlled headers on a cross-site POST.
/// </summary>
public static class LocalMutationRequestPolicy
{
    public static bool IsAllowed(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase)) return false;

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;

        var expected = $"{request.Scheme}://{request.Host}";
        return string.Equals(originUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'), expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.TrimEnd('/'), originUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
