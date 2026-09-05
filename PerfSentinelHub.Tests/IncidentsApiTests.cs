using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests;

public sealed class IncidentsApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string FixtureId = "d650edad80ac5c2d99b8d1dde07100c2";
    private const string EmptyId = "0000000000000000000000000000000e";
    private const string CompleteId = "0000000000000000000000000000000c";
    private const string EndedId = "0000000000000000000000000000000d";
    // The fixture's at_ms plus 45 s.
    private const long EndedAtMs = 1_788_607_484_029;

    private readonly HttpClient _client = factory.CreateClient();

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-incidents-0.20.0.json");

    [Fact]
    public async Task The_listing_carries_the_hub_fields_and_no_findings()
    {
        await SeedAsync();
        var incident = Assert.Single(
            (await ListAsync("/api/incidents?service=shop-svc")).EnumerateArray());

        Assert.Equal(FixtureId, incident.GetProperty("id").GetString());
        Assert.Equal("oom_kill", incident.GetProperty("kind").GetString());
        Assert.Equal("container memory limit reached", incident.GetProperty("detail").GetString());
        Assert.False(incident.TryGetProperty("findings", out _));
        Assert.Equal(JsonValueKind.Null, incident.GetProperty("ended_at_ms").ValueKind);
        Assert.Equal("test", incident.GetProperty("source_id").GetString());
        Assert.Equal("Test", incident.GetProperty("source_name").GetString());
        Assert.Equal("test", incident.GetProperty("environment").GetString());
        Assert.Equal(5000, incident.GetProperty("first_seen").GetInt64());
        Assert.Equal(5000, incident.GetProperty("last_seen").GetInt64());
        Assert.Equal(2, incident.GetProperty("finding_count").GetInt32());
        // The capture's oldest finding sits inside the window, so the ring no
        // longer reached back to the window's start.
        Assert.Equal("partial", incident.GetProperty("capture").GetString());
    }

    [Fact]
    public async Task One_incident_carries_its_findings()
    {
        await SeedAsync();
        using var response = await _client.GetAsync($"/api/incidents/{FixtureId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        var incident = document.RootElement;
        Assert.Equal(2, incident.GetProperty("findings").GetArrayLength());
        Assert.Equal(
            "n_plus_one_sql",
            incident.GetProperty("findings")[0].GetProperty("finding").GetProperty("type").GetString());
        Assert.Equal("partial", incident.GetProperty("capture").GetString());
        Assert.Equal("Test", incident.GetProperty("source_name").GetString());
    }

    [Fact]
    public async Task Capture_reads_the_oldest_finding_against_the_window()
    {
        await SeedAsync();
        var captures = (await ListAsync("/api/incidents?source_id=test")).EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, item => item.GetProperty("capture").GetString());

        Assert.Equal("partial", captures[FixtureId]);
        Assert.Equal("empty", captures[EmptyId]);
        Assert.Equal("complete", captures[CompleteId]);
    }

    [Fact]
    public async Task Filters_and_paging_follow_the_query()
    {
        await SeedAsync();
        var all = (await ListAsync("/api/incidents")).EnumerateArray().Select(Id).ToArray();
        // Newest first, the daemon's own order.
        Assert.Equal([CompleteId, EmptyId, FixtureId, EndedId], all);
        Assert.Equal([EmptyId], (await ListAsync("/api/incidents?service=other-svc")).EnumerateArray().Select(Id));
        Assert.Equal([EmptyId], (await ListAsync("/api/incidents?limit=1&offset=1")).EnumerateArray().Select(Id));
        Assert.Empty((await ListAsync("/api/incidents?service=missing")).EnumerateArray());
    }

    [Theory]
    [InlineData("/api/incidents?limit=0")]
    [InlineData("/api/incidents?limit=10001")]
    [InlineData("/api/incidents?offset=-1")]
    [InlineData("/api/incidents?offset=x")]
    [InlineData("/api/incidents?service=a&service=b")]
    [InlineData("/api/incidents?service=%FF")]
    [InlineData("/api/incidents?source_id=nope")]
    public async Task Invalid_query_is_rejected(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_incident_is_not_found()
    {
        using var response = await _client.GetAsync(
            "/api/incidents/ffffffffffffffffffffffffffffffff",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null, 100L, "empty")]
    [InlineData(100L, 100L, "complete")]
    [InlineData(99L, 100L, "complete")]
    [InlineData(101L, 100L, "partial")]
    public void Capture_is_the_daemon_reading_of_oldest_finding_ms(long? oldest, long windowFrom, string expected)
    {
        Assert.Equal(expected, IncidentWriter.Capture(oldest, windowFrom));
    }

    [Fact]
    public async Task An_end_kept_in_the_column_alone_is_still_written()
    {
        await SeedAsync();
        using var response = await _client.GetAsync($"/api/incidents/{EndedId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        var incident = document.RootElement;
        // The richer document was kept, findings and all, and the end the
        // poorer re-capture carried is read from the column, not from it.
        Assert.Equal(2, incident.GetProperty("findings").GetArrayLength());
        Assert.Equal(EndedAtMs, incident.GetProperty("ended_at_ms").GetInt64());
    }

    private static string Id(JsonElement incident)
    {
        return incident.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ListAsync(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        return document.RootElement.Clone();
    }

    // The capture plus three variants of it: one from an empty ring, one whose
    // ring still reached past the window's start, one whose end arrived later.
    private async Task SeedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var template = fixture.RootElement[0].GetRawText();
        var atMs = fixture.RootElement[0].GetProperty("at_ms").GetInt64();

        var empty = JsonNode.Parse(template)!;
        empty["id"] = EmptyId;
        empty["service"] = "other-svc";
        empty["kind"] = "deploy";
        empty["at_ms"] = atMs + 1000;
        empty["findings"] = new JsonArray();
        empty.AsObject().Remove("oldest_finding_ms");

        var complete = JsonNode.Parse(template)!;
        complete["id"] = CompleteId;
        complete["service"] = "complete-svc";
        complete["at_ms"] = atMs + 2000;
        complete["oldest_finding_ms"] = complete["window_from_ms"]!.GetValue<long>() - 1;

        // And one whose end arrived on a poorer re-capture: the column takes
        // the end, the richer document is kept without it.
        var ended = JsonNode.Parse(template)!;
        ended["id"] = EndedId;
        ended["service"] = "ended-svc";
        ended["at_ms"] = atMs - 1000;
        var recapture = JsonNode.Parse(ended.ToJsonString())!;
        recapture["findings"] = new JsonArray();
        recapture.AsObject().Remove("oldest_finding_ms");
        recapture["ended_at_ms"] = EndedAtMs;

        await UpsertAsync(
            $"[{template},{empty.ToJsonString()},{complete.ToJsonString()},{ended.ToJsonString()}]",
            cancellationToken);
        await UpsertAsync($"[{recapture.ToJsonString()}]", cancellationToken);
    }

    private async Task UpsertAsync(string payload, CancellationToken cancellationToken)
    {
        var page = IncidentParser.Parse(System.Text.Encoding.UTF8.GetBytes(payload));
        Assert.Equal(0, page.RejectedCount);
        await factory.Database.UpsertIncidentsAsync("test", page.Incidents, 5000, cancellationToken);
    }
}
