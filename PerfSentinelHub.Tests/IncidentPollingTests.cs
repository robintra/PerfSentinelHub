using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class IncidentPollingTests : IDisposable
{
    // The id of the one incident the 0.20.0 capture holds.
    private const string FixtureId = "d650edad80ac5c2d99b8d1dde07100c2";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-incidents-{Guid.NewGuid():N}.db");

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-incidents-0.20.0.json");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    [Fact]
    public async Task Incidents_are_paged_until_a_short_page_and_stored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var template = await TemplateAsync(cancellationToken);
        var offsets = new List<string>();
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
            {
                var offset = context.Request.Query["offset"].ToString();
                offsets.Add(offset);
                // Two full pages, then a short one: the loop must stop on the
                // short page and never ask for a fourth.
                var count = offset == "200" ? 5 : DaemonClient.IncidentsPageSize;
                await context.Response.WriteAsync(Page(template, int.Parse(offset), count), cancellationToken);
            }
            else
            {
                await Baseline(context, cancellationToken);
            }
        }, cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(205, result.IncidentCount);
        Assert.Equal(["0", "100", "200"], offsets);
        Assert.Equal(205, (await database.ListIncidentsAsync(new IncidentQuery(null, null, 0, 1000), cancellationToken)).Count);
        var read = Assert.Contains("prod", await database.QueryIncidentReadsAsync(cancellationToken));
        Assert.Equal(IncidentReadStates.Ok, read.State);
        Assert.Null(read.LastErrorCode);
    }

    [Fact]
    public async Task Paging_stops_at_the_cap_against_a_daemon_that_never_runs_short()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var template = await TemplateAsync(cancellationToken);
        var offsets = new List<string>();
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
            {
                var offset = context.Request.Query["offset"].ToString();
                offsets.Add(offset);
                // A full page at every offset: only the cap ends the loop.
                var limit = int.Parse(context.Request.Query["limit"].ToString());
                await context.Response.WriteAsync(Page(template, int.Parse(offset), limit), cancellationToken);
            }
            else
            {
                await Baseline(context, cancellationToken);
            }
        }, cancellationToken);
        var logger = new ListLogger<SourcePoller>();
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken, logger);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(SourcePoller.IncidentsCap, result.IncidentCount);
        Assert.Equal(
            Enumerable.Range(0, SourcePoller.IncidentsCap / DaemonClient.IncidentsPageSize)
                .Select(page => (page * DaemonClient.IncidentsPageSize).ToString()),
            offsets);
        Assert.Contains(logger.Messages, message => message.Contains("stopped paging", StringComparison.Ordinal));
        Assert.Equal(
            SourcePoller.IncidentsCap,
            (await database.ListIncidentsAsync(new IncidentQuery(null, null, 0, 2000), cancellationToken)).Count);
    }

    [Fact]
    public async Task A_page_over_the_body_cap_is_re_read_at_half_the_size()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var template = await TemplateAsync(cancellationToken);
        const int held = 70;
        var requests = new List<string>();
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
            {
                var limit = int.Parse(context.Request.Query["limit"].ToString());
                var offset = int.Parse(context.Request.Query["offset"].ToString());
                requests.Add($"{limit}@{offset}");
                if (limit > 50)
                {
                    // A full page of thousand-finding incidents does not fit.
                    await WriteOversizedAsync(context, cancellationToken);
                    return;
                }

                await context.Response.WriteAsync(Page(template, offset, Math.Min(limit, held - offset)), cancellationToken);
            }
            else
            {
                await Baseline(context, cancellationToken);
            }
        }, cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(held, result.IncidentCount);
        Assert.Equal(["100@0", "50@0", "50@50"], requests);
        Assert.Equal(held, (await database.ListIncidentsAsync(new IncidentQuery(null, null, 0, 1000), cancellationToken)).Count);
        await AssertReachableAsync(database, IncidentReadStates.Ok, null, cancellationToken);
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public async Task An_absent_incidents_surface_is_not_a_failure(int statusCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
                context.Response.StatusCode = statusCode;
            else
                await Baseline(context, cancellationToken);
        }, cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Null(result.IncidentCount);
        await AssertReachableAsync(database, IncidentReadStates.Absent, null, cancellationToken);
    }

    [Fact]
    public async Task A_refused_key_is_filed_without_touching_reachability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("{\"error\":\"missing or invalid X-API-Key\"}", cancellationToken);
            }
            else
            {
                await Baseline(context, cancellationToken);
            }
        }, cancellationToken);
        var logger = new ListLogger<SourcePoller>();
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken, logger);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Null(result.IncidentCount);
        Assert.Equal(1, result.ImportedCount);
        await AssertReachableAsync(database, IncidentReadStates.Unauthorized, null, cancellationToken);
        Assert.Contains(logger.Messages, message => message.Contains("refused", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("must-not-leak", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_oversized_page_is_filed_as_an_error_and_the_poll_still_succeeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var limits = new List<string>();
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
            {
                limits.Add(context.Request.Query["limit"].ToString());
                await WriteOversizedAsync(context, cancellationToken);
            }
            else
            {
                await Baseline(context, cancellationToken);
            }
        }, cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Null(result.IncidentCount);
        // Halved down to a single incident, which is the daemon's to fix.
        Assert.Equal(["100", "50", "25", "12", "6", "3", "1"], limits);
        await AssertReachableAsync(database, IncidentReadStates.Error, "response_too_large", cancellationToken);
    }

    [Fact]
    public async Task A_malformed_page_is_filed_under_its_own_error_code()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
                await context.Response.WriteAsync("{}", cancellationToken);
            else
                await Baseline(context, cancellationToken);
        }, cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Null(result.IncidentCount);
        Assert.Equal(1, result.ImportedCount);
        await AssertReachableAsync(database, IncidentReadStates.Error, "invalid_incidents", cancellationToken);
    }

    [Fact]
    public async Task Two_daemons_fed_the_same_alert_keep_a_capture_each()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var template = await TemplateAsync(cancellationToken);
        // The same id, since it hashes nothing of the daemon, against a ring
        // that held one of the two findings.
        var poorer = JsonNode.Parse(template)!;
        poorer["findings"]!.AsArray().RemoveAt(1);
        await using var first = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
                await context.Response.WriteAsync($"[{template}]", cancellationToken);
            else
                await Baseline(context, cancellationToken);
        }, cancellationToken);
        await using var second = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
                await context.Response.WriteAsync($"[{poorer.ToJsonString()}]", cancellationToken);
            else
                await Baseline(context, cancellationToken);
        }, cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        await poller.PollAsync(Source(first, "a"), cancellationToken);
        await poller.PollAsync(Source(second, "b"), cancellationToken);

        var fromSecond = Assert.Single(
            await database.ListIncidentsAsync(new IncidentQuery(null, "b", 0, 10), cancellationToken));
        Assert.Equal(1, fromSecond.FindingCount);
        Assert.Equal(2, (await database.ListIncidentsAsync(new IncidentQuery(null, null, 0, 10), cancellationToken)).Count);
        // One id on the single-incident route reads as the richest capture.
        var richest = await database.FindIncidentAsync(FixtureId, cancellationToken);
        Assert.NotNull(richest);
        Assert.Equal("a", richest.SourceId);
        Assert.Equal(2, richest.FindingCount);
    }

    [Fact]
    public async Task A_repost_with_an_end_updates_the_row_and_a_poorer_recapture_does_not_replace_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var template = await TemplateAsync(cancellationToken);
        var atMs = JsonNode.Parse(template)!["at_ms"]!.GetValue<long>();
        var endedAtMs = atMs + 45_000;
        var ended = JsonNode.Parse(template)!;
        ended["ended_at_ms"] = endedAtMs;
        // What a daemon restarted after the incident lists once Alertmanager
        // repeats the alert: the same id against a ring that holds nothing.
        var recaptured = JsonNode.Parse(template)!;
        recaptured["findings"] = new JsonArray();
        recaptured.AsObject().Remove("oldest_finding_ms");
        var pages = new Queue<string>([
            $"[{template}]",
            $"[{ended.ToJsonString()}]",
            $"[{recaptured.ToJsonString()}]"
        ]);
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
                await context.Response.WriteAsync(pages.Dequeue(), cancellationToken);
            else
                await Baseline(context, cancellationToken);
        }, cancellationToken);
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(5_000_000));
        var (poller, database) = await BuildAsync(clock, cancellationToken);
        var source = Source(daemon);

        await poller.PollAsync(source, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.PollAsync(source, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.PollAsync(source, cancellationToken);

        var row = await database.FindIncidentAsync(FixtureId, cancellationToken);
        Assert.NotNull(row);
        Assert.Equal(endedAtMs, row.EndedAtMs);
        Assert.Equal(5_000_000, row.FirstSeenMs);
        Assert.Equal(5_120_000, row.LastSeenMs);
        Assert.Equal(2, row.FindingCount);
        Assert.NotNull(row.FindingsJson);
        using var findings = JsonDocument.Parse(row.FindingsJson);
        Assert.Equal(2, findings.RootElement.GetArrayLength());
        using var document = JsonDocument.Parse(row.IncidentJson);
        Assert.True(document.RootElement.TryGetProperty("oldest_finding_ms", out _));
    }

    private static async Task<string> TemplateAsync(CancellationToken cancellationToken)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        return fixture.RootElement[0].GetRawText();
    }

    // Distinct ids, one per position: the daemon's id is 32 lowercase hex.
    private static string Page(string template, int from, int count)
    {
        var incidents = Enumerable.Range(from, count)
            .Select(index => template.Replace(FixtureId, index.ToString("x32"), StringComparison.Ordinal));
        return $"[{string.Join(',', incidents)}]";
    }

    // One byte over the incidents cap, announced up front so the client refuses
    // it on the header, the way a daemon's own Content-Length reads.
    private static async Task WriteOversizedAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var oversized = new byte[DaemonClient.IncidentsMaxBytes + 1];
        context.Response.ContentLength = oversized.Length;
        await context.Response.Body.WriteAsync(oversized, cancellationToken);
    }

    private static async Task Baseline(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.Path == "/api/status")
        {
            await context.Response.WriteAsync("{\"version\":\"0.20.0\"}", cancellationToken);
            return;
        }

        if (context.Request.Path == "/api/findings")
        {
            var findings = await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json"),
                cancellationToken);
            await context.Response.Body.WriteAsync(findings, cancellationToken);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private async Task<(SourcePoller Poller, HubDatabase Database)> BuildAsync(
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        ILogger<SourcePoller>? logger = null)
    {
        var options = new HubOptions { DatabasePath = _databasePath, HttpTimeout = TimeSpan.FromSeconds(2) };
        var database = new HubDatabase(Options.Create(options), timeProvider);
        await database.InitializeAsync(cancellationToken);
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            timeProvider,
            logger ?? NullLogger<SourcePoller>.Instance);
        return (poller, database);
    }

    private static SourceOptions Source(FakeDaemon daemon, string id = "prod")
    {
        return new SourceOptions
        {
            Id = id,
            Name = "Production",
            Environment = "production",
            BaseUrl = daemon.BaseUrl,
            AuthHeaderName = "X-API-Key",
            AuthHeaderValue = "must-not-leak"
        };
    }

    // The findings leg succeeded, so the source is reachable whatever the
    // incidents leg came to, and that outcome sits in its own table.
    private static async Task AssertReachableAsync(
        HubDatabase database,
        string expectedState,
        string? expectedErrorCode,
        CancellationToken cancellationToken)
    {
        var state = Assert.Contains("prod", await database.QuerySourceStatesAsync(cancellationToken));
        Assert.Null(state.UnreachableSinceMs);
        Assert.Null(state.LastErrorCode);
        var read = Assert.Contains("prod", await database.QueryIncidentReadsAsync(cancellationToken));
        Assert.Equal(expectedState, read.State);
        Assert.Equal(expectedErrorCode, read.LastErrorCode);
    }
}
