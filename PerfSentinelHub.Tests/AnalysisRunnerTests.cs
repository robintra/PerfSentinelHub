using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

// Runs a real subprocess against a stub engine, the way the container will.
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class AnalysisRunnerTests : IDisposable
{
    private const long Now = 1_787_839_140_000;

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-runner-{Guid.NewGuid():N}");

    private const string ReportJson = """
        {
          "analysis": {"traces_analyzed": 12},
          "findings": [
            {"severity": "critical"}, {"severity": "warning"},
            {"severity": "warning"}, {"severity": "info"}
          ],
          "quality_gate": {"passed": false},
          "binary_version": "0.16.0",
          "warning_details": [
            {"kind": "snapshot_scope", "message": "Findings capped at 4 of 118 retained"}
          ]
        }
        """;

    [Fact]
    public async Task A_backend_run_queries_then_renders_and_summarises()
    {
        var runner = Runner(StubEngine(ReportJson, exitCode: 0));

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisStatuses.Succeeded, outcome.Status);
        Assert.Null(outcome.ErrorCode);
        // Read back from the engine's own output, never assumed by the Hub.
        Assert.Equal("0.16.0", outcome.ProducerVersion);
        var summary = outcome.Summary!;
        Assert.False(summary.Empty);
        Assert.Equal(4, summary.Findings);
        Assert.Equal(1, summary.Critical);
        Assert.Equal(2, summary.Warning);
        Assert.Equal(1, summary.Info);
        Assert.Equal(12, summary.TracesAnalyzed);
        Assert.False(summary.QualityGatePassed);
        Assert.Equal("snapshot_scope", Assert.Single(summary.Warnings).Kind);

        Assert.True(File.Exists(runner.ReportPath("run-1")));
        // The scratch input must not survive: it is a copy of the report.
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "run-1.input.json")));
    }

    [Fact]
    public async Task Zero_traces_is_an_empty_success_rather_than_a_failure()
    {
        var runner = Runner(StubEngine(
            """{"analysis": {"traces_analyzed": 0}, "findings": [], "quality_gate": {"passed": true}}""",
            exitCode: 0));

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisStatuses.Succeeded, outcome.Status);
        // A gate that passes on zero traces has not measured anything, so the
        // launcher labels this run empty rather than green.
        Assert.True(outcome.Summary!.Empty);
        Assert.True(outcome.Summary.QualityGatePassed);
    }

    [Theory]
    [InlineData("error sending request for url: Connection refused", AnalysisErrorCodes.SourceUnreachable)]
    [InlineData("HTTP status 401 Unauthorized", AnalysisErrorCodes.SourceAuthFailed)]
    [InlineData("backend returned 400 Bad Request", AnalysisErrorCodes.SourceRejectedRequest)]
    [InlineData("thread 'main' panicked", AnalysisErrorCodes.BinaryFailed)]
    public async Task A_refused_query_names_an_owner_without_leaking_stderr(string stderr, string expected)
    {
        var runner = Runner(StubEngine(ReportJson, exitCode: 1, standardError: stderr));

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisStatuses.Failed, outcome.Status);
        Assert.Equal(expected, outcome.ErrorCode);
        Assert.Null(outcome.Summary);
    }

    [Fact]
    public async Task Output_that_is_not_a_report_fails_rather_than_rendering_nothing()
    {
        var runner = Runner(StubEngine("not json at all", exitCode: 0));

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisErrorCodes.BinaryFailed, outcome.ErrorCode);
        Assert.False(File.Exists(runner.ReportPath("run-1")));
    }

    [Fact]
    public async Task A_run_past_the_ceiling_is_killed_and_reported_as_a_timeout()
    {
        var runner = Runner(StubEngine(ReportJson, exitCode: 0, sleepSeconds: 30), TimeSpan.FromSeconds(1));

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisErrorCodes.Timeout, outcome.ErrorCode);
    }

    [Fact]
    public async Task No_engine_binary_is_an_internal_failure_rather_than_a_crash()
    {
        var runner = Runner(binaryPath: null);

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisErrorCodes.Internal, outcome.ErrorCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private AnalysisRunner Runner(string? binaryPath, TimeSpan? timeout = null)
    {
        var options = Options.Create(new HubOptions
        {
            Analysis = new AnalysisOptions
            {
                EngineBinaryPath = binaryPath,
                ReportDirectory = Path.Combine(_workspace, "reports"),
                Timeout = timeout ?? TimeSpan.FromSeconds(30)
            }
        });
        return new AnalysisRunner(
            new DaemonClient(new HttpClient(), options),
            options,
            NullLogger<AnalysisRunner>.Instance);
    }

    /// <summary>
    /// A stub perf-sentinel: answers the query subcommand with the given JSON
    /// and writes an HTML file for `report --output`, which is the two-step
    /// shape the real binary imposes.
    /// </summary>
    private string StubEngine(
        string reportJson,
        int exitCode,
        string standardError = "",
        int sleepSeconds = 0)
    {
        Directory.CreateDirectory(_workspace);
        var path = Path.Combine(_workspace, "perf-sentinel");
        File.WriteAllText(path, $"""
            #!/bin/sh
            if [ "$1" = "report" ]; then
              while [ $# -gt 0 ]; do
                if [ "$1" = "--output" ]; then shift; printf '<html>report</html>' > "$1"; fi
                shift
              done
              exit 0
            fi
            [ {sleepSeconds} -gt 0 ] && sleep {sleepSeconds}
            printf '%s' {Quote(standardError)} >&2
            printf '%s' {Quote(reportJson)}
            exit {exitCode}

            """);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string Quote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static AnalysisRun Run() => new(
        "run-1", AnalysisStatuses.Running, "target", "Target", "production",
        SourceKinds.Tempo, "{}", "operator@example.internal", Now, Now, null, null, null, null, null);

    private static SourceOptions Source() => new()
    {
        Id = "target",
        Name = "Target",
        Environment = "production",
        Kind = SourceKinds.Tempo,
        BaseUrl = new Uri("http://tempo.example:3200")
    };

    private static AnalysisRequest Request()
    {
        using var document = JsonDocument.Parse("""{"service":"orders","lookback":"1h"}""");
        return AnalysisRequest.TryParse(document.RootElement, Source(), new AnalysisOptions(), Now, out _)!;
    }
}
