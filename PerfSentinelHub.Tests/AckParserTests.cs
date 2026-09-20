using System.Text;
using System.Text.Json;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests;

public sealed class AckParserTests
{
    private const string FixtureSignature =
        "n_plus_one_sql:order-svc:_api_v1_orders:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-acks-0.24.0.json");

    [Fact]
    public async Task The_capture_parses_into_one_ack_per_origin()
    {
        var payload = await File.ReadAllBytesAsync(FixturePath, TestContext.Current.CancellationToken);

        var page = AckParser.Parse(payload);

        Assert.Equal(0, page.RejectedCount);
        Assert.Equal(2, page.Acks.Count);
        var daemon = page.Acks[0];
        Assert.Equal(FixtureSignature, daemon.Signature);
        Assert.Equal("daemon", daemon.Origin);
        Assert.Equal("alice@example.com", daemon.By);
        Assert.Null(daemon.Reason);
        Assert.Equal("2026-09-20T11:28:03.924682Z", daemon.At);
        Assert.Equal("2027-08-01T00:00:00Z", daemon.ExpiresAt);
        Assert.Equal(DateTimeOffset.Parse("2027-08-01T00:00:00Z").ToUnixTimeMilliseconds(), daemon.ExpiresAtMs);
        var toml = page.Acks[1];
        Assert.Equal("toml", toml.Origin);
        Assert.Equal("permanent baseline", toml.Reason);
        // The baseline's own text, which need not be a timestamp.
        Assert.Equal("2026-05-04", toml.At);
        Assert.Null(toml.ExpiresAt);
        Assert.Null(toml.ExpiresAtMs);
    }

    [Theory]
    [InlineData("\"action\":\"ack\"", "\"action\":\"unack\"")]
    [InlineData("\"source\":\"daemon\"", "\"source\":\"hub\"")]
    [InlineData("\"source\":\"daemon\"", "\"origin\":\"daemon\"")]
    [InlineData("\"signature\":\"n_plus_one_sql", "\"signature\":\"\",\"was\":\"n_plus_one_sql")]
    [InlineData("\"signature\":\"n_plus_one_sql", "\"signature\":\"n_plus\\u0007one_sql")]
    [InlineData("\"by\":\"alice@example.com\"", "\"by\":7")]
    [InlineData("\"at\":\"2026-09-20T11:28:03.924682Z\"", "\"at\":null")]
    [InlineData("\"expires_at\":\"2027-08-01T00:00:00Z\"", "\"expires_at\":\"next summer\"")]
    [InlineData("\"expires_at\":\"2027-08-01T00:00:00Z\"", "\"expires_at\":20270801")]
    public async Task A_malformed_ack_is_rejected_and_counted(string original, string replacement)
    {
        var template = await TemplateAsync();
        var broken = template.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(template, broken);

        var page = AckParser.Parse(Encoding.UTF8.GetBytes($"[{template},{broken},7]"));

        Assert.Single(page.Acks);
        Assert.Equal(2, page.RejectedCount);
    }

    [Fact]
    public async Task An_expiry_with_the_nanoseconds_the_daemon_can_print_still_parses()
    {
        var template = await TemplateAsync();
        var precise = template.Replace("2027-08-01T00:00:00Z", "2027-08-01T00:00:00.123456789Z",
            StringComparison.Ordinal);

        var ack = Assert.Single(AckParser.Parse(Encoding.UTF8.GetBytes($"[{precise}]")).Acks);

        Assert.Equal("2027-08-01T00:00:00.123456789Z", ack.ExpiresAt);
        Assert.Equal(DateTimeOffset.Parse("2027-08-01T00:00:00.123Z").ToUnixTimeMilliseconds(), ack.ExpiresAtMs);
    }

    [Fact]
    public async Task A_signature_is_kept_up_to_its_bound_and_rejected_past_it()
    {
        var template = await TemplateAsync();
        // Length and control characters are all the Hub judges: the shape of
        // a signature is the daemon's rule.
        var atBound = template.Replace(FixtureSignature, new string('s', 1024), StringComparison.Ordinal);
        var pastBound = template.Replace(FixtureSignature, new string('s', 1025), StringComparison.Ordinal);

        var page = AckParser.Parse(Encoding.UTF8.GetBytes($"[{atBound},{pastBound}]"));

        Assert.Equal(1024, Assert.Single(page.Acks).Signature.Length);
        Assert.Equal(1, page.RejectedCount);
    }

    [Fact]
    public async Task The_free_text_of_an_ack_is_truncated_not_rejected()
    {
        var template = await TemplateAsync();
        var verbose = template
            .Replace("alice@example.com", new string('b', 300), StringComparison.Ordinal)
            .Replace("\"at\":\"2026-09-20T11:28:03.924682Z\"",
                $"\"at\":\"{new string('a', 80)}\",\"reason\":\"{new string('r', 1100)}\"", StringComparison.Ordinal);

        var ack = Assert.Single(AckParser.Parse(Encoding.UTF8.GetBytes($"[{verbose}]")).Acks);

        Assert.Equal(256, ack.By.Length);
        Assert.Equal(1024, ack.Reason!.Length);
        Assert.Equal(64, ack.At.Length);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void A_body_that_is_not_an_array_is_invalid(string body)
    {
        Assert.Throws<InvalidDataException>(() => AckParser.Parse(Encoding.UTF8.GetBytes(body)));
    }

    private static async Task<string> TemplateAsync()
    {
        using var fixture = JsonDocument.Parse(
            await File.ReadAllBytesAsync(FixturePath, TestContext.Current.CancellationToken));
        return fixture.RootElement[0].GetRawText();
    }
}
