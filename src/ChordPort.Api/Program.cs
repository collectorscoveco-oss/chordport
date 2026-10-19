using ChordPort.Api.Data;
using ChordPort.Api.Domain;
using ChordPort.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

var applicationBaseDirectory = AppContext.BaseDirectory;
var dataDirectory = RuntimeDataDirectory.ResolveDefault(applicationBaseDirectory);
Directory.CreateDirectory(dataDirectory);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = applicationBaseDirectory,
    WebRootPath = Path.Combine(applicationBaseDirectory, "wwwroot")
});
var defaultConnectionString = $"Data Source={Path.Combine(dataDirectory, "chordport.db")}";
var dataProtectionDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(dataProtectionDirectory);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dataProtectionDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDirectory));
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
builder.Services.AddDbContext<ChordPortDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("ChordPort") ?? defaultConnectionString));
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<ICatalogAdapter>(sp => sp.GetRequiredService<CatalogService>());
builder.Services.AddSingleton<SettingsService>();
builder.Services.Configure<MusicBrainzOptions>(builder.Configuration.GetSection(MusicBrainzOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MusicBrainzOptions>>().Value);
builder.Services.AddHttpClient<IMusicMetadataProvider, MusicBrainzClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MusicBrainzOptions>>().Value;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChordPort/0.1 (local metadata search)");
    client.Timeout = options.Timeout;
});
builder.Services.AddSingleton<SabnzbdSecretProtector>();
builder.Services.AddHttpClient<SabnzbdClient>(client => client.Timeout = TimeSpan.FromSeconds(5)).RemoveAllLoggers();
builder.Services.AddSingleton(new UpdateCheckOptions { CurrentVersion = "0.1.1" });
builder.Services.AddHttpClient<UpdateCheckClient>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChordPort/0.1.1 (local update check)");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton<HealthService>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChordPortDbContext>();
    db.Database.EnsureCreated();
    EnsureColumn(db, SettingsColumn.MetadataProvider);
    EnsureColumn(db, SettingsColumn.SabnzbdEnabled);
    EnsureColumn(db, SettingsColumn.SabnzbdServerUrl);
    EnsureColumn(db, SettingsColumn.SabnzbdCategory);
    EnsureColumn(db, SettingsColumn.SabnzbdProtectedApiKey);
}

app.MapGet("/api/health", (HealthService health) => Results.Ok(health.Get()));
app.MapGet("/api/search", async (string? mode, string? q, string? provider, ICatalogAdapter catalog, IMusicMetadataProvider musicBrainz, CancellationToken cancellationToken) =>
{
    if (!Enum.TryParse<SearchMode>(mode, true, out var parsed)) return Results.BadRequest(new { error = "mode must be Songs, Artists, or Albums." });
    var selected = string.IsNullOrWhiteSpace(provider) ? "Local catalog" : provider;
    if (selected == "Local catalog")
    {
        var results = catalog.Search(parsed, q).Select(song => new MetadataSearchResult(song.Id, "local-song", song.Title, song.Artist, song.Album, song.DurationSeconds)).ToArray();
        return Results.Ok(new MetadataSearchResponse("Local catalog", results.Length == 0 ? "empty" : "ok", results, results.Length == 0 ? "No local catalog matches found." : null));
    }
    if (selected != "MusicBrainz") return Results.BadRequest(new { error = "provider must be Local catalog or MusicBrainz." });
    return Results.Ok(await musicBrainz.SearchAsync(parsed, q, cancellationToken));
});
app.MapGet("/api/queue", async (ChordPortDbContext db) =>
    Results.Ok((await db.QueueRequests.ToListAsync()).OrderBy(item => item.RequestedAt)));
