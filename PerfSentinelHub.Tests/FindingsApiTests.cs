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
    private const long DayMs = 86_400_000;

    private static readonly string[] FindingStatuses = ["active", "likely_resolved", "not_observed"];

    private static readonly SourceSnapshot Production = new("production-a", "Production A", "production", "0.11.2");
    private static readonly SourceSnapshot Staging = new("staging-a", "Staging A", "staging", "0.11.2");

    // A second daemon of the environment, so a scope can hold more than one source.
    private static readonly SourceSnapshot StagingB = new("staging-b", "Staging B", "staging", "0.11.2");

    // A pair no other test mirrors acks for: the ack ledger is per source and outlives a test.
    private static readonly SourceSnapshot AckedProduction =
        new("acked-production-a", "Acked Production A", "acked-production", "0.24.0");

    private static readonly SourceSnapshot OpenStaging =
        new("open-staging-a", "Open Staging A", "open-staging", "0.24.0");

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
    [InlineData("/api/findings?from=x")]
    [InlineData("/api/findings?from=-1")]
    [InlineData("/api/findings?to=x")]
    [InlineData("/api/findings?from=2&to=1")]
    [InlineData("/api/findings?signature=a%01b")]
    [InlineData("/api/findings?signature=a%0Ab")]
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
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("signature")]
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

        // "b" and "c" share a last_seen, so only the signature orders them.
        var source = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await factory.Database.UpsertBatchAsync(
            source, new ParsedBatch([await VariantAsync("paged:d", "paged")], 0), 5000, cancellationToken);
        await factory.Database.UpsertBatchAsync(
            source,
            new ParsedBatch([await VariantAsync("paged:c", "paged"), await VariantAsync("paged:b", "paged")], 0),
            6000,
            cancellationToken);
        await factory.Database.UpsertBatchAsync(
            source, new ParsedBatch([await VariantAsync("paged:a", "paged")], 0), 7000, cancellationToken);

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
    public async Task From_and_to_bound_the_read_to_an_observation_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = new ParsedBatch([await VariantAsync("window:x", "window")], 0);
        await factory.Database.UpsertBatchAsync(Production, batch, 10 * DayMs + 1000, cancellationToken);

        const string path = "/api/findings?service=window";
        Assert.Equal(1, await CountAsync($"{path}&from={10 * DayMs}&to={11 * DayMs - 1}"));
        Assert.Equal(1, await CountAsync($"{path}&from={10 * DayMs}&to={10 * DayMs}"));
        // Either side stays open when its bound is absent.
        Assert.Equal(1, await CountAsync($"{path}&from={10 * DayMs}"));
        Assert.Equal(1, await CountAsync($"{path}&to={10 * DayMs}"));
        Assert.Equal(0, await CountAsync($"{path}&from={11 * DayMs}"));
        Assert.Equal(0, await CountAsync($"{path}&to={10 * DayMs - 1}"));
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
        await factory.Database.UpsertBatchAsync(Staging, new ParsedBatch([open], 0), 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(Production, new ParsedBatch([Acked(open)], 0), 5000, cancellationToken);
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        const string unacked = "/api/findings?service=ack-scope&include_acked=false";
        Assert.Equal(1, await CountAsync($"{unacked}&environment=staging", client));
        Assert.Equal(0, await CountAsync($"{unacked}&environment=production", client));
        // The fleet lists it through staging, whose own copy is not acked.
        Assert.Equal(1, await CountAsync(unacked, client));
    }

    [Fact]
    public async Task A_legacy_source_row_falls_back_to_the_shared_envelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = new ParsedBatch([await VariantAsync("legacy:x", "legacy")], 0);
        await factory.Database.UpsertBatchAsync(Staging, batch, 4000, cancellationToken);
        await ForgetOwnCopyAsync(Staging, "legacy:x");

        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        var envelope = Assert.Single(
            (await ListAsync("/api/findings?service=legacy&environment=staging&severity=critical", client))
            .EnumerateArray());
        Assert.Equal("critical 4000 4000 staging-a", Describe(envelope));
        Assert.True(envelope.GetProperty("future_contract_field").GetProperty("preserve").GetBoolean());
    }

    [Fact]
    public async Task A_legacy_source_row_is_judged_on_the_shared_envelope_in_any_scope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var open = await VariantAsync("legacy-ack:x", "legacy-ack");
        await factory.Database.UpsertBatchAsync(StagingB, new ParsedBatch([open], 0), 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(Staging, new ParsedBatch([Acked(open)], 0), 5000, cancellationToken);
        await factory.Database.UpsertBatchAsync(Production, new ParsedBatch([open], 0), 6000, cancellationToken);
        await ForgetOwnCopyAsync(StagingB, "legacy-ack:x");
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        // The shared copy is production's, un-acked. Staging B reads it from inside
        // its environment too, not the acked copy that is the freshest in that scope.
        const string unacked = "/api/findings?service=legacy-ack&include_acked=false";
        Assert.Equal(1, await CountAsync(unacked, client));
        Assert.Equal(1, await CountAsync($"{unacked}&environment=staging", client));
        Assert.Equal(0, await CountAsync($"{unacked}&source_id=staging-a", client));
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
    public async Task A_signature_filter_returns_that_finding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = new ParsedBatch(
            [
                await VariantAsync("by-signature:x", "by-signature"),
                await VariantAsync("by-signature:y", "by-signature")
            ],
            0);
        await factory.Database.UpsertBatchAsync(Production, batch, 4000, cancellationToken);

        Assert.Equal(["by-signature:y"], await SignaturesAsync("/api/findings?signature=by-signature%3Ay"));
        Assert.Empty(await SignaturesAsync("/api/findings?signature=by-signature%3Az"));
    }

    [Fact]
    public async Task A_signature_is_bounded_in_length()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Empty(await SignaturesAsync($"/api/findings?signature={new string('a', 1024)}"));

        using var response = await _client.GetAsync(
            $"/api/findings?signature={new string('a', 1025)}", cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_mirrored_ack_is_listed_per_source()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (first, second) = (Daemon("listed-a"), Daemon("listed-b"));
        var batch = new ParsedBatch([await VariantAsync("listed:x", "listed")], 0);
        await factory.Database.UpsertBatchAsync(second, batch, 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(first, batch, 4000, cancellationToken);
        await MirrorAsync(second, 5000, AckReadStates.Ok, Ack("listed:x") with { Origin = "toml", Reason = null });
        await MirrorAsync(
            first,
            5000,
            AckReadStates.Ok,
            Ack("listed:x") with { ExpiresAt = "2099-01-01T00:00:00Z", ExpiresAtMs = 4_070_908_800_000 });

        var envelope = Assert.Single((await ListAsync("/api/findings?service=listed")).EnumerateArray());

        // Ordered by source id, an absent reason or expiry left out.
        Assert.Equal(
            """[{"source_id":"listed-a","source":"daemon","by":"robin","reason":"known","at":"2026-09-20T10:00:00Z","expires_at":"2099-01-01T00:00:00Z"},""" +
            """{"source_id":"listed-b","source":"toml","by":"robin","at":"2026-09-20T10:00:00Z"}]""",
            envelope.GetProperty("acks").GetRawText());
        // The daemon's own field is relayed as it came.
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("acknowledged_by").ValueKind);
    }

    [Fact]
    public async Task An_expired_mirrored_ack_is_not_listed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Daemon("expired-a");
        var batch = new ParsedBatch([await VariantAsync("expired:x", "expired")], 0);
        await factory.Database.UpsertBatchAsync(source, batch, 4000, cancellationToken);
        // The host's clock reads 10 s, and an ack expiring now has expired.
        await MirrorAsync(
            source,
            5000,
            AckReadStates.Ok,
            Ack("expired:x") with { ExpiresAt = "1970-01-01T00:00:10Z", ExpiresAtMs = 10_000 });

        var envelope = Assert.Single(
            (await ListAsync("/api/findings?service=expired&include_acked=false")).EnumerateArray());
        Assert.False(envelope.TryGetProperty("acks", out _));
    }

    [Fact]
    public async Task An_envelope_cannot_forge_acks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Daemon("forged-a");
        var open = await VariantAsync("forged:x", "forged");
        var forged = open with
        {
            EnvelopeJson = open.EnvelopeJson.Replace(
                "\"acknowledged_by\": null",
                "\"acks\": [{\"source_id\": \"forged\"}], \"acknowledged_by\": null",
                StringComparison.Ordinal)
        };
        Assert.NotEqual(open.EnvelopeJson, forged.EnvelopeJson);
        await factory.Database.UpsertBatchAsync(source, new ParsedBatch([forged], 0), 4000, cancellationToken);

        var unmirrored = Assert.Single((await ListAsync("/api/findings?service=forged")).EnumerateArray());
        Assert.False(unmirrored.TryGetProperty("acks", out _));

        await MirrorAsync(source, 5000, AckReadStates.Ok, Ack("forged:x"));
        var mirrored = Assert.Single((await ListAsync("/api/findings?service=forged")).EnumerateArray());
        var acks = Assert.Single(mirrored.EnumerateObject(), property => property.NameEquals("acks")).Value;
        Assert.Equal("forged-a", Assert.Single(acks.EnumerateArray()).GetProperty("source_id").GetString());
    }

    [Fact]
    public async Task The_fresher_mirror_decides_include_acked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Daemon("mirror-a");
        var open = await VariantAsync("mirror:x", "mirror");
        await factory.Database.UpsertBatchAsync(source, new ParsedBatch([open], 0), 4000, cancellationToken);
        const string unacked = "/api/findings?service=mirror&include_acked=false";
        Assert.Equal(1, await CountAsync(unacked));

        // A relay refreshes the mirror at once, ahead of the envelope the next poll brings.
        await MirrorAsync(source, 5000, AckReadStates.Ok, Ack("mirror:x"));
        Assert.Equal(0, await CountAsync(unacked));

        // A revoke shows again, whatever the last envelope still says.
        await factory.Database.UpsertBatchAsync(source, new ParsedBatch([Acked(open)], 0), 6000, cancellationToken);
        await MirrorAsync(source, 6000, AckReadStates.Ok);
        Assert.Equal(1, await CountAsync(unacked));
    }

    [Fact]
    public async Task The_fresher_envelope_decides_include_acked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Daemon("envelope-a");
        await MirrorAsync(source, 4000, AckReadStates.Ok, Ack("envelope:x"));
        // A push after the last ack read: the mirror no longer says what the daemon holds.
        var batch = new ParsedBatch(
            [await VariantAsync("envelope:x", "envelope"), Acked(await VariantAsync("envelope:y", "envelope"))], 0);
        await factory.Database.UpsertBatchAsync(source, batch, 5000, cancellationToken);

        Assert.Equal(
            ["envelope:x"], await SignaturesAsync("/api/findings?service=envelope&include_acked=false"));
    }

    [Fact]
    public async Task A_source_without_an_ack_read_falls_back_to_the_envelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = new ParsedBatch(
            [await VariantAsync("unread:x", "unread"), Acked(await VariantAsync("unread:y", "unread"))], 0);
        await factory.Database.UpsertBatchAsync(Daemon("unread-a"), batch, 4000, cancellationToken);

        Assert.Equal(["unread:x"], await SignaturesAsync("/api/findings?service=unread&include_acked=false"));
    }

    [Fact]
    public async Task A_truncated_mirror_is_listed_but_does_not_decide()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Daemon("truncated-a");
        var batch = new ParsedBatch(
            [await VariantAsync("truncated:x", "truncated"), Acked(await VariantAsync("truncated:y", "truncated"))],
            0);
        await factory.Database.UpsertBatchAsync(source, batch, 4000, cancellationToken);
        // The listing lost its tail: what it holds is true, what it lacks proves nothing.
        await MirrorAsync(source, 5000, AckReadStates.Truncated, Ack("truncated:x"));

        var envelope = Assert.Single(
            (await ListAsync("/api/findings?service=truncated&include_acked=false")).EnumerateArray());
        Assert.Equal("truncated:x", envelope.GetProperty("finding").GetProperty("signature").GetString());
        Assert.Equal(1, envelope.GetProperty("acks").GetArrayLength());
    }

    [Fact]
    public async Task A_failed_ack_read_neither_lists_nor_decides()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Daemon("failed-a");
        var batch = new ParsedBatch([await VariantAsync("failed:x", "failed")], 0);
        await factory.Database.UpsertBatchAsync(source, batch, 4000, cancellationToken);
        await MirrorAsync(source, 5000, AckReadStates.Ok, Ack("failed:x"));
        // The rows of the last good read stay, and nothing says they still hold.
        await factory.Database.RecordAckReadAsync(
            source.SourceId, 6000, AckReadStates.Error, "timeout", cancellationToken);

        var envelope = Assert.Single(
            (await ListAsync("/api/findings?service=failed&include_acked=false")).EnumerateArray());
        Assert.False(envelope.TryGetProperty("acks", out _));
    }

    [Fact]
    public async Task A_finding_acked_in_one_source_stays_listed_through_the_other()
    {
        await SeedSplitAckAsync("split-fleet");

        var envelope = Assert.Single(
            (await ListAsync("/api/findings?service=split-fleet&include_acked=false")).EnumerateArray());
        var ack = Assert.Single(envelope.GetProperty("acks").EnumerateArray());
        Assert.Equal(AckedProduction.SourceId, ack.GetProperty("source_id").GetString());
    }

    [Theory]
    [InlineData("environment=open-staging", 1, 0)]
    [InlineData("source_id=open-staging-a", 1, 0)]
    [InlineData("environment=acked-production", 0, 1)]
    [InlineData("source_id=acked-production-a", 0, 1)]
    public async Task A_scope_judges_the_mirrored_acks_of_its_own_sources(string scope, int unacked, int acks)
    {
        await SeedSplitAckAsync("split-scope");
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        var path = $"/api/findings?service=split-scope&{scope}";
        Assert.Equal(unacked, await CountAsync($"{path}&include_acked=false", client));
        var envelope = Assert.Single((await ListAsync(path, client)).EnumerateArray());
        Assert.Equal(acks, envelope.TryGetProperty("acks", out var listed) ? listed.GetArrayLength() : 0);
    }

    [Fact]
    public async Task Each_source_carries_its_last_ack_read()
    {
        await MirrorAsync(OpenStaging, 7000, AckReadStates.Truncated);
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();

        var sources = await ListAsync("/api/sources", client);
        var read = sources.EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == OpenStaging.SourceId);
        Assert.Equal(AckReadStates.Truncated, read.GetProperty("acks_state").GetString());
        Assert.Equal(7000, read.GetProperty("acks_read_ms").GetInt64());
        // A source whose acks nobody has read says so with a null rather than the epoch.
        var never = sources.EnumerateArray().Single(source => source.GetProperty("id").GetString() == "test");
        Assert.Equal(JsonValueKind.Null, never.GetProperty("acks_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, never.GetProperty("acks_read_ms").ValueKind);
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
                    Configured(Staging),
                    Configured(StagingB),
                    Configured(AckedProduction),
                    Configured(OpenStaging)
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

    // A source of its own, so the ack ledger one test files never judges another's findings.
    private static SourceSnapshot Daemon(string id)
    {
        return new SourceSnapshot(id, id, "acks", "0.24.0");
    }

    private static ParsedAck Ack(string signature)
    {
        return new ParsedAck(signature, "daemon", "robin", "known", "2026-09-20T10:00:00Z", null, null);
    }

    // The envelope a daemon serves once the finding is acked there.
    private static ParsedFinding Acked(ParsedFinding finding)
    {
        return finding with
        {
            EnvelopeJson = finding.EnvelopeJson.Replace(
                "\"acknowledged_by\": null", "\"acknowledged_by\": \"robin\"", StringComparison.Ordinal)
        };
    }

    // The source's row as a version without the per-source columns left it.
    private async Task ForgetOwnCopyAsync(SourceSnapshot source, string signature)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await factory.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE finding_sources SET finding_json = NULL, severity = NULL
                              WHERE source_id = $source_id AND signature = $signature;
                              """;
        command.Parameters.AddWithValue("$source_id", source.SourceId);
        command.Parameters.AddWithValue("$signature", signature);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
    }

    // What an ack read of that source leaves: its whole mirror and the ledger row.
    private Task MirrorAsync(SourceSnapshot source, long readAtMs, string state, params ParsedAck[] acks)
    {
        return factory.Database.ReplaceSourceAcksAsync(
            source.SourceId, acks, state, readAtMs, TestContext.Current.CancellationToken);
    }

    // One finding both sources carry un-acked in their envelope, acked since at production alone.
    private async Task SeedSplitAckAsync(string service)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = new ParsedBatch([await VariantAsync($"{service}:x", service)], 0);
        await factory.Database.UpsertBatchAsync(AckedProduction, batch, 4000, cancellationToken);
        await factory.Database.UpsertBatchAsync(OpenStaging, batch, 4000, cancellationToken);
        await MirrorAsync(AckedProduction, 5000, AckReadStates.Ok, Ack($"{service}:x"));
        await MirrorAsync(OpenStaging, 5000, AckReadStates.Ok);
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
