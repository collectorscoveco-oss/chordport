using Microsoft.AspNetCore.DataProtection;

namespace ChordPort.Api.Services;

public sealed class SabnzbdSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("ChordPort.Sabnzbd.ApiKey.v1");
    public string Protect(string apiKey) => _protector.Protect(apiKey);
    public string Unprotect(string protectedApiKey) => _protector.Unprotect(protectedApiKey);
}

public sealed record SabnzbdSettings(bool Enabled, string ServerUrl, string Category, string ApiKey);
public sealed record SabnzbdTestResponse(bool Connected, string Message);
public sealed record SabnzbdSubmitResponse(bool Submitted, string? JobId, string Message);
public sealed record StoredSabnzbdSettings(bool Enabled, string ServerUrl, string Category, string? ProtectedApiKey);
public sealed record SabnzbdSettingsInput(bool Enabled, string ServerUrl, string Category, string? ApiKey, bool ClearApiKey);
public sealed record SabnzbdSettingsResponse(bool Enabled, string ServerUrl, string Category, bool HasApiKey, string Status);

public static class SabnzbdSettingsMerger
{
    public static StoredSabnzbdSettings Merge(StoredSabnzbdSettings existing, SabnzbdSettingsInput next)
    {
        var protectedKey = next.ClearApiKey ? null : string.IsNullOrWhiteSpace(next.ApiKey) ? existing.ProtectedApiKey : next.ApiKey;
        return new(next.Enabled, next.ServerUrl.Trim(), string.IsNullOrWhiteSpace(next.Category) ? "music" : next.Category.Trim(), protectedKey);
    }
}

public static class SabnzbdSettingsMapper
{
    public static SabnzbdSettingsResponse ToResponse(StoredSabnzbdSettings stored, string status = "saved") =>
        new(stored.Enabled, stored.ServerUrl, stored.Category, !string.IsNullOrWhiteSpace(stored.ProtectedApiKey), status);
}
