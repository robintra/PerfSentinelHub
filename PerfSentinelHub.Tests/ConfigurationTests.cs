using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

public sealed class ConfigurationTests
{
    private const string AckSecret = "ack-secret"; // gitleaks:allow -- synthetic test credential

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
    [InlineData("", "http://daemon:4318", "prod")]
    [InlineData("bad/source", "http://daemon:4318", "prod")]
    [InlineData("prod", "file:///tmp/findings", "prod")]
    [InlineData("prod", "http://user@daemon:4318", "prod")]
    // An environment is a Prometheus label value and a member of the read API's closed set.
    [InlineData("prod", "http://daemon:4318", "pro\nd")]
    public void Invalid_source_is_rejected(string id, string url, string environment)
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { Id = id, BaseUrl = new Uri(url), Environment = environment }]
        };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_blank_name_does_not_hide_a_control_character_in_the_environment()
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { Name = "", Environment = "pro\nd" }]
        };

        // Both in one pass, so a bad source costs one restart and not two.
        string[] failures = [.. new HubOptionsValidator().Validate(null, options).Failures ?? []];
        Assert.Contains(failures, failure => failure.Contains("requires a name", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("control characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_global_options_are_rejected()
    {
        HubOptions[] invalid =
        [
            ValidOptions() with { Sources = [] },
            ValidOptions() with { Sources = [ValidSource() with { BaseUrl = null }] },
            ValidOptions() with { Sources = [ValidSource() with { BaseUrl = new Uri("https://daemon.example?a=b") }] },
            ValidOptions() with { Sources = [ValidSource() with { PublicUrl = new Uri("https://daemon.example#a") }] },
            ValidOptions() with { Sources = [ValidSource() with { PublicUrl = new Uri("ftp://daemon.example") }] },
            // A public header name with no public route has nothing to describe.
            ValidOptions() with { Sources = [ValidSource() with { PublicAuthHeaderName = "Authorization" }] },
            ValidOptions() with
            {
                Sources =
                [
                    ValidSource() with
                    {
                        PublicUrl = new Uri("https://public.example"), PublicAuthHeaderName = "Bad Header"
                    }
                ]
            },
            ValidOptions() with { Sources = [ValidSource(), ValidSource()] },
            ValidOptions() with { DatabasePath = "relative.db" },
            ValidOptions() with { PollInterval = TimeSpan.Zero },
            ValidOptions() with { HttpTimeout = TimeSpan.Zero },
            ValidOptions() with { Retention = TimeSpan.Zero },
            ValidOptions() with { MaxConcurrentPolls = 0 },
            ValidOptions() with { MaxConcurrentPolls = 33 },
            ValidOptions() with { DefaultReadLimit = 0 },
            ValidOptions() with { MaxReadLimit = 10_001 },
            ValidOptions() with { DefaultReadLimit = 101, MaxReadLimit = 100 },
            // A run row has to outlive the report it produced: an expired run
            // keeps its parameters so it can be relaunched as it stands.
            ValidOptions() with
            {
                Analysis = new AnalysisOptions
                {
                    ReportRetention = TimeSpan.FromDays(2),
                    RunRetention = TimeSpan.FromDays(1)
                }
            },
            ValidOptions() with
            {
                Analysis = new AnalysisOptions
                {
                    ReportRetention = TimeSpan.FromDays(1),
                    RunRetention = TimeSpan.FromDays(1)
                }
            }
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

    // One validator words both pairs, and the read pair's wording predates it.
    [Theory]
    [InlineData(null, "secret", "Source 'prod' must provide both auth header name and value.")]
    [InlineData("Authorization", "secret\nInjected", "Source 'prod' auth header contains a newline.")]
    [InlineData("Bad Header", "secret", "Source 'prod' auth header is invalid.")]
    public void The_read_credential_keeps_its_messages(string? name, string value, string message)
    {
        var source = ValidSource() with { AuthHeaderName = name, AuthHeaderValue = value };

        Assert.Equal(message, Assert.Single(Failures(source)));
    }

    [Theory]
    [InlineData("X-API-Key", null, "Source 'prod' must provide both ack header name and value.")]
    [InlineData(null, "ack-secret", "Source 'prod' must provide both ack header name and value.")]
    [InlineData("X-API-Key\r\nInjected", "ack-secret", "Source 'prod' ack header contains a newline.")]
    [InlineData("X-API-Key", "ack-secret\r\nInjected", "Source 'prod' ack header contains a newline.")]
    [InlineData("Bad Header", "ack-secret", "Source 'prod' ack header is invalid.")]
    public void Invalid_ack_header_is_rejected(string? name, string? value, string message)
    {
        var source = ValidSource() with { AckHeaderName = name, AckHeaderValue = value };

        Assert.Equal(message, Assert.Single(Failures(source)));
    }

    [Fact]
    public void Only_a_daemon_can_carry_an_ack_credential()
    {
        // A trace backend has no ack route: the credential would sit there for a
        // relay that can never use it.
        var source = AckSource() with { Kind = SourceKinds.Tempo };

        Assert.Equal(
            "Source 'prod' is not a daemon and cannot carry an ack credential.",
            Assert.Single(Failures(source)));
    }

    [Theory]
    [InlineData(AckSecret, AckSecret)]
    [InlineData("Bearer " + AckSecret, AckSecret)]
    [InlineData(AckSecret, "bearer  " + AckSecret)]
    public void An_ack_credential_differs_from_the_read_credential(string readValue, string ackValue)
    {
        // The daemon refuses a read key equal to its ack key, since every reader
        // could then write. It reads a key bare or behind a Bearer scheme.
        var source = AckSource() with
        {
            AuthHeaderName = "Authorization",
            AuthHeaderValue = readValue,
            AckHeaderValue = ackValue
        };

        Assert.Equal(
            "Source 'prod' ack credential must differ from its read credential.",
            Assert.Single(Failures(source)));
    }

    [Fact]
    public void A_daemon_can_carry_an_ack_credential_of_its_own()
    {
        Assert.Empty(Failures(AckSource()));
        Assert.Empty(Failures(AckSource() with { AuthHeaderName = "X-API-Key", AuthHeaderValue = "read-secret" }));
    }

    [Theory]
    [InlineData(false, false, true, 1)]
    [InlineData(true, false, true, 0)]
    [InlineData(false, true, true, 0)]
    [InlineData(false, false, false, 0)]
    public void An_ack_credential_is_warned_about_when_the_relay_can_identify_nobody(
        bool signsIn,
        bool trustsIdentityHeader,
        bool relays,
        int warnings)
    {
        var logger = new ListLogger<Program>();
        var options = ValidOptions() with
        {
            Auth = new AuthOptions { Enabled = signsIn },
            AckRelay = new AckRelayOptions { TrustIdentityHeader = trustsIdentityHeader },
            Sources = [relays ? AckSource() : ValidSource()]
        };

        HubAuthentication.WarnWhenAckRelayIdentifiesNobody(options, logger);

        Assert.Equal(warnings, logger.Messages.Count);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(AckSecret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("false", 1)]
    [InlineData("true", 0)]
    public async Task The_host_warns_at_startup_unless_the_identity_header_is_trusted(
        string trustIdentityHeader,
        int warnings)
    {
        var logger = new ListLogger<Program>();
        await using var hub = new HubApplicationFactory();
        await using var scoped = hub.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Hub:AckRelay:TrustIdentityHeader", trustIdentityHeader);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILogger<Program>>(logger);
                services.PostConfigure<HubOptions>(options => options.Sources = [AckSource()]);
            });
        });

        using var client = scoped.CreateClient();

        Assert.Equal(warnings, logger.Messages.Count(message =>
            message.Contains("ack credential", StringComparison.Ordinal)));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(AckSecret, StringComparison.Ordinal));
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
        var source = ValidSource() with
        {
            // gitleaks:allow has to sit on the line the scanner matched, and the
            // match starts here, not on the brace that closed the initialiser.
            ImportApiKey = "0123456789abcdef0123456789abcdef\n" // gitleaks:allow -- synthetic test credential
        };
        var options = ValidOptions() with { Sources = [source] };

        Assert.Equal("0123456789abcdef0123456789abcdef", source.ImportApiKey);
        Assert.True(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Enabled_authentication_requires_a_usable_provider()
    {
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions() with { Auth = ValidAuth() }).Succeeded);

        AuthOptions[] invalid =
        [
            ValidAuth() with { ClientId = "" },
            ValidAuth() with { ClientSecret = null },
            ValidAuth() with { IdentityClaim = " " },
            ValidAuth() with { AuthorizationEndpoint = null },
            ValidAuth() with { TokenEndpoint = new Uri("/token", UriKind.Relative) },
            // Plain http carries the code and the access token in clear, loopback excepted.
            ValidAuth() with { UserInformationEndpoint = new Uri("http://idp.example/userinfo") },
            ValidAuth() with { TokenEndpoint = new Uri("https://user:pw@idp.example/token") }
        ];

        Assert.All(invalid, auth => Assert.False(
            new HubOptionsValidator().Validate(null, ValidOptions() with { Auth = auth }).Succeeded));
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions() with
        {
            Auth = ValidAuth() with { TokenEndpoint = new Uri("http://127.0.0.1:8081/token") }
        }).Succeeded);
        // Off, nothing about the provider is read, so nothing is required.
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions() with
        {
            Auth = new AuthOptions { Enabled = false }
        }).Succeeded);
    }

    private static AuthOptions ValidAuth()
    {
        return new AuthOptions
        {
            Enabled = true,
            AuthorizationEndpoint = new Uri("https://idp.example/authorize"),
            TokenEndpoint = new Uri("https://idp.example/token"),
            UserInformationEndpoint = new Uri("https://idp.example/userinfo"),
            ClientId = "hub",
            ClientSecret = "secret" // gitleaks:allow -- synthetic test credential
        };
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
    public void Trace_retention_belongs_to_a_backend_and_stays_in_range()
    {
        SourceOptions[] invalid =
        [
            // A daemon takes no window, so nothing would read the value.
            ValidSource() with { RetentionHours = 24 },
            ValidSource() with { Kind = SourceKinds.Tempo, RetentionHours = 0 },
            ValidSource() with { Kind = SourceKinds.Tempo, RetentionHours = 87_601 }
        ];

        Assert.All(invalid, source => Assert.False(
            new HubOptionsValidator().Validate(null, ValidOptions() with { Sources = [source] }).Succeeded));
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions() with
        {
            Sources = [ValidSource() with { Kind = SourceKinds.Tempo, RetentionHours = 24 }]
        }).Succeeded);
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
            // Above the engine's own ceiling: accepted here, this only moved the
            // failure to argument parsing, after the operator had been shown the
            // number and the command carrying it.
            new() { MaxTracesCap = AnalysisOptions.EngineMaxTraces + 1 },
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
                options.Sources =
                [
                    new SourceOptions
                    {
                        Id = "prod",
                        Name = "Production",
                        Environment = "prod",
                        BaseUrl = new Uri("file:///tmp/x")
                    }
                ];
            })));

        Assert.Throws<OptionsValidationException>(factory.CreateClient);
    }

    internal static HubOptions ValidOptions()
    {
        return new HubOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "hub.db"),
            Sources = [ValidSource()]
        };
    }

    private static SourceOptions ValidSource()
    {
        return new SourceOptions
        {
            Id = "prod",
            Name = "Production",
            Environment = "prod",
            BaseUrl = new Uri("https://daemon.example")
        };
    }

    private static SourceOptions AckSource()
    {
        return ValidSource() with { AckHeaderName = "X-API-Key", AckHeaderValue = AckSecret };
    }

    private static string[] Failures(SourceOptions source)
    {
        return [.. new HubOptionsValidator().Validate(null, ValidOptions() with { Sources = [source] }).Failures ?? []];
    }
}
