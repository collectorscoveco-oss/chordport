using Microsoft.AspNetCore.Http;

namespace ChordPort.Api.Services;

/// <summary>
/// Rejects browser cross-site unsafe API requests before endpoint binding reads their body.
/// Browser mutations may originate only from the shipped fixed local UI origin. The Host
/// header is intentionally not consulted because it is controlled by the requester.
/// Requests with neither browser Origin nor Fetch Metadata remain permitted for local
/// non-browser clients; a web form cannot suppress those browser-controlled headers on a
/// cross-site POST.
/// </summary>
public static class LocalMutationRequestPolicy
{
    private const string LocalUiOrigin = "http://127.0.0.1:5098";

    public static bool IsAllowed(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase)) return false;

        var origin = request.Headers.Origin.ToString();
        return string.IsNullOrWhiteSpace(origin)
            || string.Equals(origin, LocalUiOrigin, StringComparison.OrdinalIgnoreCase);
    }
}
