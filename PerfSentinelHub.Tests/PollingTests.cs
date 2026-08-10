using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class PollingTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-poll-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Poll_uses_the_daemon_contract_and_persists_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await File.ReadAllBytesAsync(FixturePath, cancellationToken);
        var requests = new List<(string Path, string? Auth)>();
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            requests.Add(($"{context.Request.Path}{context.Request.QueryString}", context.Request.Headers.Authorization));
            if (context.Request.Path == "/api/status")
                await context.Response.WriteAsJsonAsync(new { version = "0.11.2" }, cancellationToken);
            else
                await context.Response.Body.WriteAsync(fixture, cancellationToken);
        }, cancellationToken);

        var options = new HubOptions { DatabasePath = _databasePath, HttpTimeout = TimeSpan.FromSeconds(2) };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            TimeProvider.System,
            NullLogger<SourcePoller>.Instance);
        var source = new SourceOptions
        {
            Id = "prod",
            Name = "Production",
            Environment = "production",
            BaseUrl = daemon.BaseUrl,
            AuthHeaderName = "Authorization",
            AuthHeaderValue = "Bearer test-secret"
        };

        var result = await poller.PollAsync(source, cancellationToken);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal("0.11.2", result.ProducerVersion);
        Assert.False(result.IsPossiblyTruncated);
        Assert.Equal(
            [
                ("/api/status", "Bearer test-secret"),
                ("/api/findings?limit=1000&include_acked=true", "Bearer test-secret")
            ],
            requests);
        Assert.Single(await database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 10),
            cancellationToken));

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT producer_version, unreachable_since_ms, last_error_code
            FROM source_state WHERE source_id = 'prod';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal("0.11.2", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public async Task Poll_reports_a_cap_sized_snapshot_as_possibly_truncated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            cancellationToken));
        var finding = fixture.RootElement[0].GetRawText();
        var payload = System.Text.Encoding.UTF8.GetBytes(
            $"[{string.Join(',', Enumerable.Repeat(finding, 1000))}]");
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (context.Request.Path == "/api/status")
                await context.Response.WriteAsync("{\"version\":\"0.11.2\"}", cancellationToken);
            else
                await context.Response.Body.WriteAsync(payload, cancellationToken);
        }, cancellationToken);
        var options = new HubOptions { DatabasePath = _databasePath };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var logger = new ListLogger<SourcePoller>();
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            TimeProvider.System,
            logger);

        var result = await poller.PollAsync(new SourceOptions
        {
            Id = "capped",
            Name = "Capped",
            Environment = "test",
            BaseUrl = daemon.BaseUrl
        }, cancellationToken);

        Assert.True(result.IsPossiblyTruncated);
        Assert.Contains(logger.Messages, message => message.Contains("possibly truncated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http", "http_error")]
    [InlineData("status", "invalid_status")]
    [InlineData("findings", "invalid_findings")]
    [InlineData("large", "response_too_large")]
    [InlineData("timeout", "timeout")]
    public async Task Failure_is_classified_recorded_and_does_not_log_credentials(
        string mode,
        string expectedCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (mode == "http")
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
            if (mode == "timeout")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                return;
            }
            if (context.Request.Path == "/api/status")
            {
                await context.Response.WriteAsync(
                    mode == "status" ? "{}" : "{\"version\":\"0.11.2\"}",
                    cancellationToken);
                return;
            }
            if (mode == "large")
            {
                var oversized = new byte[16 * 1024 * 1024 + 1];
                context.Response.ContentLength = oversized.Length;
                await context.Response.Body.WriteAsync(oversized, cancellationToken);
                return;
            }
            await context.Response.WriteAsync("{}", cancellationToken);
        }, cancellationToken);
        var options = new HubOptions
        {
            DatabasePath = _databasePath,
            HttpTimeout = mode == "timeout" ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(2)
        };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var logger = new ListLogger<SourcePoller>();
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            TimeProvider.System,
            logger);
        var source = new SourceOptions
        {
            Id = mode,
            Name = mode,
            Environment = "test",
            BaseUrl = daemon.BaseUrl,
            AuthHeaderName = "Authorization",
            AuthHeaderValue = "Bearer must-not-leak"
        };

        var exception = await Assert.ThrowsAsync<SourcePollException>(() =>
            poller.PollAsync(source, cancellationToken));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.Contains(logger.Messages, message => message.Contains(mode, StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(expectedCode, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("must-not-leak", StringComparison.Ordinal));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT unreachable_since_ms, last_error_code FROM source_state WHERE source_id = $id;";
        command.Parameters.AddWithValue("$id", mode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.False(reader.IsDBNull(0));
        Assert.Equal(expectedCode, reader.GetString(1));
    }

    [Fact]
    public async Task Success_after_failure_clears_unreachable_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await File.ReadAllBytesAsync(FixturePath, cancellationToken);
        var fail = true;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            if (fail)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
            if (context.Request.Path == "/api/status")
                await context.Response.WriteAsync("{\"version\":\"0.11.2\"}", cancellationToken);
            else
                await context.Response.Body.WriteAsync(fixture, cancellationToken);
        }, cancellationToken);
        var options = new HubOptions { DatabasePath = _databasePath };
        var database = new HubDatabase(Options.Create(options), TimeProvider.System);
        await database.InitializeAsync(cancellationToken);
        var poller = new SourcePoller(
            new DaemonClient(new HttpClient(), Options.Create(options)),
            database,
            TimeProvider.System,
            NullLogger<SourcePoller>.Instance);
        var source = new SourceOptions
        {
            Id = "recovering",
            Name = "Recovering",
            Environment = "test",
            BaseUrl = daemon.BaseUrl
        };

        await Assert.ThrowsAsync<SourcePollException>(() => poller.PollAsync(source, cancellationToken));
        fail = false;
        await poller.PollAsync(source, cancellationToken);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unreachable_since_ms, last_error_code
            FROM source_state WHERE source_id = 'recovering';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-findings-0.11.2.json");

    private sealed class FakeDaemon(WebApplication app, Uri baseUrl) : IAsyncDisposable
    {
        public Uri BaseUrl { get; } = baseUrl;

        public static async Task<FakeDaemon> StartAsync(
            RequestDelegate handler,
            CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Map("/api/status", handler);
            app.Map("/api/findings", handler);
            await app.StartAsync(cancellationToken);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!;
            return new FakeDaemon(app, new Uri(addresses.Addresses.Single()));
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
