using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using ChordPort.Api.Domain;
using ChordPort.Api.Services;

namespace ChordPort.Api.Tests;

public sealed class IndexerTests
{
    [Theory]
    [InlineData("https://news.example.test", true)]
    [InlineData("https://news.example.test/", true)]
    [InlineData("http://127.0.0.1:9117", true)]
    [InlineData("http://192.168.1.20", true)]
    [InlineData("http://news.example.test", false)]
    [InlineData("ftp://news.example.test", false)]
    [InlineData("https://user@news.example.test", false)]
    [InlineData("https://news.example.test/path", false)]
    [InlineData("https://news.example.test?x=1", false)]
    [InlineData("https://news.example.test#fragment", false)]
    public void Endpoint_policy_allows_https_remote_or_private_http_roots_only(string raw, bool allowed)
    {
        Assert.Equal(allowed, IndexerEndpointValidator.Validate(raw).IsValid);
    }

    [Theory]
    [InlineData("https://8.8.8.8", true)]
    [InlineData("https://[::ffff:127.0.0.1]", false)]
    [InlineData("https://[::]", false)]
    [InlineData("https://[fe80::1]", false)]
    [InlineData("https://169.254.169.254", false)]
    [InlineData("https://10.0.0.1", false)]
    [InlineData("https://100.64.0.1", false)]
    [InlineData("https://192.0.2.1", false)]
    [InlineData("https://198.18.0.1", false)]
    [InlineData("https://224.0.0.1", false)]
    [InlineData("https://[fc00::1]", false)]
    [InlineData("https://[fec0::1]", false)]
    [InlineData("https://[ff02::1]", false)]
    [InlineData("https://[2001:db8::1]", false)]
    public void Endpoint_policy_rejects_non_public_https_ip_literals(string raw, bool allowed)
    {
        Assert.Equal(allowed, IndexerEndpointValidator.Validate(raw).IsValid);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Connect_address_policy_allows_only_public_https_addresses(string raw, bool allowed)
    {
        Assert.Equal(allowed, IndexerAddressPolicy.IsPublicHttpsAddress(IPAddress.Parse(raw)));
    }

    [Fact]
    public void Indexer_settings_preserve_blank_key_clear_explicit_key_and_redact_response()
    {
        var existing = new StoredIndexer("id", "Example", "Newznab", "https://news.example.test", true, 1, "3000,3010", "protected-key", null, null, null, null);
        var preserved = IndexerSettingsMerger.Merge(existing, new IndexerInput("Example", "Newznab", "https://news.example.test", true, 1, "3000,3010", null, false));
        var cleared = IndexerSettingsMerger.Merge(existing, new IndexerInput("Example", "Newznab", "https://news.example.test", true, 1, "3000,3010", null, true));
        var response = IndexerMapper.ToResponse(preserved);

        Assert.Equal("protected-key", preserved.ProtectedApiKey);
        Assert.Null(cleared.ProtectedApiKey);
        Assert.True(response.HasApiKey);
        Assert.DoesNotContain("protected-key", System.Text.Json.JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task Capability_test_uses_fixed_caps_request_parses_music_categories_and_never_returns_key()
    {
        const string key = "indexer-test-sentinel";
        var handler = new RecordingHandler(_ => Xml("<caps><server type=\"newznab\"/><categories><category id=\"3000\" name=\"Music\"><subcat id=\"3010\" name=\"MP3\"/></category></categories></caps>"));
        var result = await new IndexerClient(new HttpClient(handler)).TestCapabilitiesAsync(new IndexerConnection("Newznab", "https://news.example.test", key), CancellationToken.None);

        Assert.True(result.Connected);
        Assert.True(result.ProtocolConfirmed);
        Assert.Contains("3000 Music", result.CapabilitySummary);
        Assert.Contains("3010 MP3", result.CapabilitySummary);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api", request.RequestUri!.AbsolutePath);
        Assert.Equal($"t=caps&apikey={key}", request.RequestUri.Query.TrimStart('?'));
        Assert.DoesNotContain(key, result.Message);
        Assert.DoesNotContain(key, result.CapabilitySummary);
    }

    [Fact]
    public async Task Capability_test_returns_safe_failure_for_bad_or_unreachable_xml()
    {
        var broken = new IndexerClient(new HttpClient(new RecordingHandler(_ => Xml("<caps>"))));
        var result = await broken.TestCapabilitiesAsync(new IndexerConnection("Torznab", "https://news.example.test", "secret"), CancellationToken.None);
        Assert.False(result.Connected);
        Assert.Equal("Indexer returned an unexpected capability response.", result.Message);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Theory]
    [InlineData("Newznab", "newznab", true)]
    [InlineData("Torznab", "TORZNAB", true)]
    [InlineData("Newznab", "torznab", false)]
    [InlineData("Newznab", "not-newznab", false)]
    [InlineData("Newznab", "", false)]
    public async Task Capability_test_confirms_only_the_configured_protocol_type(string configuredType, string serverType, bool confirmed)
    {
        var handler = new RecordingHandler(_ => Xml($"<caps><server type=\"{serverType}\"/></caps>"));
        var result = await new IndexerClient(new HttpClient(handler)).TestCapabilitiesAsync(new IndexerConnection(configuredType, "https://news.example.test", "secret"), CancellationToken.None);

        Assert.Equal(confirmed, result.ProtocolConfirmed);
        Assert.Equal(confirmed, result.Connected);
        Assert.Equal(confirmed ? "Indexer capability test succeeded." : "Indexer returned an unexpected capability response.", result.Message);
    }

    [Fact]
    public async Task Capability_test_rejects_a_non_caps_root_document()
    {
        var result = await new IndexerClient(new HttpClient(new RecordingHandler(_ => Xml("<response><server type=\"newznab\"/></response>")))).TestCapabilitiesAsync(new IndexerConnection("Newznab", "https://news.example.test", "secret"), CancellationToken.None);

        Assert.False(result.ProtocolConfirmed);
        Assert.Equal("Indexer returned an unexpected capability response.", result.Message);
    }

    [Fact]
    public async Task Indexer_api_supports_create_list_update_and_delete_without_returning_api_key()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/indexers", new { name = "Primary", type = "Newznab", serverUrl = "https://news.example.test", enabled = true, priority = 10, categories = "3000,3010", apiKey = "api-redaction-sentinel", clearApiKey = false });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadAsStringAsync();
        Assert.Contains("\"hasApiKey\":true", body);
        Assert.DoesNotContain("api-redaction-sentinel", body);
        var listed = await client.GetStringAsync("/api/indexers");
        Assert.Contains("Primary", listed);
        Assert.DoesNotContain("api-redaction-sentinel", listed);
        var id = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("id").GetString();
        var updated = await client.PutAsJsonAsync($"/api/indexers/{id}", new { name = "Primary", type = "Newznab", serverUrl = "https://news.example.test", enabled = false, priority = 2, categories = "3000", apiKey = "", clearApiKey = false });
        updated.EnsureSuccessStatusCode();
        Assert.Contains("\"hasApiKey\":true", await updated.Content.ReadAsStringAsync());
        var deleted = await client.DeleteAsync($"/api/indexers/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("[]", await client.GetStringAsync("/api/indexers"));
    }

    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(responder(request)); }
    }
}
