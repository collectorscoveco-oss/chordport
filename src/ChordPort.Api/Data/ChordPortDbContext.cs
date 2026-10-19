using ChordPort.Api.Domain;
using Microsoft.EntityFrameworkCore;
namespace ChordPort.Api.Data;
public sealed class ChordPortDbContext(DbContextOptions<ChordPortDbContext> options) : DbContext(options)
{
    public DbSet<QueueRequestEntity> QueueRequests => Set<QueueRequestEntity>();
    public DbSet<SettingsEntity> Settings => Set<SettingsEntity>();
}
public sealed class QueueRequestEntity
{
    public string Id { get; set; } = string.Empty; public string SongId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty; public string Artist { get; set; } = string.Empty; public DateTimeOffset RequestedAt { get; set; }
}
public sealed class SettingsEntity { public int Id { get; set; } = 1; public string LibraryRoot { get; set; } = "Music"; public string ImportMode { get; set; } = "Manual"; public string MetadataProvider { get; set; } = "Local catalog"; }
