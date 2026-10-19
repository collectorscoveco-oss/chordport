using ChordPort.Api.Domain;
using Microsoft.EntityFrameworkCore;
namespace ChordPort.Api.Data;
public sealed class ChordPortDbContext(DbContextOptions<ChordPortDbContext> options) : DbContext(options)
{
    public DbSet<QueueRequestEntity> QueueRequests => Set<QueueRequestEntity>();
    public DbSet<SettingsEntity> Settings => Set<SettingsEntity>();
    public DbSet<IndexerEntity> Indexers => Set<IndexerEntity>();
}
public sealed class QueueRequestEntity
{
    public string Id { get; set; } = string.Empty; public string SongId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty; public string Artist { get; set; } = string.Empty; public DateTimeOffset RequestedAt { get; set; }
}
public sealed class SettingsEntity { public int Id { get; set; } = 1; public string LibraryRoot { get; set; } = "Music"; public string ImportMode { get; set; } = "Manual"; public string MetadataProvider { get; set; } = "Local catalog"; public bool SabnzbdEnabled { get; set; } public string SabnzbdServerUrl { get; set; } = "http://127.0.0.1:8080"; public string SabnzbdCategory { get; set; } = "music"; public string? SabnzbdProtectedApiKey { get; set; } }
public sealed class IndexerEntity
{
    public string Id { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public string Type { get; set; } = string.Empty; public string ServerUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } public int Priority { get; set; } public string? Categories { get; set; } public string? ProtectedApiKey { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; } public string? LastTestStatus { get; set; } public string? LastTestMessage { get; set; } public string? CapabilitySummary { get; set; }
}
