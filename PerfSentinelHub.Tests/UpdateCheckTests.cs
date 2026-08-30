using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("v0.17.0", "0.17.0")]
    [InlineData("0.17.0", "0.17.0")]
    [InlineData("v1.0.0-rc.1", "1.0.0-rc.1")]
    [InlineData("  v0.16.0  ", "0.16.0")]
    // A tag that is not a version at all, which the launcher would otherwise
    // interpolate into a release URL.
    [InlineData("nightly", null)]
    [InlineData("v", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("v0.1.0/../../etc", null)]
    [InlineData("v0.1.0 0.2.0", null)]
    public void A_tag_becomes_a_version_or_nothing(string? tag, string? expected) =>
        Assert.Equal(expected, UpdateChecker.Normalize(tag));

    [Fact]
    public void The_default_check_is_https_and_states_its_whole_destination()
    {
        var settings = new UpdateCheckOptions();

        Assert.True(settings.Enabled);
        Assert.Equal(TimeSpan.FromDays(1), settings.Interval);
        foreach (var endpoint in new[] { settings.EngineEndpoint, settings.HubEndpoint })
        {
            Assert.Equal(Uri.UriSchemeHttps, endpoint.Scheme);
            Assert.Empty(endpoint.Query);
            Assert.Empty(endpoint.UserInfo);
        }
    }

    [Fact]
    public void An_endpoint_that_hides_part_of_itself_is_rejected()
    {
        UpdateCheckOptions[] invalid =
        [
            new() { Interval = TimeSpan.FromMinutes(1) },
            new() { EngineEndpoint = new Uri("http://api.github.com/x") },
            // Userinfo without a password: the validator refuses any of it, and a
            // literal user:pass here is a credential shape every secret scanner
            // is right to stop on.
            new() { EngineEndpoint = new Uri("https://someone@api.github.com/x") },
            new() { HubEndpoint = new Uri("https://api.github.com/x?token=secret") },
            new() { HubEndpoint = new Uri("https://api.github.com/x#frag") }
        ];

        Assert.All(invalid, update => Assert.False(
            new HubOptionsValidator().Validate(null, ConfigurationTests.ValidOptions() with { UpdateCheck = update })
                .Succeeded));
    }

    [Theory]
    // The floor itself is allowed, one tick under it is not. Tested at the edge
    // because a comparison that slipped to <= would pass every other case.
    [InlineData(15, true)]
    [InlineData(14, false)]
    public void The_interval_floor_is_the_fifteen_minutes_the_readme_documents(int minutes, bool valid)
    {
        var options = ConfigurationTests.ValidOptions() with
        {
            UpdateCheck = new UpdateCheckOptions { Interval = TimeSpan.FromMinutes(minutes) }
        };

        Assert.Equal(valid, new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_disabled_check_stops_validating_what_it_will_never_read()
    {
        // Off, the endpoints are inert, so a deployment with no egress is not
        // forced to keep a URL it does not use in a valid shape.
        var options = ConfigurationTests.ValidOptions() with
        {
            UpdateCheck = new UpdateCheckOptions
            {
                Enabled = false,
                Interval = TimeSpan.FromSeconds(1),
                EngineEndpoint = new Uri("http://nowhere.invalid/x?a=b")
            }
        };

        Assert.True(new HubOptionsValidator().Validate(null, options).Succeeded);
    }
}
