using ChordPort.Api.Data;
using ChordPort.Api.Domain;
using ChordPort.Api.Services;
using Microsoft.EntityFrameworkCore;

var applicationBaseDirectory = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = applicationBaseDirectory,
    WebRootPath = Path.Combine(applicationBaseDirectory, "wwwroot")
});
builder.Services.AddDbContext<ChordPortDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("ChordPort") ?? "Data Source=chordport.db"));
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
builder.Services.AddSingleton<HealthService>();
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChordPortDbContext>();
    db.Database.EnsureCreated();
    try { db.Database.ExecuteSqlRaw("ALTER TABLE Settings ADD COLUMN MetadataProvider TEXT NOT NULL DEFAULT 'Local catalog'"); }
    catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
        // Existing V0.1 databases already have the column.
    }
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
    var value = await db.Settings.FindAsync(1);
    if (value is null) { value = new SettingsEntity(); db.Settings.Add(value); await db.SaveChangesAsync(); }
    return Results.Ok(new Settings(value.LibraryRoot, value.ImportMode, value.MetadataProvider));
});
app.MapPut("/api/settings", async (Settings settings, SettingsService validator, ChordPortDbContext db) =>
{
    var validation = validator.Update(settings);
    if (!validation.IsValid) return Results.BadRequest(new { error = validation.Error });
    var entity = await db.Settings.FindAsync(1);
    if (entity is null) { entity = new SettingsEntity(); db.Settings.Add(entity); }
    entity.LibraryRoot = validator.Get().LibraryRoot; entity.ImportMode = validator.Get().ImportMode; entity.MetadataProvider = validator.Get().MetadataProvider;
    await db.SaveChangesAsync(); return Results.Ok(new Settings(entity.LibraryRoot, entity.ImportMode, entity.MetadataProvider));
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();
public partial class Program { }
