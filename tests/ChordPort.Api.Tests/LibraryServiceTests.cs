using ChordPort.Api.Domain;
using ChordPort.Api.Services;

namespace ChordPort.Api.Tests;

public sealed class LibraryServiceTests
{
    [Fact]
    public void SearchSongs_filters_catalog_case_insensitively()
    {
        var service = new CatalogService();
        var results = service.Search(SearchMode.Songs, "midnight");
        Assert.Single(results);
        Assert.Equal("Midnight Signals", results[0].Title);
    }

    [Fact]
    public void SearchArtists_returns_matching_artist_catalog_items()
    {
        var service = new CatalogService();
        var results = service.Search(SearchMode.Artists, "North");
        Assert.Equal(2, results.Count);
        Assert.All(results, item => Assert.Equal("The Northline", item.Artist));
    }

    [Fact]
    public void Queue_add_then_remove_changes_request_state()
    {
        var queue = new QueueService();
        var request = queue.Add("song-1");
        Assert.Single(queue.GetAll());
        Assert.True(queue.Remove(request.Id));
        Assert.Empty(queue.GetAll());
    }

    [Fact]
    public void Settings_rejects_blank_library_root()
    {
        var settings = new SettingsService();
        var result = settings.Update(new Settings("   ", "Manual"));
        Assert.False(result.IsValid);
        Assert.Equal("Library root is required.", result.Error);
    }

    [Fact]
    public void Health_reports_healthy_local_service()
    {
        var health = new HealthService();
        Assert.Equal("healthy", health.Get().Status);
    }
}
