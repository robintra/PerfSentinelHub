using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class AckPollingTests : IDisposable
{
    private const string DaemonSignature =
        "n_plus_one_sql:order-svc:_api_v1_orders:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string TomlSignature = "slow_sql:billing-svc:GET__invoices:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-acks-{Guid.NewGuid():N}.db");

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-acks-0.24.0.json");

    public void Dispose()
    {
        TestPool.ClearFor(_databasePath);
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    [Fact]
    public async Task Both_origins_are_mirrored_and_the_read_is_filed_ok()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await File.ReadAllTextAsync(FixturePath, cancellationToken);
        await using var daemon = await DaemonAsync(
            "0.24.0", context => context.Response.WriteAsync(fixture, cancellationToken), cancellationToken);
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(5_000_000));
        var (poller, database) = await BuildAsync(clock, cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        var mirror = await MirrorAsync(database, cancellationToken);
        Assert.Equal(2, mirror.Count);
        Assert.Equal(
            new ParsedAck(
                DaemonSignature, "daemon", "alice@example.com", null, "2026-09-20T11:28:03.924682Z",
                "2027-08-01T00:00:00Z", DateTimeOffset.Parse("2027-08-01T00:00:00Z").ToUnixTimeMilliseconds()),
            mirror[0].Ack);
        Assert.Equal(
            new ParsedAck(TomlSignature, "toml", "ci-bot", "permanent baseline", "2026-05-04", null, null),
            mirror[1].Ack);
        Assert.All(mirror, row => Assert.Equal(5_000_000, row.ReadAtMs));
        await AssertReachableAsync(database, AckReadStates.Ok, null, cancellationToken);
    }

    [Fact]
    public async Task A_revoked_ack_disappears_on_the_next_read()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await File.ReadAllTextAsync(FixturePath, cancellationToken);
        var pages = new Queue<string>([fixture, $"[{await TemplateAsync(1, cancellationToken)}]"]);
        await using var daemon = await DaemonAsync(
            "0.24.0", context => context.Response.WriteAsync(pages.Dequeue(), cancellationToken), cancellationToken);
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(5_000_000));
        var (poller, database) = await BuildAsync(clock, cancellationToken);
        var source = Source(daemon);

        await poller.PollAsync(source, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(1));
        await poller.PollAsync(source, cancellationToken);

        var kept = Assert.Single(await MirrorAsync(database, cancellationToken));
        Assert.Equal(TomlSignature, kept.Ack.Signature);
        // Rewritten by the read that kept it, which is what the purge reads.
        Assert.Equal(5_060_000, kept.ReadAtMs);
    }

    [Fact]
    public async Task A_baseline_ack_wins_over_a_daemon_ack_on_the_same_signature()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fromDaemon = await TemplateAsync(0, cancellationToken);
        var fromBaseline = (await TemplateAsync(1, cancellationToken))
            .Replace(TomlSignature, DaemonSignature, StringComparison.Ordinal);
        await using var daemon = await DaemonAsync(
            "0.24.0",
            context => context.Response.WriteAsync($"[{fromDaemon},{fromBaseline}]", cancellationToken),
            cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        // The daemon's own precedence: its lookup reads the baseline first.
        var (ack, _) = Assert.Single(await MirrorAsync(database, cancellationToken));
        Assert.Equal("toml", ack.Origin);
        Assert.Equal("ci-bot", ack.By);
        Assert.Null(ack.ExpiresAt);
    }

    [Fact]
    public async Task A_refused_key_is_filed_without_touching_reachability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonAsync(
            "0.24.0",
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("{\"error\":\"missing or invalid X-API-Key\"}", cancellationToken);
            },
            cancellationToken);
        var logger = new ListLogger<AckReader>();
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken, logger);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(1, result.ImportedCount);
        await AssertReachableAsync(database, AckReadStates.Unauthorized, null, cancellationToken);
        Assert.Contains(logger.Messages, message => message.Contains("refused", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("must-not-leak", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    public async Task An_absent_ack_listing_is_not_a_failure(int statusCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonAsync(
            "0.24.0",
            context =>
            {
                context.Response.StatusCode = statusCode;
                return Task.CompletedTask;
            },
            cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        await AssertReachableAsync(database, AckReadStates.Absent, null, cancellationToken);
    }

    [Theory]
    [InlineData("0.11.2", false)]
    [InlineData("0.23.1", false)]
    [InlineData("0.23.9-rc.1", false)]
    [InlineData("nightly", false)]
    [InlineData("0.24.0-rc.1", true)]
    [InlineData("0.24.0", true)]
    [InlineData("1.0.0", true)]
    public async Task Only_a_daemon_that_can_list_the_baseline_is_asked(string version, bool asked)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var requests = 0;
        await using var daemon = await DaemonAsync(
            version,
            context =>
            {
                Interlocked.Increment(ref requests);
                return context.Response.WriteAsync("[]", cancellationToken);
            },
            cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        await poller.PollAsync(Source(daemon), cancellationToken);

        // An older daemon ignores include_toml and answers without the
        // baseline, which would read as "nothing is acked there".
        Assert.Equal(asked ? 1 : 0, requests);
        await AssertReachableAsync(
            database, asked ? AckReadStates.Ok : AckReadStates.Absent, null, cancellationToken);
    }

    [Fact]
    public async Task A_page_at_the_daemon_cap_is_filed_truncated_and_still_mirrored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var template = await TemplateAsync(0, cancellationToken);
        var page = string.Join(',', Enumerable.Range(0, AckReader.AcksCap).Select(index =>
            template.Replace(DaemonSignature, $"slow_sql:svc:GET__rows:{index:x32}", StringComparison.Ordinal)));
        await using var daemon = await DaemonAsync(
            "0.24.0", context => context.Response.WriteAsync($"[{page}]", cancellationToken), cancellationToken);
        var logger = new ListLogger<AckReader>();
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken, logger);

        await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(AckReader.AcksCap, (await MirrorAsync(database, cancellationToken)).Count);
        await AssertReachableAsync(database, AckReadStates.Truncated, null, cancellationToken);
        Assert.Contains(logger.Messages, message => message.Contains("truncated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK, "{}", "invalid_acks")]
    [InlineData(StatusCodes.Status200OK, "[", "invalid_acks")]
    [InlineData(StatusCodes.Status500InternalServerError, "", "http_error")]
    public async Task A_failed_read_is_filed_under_its_own_error_code_and_the_poll_still_succeeds(
        int statusCode,
        string body,
        string expectedCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonAsync(
            "0.24.0",
            async context =>
            {
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsync(body, cancellationToken);
            },
            cancellationToken);
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken);

        var result = await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(1, result.ImportedCount);
        await AssertReachableAsync(database, AckReadStates.Error, expectedCode, cancellationToken);
    }

    [Fact]
    public async Task A_rejected_item_is_counted_and_the_rest_is_kept()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var kept = await TemplateAsync(0, cancellationToken);
        var rejected = (await TemplateAsync(1, cancellationToken))
            .Replace("\"source\":\"toml\"", "\"source\":\"elsewhere\"", StringComparison.Ordinal);
        await using var daemon = await DaemonAsync(
            "0.24.0",
            context => context.Response.WriteAsync($"[{kept},{rejected}]", cancellationToken),
            cancellationToken);
        var logger = new ListLogger<AckReader>();
        var (poller, database) = await BuildAsync(TimeProvider.System, cancellationToken, logger);

        await poller.PollAsync(Source(daemon), cancellationToken);

        Assert.Equal(DaemonSignature, Assert.Single(await MirrorAsync(database, cancellationToken)).Ack.Signature);
        await AssertReachableAsync(database, AckReadStates.Ok, null, cancellationToken);
        Assert.Contains(logger.Messages, message => message.Contains("rejected 1", StringComparison.Ordinal));
    }

    // Rewritten compact, so a replacement holds however the fixture file is laid out.
    private static async Task<string> TemplateAsync(int index, CancellationToken cancellationToken)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        return JsonNode.Parse(fixture.RootElement[index].GetRawText())!.ToJsonString();
    }

    // A daemon whose ack listing is the test's and whose other routes are the
    // plainest poll there is: one finding, no incidents surface.
    private static Task<FakeDaemon> DaemonAsync(
        string version,
        RequestDelegate acks,
        CancellationToken cancellationToken)
    {
        return FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/acks")
            {
                await acks(context);
            }
            else if (context.Request.Path == "/api/status")
            {
                await context.Response.WriteAsJsonAsync(new { version }, cancellationToken);
            }
            else if (context.Request.Path == "/api/findings")
            {
                var findings = await File.ReadAllBytesAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json"),
                    cancellationToken);
                await context.Response.Body.WriteAsync(findings, cancellationToken);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }, cancellationToken);
    }

    private async Task<(SourcePoller Poller, HubDatabase Database)> BuildAsync(
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        ILogger<AckReader>? logger = null)
    {
        var options = new HubOptions { DatabasePath = _databasePath, HttpTimeout = TimeSpan.FromSeconds(2) };
        var database = new HubDatabase(Options.Create(options), timeProvider);
        await database.InitializeAsync(cancellationToken);
        var client = new DaemonClient(new HttpClient(), Options.Create(options));
        var poller = new SourcePoller(
            client,
            database,
            new IncidentReader(client, database, NullLogger<IncidentReader>.Instance),
            new AckReader(client, database, logger ?? NullLogger<AckReader>.Instance),
            timeProvider,
            NullLogger<SourcePoller>.Instance);
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

    // The mirror of the one source these tests poll, as the parser's own record
    // beside the time of the read that wrote the row.
    private static async Task<List<(ParsedAck Ack, long ReadAtMs)>> MirrorAsync(
        HubDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT signature, origin, acked_by, reason, acked_at, expires_at, expires_at_ms, read_at_ms
                              FROM source_acks WHERE source_id = $source_id ORDER BY signature;
                              """;
        command.Parameters.AddWithValue("$source_id", "prod");
        var rows = new List<(ParsedAck Ack, long ReadAtMs)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((
                new ParsedAck(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6)),
                reader.GetInt64(7)));
        return rows;
    }

    // The findings leg succeeded, so the source is reachable whatever the ack
    // leg came to, and that outcome sits in its own table.
    private static async Task AssertReachableAsync(
        HubDatabase database,
        string expectedState,
        string? expectedErrorCode,
        CancellationToken cancellationToken)
    {
        var state = Assert.Contains("prod", await database.QuerySourceStatesAsync(cancellationToken));
        Assert.Null(state.UnreachableSinceMs);
        Assert.Null(state.LastErrorCode);
        var read = Assert.Contains("prod", await database.QueryAckReadsAsync(cancellationToken));
        Assert.Equal(expectedState, read.State);
        Assert.Equal(expectedErrorCode, read.LastErrorCode);
    }
}
