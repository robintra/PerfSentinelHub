using System.Text;
using System.Text.Json;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests;

public sealed class IncidentParserTests
{
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-incidents-0.20.0.json");

    [Fact]
    public async Task The_capture_parses_into_columns_and_keeps_the_document_whole()
    {
        var payload = await File.ReadAllBytesAsync(FixturePath, TestContext.Current.CancellationToken);

        var page = IncidentParser.Parse(payload);

        Assert.Equal(0, page.RejectedCount);
        var incident = Assert.Single(page.Incidents);
        Assert.Equal("d650edad80ac5c2d99b8d1dde07100c2", incident.Id);
        Assert.Equal("shop-svc", incident.Service);
        Assert.Equal("oom_kill", incident.Kind);
        Assert.Null(incident.EndedAtMs);
        Assert.Equal(incident.AtMs - 300_000, incident.WindowFromMs);
        Assert.True(incident.WindowToMs > incident.AtMs);
        Assert.NotNull(incident.OldestFindingMs);
        Assert.Equal(2, incident.FindingCount);
        using var document = JsonDocument.Parse(incident.IncidentJson);
        Assert.Equal(2, document.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Theory]
    [InlineData("\"id\":\"d650edad80ac5c2d99b8d1dde07100c2\"", "\"id\":\"D650EDAD80AC5C2D99B8D1DDE07100C2\"")]
    [InlineData("\"id\":\"d650edad80ac5c2d99b8d1dde07100c2\"", "\"id\":\"d650edad\"")]
    [InlineData("\"service\":\"shop-svc\"", "\"service\":\"\"")]
    [InlineData("\"at_ms\":", "\"at_ms\":1,\"was_at_ms\":")]
    [InlineData("\"findings\":", "\"finding_rows\":")]
    public async Task A_malformed_incident_is_rejected_and_counted(string original, string replacement)
    {
        var template = await TemplateAsync();
        var broken = template.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(template, broken);
        var payload = Encoding.UTF8.GetBytes($"[{template},{broken}]");

        var page = IncidentParser.Parse(payload);

        Assert.Single(page.Incidents);
        Assert.Equal(1, page.RejectedCount);
    }

    [Fact]
    public async Task An_unknown_kind_folds_to_other()
    {
        var template = await TemplateAsync();
        var payload = Encoding.UTF8.GetBytes(
            $"[{template.Replace("\"kind\":\"oom_kill\"", "\"kind\":\"eclipse\"", StringComparison.Ordinal)}]");

        var incident = Assert.Single(IncidentParser.Parse(payload).Incidents);

        Assert.Equal("other", incident.Kind);
        Assert.Contains("other", IncidentParser.Kinds);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void A_body_that_is_not_an_array_is_invalid(string body)
    {
        Assert.Throws<InvalidDataException>(() => IncidentParser.Parse(Encoding.UTF8.GetBytes(body)));
    }

    private static async Task<string> TemplateAsync()
    {
        using var fixture = JsonDocument.Parse(
            await File.ReadAllBytesAsync(FixturePath, TestContext.Current.CancellationToken));
        return fixture.RootElement[0].GetRawText();
    }
}
