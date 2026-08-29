using System.Net;
using System.Text.Json;

namespace PerfSentinelHub.Tests;

public sealed class StatusTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Status_is_stable_and_health_endpoints_are_distinct()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var status = await _client.GetAsync("/api/status", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        using var body = await JsonDocument.ParseAsync(
            await status.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.Equal("perf-sentinel-hub", body.RootElement.GetProperty("service").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("version").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live", cancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready", cancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Static_assets_are_revalidated_rather_than_cached_by_age()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var asset = await _client.GetAsync("/app.js", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        // No name here carries a fingerprint, so the browser must ask every time
        // rather than pick its own freshness from the last-modified date.
        Assert.True(asset.Headers.CacheControl?.NoCache);
        Assert.NotNull(asset.Headers.ETag);
    }
}
