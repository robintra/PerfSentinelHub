using System.Net;
using System.Text.Json;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class FindingsApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private static readonly string[] FindingStatuses = ["active", "likely_resolved", "not_observed"];

    private readonly HttpClient _client = factory.CreateClient();

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-findings-0.11.2.json");

    [Fact]
    public async Task Findings_filters_and_preserves_opaque_fields_with_additive_metadata()
    {
        await SeedAsync();
        using var response = await _client.GetAsync(
            "/api/findings?service=rider-smoke&finding_type=blocking_wait&severity=critical&limit=1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken));
        var envelope = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("blocking_wait", envelope.GetProperty("finding").GetProperty("type").GetString());
        Assert.True(envelope.GetProperty("future_contract_field").GetProperty("preserve").GetBoolean());
        Assert.True(envelope.TryGetProperty("first_seen", out _));
        Assert.Equal(2, envelope.GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task Every_envelope_carries_a_derived_status()
    {
        await SeedAsync();
        using var response = await _client.GetAsync(
            "/api/findings?service=rider-smoke",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken));
        var envelope = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Contains(
            envelope.GetProperty("status").GetString(),
            FindingStatuses);
    }

    [Theory]
    [InlineData("/api/findings?limit=0")]
    [InlineData("/api/findings?limit=10001")]
    [InlineData("/api/findings?service=a&service=b")]
    [InlineData("/api/findings?service=%FF")]
    [InlineData("/api/findings?status=resolved")]
    public async Task Invalid_query_is_rejected(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task No_matching_findings_returns_an_empty_array()
    {
        await SeedAsync();
        using var response = await _client.GetAsync(
            "/api/findings?service=missing",
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Empty(document.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Acknowledged_findings_are_excluded_when_include_acked_is_false()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var acked = batch.Findings[0] with
        {
            Signature = "blocking_wait:acked:checkout",
            Service = "acked",
            TraceId = null,
            EnvelopeJson = batch.Findings[0].EnvelopeJson.Replace(
                "\"acknowledged_by\": null",
                "\"acknowledged_by\": \"robin\"",
                StringComparison.Ordinal)
        };
        await factory.Database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            new ParsedBatch([acked], 0),
            4000,
            cancellationToken);

        Assert.Equal(1, await CountAsync("/api/findings?service=acked"));
        Assert.Equal(1, await CountAsync("/api/findings?service=acked&include_acked=true"));
        Assert.Equal(0, await CountAsync("/api/findings?service=acked&include_acked=false"));
    }

    [Fact]
    public async Task Trace_lookup_returns_only_the_matching_envelope()
    {
        await SeedAsync();
        using var response = await _client.GetAsync(
            "/api/findings/rider-trace-file-line",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken));
        var envelope = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(
            "rider-trace-file-line",
            envelope.GetProperty("finding").GetProperty("trace_id").GetString());
    }

    private async Task<int> CountAsync(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken));
        return document.RootElement.GetArrayLength();
    }

    private async Task SeedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var payload = await File.ReadAllBytesAsync(FixturePath, cancellationToken);
        var batch = FindingParser.Parse(payload);
        await factory.Database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            batch,
            1000,
            cancellationToken);
        await factory.Database.UpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            batch,
            2000,
            cancellationToken);

        var original = batch.Findings[0];
        var other = original with
        {
            Signature = "slow_sql:other:query",
            Service = "other",
            FindingType = "slow_sql",
            TraceId = "other-trace",
            EnvelopeJson = original.EnvelopeJson
                .Replace("blocking_wait:rider-smoke:checkout:slow-path", "slow_sql:other:query",
                    StringComparison.Ordinal)
                .Replace("rider-trace-file-line", "other-trace", StringComparison.Ordinal)
                .Replace("\"type\": \"blocking_wait\"", "\"type\": \"slow_sql\"", StringComparison.Ordinal)
                .Replace("\"service\": \"rider-smoke\"", "\"service\": \"other\"", StringComparison.Ordinal)
        };
        await factory.Database.UpsertBatchAsync(
            new SourceSnapshot("production-a", "Production A", "production", "0.11.2"),
            new ParsedBatch([other], 0),
            3000,
            cancellationToken);
    }
}
