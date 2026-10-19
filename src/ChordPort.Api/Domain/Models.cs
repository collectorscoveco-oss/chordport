using ChordPort.Api.Services;

namespace ChordPort.Api.Domain;

public enum SearchMode { Songs, Artists, Albums }
public sealed record Song(string Id, string Title, string Artist, string Album, int DurationSeconds);
public sealed record QueueRequest(string Id, string SongId, string Title, string Artist, DateTimeOffset RequestedAt);
public sealed record Settings(string LibraryRoot, string ImportMode, string MetadataProvider = "Local catalog");
public sealed record SettingsUpdate(string LibraryRoot, string ImportMode, string MetadataProvider = "Local catalog", SabnzbdSettingsInput? Sabnzbd = null);
public sealed record SettingsResponse(string LibraryRoot, string ImportMode, string MetadataProvider, SabnzbdSettingsResponse Sabnzbd);
public sealed record MetadataSearchResult(string Id, string EntityType, string Title, string Artist, string Album, int DurationSeconds);
public sealed record MetadataSearchResponse(string Provider, string Status, IReadOnlyList<MetadataSearchResult> Results, string? Message);
public sealed record ValidationResult(bool IsValid, string? Error);
public sealed record HealthStatus(string Status, string Service, DateTimeOffset CheckedAt);
public sealed record QueueCreateRequest(string SongId);
