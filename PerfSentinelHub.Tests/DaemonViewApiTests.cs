using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

public sealed class DaemonViewApiTests(HubApplicationFactory factory)
    : IClassFixture<HubApplicationFactory>
{
    private const string Status = """
                                  {"version":"0.16.0","uptime_seconds":864000,"active_traces":900,
                                   "max_active_traces":1000,"analysis_queue_depth":3,"analysis_queue_capacity":256,
                                   "stored_findings":40,"max_retained_findings":10000}
                                  """;

    // Every key a daemon publishes, so the defaults-coverage test actually
    // exercises the whole section rather than a sample of it.
    private const string Config = """
                                  {"listen_addr":"0.0.0.0","listen_port":4318,"listen_port_grpc":4317,
                                   "json_socket":"/tmp/perf-sentinel.sock","max_active_traces":1000,
                                   "trace_ttl_ms":30000,"sampling_rate":0.5,"max_events_per_trace":1000,
                                   "max_payload_size":16777216,"environment":"production",
                                   "max_retained_findings":10000,"max_export_findings":1000,
                                   "max_retained_traces":50,"ingest_queue_capacity":1024,
                                   "analysis_queue_capacity":1024,"memory_high_water_pct":0,
                                   "api_enabled":true,"tls_configured":true,"ack_enabled":true,
                                   "ack_api_key_set":true,"cors_allowed_origins":[],
                                   "archive_configured":false,"correlation_enabled":true,
                                   "correlation_window_ms":600000,"correlation_lag_threshold_ms":5000,
                                   "correlation_min_co_occurrences":5,"correlation_min_confidence":0.7,
                                   "correlation_max_tracked_pairs":10000}
                                  """;

    private const string Report = """
                                  {"binary_version":"0.16.0",
                                   "detection_config":{"n_plus_one_threshold":5,"window_ms":500,
                                     "slow_threshold_ms":500,"slow_min_occurrences":3,"max_fanout":20,
                                     "chatty_service_min_calls":15,"pool_saturation_concurrent_threshold":10,
                                     "serialized_min_sequential":3,"sanitizer_aware_classification":"auto",
                                     "sanitizer_aware_min_cv":0.5},
                                   "green_summary":{"energy_model":"measured","scoring_config":{"api_version":"1.0"}},
                                   "warning_details":[{"kind":"tuning","message":"ingest queue is undersized"}]}
                                  """;

    [Fact]
    public async Task A_daemon_view_relays_its_settings_and_its_gauges()
    {
        var (view, payload) = await ReadViewAsync(Answering);

        Assert.Equal("0.16.0", view.GetProperty("version").GetString());
        Assert.Equal(864_000, view.GetProperty("uptime_seconds").GetInt64());
        // 900 of 1000 is the line the daemon's own advisor uses.
        Assert.Equal("near_capacity", view.GetProperty("state").GetString());
        Assert.Equal(90.0, view.GetProperty("traces").GetProperty("pct").GetDouble());
        Assert.True(view.GetProperty("traces").GetProperty("at_capacity").GetBoolean());
        Assert.False(view.GetProperty("analysis_queue").GetProperty("at_capacity").GetBoolean());

        // Relayed whole: a field a later engine minor adds has to survive.
        Assert.Equal(0.5, view.GetProperty("config").GetProperty("sampling_rate").GetDouble());
        Assert.True(view.GetProperty("config").GetProperty("tls_configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("config_unavailable_reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("error_code").ValueKind);

        Assert.Equal(JsonValueKind.Null, view.GetProperty("hints_unavailable_reason").ValueKind);
        Assert.Equal(0, view.GetProperty("warnings_dropped").GetInt32());
        var warning = view.GetProperty("warnings").EnumerateArray().Single();
        Assert.Equal("tuning", warning.GetProperty("kind").GetString());
        Assert.Equal("ingest queue is undersized", warning.GetProperty("message").GetString());
        Assert.Contains("\"source_id\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_view_carries_the_sections_the_config_endpoint_does_not()
    {
        // /api/config publishes [daemon] alone. These three ride the snapshot,
        // and they are the reason the view reads it at all.
        var (view, _) = await ReadViewAsync(Answering);

        Assert.Equal(5, view.GetProperty("detection_config").GetProperty("n_plus_one_threshold").GetInt32());
        Assert.Equal("1.0", view.GetProperty("scoring_config").GetProperty("api_version").GetString());
        Assert.Equal("measured", view.GetProperty("energy_model").GetString());
    }

    [Fact]
    public async Task Every_setting_the_daemon_publishes_has_a_default_to_compare_against()
    {
        // A key missing here, or spelled the way the config file spells it
        // rather than the way the engine serialises it, would silently read as
        // unchanged on a value somebody did change.
        var (view, _) = await ReadViewAsync(Answering);

        AssertCovered(view, "config", "daemon_defaults");
        AssertCovered(view, "detection_config", "detection_defaults");
        // The comparison is only as good as the version it was taken from, and
        // the reader has to be able to say which one that was.
        Assert.False(string.IsNullOrWhiteSpace(view.GetProperty("defaults_engine_version").GetString()));
    }

    private static void AssertCovered(JsonElement view, string section, string defaults)
    {
        var known = view.GetProperty(defaults).EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var published = view.GetProperty(section).EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        Assert.NotEmpty(published);
        Assert.DoesNotContain(published, name => !known.Contains(name));
    }

    [Fact]
    public async Task A_daemon_with_its_query_api_off_says_which_absence_it_is()
    {
        var (view, _) = await ReadViewAsync(context => context.Request.Path == "/api/config"
            ? Respond(context, HttpStatusCode.NotFound, "{}")
            : Answering(context));

        // A 404 is a configuration statement an operator can act on, and not
        // the same thing as a daemon that did not answer.
        Assert.Equal(JsonValueKind.Null, view.GetProperty("config").ValueKind);
        Assert.Equal("api_disabled", view.GetProperty("config_unavailable_reason").GetString());
        // Everything else still came back.
        Assert.Equal("0.16.0", view.GetProperty("version").GetString());
        Assert.Single(view.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task A_daemon_that_does_not_answer_fabricates_no_state()
    {
        var (view, _) = await ReadViewAsync(context => Respond(context, HttpStatusCode.InternalServerError, "no"));

        Assert.Equal("unreachable", view.GetProperty("state").GetString());
        Assert.Equal("http_error", view.GetProperty("error_code").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("traces").ValueKind);
        // A 500 is the daemon answering, so the config is unreadable, not
        // unreachable, and the unread export is named rather than silent.
        Assert.Equal("unreadable", view.GetProperty("config_unavailable_reason").GetString());
        Assert.Equal("http_error", view.GetProperty("hints_unavailable_reason").GetString());
        Assert.Empty(view.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task A_config_past_its_cap_is_dropped_rather_than_relayed()
    {
        var oversized = "{\"pad\":\"" + new string('x', 80 * 1024) + "\"}";
        var (view, _) = await ReadViewAsync(context => context.Request.Path == "/api/config"
            ? Respond(context, HttpStatusCode.OK, oversized)
            : Answering(context));

        Assert.Equal(JsonValueKind.Null, view.GetProperty("config").ValueKind);
        // The daemon answered with something the Hub refused to relay, which
        // is a different action for an operator than nothing answering.
        Assert.Equal("unreadable", view.GetProperty("config_unavailable_reason").GetString());
        // The status still answered, so the daemon itself is not unreachable.
        Assert.Equal("near_capacity", view.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_config_that_is_not_an_object_is_not_relayed()
    {
        var (view, _) = await ReadViewAsync(context => context.Request.Path == "/api/config"
            ? Respond(context, HttpStatusCode.OK, "[1,2,3]")
            : Answering(context));

        Assert.Equal(JsonValueKind.Null, view.GetProperty("config").ValueKind);
        Assert.Equal("unreadable", view.GetProperty("config_unavailable_reason").GetString());
    }

    [Fact]
    public async Task A_status_refresh_carries_the_gauges_and_nothing_that_needs_a_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(Answering, cancellationToken);
        await using var scoped = Scoped(daemon.BaseUrl, null, null);
        using var client = scoped.CreateClient();

        using var response = await client.GetAsync(
            "/api/sources/probe/daemon?refresh=status",
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var view = body.RootElement;

        Assert.Equal(90.0, view.GetProperty("traces").GetProperty("pct").GetDouble());
        Assert.Equal("0.16.0", view.GetProperty("version").GetString());
        // No config, no defaults, no hints, no state: none of it can change
        // without a restart or a full read, so a light body never carries it.
        Assert.False(view.TryGetProperty("config", out _));
        Assert.False(view.TryGetProperty("warnings", out _));
        Assert.False(view.TryGetProperty("state", out _));
    }

    [Fact]
    public async Task The_view_never_echoes_the_source_auth_header_value()
    {
        await using var daemon = await FakeDaemon.StartAsync(
            Answering,
            TestContext.Current.CancellationToken);
        await using var scoped = Scoped(daemon.BaseUrl, "X-Scope-OrgID", "tenant-42");
        using var client = scoped.CreateClient();

        using var response = await client.GetAsync(
            "/api/sources/probe/daemon",
            TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("tenant-42", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trace_backend_is_refused_because_it_runs_no_daemon()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scoped = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Sources =
                [
                    .. options.Sources,
                    new SourceOptions
                    {
                        Id = "backend",
                        Name = "Backend",
                        Environment = "production",
                        Kind = SourceKinds.Tempo,
                        BaseUrl = new Uri("http://tempo.example:3200")
                    }
                ])));
        using var client = scoped.CreateClient();

        using var response = await client.GetAsync("/api/sources/backend/daemon", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Contains("trace backend", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_source_is_not_found()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/sources/nobody/daemon",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static Task Answering(HttpContext context)
    {
        return context.Request.Path.Value switch
        {
            "/api/status" => Respond(context, HttpStatusCode.OK, Status),
            "/api/config" => Respond(context, HttpStatusCode.OK, Config),
            "/api/export/report" => Respond(context, HttpStatusCode.OK, Report),
            _ => Respond(context, HttpStatusCode.NotFound, "{}")
        };
    }

    private static async Task Respond(HttpContext context, HttpStatusCode statusCode, string body)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes(body),
            context.RequestAborted);
    }

    private async Task<(JsonElement View, string Payload)> ReadViewAsync(RequestDelegate handler)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(handler, cancellationToken);
        await using var scoped = Scoped(daemon.BaseUrl, null, null);
        using var client = scoped.CreateClient();

        using var response = await client.GetAsync("/api/sources/probe/daemon", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var body = JsonDocument.Parse(payload);
        return (body.RootElement.Clone(), payload);
    }

    [Fact]
    public async Task A_full_gate_refuses_a_full_read_and_says_when_to_come_back()
    {
        // The cap exists because each full read buffers a report snapshot, so
        // it is a memory contract rather than a detail. A status refresh goes
        // around it, and has to keep doing so while the gate is full.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(Answering, cancellationToken);
        await using var scoped = Scoped(daemon.BaseUrl, null, null);
        using var client = scoped.CreateClient();
        var gate = scoped.Services.GetRequiredService<DaemonViewGate>();

        var entered = Enumerable.Range(0, DaemonViewGate.MaxReads).Count(_ => gate.TryEnter());
        try
        {
            Assert.Equal(DaemonViewGate.MaxReads, entered);
            Assert.False(gate.TryEnter());

            using var refused = await client.GetAsync("/api/sources/probe/daemon", cancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("1", refused.Headers.RetryAfter?.ToString());

            using var light = await client.GetAsync(
                "/api/sources/probe/daemon?refresh=status",
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, light.StatusCode);
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++)
                gate.Exit();
        }
    }

    private WebApplicationFactory<Program> Scoped(Uri baseUrl, string? headerName, string? headerValue)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Sources =
                [
                    .. options.Sources,
                    new SourceOptions
                    {
                        Id = "probe",
                        Name = "Probe",
                        Environment = "production",
                        Kind = SourceKinds.Daemon,
                        BaseUrl = baseUrl,
                        AuthHeaderName = headerName,
                        AuthHeaderValue = headerValue
                    }
                ])));
    }
}
