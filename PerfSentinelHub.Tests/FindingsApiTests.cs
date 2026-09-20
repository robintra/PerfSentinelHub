using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class FindingsApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private static readonly string[] FindingStatuses = ["active", "likely_resolved", "not_observed"];

    private static readonly SourceSnapshot Production = new("production-a", "Production A", "production", "0.11.2");
    private static readonly SourceSnapshot Staging = new("staging-a", "Staging A", "staging", "0.11.2");

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
    [InlineData("/api/findings?offset=-1")]
    [InlineData("/api/findings?offset=x")]
    [InlineData("/api/findings?offset=1000001")]
    [InlineData("/api/findings?environment=nope")]
    [InlineData("/api/findings?source_id=nope")]
    public async Task Invalid_query_is_rejected(string path)
    {
        using var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Grafana sends a single space for its All choice, and a query string spells it %20 or +.
    [Theory]
    [InlineData("service")]
    [InlineData("finding_type")]
    [InlineData("severity")]
    [InlineData("status")]
    [InlineData("environment")]
    [InlineData("source_id")]
    public async Task A_blank_filter_reads_as_absent(string name)
    {
        await SeedAsync();
        var unfiltered = await BodyAsync("/api/findings");

        Assert.NotEqual("[]", unfiltered);
        Assert.Equal(unfiltered, await BodyAsync($"/api/findings?{name}="));
        Assert.Equal(unfiltered, await BodyAsync($"/api/findings?{name}=%20"));
        Assert.Equal(unfiltered, await BodyAsync($"/api/findings?{name}=+"));
    }

    [Fact]
    public async Task Offset_pages_without_overlap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(FixturePath, cancellationToken));
        var original = batch.Findings[0];
        ParsedFinding Paged(string signature) => original with
        {
            Signature = signature,
            Service = "paged",
            TraceId = null,
            EnvelopeJson = original.EnvelopeJson.Replace(
                "blocking_wait:rider-smoke:checkout:slow-path",
                signature,
                StringComparison.Ordinal)
        };

        // "b" and "c" share a last_seen, so only the signature orders them.
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await factory.Database.UpsertBatchAsync(
            source, new ParsedBatch([Paged("paged:d")], 0), 5000, cancellationToken);
        await factory.Database.UpsertBatchAsync(
            source, new ParsedBatch([Paged("paged:c"), Paged("paged:b")], 0), 6000, cancellationToken);
        await factory.Database.UpsertBatchAsync(
            source, new ParsedBatch([Paged("paged:a")], 0), 7000, cancellationToken);

        var whole = await SignaturesAsync("/api/findings?service=paged");
        var first = await SignaturesAsync("/api/findings?service=paged&limit=2&offset=0");
        var second = await SignaturesAsync("/api/findings?service=paged&limit=2&offset=2");

        string[] expected = ["paged:a", "paged:b", "paged:c", "paged:d"];
        Assert.Equal(expected, whole);
        Assert.Equal(2, first.Length);
        Assert.Equal(expected, first.Concat(second));
        Assert.Empty(await SignaturesAsync("/api/findings?service=paged&offset=4"));
    }

    [Fact]
    public async Task A_scoped_read_describes_the_environment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var critical = await VariantAsync("scoped:x", "scoped");
        var milder = critical with
        {
            Severity = "warning",
            Confidence = "daemon_staging",
            ConfidenceRank = 3,
            EnvelopeJson = critical.EnvelopeJson.Replace(
                "\"severity\": \"critical\"", "\"severity\": \"warning\"", StringComparison.Ordinal)
        };
        await factory.Database.UpsertBatchAsync(Production, new ParsedBatch([critical], 0), 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(Staging, new ParsedBatch([milder], 0), 6000, cancellationToken);
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        const string fleetPath = "/api/findings?service=scoped";
        const string productionPath = $"{fleetPath}&environment=production";

        // The fleet serves staging's fresher copy over the whole life of the finding.
        var fleet = Assert.Single((await ListAsync(fleetPath, client)).EnumerateArray());
        Assert.Equal("warning 4000 6000 production-a,staging-a", Describe(fleet));

        var production = await ListAsync(productionPath, client);
        Assert.Equal("critical 4000 4000 production-a", Describe(Assert.Single(production.EnumerateArray())));
        var staging = Assert.Single((await ListAsync($"{fleetPath}&environment=staging", client)).EnumerateArray());
        Assert.Equal("warning 6000 6000 staging-a", Describe(staging));
        // Fleet-wide on purpose: staging itself only ever reported daemon_staging.
        Assert.Equal("daemon_production", staging.GetProperty("max_confidence").GetString());

        // A daemon is a scope of one, and a filter judges the envelope the scope serves.
        Assert.Equal(
            production.GetRawText(),
            (await ListAsync($"{fleetPath}&source_id=production-a", client)).GetRawText());
        Assert.Equal(1, await CountAsync($"{productionPath}&severity=critical", client));
        Assert.Equal(0, await CountAsync($"{productionPath}&severity=warning", client));
        Assert.Equal(1, await CountAsync($"{fleetPath}&severity=warning", client));
    }

    [Fact]
    public async Task A_source_outside_the_environment_lists_nothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = new ParsedBatch([await VariantAsync("outside:x", "outside")], 0);
        await factory.Database.UpsertBatchAsync(Production, batch, 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(Staging, batch, 4000, cancellationToken);
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        const string staging = "/api/findings?service=outside&source_id=staging-a";
        Assert.Equal(1, await CountAsync($"{staging}&environment=staging", client));
        Assert.Equal(0, await CountAsync($"{staging}&environment=production", client));
    }

    [Fact]
    public async Task An_ack_in_one_environment_does_not_hide_the_finding_in_another()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var open = await VariantAsync("ack-scope:x", "ack-scope");
        var acked = open with
        {
            EnvelopeJson = open.EnvelopeJson.Replace(
                "\"acknowledged_by\": null", "\"acknowledged_by\": \"robin\"", StringComparison.Ordinal)
        };
        await factory.Database.UpsertBatchAsync(Staging, new ParsedBatch([open], 0), 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(Production, new ParsedBatch([acked], 0), 5000, cancellationToken);
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        const string unacked = "/api/findings?service=ack-scope&include_acked=false";
        Assert.Equal(1, await CountAsync($"{unacked}&environment=staging", client));
        Assert.Equal(0, await CountAsync($"{unacked}&environment=production", client));
        // The fleet judges the shared copy, which is production's fresher one.
        Assert.Equal(0, await CountAsync(unacked, client));
    }

    [Fact]
    public async Task A_legacy_source_row_falls_back_to_the_shared_envelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = new ParsedBatch([await VariantAsync("legacy:x", "legacy")], 0);
        await factory.Database.UpsertBatchAsync(Staging, batch, 4000, cancellationToken);
        await using (var connection = await factory.Database.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            // The row as a version without the per-source columns left it.
            command.CommandText = """
                                  UPDATE finding_sources SET finding_json = NULL, severity = NULL
                                  WHERE signature = 'legacy:x';
                                  """;
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        var envelope = Assert.Single(
            (await ListAsync("/api/findings?service=legacy&environment=staging&severity=critical", client))
            .EnumerateArray());
        Assert.Equal("critical 4000 4000 staging-a", Describe(envelope));
        Assert.True(envelope.GetProperty("future_contract_field").GetProperty("preserve").GetBoolean());
    }

    [Fact]
    public async Task The_scoped_status_is_the_environments_own()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = new ParsedBatch([await VariantAsync("scope-status:x", "scope-status")], 0);
        await factory.Database.UpsertBatchAsync(Staging, batch, 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(Production, batch, 9500, cancellationToken);
        // The host's clock reads 10 s, so a grace of one second leaves staging quiet.
        await using var scoped = Scoped(TimeSpan.FromSeconds(1));
        using var client = scoped.CreateClient();

        const string path = "/api/findings?service=scope-status";
        var fleet = Assert.Single((await ListAsync(path, client)).EnumerateArray());
        Assert.Equal("active", fleet.GetProperty("status").GetString());
        // Production heartbeats the endpoint and saw the finding, from outside the scope.
        var staging = Assert.Single((await ListAsync($"{path}&environment=staging", client)).EnumerateArray());
        Assert.Equal("not_observed", staging.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Scope_applies_before_the_page_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var older = new ParsedBatch([await VariantAsync("scope-page:staging", "scope-page")], 0);
        var fresher = new ParsedBatch([await VariantAsync("scope-page:production", "scope-page")], 0);
        await factory.Database.UpsertBatchAsync(Staging, older, 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(Production, fresher, 5000, cancellationToken);
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        // A scope applied after the limit would cut the page down to production's fresher row, then empty it.
        Assert.Equal(
            ["scope-page:staging"],
            await SignaturesAsync("/api/findings?service=scope-page&environment=staging&limit=1", client));
    }

    [Fact]
    public async Task Sources_carry_their_id()
    {
        await SeedAsync();
        var envelope = Assert.Single((await ListAsync("/api/findings?service=rider-smoke")).EnumerateArray());

        var sources = envelope.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(["production-a", "staging-a"], sources.Select(source => source.GetProperty("id").GetString()));
        Assert.All(sources, source => Assert.Equal("id", source.EnumerateObject().First().Name));
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

    // The severity the envelope states, the Hub's two clocks, and who reported it.
    private static string Describe(JsonElement envelope)
    {
        var sources = envelope.GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("id").GetString());
        return $"{envelope.GetProperty("finding").GetProperty("severity").GetString()} " +
               $"{envelope.GetProperty("first_seen").GetInt64()} {envelope.GetProperty("last_seen").GetInt64()} " +
               string.Join(',', sources);
    }

    // The ids the seeds write as, so an environment resolves to rows that exist.
    // The host shares the fixture's database file.
    private WebApplicationFactory<Program> Scoped(TimeSpan? grace = null)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                options.ResolutionGrace = grace ?? options.ResolutionGrace;
                options.Sources =
                [
                    .. options.Sources,
                    Configured(Production),
                    Configured(Staging)
                ];
            })));
    }

    private static SourceOptions Configured(SourceSnapshot source)
    {
        return new SourceOptions
        {
            Id = source.SourceId,
            Name = source.SourceName,
            Environment = source.Environment,
            BaseUrl = new Uri("http://127.0.0.1:1")
        };
    }

    // One finding under a service of its own, so a test reads only what it seeded.
    private static async Task<ParsedFinding> VariantAsync(string signature, string service)
    {
        var batch = FindingParser.Parse(
            await File.ReadAllBytesAsync(FixturePath, TestContext.Current.CancellationToken));
        var original = batch.Findings[0];
        return original with
        {
            Signature = signature,
            Service = service,
            TraceId = null,
            EnvelopeJson = original.EnvelopeJson.Replace(original.Signature, signature, StringComparison.Ordinal)
        };
    }

    private async Task<string> BodyAsync(string path, HttpClient? client = null)
    {
        using var response = await (client ?? _client).GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<JsonElement> ListAsync(string path, HttpClient? client = null)
    {
        using var document = JsonDocument.Parse(await BodyAsync(path, client));
        return document.RootElement.Clone();
    }

    private async Task<string[]> SignaturesAsync(string path, HttpClient? client = null)
    {
        using var document = JsonDocument.Parse(await BodyAsync(path, client));
        return
        [
            .. document.RootElement.EnumerateArray()
                .Select(envelope => envelope.GetProperty("finding").GetProperty("signature").GetString()!)
        ];
    }

    private async Task<int> CountAsync(string path, HttpClient? client = null)
    {
        return (await ListAsync(path, client)).GetArrayLength();
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
