using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

// Drives a real worker over a stub engine, end to end.
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class AnalysisApiTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-analysis-{Guid.NewGuid():N}");

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AnalysisApiTests()
    {
        Directory.CreateDirectory(_workspace);
        var enginePath = WriteStubEngine();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                // The real clock: the worker's idle delay runs on the injected
                // TimeProvider, and a frozen one would park it forever.
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(TimeProvider.System);
                services.PostConfigure<HubOptions>(options =>
                {
                    options.DatabasePath = Path.Combine(_workspace, "hub.db");
                    options.Analysis = new AnalysisOptions
                    {
                        EngineBinaryPath = enginePath,
                        ReportDirectory = Path.Combine(_workspace, "reports"),
                        Timeout = TimeSpan.FromSeconds(20)
                    };
                    options.Sources =
                    [
                        new SourceOptions
                        {
                            Id = "prod-tempo",
                            Name = "Tempo, production",
                            Environment = "production",
                            Kind = SourceKinds.Tempo,
                            RetentionHours = 168,
                            BaseUrl = new Uri("http://tempo.example:3200")
                        }
                    ];
                });
            }));
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task A_submitted_run_is_executed_and_its_report_is_served_from_the_same_origin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var submission = await SubmitAsync("""
            {"source_id":"prod-tempo","request":{"service":"order-service","lookback":"1h","max_traces":100}}
            """, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, submission.StatusCode);
        var id = (await submission.Content.ReadFromJsonAsync<JsonElement>(cancellationToken))
            .GetProperty("id").GetString()!;

        var run = await WaitForTerminalStatusAsync(id, cancellationToken);
        Assert.Equal(AnalysisStatuses.Succeeded, run.GetProperty("status").GetString());
        Assert.Equal("0.16.0", run.GetProperty("producer_version").GetString());
        // Recorded at submission so the card survives the source leaving the
        // configuration.
        Assert.Equal("Tempo, production", run.GetProperty("source_name").GetString());
        Assert.Equal("operator@example.internal", run.GetProperty("requested_by").GetString());
        Assert.Equal("order-service", run.GetProperty("request").GetProperty("service").GetString());
        Assert.Equal(3, run.GetProperty("result").GetProperty("findings").GetInt32());
        Assert.False(run.GetProperty("result").GetProperty("empty").GetBoolean());
        Assert.True(run.GetProperty("expires_at_ms").GetInt64() > run.GetProperty("finished_at_ms").GetInt64());

        using var report = await _client.GetAsync($"/reports/{id}.html", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal("text/html", report.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<html>", await report.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);

        using var list = await _client.GetAsync("/api/analyses", cancellationToken);
        var runs = await list.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Contains(runs.EnumerateArray(), item => item.GetProperty("id").GetString() == id);
    }

    [Theory]
    // An unknown source is not a bad request shape, but it is still refusable
    // before anything is queued.
    [InlineData("""{"source_id":"absent","request":{}}""")]
    [InlineData("""{"source_id":"prod-tempo","request":{"service":"orders"}}""")]
    [InlineData("""{"source_id":"prod-tempo","request":{"trace_id":"abc","lookback":"1h"}}""")]
    [InlineData("not json")]
    public async Task An_impossible_submission_is_refused_without_queueing_anything(string body)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await SubmitAsync(body, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var status = await _client.GetAsync("/api/status", cancellationToken);
        var payload = await status.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(0, payload.GetProperty("queue_depth").GetInt32());
    }

    [Theory]
    // What this actually pins is the response, not the IsRunId guard: an id the
    // Hub never minted finds no row and 404s with or without it. The guard is
    // defence in depth ahead of that lookup, and its removal is deliberately
    // invisible from out here.
    [InlineData("ABCDEF0123456789")]
    [InlineData("ZZZZZZZZZZZZZZZZ")]
    [InlineData("abc")]
    [InlineData("../../etc/passwd")]
    public async Task A_report_id_outside_the_minted_vocabulary_is_not_found(string id)
    {
        using var response = await _client.GetAsync($"/reports/{id}.html", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }


    [Fact]
    public async Task The_engine_version_and_the_declared_retention_reach_the_launcher()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var status = await _client.GetAsync("/api/status", cancellationToken);
        var payload = await status.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("0.16.0", payload.GetProperty("engine_version").GetString());
        // The browser never sees the header the proxy adds, so the identity has
        // to come back from the server or the topbar cannot show it.
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("identity").ValueKind);

        using var identified = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        identified.Headers.Add("X-Forwarded-User", "operator@example.internal");
        using var response = await _client.SendAsync(identified, cancellationToken);
        var claimed = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("operator@example.internal", claimed.GetProperty("identity").GetString());

        using var sources = await _client.GetAsync("/api/sources", cancellationToken);
        var listed = await sources.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(168, listed.EnumerateArray().Single().GetProperty("retention_hours").GetInt32());
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private async Task<HttpResponseMessage> SubmitAsync(string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/analyses");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Forwarded-User", "operator@example.internal");
        return await _client.SendAsync(request, cancellationToken);
    }

    private async Task<JsonElement> WaitForTerminalStatusAsync(string id, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            using var response = await _client.GetAsync($"/api/analyses/{id}", cancellationToken);
            var run = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (run.GetProperty("status").GetString() is not (AnalysisStatuses.Pending or AnalysisStatuses.Running))
                return run.Clone();
            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Run {id} never reached a terminal status.");
    }

    private string WriteStubEngine()
    {
        var path = Path.Combine(_workspace, "perf-sentinel");
        File.WriteAllText(path, """
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "perf-sentinel 0.16.0"; exit 0; fi
            if [ "$1" = "report" ]; then
              while [ $# -gt 0 ]; do
                if [ "$1" = "--output" ]; then shift; printf '<html>report</html>' > "$1"; fi
                shift
              done
              exit 0
            fi
            cat <<'JSON'
            {"analysis":{"traces_analyzed":42},
             "findings":[{"severity":"critical"},{"severity":"warning"},{"severity":"info"}],
             "quality_gate":{"passed":false},
             "binary_version":"0.16.0"}
            JSON

            """);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
