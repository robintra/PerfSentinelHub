using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

public sealed class AnalysisLauncherApiTests(HubApplicationFactory factory)
    : IClassFixture<HubApplicationFactory>
{
    [Fact]
    public async Task Status_reports_what_a_run_costs_in_the_wire_contract_casing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/status", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = body.RootElement;
        Assert.Equal("perf-sentinel-hub", root.GetProperty("service").GetString());
        // No engine binary is configured in tests, and the launcher has to be
        // able to tell "not configured" from a version it can compare.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("engine_version").ValueKind);
        Assert.Equal(0, root.GetProperty("queue_depth").GetInt32());
        Assert.Equal(2, root.GetProperty("workers").GetInt32());

        var limits = root.GetProperty("limits");
        Assert.Equal(2000, limits.GetProperty("max_traces_cap").GetInt32());
        Assert.Equal(300, limits.GetProperty("analysis_timeout_seconds").GetInt32());
        Assert.Equal(24, limits.GetProperty("report_retention_hours").GetInt32());
    }

    [Fact]
    public async Task Sources_separate_never_observed_from_unreachable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var before = await ReadSourceAsync(client, "test", cancellationToken);
        Assert.True(before.GetProperty("reachable").GetBoolean());
        Assert.Equal(SourceKinds.Daemon, before.GetProperty("kind").GetString());
        // Never polled is not the epoch. The launcher renders these as absent.
        Assert.Equal(JsonValueKind.Null, before.GetProperty("last_attempt_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("last_success_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("last_error_code").ValueKind);

        await factory.Database.MarkSourceFailureAsync("test", 1_700_000_000_000, "connect_timeout", cancellationToken);

        var after = await ReadSourceAsync(client, "test", cancellationToken);
        Assert.False(after.GetProperty("reachable").GetBoolean());
        Assert.Equal(1_700_000_000_000, after.GetProperty("unreachable_since_ms").GetInt64());
        Assert.Equal("connect_timeout", after.GetProperty("last_error_code").GetString());
    }

    [Fact]
    public async Task A_trace_backend_is_listed_and_carries_no_producer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scoped = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Sources =
                [
                    ..options.Sources,
                    new SourceOptions
                    {
                        Id = "victoria",
                        Name = "Victoria Traces",
                        Environment = "production",
                        Kind = SourceKinds.JaegerQuery,
                        RetentionHours = 2160,
                        BaseUrl = new Uri("http://127.0.0.1:10428")
                    }
                ])));
        using var client = scoped.CreateClient();

        var source = await ReadSourceAsync(client, "victoria", cancellationToken);

        Assert.Equal(SourceKinds.JaegerQuery, source.GetProperty("kind").GetString());
        // Bounds the picker: asking for more than the backend keeps returns
        // nothing useful after a full run.
        Assert.Equal(2160, source.GetProperty("retention_hours").GetInt32());
        // A backend stores traces and detects nothing, so it has no producer
        // version to compare against the engine.
        Assert.Equal(JsonValueKind.Null, source.GetProperty("producer_version").ValueKind);
    }

    [Fact]
    public async Task A_source_publishes_the_endpoint_and_subcommand_a_command_would_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scoped = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Sources =
                [
                    ..options.Sources,
                    new SourceOptions
                    {
                        Id = "tempo",
                        Name = "Tempo",
                        Environment = "production",
                        Kind = SourceKinds.Tempo,
                        BaseUrl = new Uri("http://tempo.obs.svc:3200/"),
                        AuthHeaderName = "Authorization",
                        AuthHeaderValue = "Bearer topsecret" // gitleaks:allow -- synthetic test credential
                    }
                ])));
        using var client = scoped.CreateClient();

        var backend = await ReadSourceAsync(client, "tempo", cancellationToken);
        // The trailing slash is gone: the printed command and the launched run
        // have to spell the endpoint the same way.
        Assert.Equal("http://tempo.obs.svc:3200", backend.GetProperty("base_url").GetString());
        Assert.Equal("tempo", backend.GetProperty("engine_subcommand").GetString());

        var daemon = await ReadSourceAsync(client, "test", cancellationToken);
        // A daemon is read over HTTP, so there is no command to spell at all.
        Assert.Equal(JsonValueKind.Null, daemon.GetProperty("engine_subcommand").ValueKind);
    }

    [Fact]
    public async Task A_source_with_an_auth_header_names_it_and_never_its_value()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scoped = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Sources =
                [
                    ..options.Sources,
                    new SourceOptions
                    {
                        Id = "guarded",
                        Name = "Guarded",
                        Environment = "production",
                        Kind = SourceKinds.JaegerQuery,
                        BaseUrl = new Uri("http://jaeger.obs.svc:16686"),
                        AuthHeaderName = "X-Scope-OrgID",
                        AuthHeaderValue = "tenant-42" // gitleaks:allow -- synthetic test credential
                    }
                ])));
        using var client = scoped.CreateClient();

        using var response = await client.GetAsync("/api/sources", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        using var body = JsonDocument.Parse(payload);
        var source = body.RootElement.EnumerateArray()
            .Single(candidate => candidate.GetProperty("id").GetString() == "guarded");
        // The name makes the note actionable, the value never leaves the Hub.
        Assert.Equal("X-Scope-OrgID", source.GetProperty("auth_header_name").GetString());
        Assert.DoesNotContain("tenant-42", payload, StringComparison.Ordinal);

        var open = await ReadSourceAsync(client, "test", cancellationToken);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("auth_header_name").ValueKind);
    }

    private static async Task<JsonElement> ReadSourceAsync(
        HttpClient client,
        string sourceId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/api/sources", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var body = JsonDocument.Parse(payload);
        return body.RootElement
            .EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == sourceId)
            .Clone();
    }
}
