using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Indexed_source_configuration_binds_to_the_options_model()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Sources:0:Id"] = "test",
                ["Hub:Sources:0:Name"] = "Test",
                ["Hub:Sources:0:Environment"] = "test",
                ["Hub:Sources:0:BaseUrl"] = "http://127.0.0.1:4318"
            })
            .Build();

        var options = configuration.GetSection(HubOptions.SectionName).Get<HubOptions>();

        Assert.NotNull(options);
        Assert.Single(options.Sources);
    }

    [Theory]
    [InlineData("", "http://daemon:4318")]
    [InlineData("bad/source", "http://daemon:4318")]
    [InlineData("prod", "file:///tmp/findings")]
    [InlineData("prod", "http://user@daemon:4318")]
    public void Invalid_source_is_rejected(string id, string url)
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { Id = id, BaseUrl = new Uri(url) }]
        };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Invalid_global_options_are_rejected()
    {
        HubOptions[] invalid =
        [
            ValidOptions() with { Sources = [] },
            ValidOptions() with { Sources = [ValidSource() with { BaseUrl = null }] },
            ValidOptions() with { Sources = [ValidSource() with { BaseUrl = new Uri("https://daemon.example?a=b") }] },
            ValidOptions() with { Sources = [ValidSource(), ValidSource()] },
            ValidOptions() with { DatabasePath = "relative.db" },
            ValidOptions() with { PollInterval = TimeSpan.Zero },
            ValidOptions() with { HttpTimeout = TimeSpan.Zero },
            ValidOptions() with { Retention = TimeSpan.Zero },
            ValidOptions() with { MaxConcurrentPolls = 0 },
            ValidOptions() with { MaxConcurrentPolls = 33 },
            ValidOptions() with { DefaultReadLimit = 0 },
            ValidOptions() with { MaxReadLimit = 10_001 },
            ValidOptions() with { DefaultReadLimit = 101, MaxReadLimit = 100 }
        ];

        Assert.All(invalid, options =>
            Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded));
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("Authorization\r\nInjected", "secret")]
    [InlineData("Authorization", "secret\r\nInjected")]
    public void Invalid_auth_header_is_rejected(string? name, string? value)
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { AuthHeaderName = name, AuthHeaderValue = value }]
        };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef\t0123456789abcdef")]
    public void Invalid_import_key_is_rejected(string value)
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { ImportApiKey = value }]
        };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Import_key_is_trimmed_like_the_daemon_trims_its_key_file()
    {
        var source = ValidSource() with { ImportApiKey = "0123456789abcdef0123456789abcdef\n" }; // gitleaks:allow -- synthetic test credential
        var options = ValidOptions() with { Sources = [source] };

        Assert.Equal("0123456789abcdef0123456789abcdef", source.ImportApiKey);
        Assert.True(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Analysis_settings_bind_from_the_documented_strings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Analysis:EngineBinaryPath"] = "/opt/perf-sentinel/perf-sentinel",
                ["Hub:Analysis:Timeout"] = "00:05:00",
                // A day is not "24:00:00": TimeSpan hours stop at 23, and the
                // README hands the operator this exact string to copy.
                ["Hub:Analysis:ReportRetention"] = "1.00:00:00",
                ["Hub:Sources:0:Id"] = "test",
                ["Hub:Sources:0:Name"] = "Test",
                ["Hub:Sources:0:Environment"] = "test",
                ["Hub:Sources:0:Kind"] = "jaeger_query",
                ["Hub:Sources:0:BaseUrl"] = "http://127.0.0.1:10428"
            })
            .Build();

        var options = configuration.GetSection(HubOptions.SectionName).Get<HubOptions>();

        Assert.NotNull(options);
        Assert.Equal("/opt/perf-sentinel/perf-sentinel", options.Analysis.EngineBinaryPath);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Analysis.Timeout);
        Assert.Equal(TimeSpan.FromHours(24), options.Analysis.ReportRetention);
        Assert.Equal(SourceKinds.JaegerQuery, options.Sources[0].Kind);
        Assert.True(new HubOptionsValidator().Validate(null, options with
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "hub.db")
        }).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Daemon")]
    [InlineData("victoria")]
    public void Unknown_source_kind_is_rejected(string kind)
    {
        var options = ValidOptions() with { Sources = [ValidSource() with { Kind = kind }] };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Only_a_daemon_can_carry_an_import_key()
    {
        // A trace backend never pushes: a key on one is a misconfiguration
        // that would otherwise sit there authorising an import path nothing
        // uses.
        var source = ValidSource() with
        {
            Kind = SourceKinds.Tempo,
            ImportApiKey = "0123456789abcdef0123456789abcdef" // gitleaks:allow -- synthetic test credential
        };

        Assert.False(new HubOptionsValidator().Validate(null, ValidOptions() with { Sources = [source] }).Succeeded);
    }

    [Fact]
    public void Invalid_analysis_options_are_rejected()
    {
        AnalysisOptions[] invalid =
        [
            new() { EngineBinaryPath = "relative/perf-sentinel" },
            new() { EngineBinaryPath = "  " },
            new() { Workers = 0 },
            new() { Workers = 17 },
            new() { MaxTracesCap = 0 },
            new() { Timeout = TimeSpan.Zero },
            new() { Timeout = TimeSpan.FromHours(2) },
            new() { ReportRetention = TimeSpan.Zero }
        ];

        Assert.All(invalid, analysis => Assert.False(
            new HubOptionsValidator().Validate(null, ValidOptions() with { Analysis = analysis }).Succeeded));
    }

    [Fact]
    public void Invalid_bound_configuration_stops_the_host()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                options.DatabasePath = Path.Combine(Path.GetTempPath(), $"hub-{Guid.NewGuid():N}.db");
                options.Sources = [new SourceOptions
                {
                    Id = "prod",
                    Name = "Production",
                    Environment = "prod",
                    BaseUrl = new Uri("file:///tmp/x")
                }];
            })));

        Assert.Throws<OptionsValidationException>(factory.CreateClient);
    }

    private static HubOptions ValidOptions() => new()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), "hub.db"),
        Sources = [ValidSource()]
    };

    private static SourceOptions ValidSource() => new()
    {
        Id = "prod",
        Name = "Production",
        Environment = "prod",
        BaseUrl = new Uri("https://daemon.example")
    };
}
