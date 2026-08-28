using System.Text.Json;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

public sealed class AnalysisRequestTests
{
    private const long Now = 1_787_839_140_000;

    [Fact]
    public void A_daemon_snapshot_takes_no_parameters()
    {
        Assert.NotNull(Parse("{}", SourceKinds.Daemon, out var accepted));
        Assert.Null(accepted);

        Assert.Null(Parse("""{"service":"orders"}""", SourceKinds.Daemon, out var rejected));
        Assert.Equal("A daemon snapshot takes no parameters.", rejected);
    }

    [Theory]
    // A trace ID resolves to exactly one trace, so no window applies to it.
    [InlineData("""{"trace_id":"abc123","lookback":"1h"}""")]
    [InlineData("""{"trace_id":"abc123","service":"orders"}""")]
    [InlineData("""{"trace_id":"abc123","from_ms":1,"to_ms":2}""")]
    // Mutually exclusive, the way the engine takes them.
    [InlineData("""{"service":"orders","lookback":"1h","from_ms":1,"to_ms":2}""")]
    // Half an absolute window is not a window.
    [InlineData("""{"service":"orders","from_ms":1787838000000}""")]
    // No implicit window: the engine's own 1h default would not be recorded.
    [InlineData("""{"service":"orders"}""")]
    [InlineData("""{"service":"orders","lookback":"1 hour"}""")]
    [InlineData("""{"service":"orders","lookback":"0h"}""")]
    [InlineData("""{"service":"","lookback":"1h"}""")]
    [InlineData("""{"trace_id":"not a trace id"}""")]
    public void An_impossible_pair_is_refused_before_it_reaches_the_engine(string payload)
    {
        Assert.Null(Parse(payload, SourceKinds.Tempo, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void An_absolute_window_must_be_ordered_and_already_past()
    {
        Assert.Null(Parse($$"""{"service":"orders","from_ms":{{Now - 1000}},"to_ms":{{Now - 2000}}}""",
            SourceKinds.Tempo, out var reversed));
        Assert.Equal("The window's start must come before its end.", reversed);

        Assert.Null(Parse($$"""{"service":"orders","from_ms":{{Now}},"to_ms":{{Now + 3_600_000}}}""",
            SourceKinds.Tempo, out var future));
        Assert.Equal("The window's end cannot be in the future.", future);
    }

    [Fact]
    public void Max_traces_is_bounded_by_the_service_cap()
    {
        Assert.Null(Parse("""{"service":"orders","lookback":"1h","max_traces":2001}""",
            SourceKinds.Tempo, out var above));
        Assert.Equal("max_traces must be between 1 and 2000.", above);
        Assert.NotNull(Parse("""{"service":"orders","lookback":"1h","max_traces":2000}""",
            SourceKinds.Tempo, out _));
    }

    [Fact]
    public void A_non_numeric_timestamp_is_absent_rather_than_a_crash()
    {
        // JsonElement.TryGetInt64 throws on a string element instead of
        // returning false, so a wrong type must not reach it.
        Assert.Null(Parse("""{"service":"orders","from_ms":"yesterday","to_ms":"today"}""",
            SourceKinds.Tempo, out var error));
        Assert.Equal("A service request needs either a lookback or an absolute window.", error);
    }

    [Fact]
    public void A_relative_window_becomes_the_engine_command_line()
    {
        var request = Parse("""{"service":"order-service","lookback":"90m","max_traces":250}""",
            SourceKinds.Tempo, out _);

        Assert.Equal(
            [
                "tempo", "--endpoint", "http://tempo.example:3200", "--format", "json",
                "--service", "order-service", "--lookback", "90m", "--max-traces", "250"
            ],
            request!.ToEngineArguments(Source(SourceKinds.Tempo)));
    }

    [Fact]
    public void An_absolute_window_becomes_iso_8601_utc()
    {
        var request = Parse($$"""{"service":"orders","from_ms":1787835540600,"to_ms":1787838540400}""",
            SourceKinds.JaegerQuery, out _);

        var arguments = request!.ToEngineArguments(Source(SourceKinds.JaegerQuery)).ToList();

        Assert.Equal("jaeger-query", arguments[0]);
        // The residual milliseconds are truncated: the engine takes whole seconds.
        Assert.Equal("2026-08-27T12:59:00Z", arguments[arguments.IndexOf("--from") + 1]);
        Assert.Equal("2026-08-27T13:49:00Z", arguments[arguments.IndexOf("--to") + 1]);
        // The engine refuses --lookback alongside --from/--to, so an absolute
        // window must not carry one.
        Assert.DoesNotContain("--lookback", arguments);
        // Absent from the submission, so the Hub sends the engine's own default
        // rather than leaving the cap unstated in the stored run.
        Assert.Equal("100", arguments[arguments.IndexOf("--max-traces") + 1]);
    }

    [Fact]
    public void A_trace_request_carries_no_window_at_all()
    {
        var request = Parse("""{"trace_id":"abc123def456"}""", SourceKinds.Tempo, out _);

        var arguments = request!.ToEngineArguments(Source(SourceKinds.Tempo));

        Assert.Equal(["tempo", "--endpoint", "http://tempo.example:3200", "--format", "json",
            "--trace-id", "abc123def456"], arguments);
    }

    private static AnalysisRequest? Parse(string payload, string kind, out string? error)
    {
        using var document = JsonDocument.Parse(payload);
        return AnalysisRequest.TryParse(
            document.RootElement,
            Source(kind),
            new AnalysisOptions(),
            Now,
            out error);
    }

    private static SourceOptions Source(string kind) => new()
    {
        Id = "target",
        Name = "Target",
        Environment = "production",
        Kind = kind,
        BaseUrl = new Uri("http://tempo.example:3200")
    };
}
