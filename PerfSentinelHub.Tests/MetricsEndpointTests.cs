using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class MetricsEndpointTests : IDisposable
{
    private readonly HttpClient _client;

    // Starts at the real hour and only moves when a test advances it, which is
    // what lets one test scrape on both sides of the findings cache.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"perf-sentinel-hub-metrics-{Guid.NewGuid():N}.db");

    private readonly WebApplicationFactory<Program> _factory;

    public MetricsEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                HubApplicationFactory.RemoveBackgroundWorkers(services);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
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
                        },
                        Daemon("payments", "production"),
                        Daemon("staging-a", "staging")
                    ];
                });
            }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
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
                     "perf_sentinel_hub_analysis_runs",
                     "perf_sentinel_hub_findings"
                 })
        {
            Assert.Contains($"# HELP {family} ", body, StringComparison.Ordinal);
            Assert.Contains($"# TYPE {family} gauge", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Only_a_daemon_gets_a_reachability_series()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = _factory.Services.GetRequiredService<HubDatabase>();
        await database.MarkSourceAttemptAsync(
            "checkout", _clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);

        var body = await ScrapeAsync(cancellationToken);
        Assert.Contains("perf_sentinel_hub_source_reachable{source=\"checkout\"} 1", body, StringComparison.Ordinal);
        // A trace backend is never polled, so calling it reachable would assert
        // something the Hub has not observed.
        Assert.DoesNotContain("source=\"tempo-eu\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_daemon_the_Hub_has_never_observed_gets_no_reachability_series()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        // Publishing 1 for a missing source_state row would read as "the last
        // poll succeeded" for a daemon the Hub has never once reached, and
        // retention drops the row of a source it stopped attempting, so a long
        // forgotten source would turn green rather than silent.
        Assert.DoesNotContain("perf_sentinel_hub_source_reachable{", body, StringComparison.Ordinal);
        Assert.DoesNotContain("perf_sentinel_hub_source_unreachable_seconds{", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_sample_can_carry_a_label_that_breaks_the_scrape()
    {
        await PushAsync("checkout", "production", _clock.GetUtcNow(), await FixtureFindingAsync());
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        // One label set per sample. An unescaped quote would close the value
        // early and turn the rest of the line into a second, malformed label.
        foreach (var line in body.Split('\n')
                     .Where(l => l.StartsWith("perf_sentinel_hub_", StringComparison.Ordinal) && l.Contains('{')))
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
            Sources =
            [
                new SourceOptions
                {
                    Id = id,
                    Name = "n",
                    Environment = "e",
                    Kind = SourceKinds.Daemon,
                    BaseUrl = new Uri("http://127.0.0.1:1")
                }
            ]
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
            Assert.Contains($"perf_sentinel_hub_analysis_runs{{status=\"{status}\"}} 0", body,
                StringComparison.Ordinal);
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
        var failedAt = _clock.GetUtcNow().AddMinutes(-5).ToUnixTimeMilliseconds();
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

    [Fact]
    public async Task Every_import_rejection_reason_reports_from_startup()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        Assert.Contains("# TYPE perf_sentinel_hub_import_rejected_total counter", body, StringComparison.Ordinal);
        // A series that only appears on the first failure reads as a scrape gap,
        // and an alert cannot tell the two apart.
        foreach (var reason in new[]
                     { "bad_request", "unauthorized", "gate_full", "write_timeout", "too_large" })
            Assert.Contains(
                $"perf_sentinel_hub_import_rejected_total{{reason=\"{reason}\"}} 0",
                body,
                StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_import_advances_its_own_reason_and_no_other()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await _client.PostAsync(
            "/api/import/findings?source_id=not-a-configured-source",
            new StringContent("{}"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await ScrapeAsync(cancellationToken);
        Assert.Equal(1, Sample(body, "perf_sentinel_hub_import_rejected_total{reason=\"unauthorized\"}"));
        Assert.Equal(0, Sample(body, "perf_sentinel_hub_import_rejected_total{reason=\"bad_request\"}"));
    }

    [Fact]
    public async Task A_daemon_that_has_never_pushed_gets_no_import_age()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        // The exporter sends nothing while it has no findings, so a zero here
        // would claim a push that never happened on a fleet that is merely quiet.
        Assert.Contains("# HELP perf_sentinel_hub_source_last_import_seconds ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("perf_sentinel_hub_source_last_import_seconds{", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_push_gives_its_daemon_an_import_age()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await PushAsync("checkout", "production", _clock.GetUtcNow().AddMinutes(-2), await FixtureFindingAsync());

        var body = await ScrapeAsync(cancellationToken);
        // The push path, not the poll path: only TryUpsertBatchAsync writes here,
        // and a poll must never make a daemon look like it pushed.
        var age = Sample(body, "perf_sentinel_hub_source_last_import_seconds{source=\"checkout\"}");
        Assert.InRange(age, 110, 180);
        Assert.DoesNotContain("perf_sentinel_hub_source_last_import_seconds{source=\"tempo-eu\"}",
            body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Findings_are_counted_per_environment_with_that_environments_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = _clock.GetUtcNow();
        var finding = await FixtureFindingAsync();
        var mystery = finding with { Signature = "mystery:x", FindingType = "mystery" };
        var slow = finding with { Signature = "slow_sql:x", FindingType = "slow_sql", Severity = "warning" };
        var resolved = finding with { Signature = "resolved:x" };
        await PushAsync("checkout", "production", now, finding, mystery, slow);
        // Quiet for longer than the grace on an endpoint its own daemon still
        // heartbeats through the push below: presumably fixed.
        await PushAsync("payments", "production", now.AddDays(-8), resolved);
        // A second daemon of the environment carries it too, and it still counts once.
        await PushAsync("payments", "production", now, finding);
        await PushAsync("staging-a", "staging", now.AddDays(-8), finding);

        var body = await ScrapeAsync(cancellationToken);
        // Neither blocking_wait nor mystery is a type the engine knows, so both
        // fold into one series, which has to carry their sum rather than repeat.
        Assert.Equal(2, Sample(body, FindingsSeries("production", "other", "critical", "active")));
        Assert.Equal(1, Sample(body, FindingsSeries("production", "slow_sql", "warning", "active")));
        Assert.Equal(1, Sample(body, FindingsSeries("production", "other", "critical", "likely_resolved")));
        // Production still sees it today, which says nothing about staging's quiet copy.
        Assert.Equal(1, Sample(body, FindingsSeries("staging", "other", "critical", "not_observed")));

        foreach (var environment in new[] { "production", "staging" })
        {
            foreach (var status in new[] { "active", "likely_resolved", "not_observed" })
            {
                using var response = await _client.GetAsync(
                    $"/api/findings?environment={environment}&status={status}", cancellationToken);
                using var document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken));
                Assert.Equal(document.RootElement.GetArrayLength(), Findings(body, environment, status));
            }
        }
    }

    [Fact]
    public async Task An_empty_combination_publishes_no_series()
    {
        var body = await ScrapeAsync(TestContext.Current.CancellationToken);
        // Unlike Every_run_status_reports_even_at_zero: the run statuses are six
        // series, the zeros of this family would be every environment times
        // every type, severity and status, nearly all of them forever empty.
        Assert.Contains("# TYPE perf_sentinel_hub_findings gauge", body, StringComparison.Ordinal);
        Assert.DoesNotContain("perf_sentinel_hub_findings{", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_environment_that_needs_escaping_still_scrapes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Configuration refuses a control character in an environment and
        // nothing else, so a quote and a backslash do reach the label.
        const string environment = @"pro""d\e";
        await using var quoted = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Sources = [.. options.Sources, Daemon("odd", environment)])));
        using var client = quoted.CreateClient();
        await PushAsync("odd", environment, _clock.GetUtcNow(), await FixtureFindingAsync());

        var body = await client.GetStringAsync("/metrics", cancellationToken);
        Assert.Equal(1, Sample(body, FindingsSeries(@"pro\""d\\e", "other", "critical", "active")));
    }

    [Fact]
    public async Task The_findings_series_are_cached_for_a_scrape_interval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await ScrapeAsync(cancellationToken);
        await PushAsync("checkout", "production", _clock.GetUtcNow(), await FixtureFindingAsync());

        // The route is anonymous and a count reads its whole scope, so a second
        // scrape inside the interval must not count again. Every other family
        // is still read at scrape time.
        _clock.Advance(TimeSpan.FromSeconds(14));
        var cached = await ScrapeAsync(cancellationToken);
        Assert.DoesNotContain("perf_sentinel_hub_findings{", cached, StringComparison.Ordinal);
        Assert.Contains("perf_sentinel_hub_source_last_import_seconds{source=\"checkout\"}", cached,
            StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromSeconds(1));
        var fresh = await ScrapeAsync(cancellationToken);
        Assert.Equal(1, Sample(fresh, FindingsSeries("production", "other", "critical", "active")));
    }

    [Theory]
    [InlineData("slow_sql", "slow_sql")]
    [InlineData("serialized_calls", "serialized_calls")]
    [InlineData("blocking_wait", "other")]
    [InlineData("SLOW_SQL", "other")]
    [InlineData("", "other")]
    public void Fold_keeps_the_vocabulary_and_buckets_the_rest(string value, string expected)
    {
        Assert.Equal(expected, FindingLabels.Fold(FindingLabels.Types, value));
    }

    [Fact]
    public void The_severity_labels_are_the_severities_storage_ranks()
    {
        // One vocabulary: storage ranks a severity by its place in the list the
        // gauge folds through, so neither can name one the other ignores. The
        // order is pinned because the rank it yields is stored.
        Assert.Equal(12, FindingLabels.Types.Length);
        Assert.Equal(["critical", "warning", "info"], FindingLabels.Severities);
        Assert.Equal([3, 2, 1], FindingLabels.Severities.Select(FindingParser.SeverityRank));
        Assert.Equal(0, FindingParser.SeverityRank(FindingLabels.Other));
    }

    private static SourceOptions Daemon(string id, string environment)
    {
        return new SourceOptions
        {
            Id = id,
            Name = id,
            Environment = environment,
            Kind = SourceKinds.Daemon,
            BaseUrl = new Uri("http://127.0.0.1:1")
        };
    }

    private static async Task<ParsedFinding> FixtureFindingAsync()
    {
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json"),
            TestContext.Current.CancellationToken));
        return batch.Findings[0];
    }

    // The push path: only TryUpsertBatchAsync records an import.
    private async Task PushAsync(
        string sourceId,
        string environment,
        DateTimeOffset at,
        params ParsedFinding[] findings)
    {
        var database = _factory.Services.GetRequiredService<HubDatabase>();
        Assert.True(await database.TryUpsertBatchAsync(
            new SourceSnapshot(sourceId, sourceId, environment, "0.11.2"),
            new ParsedBatch(findings, 0),
            at.ToUnixTimeMilliseconds(),
            TestContext.Current.CancellationToken));
    }

    private static string FindingsSeries(string environment, string findingType, string severity, string status)
    {
        return $"perf_sentinel_hub_findings{{environment=\"{environment}\",finding_type=\"{findingType}\","
               + $"severity=\"{severity}\",status=\"{status}\"}}";
    }

    // Every series of one environment and status, summed as a dashboard sums them.
    private static double Findings(string body, string environment, string status)
    {
        return body.Split('\n')
            .Where(line => line.StartsWith(
                               $"perf_sentinel_hub_findings{{environment=\"{environment}\",",
                               StringComparison.Ordinal) &&
                           line.Contains($",status=\"{status}\"}} ", StringComparison.Ordinal))
            .Sum(line => double.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture));
    }

    private static double Sample(string body, string series)
    {
        var line = body.Split('\n').Single(l => l.StartsWith(series + " ", StringComparison.Ordinal));
        return double.Parse(line[(series.Length + 1)..], CultureInfo.InvariantCulture);
    }
}
