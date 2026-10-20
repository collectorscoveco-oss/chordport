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
builder.Services.AddHttpClient<SabnzbdClient>(client => client.Timeout = TimeSpan.FromSeconds(5)).ConfigurePrimaryHttpMessageHandler(SabnzbdHttpHandlerFactory.Create).RemoveAllLoggers();
builder.Services.AddSingleton<IndexerSecretProtector>();
builder.Services.AddHttpClient<IndexerClient>(client => { client.Timeout = TimeSpan.FromSeconds(5); client.DefaultRequestHeaders.UserAgent.ParseAdd("ChordPort/0.1.6 (local indexer capability test)"); }).ConfigurePrimaryHttpMessageHandler(IndexerHttpHandlerFactory.Create).RemoveAllLoggers();
builder.Services.AddHttpClient<IndexerSearchClient>(client => { client.Timeout = TimeSpan.FromSeconds(10); client.DefaultRequestHeaders.UserAgent.ParseAdd("ChordPort/0.1.6 (manual authorized music search)"); }).ConfigurePrimaryHttpMessageHandler(IndexerHttpHandlerFactory.Create).RemoveAllLoggers();
builder.Services.AddSingleton<CandidateStore>();
builder.Services.AddSingleton(new UpdateCheckOptions { CurrentVersion = "0.1.6" });
builder.Services.AddHttpClient<UpdateCheckClient>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChordPort/0.1.6 (local update check)");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<ShutdownService>();
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
    db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Indexers (Id TEXT NOT NULL CONSTRAINT PK_Indexers PRIMARY KEY, Name TEXT NOT NULL, Type TEXT NOT NULL, ServerUrl TEXT NOT NULL, Enabled INTEGER NOT NULL, Priority INTEGER NOT NULL, Categories TEXT NULL, ProtectedApiKey TEXT NULL, LastTestedAt TEXT NULL, LastTestStatus TEXT NULL, LastTestMessage TEXT NULL, CapabilitySummary TEXT NULL)");
    db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Activities (Id TEXT NOT NULL CONSTRAINT PK_Activities PRIMARY KEY, Title TEXT NOT NULL, Indexer TEXT NOT NULL, SabJobId TEXT NULL, Status TEXT NOT NULL, Message TEXT NOT NULL, SubmittedAt TEXT NOT NULL, UpdatedAt TEXT NULL)");
}

