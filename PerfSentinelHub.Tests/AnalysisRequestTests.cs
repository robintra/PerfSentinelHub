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
        Assert.Equal(
            "A daemon snapshot takes no parameters, and detects with its own configuration.",
            rejected);
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
            request!.ToEngineArguments(Source(SourceKinds.Tempo), null));
    }

    [Fact]
    public void An_absolute_window_becomes_iso_8601_utc()
    {
        var request = Parse("""{"service":"orders","from_ms":1787835540600,"to_ms":1787838540400}""",
            SourceKinds.JaegerQuery, out _);

        var arguments = request!.ToEngineArguments(Source(SourceKinds.JaegerQuery), null).ToList();

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

        var arguments = request!.ToEngineArguments(Source(SourceKinds.Tempo), null);

        Assert.Equal([
            "tempo", "--endpoint", "http://tempo.example:3200", "--format", "json",
            "--trace-id", "abc123def456"
        ], arguments);
    }

    [Theory]
    [InlineData(SourceKinds.Tempo, "tempo")]
    [InlineData(SourceKinds.JaegerQuery, "jaeger-query")]
    public void The_endpoint_and_subcommand_a_command_would_publish_are_the_ones_the_engine_receives(
        string kind, string subcommand)
    {
        // The launcher prints a command out of these same two properties.
        // Inlining either back into ToEngineArguments would let the printed
        // command and the launched run target different things, silently.
        var source = Source(kind) with { BaseUrl = new Uri("http://backend.example:3200/prefix/") };
        var request = Parse("""{"trace_id":"abc123def456"}""", kind, out _);

        var arguments = request!.ToEngineArguments(source, null).ToList();

        Assert.Equal(subcommand, arguments[0]);
        Assert.Equal(source.EngineSubcommand, arguments[0]);
        Assert.Equal(source.EndpointArgument, arguments[2]);
        // The trailing slash is dropped once, in the one place both readers use.
        Assert.Equal("http://backend.example:3200/prefix", arguments[2]);
    }

    [Fact]
    public void An_authenticated_source_reads_its_header_from_the_environment()
    {
        // The flag names the same variable the launcher's printed command
        // does, and the value itself never enters the argument list.
        var source = Source(SourceKinds.Tempo) with
        {
            AuthHeaderName = "Authorization",
            AuthHeaderValue = "Bearer secret" // gitleaks:allow -- synthetic test credential
        };
        var service = Parse("""{"service":"orders","lookback":"1h"}""", SourceKinds.Tempo, out _);
        var trace = Parse("""{"trace_id":"abc123def456"}""", SourceKinds.Tempo, out _);

        foreach (var arguments in new[]
                 {
                     service!.ToEngineArguments(source, null),
                     trace!.ToEngineArguments(source, null)
                 })
        {
            var flag = arguments.ToList().IndexOf("--auth-header-env");
            Assert.True(flag >= 0);
            Assert.Equal(AnalysisRequest.AuthTokenVariable, arguments[flag + 1]);
            Assert.DoesNotContain(arguments, argument => argument.Contains("secret"));
        }

        Assert.DoesNotContain(
            "--auth-header-env",
            service.ToEngineArguments(Source(SourceKinds.Tempo), null));
    }

    [Fact]
    public void A_daemon_has_no_subcommand_because_it_is_read_rather_than_queried()
    {
        Assert.Null(Source(SourceKinds.Daemon).EngineSubcommand);
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

    [Fact]
    public void A_detection_override_becomes_a_config_the_engine_is_pointed_at()
    {
        var request = Parse("""
                            {"service":"orders","lookback":"1h","detection":{"n_plus_one_min_occurrences":13}}
                            """, SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal("[detection]\nn_plus_one_min_occurrences = 13\n", request!.Detection.ToToml());
        var arguments = request.ToEngineArguments(Source(SourceKinds.Tempo), "/data/reports/run.config.toml").ToList();
        Assert.Equal("/data/reports/run.config.toml", arguments[arguments.IndexOf("-c") + 1]);
    }

    [Fact]
    public void A_value_equal_to_the_engine_default_is_not_recorded_as_an_override()
    {
        // Writing it out would make the run card claim a departure from the
        // standard configuration where there is none.
        var request = Parse("""
                            {"service":"orders","lookback":"1h","detection":{"n_plus_one_min_occurrences":5}}
                            """, SourceKinds.Tempo, out _);

        Assert.True(request!.Detection.IsEmpty);
    }

    [Fact]
    public void The_classification_mode_is_written_as_a_quoted_string()
    {
        var request = Parse("""
                            {"service":"orders","lookback":"1h","detection":{"sanitizer_aware_classification":"strict"}}
                            """, SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal("[detection]\nsanitizer_aware_classification = \"strict\"\n", request!.Detection.ToToml());
    }

    [Theory]
    // TOML tells an integer from a float by the point. The engine reads `1` into
    // its float all the same, but `1.0` says what was meant.
    [InlineData("1", "1.0")]
    [InlineData("0.75", "0.75")]
    public void The_variance_threshold_keeps_a_decimal_point(string sent, string written)
    {
        var request = Parse(
            $$$"""{"service":"orders","lookback":"1h","detection":{"sanitizer_aware_min_cv":{{{sent}}}}}""",
            SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal($"[detection]\nsanitizer_aware_min_cv = {written}\n", request!.Detection.ToToml());
    }

    [Theory]
    [InlineData("""{"sanitizer_aware_classification":"auto"}""")]
    [InlineData("""{"sanitizer_aware_min_cv":0.5}""")]
    public void The_sanitizer_defaults_are_not_recorded_as_overrides(string detection)
    {
        var request = Parse(
            $$"""{"service":"orders","lookback":"1h","detection":{{detection}}}""",
            SourceKinds.Tempo, out _);

        Assert.True(request!.Detection.IsEmpty);
    }

    [Theory]
    [InlineData("0.17.0", false)]
    [InlineData("0.18.0", true)]
    [InlineData("0.19.2", true)]
    // A pre-release of the minor that added them reads them too.
    [InlineData("0.18.0-rc.1", true)]
    // No probed version means no promise about what `-c` will be refused.
    [InlineData(null, false)]
    public void The_sanitizer_knobs_are_offered_only_to_an_engine_that_reads_them(string? engine, bool offered)
    {
        var names = DetectionOverrides.SchemaFor(engine).Select(knob => knob.Name).ToList();

        Assert.Equal(offered, names.Contains("sanitizer_aware_classification"));
        Assert.Equal(offered, names.Contains("sanitizer_aware_min_cv"));
        // The eight thresholds predate the gate and are always there.
        Assert.Contains("n_plus_one_min_occurrences", names);
    }

    [Theory]
    // Bounds mirror the engine's validator: two of them floor at 2, not 1.
    [InlineData("""{"n_plus_one_min_occurrences":0}""")]
    [InlineData("""{"pool_saturation_concurrent_threshold":1}""")]
    [InlineData("""{"serialized_min_sequential":1}""")]
    [InlineData("""{"max_fanout":100001}""")]
    [InlineData("""{"n_plus_one_min_occurrences":"many"}""")]
    [InlineData("""{"no_such_setting":3}""")]
    [InlineData("""{"sanitizer_aware_classification":"loose"}""")]
    [InlineData("""{"sanitizer_aware_classification":3}""")]
    [InlineData("""{"sanitizer_aware_min_cv":0}""")]
    [InlineData("""{"sanitizer_aware_min_cv":10.5}""")]
    [InlineData("""{"sanitizer_aware_min_cv":"high"}""")]
    public void An_out_of_range_or_unknown_detection_setting_is_refused(string detection)
    {
        Assert.Null(Parse(
            $$"""{"service":"orders","lookback":"1h","detection":{{detection}}}""",
            SourceKinds.Tempo, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_daemon_takes_no_detection_override()
    {
        // It detects with its own configuration, so the Hub cannot honour one.
        Assert.Null(Parse("""{"detection":{"max_fanout":50}}""", SourceKinds.Daemon, out var error));
        Assert.Equal(
            "A daemon snapshot takes no parameters, and detects with its own configuration.",
            error);
    }

    private static SourceOptions Source(string kind)
    {
        return new SourceOptions
        {
            Id = "target",
            Name = "Target",
            Environment = "production",
            Kind = kind,
            BaseUrl = new Uri("http://tempo.example:3200")
        };
    }
}
