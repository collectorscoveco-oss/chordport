using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChordPort.Api.Tests;

public sealed class ApiWorkflowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public ApiWorkflowTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Queue_list_returns_successfully_after_adding_a_request()
    {
        var created = await _client.PostAsJsonAsync("/api/queue", new { songId = "song-1" });
        created.EnsureSuccessStatusCode();
        var listed = await _client.GetAsync("/api/queue");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    }

    [Fact]
    public async Task Search_rejects_an_unknown_metadata_provider_with_client_error()
    {
        var response = await _client.GetAsync("/api/search?mode=Songs&q=midnight&provider=Unknown");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("provider must be Local catalog or MusicBrainz.", body);
    }

    [Fact]
    public async Task MusicBrainz_search_with_malformed_base_url_returns_safe_provider_error()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("MusicBrainz:BaseUrl", "not a valid URI"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/search?mode=Songs&q=midnight&provider=MusicBrainz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"error\"", body);
        Assert.Contains("MusicBrainz could not be reached. Try again later.", body);
        Assert.DoesNotContain("UriFormatException", body);
        Assert.DoesNotContain("MusicBrainzClient", body);
    }

    [Fact]
    public async Task Settings_can_be_saved_on_a_new_database()
    {
        var response = await _client.PutAsJsonAsync("/api/settings", new { libraryRoot = "Music", importMode = "Manual" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