app.MapGet("/api/health", (HealthService health) => Results.Ok(health.Get()));
app.MapPost("/api/system/shutdown", (HttpContext context, ShutdownService shutdown) =>
{
    if (!LoopbackRequestPolicy.IsLoopback(context.Connection.RemoteIpAddress)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    context.Response.OnCompleted(() => { shutdown.RequestStop(); return Task.CompletedTask; });
    return Results.Ok(new ShutdownResponse("ChordPort is stopping."));
});
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
app.MapGet("/api/indexers", async (ChordPortDbContext db) => Results.Ok((await db.Indexers.OrderBy(item => item.Priority).ThenBy(item => item.Name).ToListAsync()).Select(ToIndexerResponse)));
app.MapPost("/api/indexers", async (IndexerInput input, ChordPortDbContext db, IndexerSecretProtector secrets) =>
{
    var error = ValidateIndexer(input); if (error is not null) return Results.BadRequest(new { error });
    var entity = new IndexerEntity { Id = Guid.NewGuid().ToString("N") };
    ApplyIndexer(entity, input, string.IsNullOrWhiteSpace(input.ApiKey) ? null : secrets.Protect(input.ApiKey));
    db.Indexers.Add(entity); await db.SaveChangesAsync(); return Results.Created($"/api/indexers/{entity.Id}", ToIndexerResponse(entity));
});
app.MapPut("/api/indexers/{id}", async (string id, IndexerInput input, ChordPortDbContext db, IndexerSecretProtector secrets) =>
{
    var entity = await db.Indexers.FindAsync(id); if (entity is null) return Results.NotFound(new { error = "Indexer was not found." });
    var error = ValidateIndexer(input); if (error is not null) return Results.BadRequest(new { error });
    ApplyIndexer(entity, input, string.IsNullOrWhiteSpace(input.ApiKey) ? null : secrets.Protect(input.ApiKey));
    await db.SaveChangesAsync(); return Results.Ok(ToIndexerResponse(entity));
});
app.MapDelete("/api/indexers/{id}", async (string id, ChordPortDbContext db) => { var entity = await db.Indexers.FindAsync(id); if (entity is null) return Results.NotFound(new { error = "Indexer was not found." }); db.Indexers.Remove(entity); await db.SaveChangesAsync(); return Results.NoContent(); });
app.MapPost("/api/indexers/{id}/test", async (string id, ChordPortDbContext db, IndexerSecretProtector secrets, IndexerClient indexers, CancellationToken cancellationToken) =>
{
    var entity = await db.Indexers.FindAsync(id); if (entity is null) return Results.NotFound(new { error = "Indexer was not found." });
    if (string.IsNullOrWhiteSpace(entity.ProtectedApiKey)) return Results.BadRequest(new { error = "Save an API key before testing this indexer." });
    try
    {
        var result = await indexers.TestCapabilitiesAsync(new IndexerConnection(entity.Type, entity.ServerUrl, secrets.Unprotect(entity.ProtectedApiKey)), cancellationToken);
        entity.LastTestedAt = DateTimeOffset.UtcNow; entity.LastTestStatus = result.Connected ? "connected" : "error"; entity.LastTestMessage = result.Message; entity.CapabilitySummary = result.CapabilitySummary; await db.SaveChangesAsync(); return Results.Ok(result);
    }
    catch (CryptographicException) { return Results.BadRequest(new { error = "The saved indexer key is unavailable. Save it again before testing." }); }
});
app.MapPost("/api/manual-search", async (string? q, ChordPortDbContext db, IndexerSecretProtector secrets, IndexerSearchClient indexers, CandidateStore candidates, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Trim().Length > 200) return Results.BadRequest(new { error = "A search query of 1 to 200 characters is required." });
    var output = new List<ManualSearchResult>();
    foreach (var entity in await db.Indexers.Where(x => x.Enabled && x.ProtectedApiKey != null).OrderBy(x => x.Priority).ThenBy(x => x.Name).ToListAsync())
    {
        try { var hits = await indexers.SearchAsync(new IndexerConnection(entity.Type, entity.ServerUrl, secrets.Unprotect(entity.ProtectedApiKey!)), string.IsNullOrWhiteSpace(entity.Categories) ? "3000" : entity.Categories!, q, cancellationToken); output.AddRange(hits.Select(hit => { var c = candidates.Issue(entity.Id, entity.Name, hit); return new ManualSearchResult(c.Id, c.Title, c.SizeBytes, c.PublishedAt, c.Seeders, c.IndexerName); })); }
        catch (CryptographicException) { }
    }
    return Results.Ok(new ManualSearchResponse(output.Take(100).ToArray(), output.Count == 0 ? "No results found from configured enabled indexers." : null));
});
app.MapPost("/api/manual-submit", async (SubmitCandidateRequest request, ChordPortDbContext db, CandidateStore candidates, IndexerSecretProtector indexerSecrets, SabnzbdSecretProtector sabSecrets, IndexerSearchClient indexers, SabnzbdClient sab, CancellationToken cancellationToken) =>
{
    if (!candidates.TryConsume(request.CandidateId, out var candidate)) return Results.BadRequest(new { error = "This result is unavailable, expired, or was already submitted. Search again." });
    var indexer = await db.Indexers.FindAsync(candidate.IndexerId); var settings = await GetOrCreateSettings(db);
    if (indexer is null || !indexer.Enabled || string.IsNullOrWhiteSpace(indexer.ProtectedApiKey) || !settings.SabnzbdEnabled || string.IsNullOrWhiteSpace(settings.SabnzbdProtectedApiKey)) return Results.BadRequest(new { error = "The selected indexer or SABnzbd configuration is no longer available." });
    try
    {
        var nzb = await indexers.GrabNzbAsync(new IndexerConnection(indexer.Type, indexer.ServerUrl, indexerSecrets.Unprotect(indexer.ProtectedApiKey)), candidate.ResultId, cancellationToken);
        if (nzb is null) return Results.BadRequest(new { error = "The indexer did not return a valid NZB file." });
        var submitted = await sab.AddFileAsync(new SabnzbdSettings(settings.SabnzbdEnabled, settings.SabnzbdServerUrl, settings.SabnzbdCategory, sabSecrets.Unprotect(settings.SabnzbdProtectedApiKey)), nzb, candidate.Title, cancellationToken);
        if (!submitted.Submitted) return Results.BadRequest(new { error = submitted.Message });
        var activity = new ActivityEntity { Id = Guid.NewGuid().ToString("N"), Title = candidate.Title, Indexer = candidate.IndexerName, SabJobId = submitted.JobId, Status = "queued", Message = "Submitted to SABnzbd. Completed downloads are not imported by this release.", SubmittedAt = DateTimeOffset.UtcNow }; db.Activities.Add(activity); await db.SaveChangesAsync(); return Results.Ok(ToActivity(activity));
    }
    catch (CryptographicException) { return Results.BadRequest(new { error = "Saved credentials are unavailable. Save them again before submitting." }); }
});
app.MapGet("/api/activity", async (ChordPortDbContext db) => Results.Ok((await db.Activities.OrderByDescending(x => x.SubmittedAt).ToListAsync()).Select(ToActivity)));
app.MapPost("/api/activity/refresh", async (ChordPortDbContext db, SabnzbdSecretProtector secrets, SabnzbdClient sab, CancellationToken cancellationToken) =>
{
    var settings = await GetOrCreateSettings(db); var activities = await db.Activities.Where(x => x.SabJobId != null).ToListAsync(); if (!settings.SabnzbdEnabled || string.IsNullOrWhiteSpace(settings.SabnzbdProtectedApiKey)) return Results.BadRequest(new { error = "Enable SABnzbd and save an API key before refreshing activity." });
    try { var statuses = await sab.GetKnownJobStatusesAsync(new SabnzbdSettings(settings.SabnzbdEnabled, settings.SabnzbdServerUrl, settings.SabnzbdCategory, secrets.Unprotect(settings.SabnzbdProtectedApiKey)), activities.Select(x => x.SabJobId!).ToArray(), cancellationToken); foreach (var activity in activities.Where(x => statuses.TryGetValue(x.SabJobId!, out _))) { activity.Status = statuses[activity.SabJobId!]; activity.Message = activity.Status == "completed" ? "Completed in SABnzbd. Manual import is next." : $"SABnzbd status: {activity.Status}."; activity.UpdatedAt = DateTimeOffset.UtcNow; } await db.SaveChangesAsync(); return Results.Ok(activities.OrderByDescending(x => x.SubmittedAt).Select(ToActivity)); }
    catch (CryptographicException) { return Results.BadRequest(new { error = "The saved SABnzbd key is unavailable. Save it again before refreshing." }); }
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

    private static string? ValidateIndexer(IndexerInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 120) return "Indexer name is required and must be 120 characters or fewer.";
        if (!IndexerClient.IsSupportedType(input.Type)) return "Indexer type must be Newznab or Torznab.";
        if (input.Priority is < 0 or > 9999) return "Indexer priority must be between 0 and 9999.";
        return IndexerEndpointValidator.Validate(input.ServerUrl).IsValid ? null : IndexerEndpointValidator.Validate(input.ServerUrl).Error;
    }
    private static void ApplyIndexer(IndexerEntity entity, IndexerInput input, string? protectedApiKey)
    {
        var merged = IndexerSettingsMerger.Merge(ToStoredIndexer(entity), input, protectedApiKey);
        entity.Name = merged.Name; entity.Type = merged.Type; entity.ServerUrl = merged.ServerUrl; entity.Enabled = merged.Enabled; entity.Priority = merged.Priority; entity.Categories = merged.Categories; entity.ProtectedApiKey = merged.ProtectedApiKey;
    }
    private static StoredIndexer ToStoredIndexer(IndexerEntity entity) => new(entity.Id, entity.Name, entity.Type, entity.ServerUrl, entity.Enabled, entity.Priority, entity.Categories, entity.ProtectedApiKey, entity.LastTestedAt, entity.LastTestStatus, entity.LastTestMessage, entity.CapabilitySummary);
    private static IndexerResponse ToIndexerResponse(IndexerEntity entity) => IndexerMapper.ToResponse(ToStoredIndexer(entity));
    private static StoredSabnzbdSettings ToStoredSettings(SettingsEntity entity) => new(entity.SabnzbdEnabled, entity.SabnzbdServerUrl, entity.SabnzbdCategory, entity.SabnzbdProtectedApiKey);
    private static SettingsResponse ToSettingsResponse(SettingsEntity entity) => new(entity.LibraryRoot, entity.ImportMode, entity.MetadataProvider, SabnzbdSettingsMapper.ToResponse(ToStoredSettings(entity)));
    private static ActivityResponse ToActivity(ActivityEntity entity) => new(entity.Id, entity.Title, entity.Indexer, entity.SabJobId, entity.Status, entity.Message, entity.SubmittedAt, entity.UpdatedAt);
}