app.MapPost("/api/queue", async (QueueCreateRequest request, ChordPortDbContext db, CatalogService catalog) =>
{
    if (string.IsNullOrWhiteSpace(request.SongId)) return Results.BadRequest(new { error = "songId is required." });
    var song = catalog.Find(request.SongId);
    if (song is null) return Results.NotFound(new { error = "Song was not found in the local catalog." });
    var entity = new QueueRequestEntity { Id = Guid.NewGuid().ToString("N"), SongId = song.Id, Title = song.Title, Artist = song.Artist, RequestedAt = DateTimeOffset.UtcNow };
    db.QueueRequests.Add(entity); await db.SaveChangesAsync(); return Results.Created($"/api/queue/{entity.Id}", entity);
});
app.MapDelete("/api/queue/{id}", async (string id, ChordPortDbContext db) =>
{
    var item = await db.QueueRequests.FindAsync(id);
    if (item is null) return Results.NotFound(new { error = "Queue request was not found." });
    db.QueueRequests.Remove(item); await db.SaveChangesAsync(); return Results.NoContent();
});
app.MapGet("/api/settings", async (ChordPortDbContext db) =>
{
    var value = await GetOrCreateSettings(db);
    return Results.Ok(ToSettingsResponse(value));
});
app.MapPut("/api/settings", async (SettingsUpdate settings, SettingsService validator, ChordPortDbContext db, SabnzbdSecretProtector secrets) =>
{
    var validation = validator.Update(new Settings(settings.LibraryRoot, settings.ImportMode, settings.MetadataProvider));
    if (!validation.IsValid) return Results.BadRequest(new { error = validation.Error });
    var entity = await GetOrCreateSettings(db);
    entity.LibraryRoot = validator.Get().LibraryRoot; entity.ImportMode = validator.Get().ImportMode; entity.MetadataProvider = validator.Get().MetadataProvider;
    if (settings.Sabnzbd is not null)
    {
        var endpoint = SabnzbdEndpointValidator.Validate(settings.Sabnzbd.ServerUrl);
        if (!endpoint.IsValid) return Results.BadRequest(new { error = endpoint.Error });
        var existing = ToStoredSettings(entity);
        var input = settings.Sabnzbd with { ApiKey = string.IsNullOrWhiteSpace(settings.Sabnzbd.ApiKey) ? null : secrets.Protect(settings.Sabnzbd.ApiKey) };
        var merged = SabnzbdSettingsMerger.Merge(existing, input);
        entity.SabnzbdEnabled = merged.Enabled; entity.SabnzbdServerUrl = merged.ServerUrl; entity.SabnzbdCategory = merged.Category; entity.SabnzbdProtectedApiKey = merged.ProtectedApiKey;
    }
    await db.SaveChangesAsync(); return Results.Ok(ToSettingsResponse(entity));
});
app.MapPost("/api/settings/sabnzbd/test", async (ChordPortDbContext db, SabnzbdSecretProtector secrets, SabnzbdClient sabnzbd, CancellationToken cancellationToken) =>
{
    var entity = await GetOrCreateSettings(db);
    if (!entity.SabnzbdEnabled || string.IsNullOrWhiteSpace(entity.SabnzbdProtectedApiKey)) return Results.BadRequest(new { error = "Enable SABnzbd and save an API key before testing." });
    try
    {
        var result = await sabnzbd.TestConnectionAsync(new SabnzbdSettings(entity.SabnzbdEnabled, entity.SabnzbdServerUrl, entity.SabnzbdCategory, secrets.Unprotect(entity.SabnzbdProtectedApiKey)), cancellationToken);
        return Results.Ok(result);
    }
    catch (Exception exception) when (exception is CryptographicException) { return Results.BadRequest(new { error = "The saved SABnzbd key is unavailable. Save it again before testing." }); }
});
app.MapGet("/api/system/update", async (UpdateCheckClient updates, CancellationToken cancellationToken) => Results.Ok(await updates.CheckAsync(cancellationToken)));
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();
public partial class Program
{
    private enum SettingsColumn { MetadataProvider, SabnzbdEnabled, SabnzbdServerUrl, SabnzbdCategory, SabnzbdProtectedApiKey }

    private static void EnsureColumn(ChordPortDbContext db, SettingsColumn column)
    {
        try
        {
            switch (column)
            {
                case SettingsColumn.MetadataProvider:
                    db.Database.ExecuteSqlRaw("ALTER TABLE Settings ADD COLUMN MetadataProvider TEXT NOT NULL DEFAULT 'Local catalog'");
                    break;
                case SettingsColumn.SabnzbdEnabled:
                    db.Database.ExecuteSqlRaw("ALTER TABLE Settings ADD COLUMN SabnzbdEnabled INTEGER NOT NULL DEFAULT 0");
                    break;
                case SettingsColumn.SabnzbdServerUrl:
                    db.Database.ExecuteSqlRaw("ALTER TABLE Settings ADD COLUMN SabnzbdServerUrl TEXT NOT NULL DEFAULT 'http://127.0.0.1:8080'");
                    break;
                case SettingsColumn.SabnzbdCategory:
                    db.Database.ExecuteSqlRaw("ALTER TABLE Settings ADD COLUMN SabnzbdCategory TEXT NOT NULL DEFAULT 'music'");
                    break;
                case SettingsColumn.SabnzbdProtectedApiKey:
                    db.Database.ExecuteSqlRaw("ALTER TABLE Settings ADD COLUMN SabnzbdProtectedApiKey TEXT NULL");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(column), column, null);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
    }

    private static async Task<SettingsEntity> GetOrCreateSettings(ChordPortDbContext db)
    {
        var entity = await db.Settings.FindAsync(1);
        if (entity is not null) return entity;
        entity = new SettingsEntity(); db.Settings.Add(entity); await db.SaveChangesAsync(); return entity;
    }

    private static StoredSabnzbdSettings ToStoredSettings(SettingsEntity entity) => new(entity.SabnzbdEnabled, entity.SabnzbdServerUrl, entity.SabnzbdCategory, entity.SabnzbdProtectedApiKey);
    private static SettingsResponse ToSettingsResponse(SettingsEntity entity) => new(entity.LibraryRoot, entity.ImportMode, entity.MetadataProvider, SabnzbdSettingsMapper.ToResponse(ToStoredSettings(entity)));
}
