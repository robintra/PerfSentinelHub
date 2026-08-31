using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-runner-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Fact]
    public async Task A_backend_run_queries_then_renders_and_summarises()
    {
        var runner = Runner(StubEngine(ReportJson, 0));

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
        // The weight is the file's own, so the launcher shows a measurement.
        Assert.Equal(new FileInfo(runner.ReportPath("run-1")).Length, summary.ReportBytes);
        // The scratch input must not survive: it is a copy of the report.
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "run-1.input.json")));
    }

    [Fact]
    public async Task The_render_always_caps_embedded_traces_so_no_finding_is_dropped()
    {
        var runner = Runner(StubEngine(ReportJson, 0));

        await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        var arguments = (await File.ReadAllTextAsync(
            Path.Combine(_workspace, "reports", "render-args.txt"),
            TestContext.Current.CancellationToken)).TrimEnd();
        // Passing the flag at all opts the sink out of size targeting, which is
        // the point: every finding reaches the report, the trees are capped.
        // EndsWith pins the exact value: a bare Contains("50") also matches
        // 500. And no --sort: impact is the 0.17.0 engine's own default, and
        // the released 0.16.0 rejects the flag outright.
        Assert.EndsWith("--max-traces-embedded 50", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--sort", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void A_daemon_report_goes_live_only_against_a_bare_origin()
    {
        // The engine's --daemon-url takes an origin and nothing else, so a
        // daemon behind a path-based ingress gets a static report instead of
        // a render that dies at argument parsing.
        var origin = new SourceOptions
        {
            Id = "d",
            Name = "D",
            Environment = "production",
            Kind = SourceKinds.Daemon,
            BaseUrl = new Uri("http://daemon.svc:4318/")
        };
        var prefixed = origin with { BaseUrl = new Uri("http://ingress.svc/daemon/") };
        var backend = origin with { Kind = SourceKinds.Tempo, BaseUrl = new Uri("http://tempo.svc:3200") };

        Assert.Equal("http://daemon.svc:4318", AnalysisRunner.LiveDaemonUrl(origin, true));
        Assert.Null(AnalysisRunner.LiveDaemonUrl(prefixed, true));
        Assert.Null(AnalysisRunner.LiveDaemonUrl(backend, true));
        // And an engine that does not take the flag renders every one of them
        // static, rather than being handed an argument it refuses to parse.
        Assert.Null(AnalysisRunner.LiveDaemonUrl(origin, false));
    }

    [Fact]
    public async Task A_daemon_run_is_rendered_live_when_the_probed_engine_takes_the_flag()
    {
        // The pure helper above says what the URL should be, and EngineProbeTests
        // says what the binary answers. This is the wire between them: without it
        // a render that ignored the probe entirely would keep every test green.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ReportJson, cancellationToken);
        }, cancellationToken);
        var options = RunnerOptions(StubEngine(
            ReportJson, 0, help: "      --daemon-url <URL>  live mode"));
        var probe = new EngineProbe(options, NullLogger<EngineProbe>.Instance);
        await probe.StartAsync(cancellationToken);
        var runner = new AnalysisRunner(
            new DaemonClient(new HttpClient(), options),
            probe,
            options,
            NullLogger<AnalysisRunner>.Instance);
        var source = new SourceOptions
        {
            Id = "live",
            Name = "Live",
            Environment = "production",
            Kind = SourceKinds.Daemon,
            BaseUrl = daemon.BaseUrl
        };

        var outcome = await runner.RunAsync(Run(), source, Request(), cancellationToken);

        Assert.True(probe.SupportsDaemonUrl);
        Assert.Equal(AnalysisStatuses.Succeeded, outcome.Status);
        var arguments = await File.ReadAllTextAsync(
            Path.Combine(_workspace, "reports", "render-args.txt"),
            cancellationToken);
        Assert.Contains(
            $"--daemon-url {source.EndpointArgument}",
            arguments,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_daemon_run_renders_static_when_the_engine_does_not_take_the_flag()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ReportJson, cancellationToken);
        }, cancellationToken);
        // Same run, same source, a binary whose help names no such flag.
        var options = RunnerOptions(StubEngine(
            ReportJson, 0, help: "      --output <FILE>  where to write"));
        var probe = new EngineProbe(options, NullLogger<EngineProbe>.Instance);
        await probe.StartAsync(cancellationToken);
        var runner = new AnalysisRunner(
            new DaemonClient(new HttpClient(), options),
            probe,
            options,
            NullLogger<AnalysisRunner>.Instance);
        var source = new SourceOptions
        {
            Id = "static",
            Name = "Static",
            Environment = "production",
            Kind = SourceKinds.Daemon,
            BaseUrl = daemon.BaseUrl
        };

        var outcome = await runner.RunAsync(Run(), source, Request(), cancellationToken);

        Assert.False(probe.SupportsDaemonUrl);
        // The run still produces its report: a missing flag costs the live
        // controls, never the render.
        Assert.Equal(AnalysisStatuses.Succeeded, outcome.Status);
        var arguments = await File.ReadAllTextAsync(
            Path.Combine(_workspace, "reports", "render-args.txt"),
            cancellationToken);
        Assert.DoesNotContain("--daemon-url", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_traces_is_an_empty_success_rather_than_a_failure()
    {
        var runner = Runner(StubEngine(
            """{"analysis": {"traces_analyzed": 0}, "findings": [], "quality_gate": {"passed": true}}""",
            0));

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
    // A window too wide for the engine's request deadline, not a backend down.
    [InlineData("Error fetching traces from Tempo: request timed out", AnalysisErrorCodes.Timeout)]
    public async Task A_refused_query_names_an_owner_without_leaking_stderr(string stderr, string expected)
    {
        var runner = Runner(StubEngine(ReportJson, 1, stderr));

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisStatuses.Failed, outcome.Status);
        Assert.Equal(expected, outcome.ErrorCode);
        Assert.Null(outcome.Summary);
    }

    [Fact]
    public async Task Output_that_is_not_a_report_fails_rather_than_rendering_nothing()
    {
        var runner = Runner(StubEngine("not json at all", 0));

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisErrorCodes.BinaryFailed, outcome.ErrorCode);
        Assert.False(File.Exists(runner.ReportPath("run-1")));
    }

    [Fact]
    public async Task A_run_past_the_ceiling_is_killed_and_reported_as_a_timeout()
    {
        var runner = Runner(StubEngine(ReportJson, 0, sleepSeconds: 30), TimeSpan.FromSeconds(1));

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisErrorCodes.Timeout, outcome.ErrorCode);
    }

    [Fact]
    public async Task No_engine_binary_is_an_internal_failure_rather_than_a_crash()
    {
        var runner = Runner(null);

        var outcome = await runner.RunAsync(Run(), Source(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisErrorCodes.Internal, outcome.ErrorCode);
    }

    private IOptions<HubOptions> RunnerOptions(string? binaryPath, TimeSpan? timeout = null)
    {
        return Options.Create(new HubOptions
        {
            Analysis = new AnalysisOptions
            {
                EngineBinaryPath = binaryPath,
                ReportDirectory = Path.Combine(_workspace, "reports"),
                Timeout = timeout ?? TimeSpan.FromSeconds(30)
            }
        });
    }

    private AnalysisRunner Runner(string? binaryPath, TimeSpan? timeout = null)
    {
        var options = RunnerOptions(binaryPath, timeout);
        return new AnalysisRunner(
            new DaemonClient(new HttpClient(), options),
            // Unprobed, so it reports no --daemon-url: these runs are backend
            // ones, where the flag never applies anyway.
            new EngineProbe(options, NullLogger<EngineProbe>.Instance),
            options,
            NullLogger<AnalysisRunner>.Instance);
    }

    /// <summary>
    ///     A stub perf-sentinel: answers the query subcommand with the given JSON
    ///     and writes an HTML file for `report --output`, which is the two-step
    ///     shape the real binary imposes.
    /// </summary>
    private string StubEngine(
        string reportJson,
        int exitCode,
        string standardError = "",
        int sleepSeconds = 0,
        string help = "")
    {
        Directory.CreateDirectory(_workspace);
        var path = Path.Combine(_workspace, "perf-sentinel");
        File.WriteAllText(path, $"""
                                 #!/bin/sh
                                 if [ "$1" = "report" ] && [ "$2" = "--help" ]; then
                                   printf '%s' {Quote(help)}
                                   exit 0
                                 fi
                                 if [ "$1" = "report" ]; then
                                   echo "$@" > render-args.txt
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

    private static string Quote(string value)
    {
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static AnalysisRun Run()
    {
        return new AnalysisRun(
            "run-1", AnalysisStatuses.Running, "target", "Target", "production",
            SourceKinds.Tempo, "{}", "operator@example.internal", Now, Now, null, null, null, null, null);
    }

    private static SourceOptions Source()
    {
        return new SourceOptions
        {
            Id = "target",
            Name = "Target",
            Environment = "production",
            Kind = SourceKinds.Tempo,
            BaseUrl = new Uri("http://tempo.example:3200")
        };
    }

    private static AnalysisRequest Request()
    {
        using var document = JsonDocument.Parse("""{"service":"orders","lookback":"1h"}""");
        return AnalysisRequest.TryParse(document.RootElement, Source(), new AnalysisOptions(), Now, out _)!;
    }
}
