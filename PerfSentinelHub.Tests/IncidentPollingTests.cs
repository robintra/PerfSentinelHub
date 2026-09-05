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
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/incidents")
            {
                var oversized = new byte[DaemonClient.IncidentsMaxBytes + 1];
                context.Response.ContentLength = oversized.Length;
                await context.Response.Body.WriteAsync(oversized, cancellationToken);
            }
            else
            {
                await Baseline(context, cancellationToken);
            }
        }, cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Null(result.IncidentCount);
        await AssertReachableAsync(database, IncidentReadStates.Error, "response_too_large", cancellationToken);
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
        using var document = JsonDocument.Parse(row.IncidentJson);
        Assert.Equal(2, document.RootElement.GetProperty("findings").GetArrayLength());
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

    private static SourceOptions Source(FakeDaemon daemon)
    {
        return new SourceOptions
        {
            Id = "prod",
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
