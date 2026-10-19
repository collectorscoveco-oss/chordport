using System.Net;
using System.Net.Http.Json;
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
    public async Task Settings_can_be_saved_on_a_new_database()
    {
        var response = await _client.PutAsJsonAsync("/api/settings", new { libraryRoot = "Music", importMode = "Manual" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
