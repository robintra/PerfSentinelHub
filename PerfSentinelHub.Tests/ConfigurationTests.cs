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
    [InlineData("prod", "file:///tmp/findings")]
    [InlineData("prod", "http://user:password@daemon:4318")]
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

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
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
