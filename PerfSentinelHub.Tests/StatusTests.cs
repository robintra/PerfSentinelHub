using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

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
        // The padded four-component AssemblyVersion would read 0.1.2.0, matching
        // neither the tag, the chart appVersion, nor the image label.
        Assert.Matches(
            @"^\d+\.\d+\.\d+$", body.RootElement.GetProperty("version").GetString());

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

    [Fact]
    public void The_suite_does_not_reach_github()
    {
        // The update check is the Hub's only outbound destination that is not a
        // configured source. A test host that leaves it on makes the suite
        // non-hermetic and spends the caller's GitHub rate limit.
        var options = factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;
        Assert.False(options.UpdateCheck.Enabled);
    }
}
