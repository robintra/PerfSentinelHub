using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class MetricsEndpointTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"perf-sentinel-hub-metrics-{Guid.NewGuid():N}.db");

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public MetricsEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                HubApplicationFactory.RemoveBackgroundWorkers(services);
                services.PostConfigure<HubOptions>(options =>
                {
                    options.DatabasePath = _databasePath;
                    options.UpdateCheck = new UpdateCheckOptions { Enabled = false };
                    options.Sources =
                    [
                        new SourceOptions
                        {
                            Id = "checkout",
                            Name = "Checkout",
                            Environment = "production",
                            Kind = SourceKinds.Daemon,
                            BaseUrl = new Uri("http://127.0.0.1:1")
                        },
                        new SourceOptions
                        {
                            Id = "tempo-eu",
                            Name = "Tempo EU",
                            Environment = "production",
                            Kind = SourceKinds.Tempo,
                            BaseUrl = new Uri("http://127.0.0.1:3")
                        }
                    ];
                });
            }));
        _client = _factory.CreateClient();
    }

    private async Task<string> ScrapeAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("/metrics", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [Fact]
    public async Task Every_family_is_declared_before_its_samples()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        foreach (var family in new[]
        {
            "perf_sentinel_hub_build_info",
            "perf_sentinel_hub_source_reachable",
            "perf_sentinel_hub_source_unreachable_seconds",
            "perf_sentinel_hub_source_last_success_seconds",
            "perf_sentinel_hub_analysis_queue_depth",
            "perf_sentinel_hub_analysis_runs"
        })
        {
            Assert.Contains($"# HELP {family} ", body, StringComparison.Ordinal);
            Assert.Contains($"# TYPE {family} gauge", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Only_a_daemon_gets_a_reachability_series()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        Assert.Contains("perf_sentinel_hub_source_reachable{source=\"checkout\"} 1", body, StringComparison.Ordinal);
        // A trace backend is never polled, so calling it reachable would assert
        // something the Hub has not observed.
        Assert.DoesNotContain("source=\"tempo-eu\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_sample_can_carry_a_label_that_breaks_the_scrape()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        // One label pair per sample. An unescaped quote would close the value
        // early and turn the rest of the line into a second, malformed label.
        foreach (var line in body.Split('\n')
                     .Where(l => l.StartsWith("perf_sentinel_hub_source_", StringComparison.Ordinal)))
        {
            Assert.Equal(1, line.Count(c => c == '{'));
            Assert.Equal(1, line.Count(c => c == '}'));
        }
    }

    [Theory]
    [InlineData("od\"d")]
    [InlineData("back\\slash")]
    [InlineData("two\\nlines")]
    [InlineData("")]
    public void A_source_id_that_would_break_a_label_never_starts_the_Hub(string id)
    {
        // The exposition is safe because configuration refuses these ids, not
        // because the writer escapes them. MetricsEndpoint escapes anyway, so
        // loosening this rule cannot silently produce a malformed scrape.
        var options = new HubOptions
        {
            DatabasePath = "/tmp/unused.db",
            Sources = [new SourceOptions
            {
                Id = id,
                Name = "n",
                Environment = "e",
                Kind = SourceKinds.Daemon,
                BaseUrl = new Uri("http://127.0.0.1:1")
            }]
        };

        var result = new HubOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public async Task Every_run_status_reports_even_at_zero()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        // A gauge that vanishes at zero reads as a scrape failure rather than as
        // "nothing is in that state".
        foreach (var status in AnalysisStatuses.All)
        {
            Assert.Contains($"perf_sentinel_hub_analysis_runs{{status=\"{status}\"}} 0", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_daemon_never_polled_gets_no_last_success_series()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        // Zero would read as "succeeded just now", the opposite of never.
        Assert.DoesNotContain("perf_sentinel_hub_source_last_success_seconds{", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_daemon_reports_a_duration_and_loses_its_reachability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = _factory.Services.GetRequiredService<HubDatabase>();
        var failedAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        await database.MarkSourceFailureAsync("checkout", failedAt, "network_error", cancellationToken);

        var body = await ScrapeAsync(cancellationToken);
        Assert.Contains("perf_sentinel_hub_source_reachable{source=\"checkout\"} 0", body, StringComparison.Ordinal);

        // Seconds, not milliseconds: five minutes has to read as roughly 300,
        // which is what a duration threshold is written against.
        var seconds = Sample(body, "perf_sentinel_hub_source_unreachable_seconds{source=\"checkout\"}");
        Assert.InRange(seconds, 290, 360);
    }

    [Fact]
    public async Task The_queue_depth_sample_matches_the_pending_run_count()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            Sample(body, "perf_sentinel_hub_analysis_runs{status=\"pending\"}"),
            Sample(body, "perf_sentinel_hub_analysis_queue_depth"));
    }

    private static double Sample(string body, string series)
    {
        var line = body.Split('\n').Single(l => l.StartsWith(series + " ", StringComparison.Ordinal));
        return double.Parse(line[(series.Length + 1)..], CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
