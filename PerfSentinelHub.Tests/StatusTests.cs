using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

public sealed class StatusTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public StatusTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<HubOptions>(options =>
            {
                options.DatabasePath = Path.Combine(Path.GetTempPath(), $"hub-{Guid.NewGuid():N}.db");
                options.Sources = [new SourceOptions
                {
                    Id = "test",
                    Name = "Test",
                    Environment = "test",
                    BaseUrl = new Uri("http://127.0.0.1:4318")
                }];
            })))
            .CreateClient();

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
}
