using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     An ack or a revoke taken on the Hub and written at one daemon. The Hub
///     holds that daemon's write key, so these cover who may spend it, what
///     leaves with it, and that a refusal never reaches the daemon.
/// </summary>
public sealed class AckRelayApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    // The factory's clock, fixed.
    private const long NowMs = 10_000;
    private const string AckKey = "ack-key"; // gitleaks:allow -- synthetic test credential
    private const string Signature = "n_plus_one_sql:order-svc:GET /api/orders/{id}:0123456789abcdef0123456789abcdef";
    private const string Reason = "known, fixed in 2.4";

    public static TheoryData<string> InvalidBodies =>
    [
        "not json",
        "[]",
        "{}",
        """{"signature":"","reason":"r"}""",
        """{"signature":7,"reason":"r"}""",
        $$"""{"signature":"{{new string('s', 1025)}}","reason":"r"}""",
        """{"signature":"a\u0007b","reason":"r"}""",
        // Either one would walk out of the daemon's ack path.
        """{"signature":".","reason":"r"}""",
        """{"signature":"..","reason":"r"}""",
        """{"signature":"known"}""",
        """{"signature":"known","reason":"   "}""",
        $$"""{"signature":"known","reason":"{{new string('r', 1025)}}"}""",
        """{"signature":"known","reason":"a\nb"}""",
        // Not a string .NET can hold, which the reader throws on.
        """{"signature":"known","reason":"\ud800"}""",
        """{"signature":"known","reason":"r","expires_at":7}""",
        """{"signature":"known","reason":"r","expires_at":"tomorrow"}""",
        // Five seconds after the epoch, behind the factory's clock.
        """{"signature":"known","reason":"r","expires_at":"1970-01-01T00:00:05Z"}"""
    ];

    [Fact]
    public async Task A_create_spends_the_ack_key_on_a_body_the_hub_rebuilt()
    {
        var body = new JsonObject
        {
            ["signature"] = Signature,
            ["reason"] = Reason,
            ["expires_at"] = "2027-01-01T01:00:00+01:00",
            ["by"] = "mallory"
        };

        var call = await RelayedAsync("create", body, "alice", false);

        Assert.Equal("POST", call.Method);
        Assert.Equal($"/api/findings/{Uri.EscapeDataString(Signature)}/ack", call.Target);
        // The read key goes under the same header name and must stay home.
        Assert.Equal(AckKey, call.ApiKey);
        Assert.Equal("application/json", call.ContentType);
        using var sent = JsonDocument.Parse(call.Body);
        // Three fields and no other: the by the client sent is not among them.
        Assert.Equal(3, sent.RootElement.EnumerateObject().Count());
        Assert.Equal("alice", sent.RootElement.GetProperty("by").GetString());
        Assert.Equal(Reason, sent.RootElement.GetProperty("reason").GetString());
        Assert.Equal("2027-01-01T00:00:00Z", sent.RootElement.GetProperty("expires_at").GetString());
    }

    [Fact]
    public async Task A_create_without_an_expiry_sends_none()
    {
        var call = await RelayedAsync("permanent", BodyOf(Signature), "alice", false);

        using var sent = JsonDocument.Parse(call.Body);
        Assert.False(sent.RootElement.TryGetProperty("expires_at", out _));
    }

    [Fact]
    public async Task A_signature_with_a_slash_stays_one_path_segment()
    {
        var call = await RelayedAsync("slash", BodyOf("a/b/../c"), "alice", false);

        Assert.Equal("/api/findings/a%2Fb%2F..%2Fc/ack", call.Target);
    }

    [Fact]
    public async Task A_revoke_sends_a_delete_in_the_name_of_the_caller()
    {
        // The documented payload: a revoke names the signature and gives no reason.
        var call = await RelayedAsync("revoke", new JsonObject { ["signature"] = Signature }, "alice", true);

        Assert.Equal("DELETE", call.Method);
        Assert.Equal($"/api/findings/{Uri.EscapeDataString(Signature)}/ack", call.Target);
        Assert.Equal(AckKey, call.ApiKey);
        Assert.Equal("alice", call.UserId);
        Assert.Empty(call.Body);
    }

    [Theory]
    [InlineData("Zoé", "Zo%C3%A9")]
    // A header value loses its outer spaces on the way, a JSON string does not.
    [InlineData("alice ", "alice%20")]
    public async Task An_identity_a_header_cannot_carry_travels_percent_encoded_on_both_routes(
        string identity,
        string expected)
    {
        var sourceId = $"encoded-{expected.Length}";
        var created = await RelayedAsync($"{sourceId}-create", BodyOf(Signature), identity, false);
        var revoked = await RelayedAsync($"{sourceId}-revoke", BodyOf(Signature), identity, true);

        using var sent = JsonDocument.Parse(created.Body);
        Assert.Equal(expected, sent.RootElement.GetProperty("by").GetString());
        Assert.Equal(expected, revoked.UserId);
    }

    [Fact]
    public async Task A_caller_nobody_names_is_refused_before_the_source_is_looked_at()
    {
        // An unknown source would be a 404: the 403 proves the identity is judged first.
        using var response = await RefusedAsync(
            "unnamed",
            Relay("no-such-source", BodyOf(Signature).ToJsonString(), null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertDetailAsync(response);
    }

    [Theory]
    [InlineData("no-such-source")]
    // A trace backend, then a daemon with no ack credential.
    [InlineData("never")]
    [InlineData("test")]
    public async Task A_source_that_relays_nothing_answers_one_and_the_same_404(string sourceId)
    {
        using var response = await RefusedAsync(
            $"norelay-{sourceId}",
            Relay(sourceId, BodyOf(Signature).ToJsonString()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            """{"detail":"This source relays no acknowledgments."}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    // A typed address or a bookmark, which is a value and not a missing header.
    [InlineData("none")]
    // The value is compared as it arrived, so a casing no browser sends is
    // refused too, and so is the folding a proxy may hand over.
    [InlineData("Same-Origin")]
    [InlineData("same-origin, same-origin")]
    public async Task A_site_header_that_is_not_same_origin_is_refused(string site)
    {
        var sourceId = $"foreign-{site.Length}";
        var request = Relay(sourceId, BodyOf(Signature).ToJsonString());
        request.Headers.Add("Sec-Fetch-Site", site);

        using var response = await RefusedAsync(sourceId, request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertDetailAsync(response);
    }

    [Fact]
    public async Task A_site_header_sent_twice_is_refused()
    {
        var request = Relay("doubled", BodyOf(Signature).ToJsonString());
        // Two values are not the one value that passes. The folded
        // "same-origin, same-origin" is refused by the theory above instead,
        // as the single value it arrives as.
        request.Headers.Add("Sec-Fetch-Site", ["same-origin", "same-origin"]);

        using var response = await RefusedAsync("doubled", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertDetailAsync(response);
    }

    [Theory]
    [InlineData("same-origin")]
    // No header at all, which is not the "none" refused above: curl, a CI job
    // and an IDE plugin do not set it, and refusing them would close the relay
    // to everything but a browser.
    [InlineData(null)]
    public async Task A_request_with_no_site_header_or_same_origin_is_relayed(string? site)
    {
        var sourceId = $"origin-{site ?? "absent"}";
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, StatusCodes.Status201Created);
        await using var scoped = Scoped(factory, Relayed(sourceId, daemon));
        await SeedAsync(factory.Database, sourceId, Signature);
        using var client = scoped.CreateClient();
        var request = Relay(sourceId, BodyOf(Signature).ToJsonString());
        if (site is not null)
            request.Headers.Add("Sec-Fetch-Site", site);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // No Sec-Fetch-Site here: the content type is what holds a caller that
    // sends none of it, and a form can post no other type than these three.
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data")]
    public async Task A_body_a_form_could_post_is_refused(string contentType)
    {
        var sourceId = $"media-{contentType.Length}";
        var request = Relay(sourceId, BodyOf(Signature).ToJsonString());
        request.Content = new StringContent(BodyOf(Signature).ToJsonString(), Encoding.UTF8, contentType);

        using var response = await RefusedAsync(sourceId, request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_refused()
    {
        var body = BodyOf(Signature);
        body["reason"] = new string('r', 8 * 1024);

        using var response = await RefusedAsync("oversized", Relay("oversized", body.ToJsonString()));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_body_is_refused(string body)
    {
        using var response = await RefusedAsync("invalid", Relay("invalid", body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertDetailAsync(response);
    }

    [Fact]
    public async Task A_revoke_asks_for_a_signature_and_nothing_else()
    {
        using var response = await RefusedAsync("revoke-invalid", Relay("revoke-invalid", "{}", revoke: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    // A finding this source never carried, then one it neither carried nor had an ack listed for.
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pair_the_hub_does_not_know_is_refused(bool revoke)
    {
        var sourceId = $"unknown-pair-{revoke}";
        // Known, but at another source, and as a finding only.
        await SeedAsync(factory.Database, $"{sourceId}-other", Signature);
        await SeedAsync(factory.Database, sourceId, "another:signature");

        using var response = await RefusedAsync(
            sourceId,
            Relay(sourceId, BodyOf(Signature).ToJsonString(), revoke: revoke));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertDetailAsync(response);
    }

    [Fact]
    public async Task An_ack_the_mirror_never_listed_is_still_revoked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, StatusCodes.Status201Created);
        await using var scoped = Scoped(factory, Relayed("unlisted", daemon));
        // Below 0.24.0 a daemon takes the write and its listing is never asked for.
        await SeedFindingAsync(factory.Database, "unlisted", Signature, "0.23.1");
        using var client = scoped.CreateClient();

        using var created = await client.SendAsync(
            Relay("unlisted", BodyOf(Signature).ToJsonString()),
            cancellationToken);
        using var revoked = await client.SendAsync(
            Relay("unlisted", BodyOf(Signature).ToJsonString(), revoke: true),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal("POST,DELETE", Methods(calls));
    }

    [Fact]
    public async Task An_ack_that_outlived_its_finding_is_still_revoked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, StatusCodes.Status204NoContent);
        await using var scoped = Scoped(factory, Relayed("outlived", daemon));
        // Retention removed the finding and the daemon still lists the ack.
        await SeedAckAsync(factory.Database, "outlived", Signature);
        using var client = scoped.CreateClient();

        using var revoked = await client.SendAsync(
            Relay("outlived", BodyOf(Signature).ToJsonString(), revoke: true),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal("DELETE", Methods(calls));
    }

    [Theory]
    [InlineData(false, StatusCodes.Status201Created, HttpStatusCode.NoContent, null)]
    [InlineData(false, StatusCodes.Status200OK, HttpStatusCode.NoContent, null)]
    [InlineData(true, StatusCodes.Status204NoContent, HttpStatusCode.NoContent, null)]
    [InlineData(false, StatusCodes.Status400BadRequest, HttpStatusCode.BadRequest, "refused the signature")]
    // Never a 401: the launcher would reload into a sign-in that cannot help.
    [InlineData(false, StatusCodes.Status401Unauthorized, HttpStatusCode.BadGateway, "ack credential")]
    [InlineData(true, StatusCodes.Status401Unauthorized, HttpStatusCode.BadGateway, "ack credential")]
    [InlineData(true, StatusCodes.Status404NotFound, HttpStatusCode.NotFound, "not acked at this daemon")]
    // The ack route itself is missing, which is not the caller's 404.
    [InlineData(false, StatusCodes.Status404NotFound, HttpStatusCode.BadGateway, "404")]
    [InlineData(false, StatusCodes.Status409Conflict, HttpStatusCode.Conflict, "CI baseline")]
    [InlineData(false, StatusCodes.Status503ServiceUnavailable, HttpStatusCode.ServiceUnavailable, "disabled")]
    [InlineData(false, StatusCodes.Status507InsufficientStorage, HttpStatusCode.InsufficientStorage, "full")]
    [InlineData(false, StatusCodes.Status500InternalServerError, HttpStatusCode.BadGateway, "500")]
    public async Task The_answer_of_the_daemon_is_translated(
        bool revoke,
        int daemonStatus,
        HttpStatusCode expected,
        string? expectedDetail)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sourceId = $"map-{revoke}-{daemonStatus}";
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, daemonStatus);
        await using var scoped = Scoped(factory, Relayed(sourceId, daemon));
        await SeedAsync(factory.Database, sourceId, Signature);
        using var client = scoped.CreateClient();

        using var response = await client.SendAsync(
            Relay(sourceId, BodyOf(Signature).ToJsonString(), revoke: revoke),
            cancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Single(calls, call => call.Method != "GET");
        if (expectedDetail is not null)
            Assert.Contains(expectedDetail, await AssertDetailAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_daemon_that_never_answers_is_a_504()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(
            context => Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted),
            cancellationToken);
        await using var scoped = Scoped(factory, true, TimeSpan.FromMilliseconds(50), Relayed("hung", daemon));
        await SeedAsync(factory.Database, "hung", Signature);
        using var client = scoped.CreateClient();

        using var response = await client.SendAsync(
            Relay("hung", BodyOf(Signature).ToJsonString()),
            cancellationToken);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await AssertDetailAsync(response);
    }

    [Fact]
    public async Task A_daemon_nothing_listens_at_is_a_502()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = new SourceOptions
        {
            Id = "closed",
            Name = "closed",
            Environment = "test",
            BaseUrl = new Uri("http://127.0.0.1:1"),
            AckHeaderName = "X-API-Key",
            AckHeaderValue = AckKey
        };
        await using var scoped = Scoped(factory, source);
        await SeedAsync(factory.Database, "closed", Signature);
        using var client = scoped.CreateClient();

        using var response = await client.SendAsync(
            Relay("closed", BodyOf(Signature).ToJsonString()),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await AssertDetailAsync(response);
    }

    [Fact]
    public async Task A_full_gate_refuses_a_relay_and_says_when_to_come_back()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, StatusCodes.Status201Created);
        await using var scoped = Scoped(factory, Relayed("gated", daemon));
        await SeedAsync(factory.Database, "gated", Signature);
        using var client = scoped.CreateClient();
        var gate = scoped.Services.GetRequiredService<AckRelayGate>();

        var entered = Enumerable.Range(0, AckRelayGate.MaxRelays).Count(_ => gate.TryEnter());
        try
        {
            Assert.Equal(AckRelayGate.MaxRelays, entered);

            using var refused = await client.SendAsync(
                Relay("gated", BodyOf(Signature).ToJsonString()),
                cancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("1", refused.Headers.RetryAfter?.ToString());
            Assert.Empty(calls);
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++)
                gate.Exit();
        }

        // The slot a relay takes is handed back, or the third one would starve.
        for (var relay = 0; relay <= AckRelayGate.MaxRelays; relay++)
        {
            using var accepted = await client.SendAsync(
                Relay("gated", BodyOf(Signature).ToJsonString()),
                cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }
    }

    [Fact]
    public async Task The_mirror_is_read_again_once_the_daemon_took_the_ack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var listing = new JsonArray(new JsonObject
        {
            ["action"] = "ack",
            ["source"] = "daemon",
            ["signature"] = Signature,
            ["by"] = "alice",
            ["reason"] = Reason,
            ["at"] = "2026-09-20T10:00:00Z"
        }).ToJsonString();
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, StatusCodes.Status201Created, listing);
        await using var scoped = Scoped(factory, Relayed("mirrored", daemon));
        // The finding alone: the read filed below can only be the relay's.
        await SeedFindingAsync(factory.Database, "mirrored", Signature, "0.24.0");
        using var client = scoped.CreateClient();

        using var response = await client.SendAsync(
            Relay("mirrored", BodyOf(Signature).ToJsonString()),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // The write first, then the listing, asked with the read key.
        Assert.Equal("POST,GET", Methods(calls));
        Assert.Equal("read-key", calls.Last().ApiKey);
        var read = Assert.Contains("mirrored", await factory.Database.QueryAckReadsAsync(cancellationToken));
        Assert.Equal(AckReadStates.Ok, read.State);
        Assert.Equal(NowMs, read.LastReadMs);
    }

    [Fact]
    public async Task A_source_never_polled_is_relayed_to_and_its_mirror_left_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, StatusCodes.Status201Created);
        await using var scoped = Scoped(factory, Relayed("pushed", daemon));
        // A push files the finding and no poll state, so no producer version.
        Assert.True(await factory.Database.TryUpsertBatchAsync(
            new SourceSnapshot("pushed", "pushed", "test", "0.24.0"),
            new ParsedBatch([Finding(Signature)], 0),
            NowMs,
            cancellationToken));
        using var client = scoped.CreateClient();

        using var response = await client.SendAsync(
            Relay("pushed", BodyOf(Signature).ToJsonString()),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("POST", Methods(calls));
    }

    [Fact]
    public async Task A_mirror_that_cannot_be_written_still_answers_204()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // A store of its own: the table this test drops is one every other test reads.
        await using var own = new HubApplicationFactory();
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, StatusCodes.Status201Created);
        await using var scoped = Scoped(own, Relayed("unwritable", daemon));
        await SeedAsync(own.Database, "unwritable", Signature);
        // Created first: a host that starts runs the schema and would bring the table back.
        using var client = scoped.CreateClient();
        await using (var connection = await own.Database.OpenConnectionAsync(cancellationToken))
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE ack_reads;";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        using var response = await client.SendAsync(
            Relay("unwritable", BodyOf(Signature).ToJsonString()),
            cancellationToken);

        // The daemon took the ack: an error here would invite a retry into a 409.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("POST,GET", Methods(calls));
    }

    // One whole relay the daemon accepts, and what reached that daemon as the write.
    private async Task<DaemonCall> RelayedAsync(string sourceId, JsonObject body, string identity, bool revoke)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(
            calls,
            revoke ? StatusCodes.Status204NoContent : StatusCodes.Status201Created);
        await using var scoped = Scoped(factory, Relayed(sourceId, daemon));
        await SeedAsync(factory.Database, sourceId, body["signature"]!.GetValue<string>());
        using var client = scoped.CreateClient();

        using var response = await client.SendAsync(
            Relay(sourceId, body.ToJsonString(), identity, revoke),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Assert.Single(calls, call => call.Method != "GET");
    }

    // A request the Hub must turn down on its own: the daemon behind the source
    // the test names sees nothing of it.
    private async Task<HttpResponseMessage> RefusedAsync(string relayedSourceId, HttpRequestMessage request)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = new ConcurrentQueue<DaemonCall>();
        await using var daemon = await DaemonAsync(calls, StatusCodes.Status201Created);
        await using var scoped = Scoped(factory, Relayed(relayedSourceId, daemon));
        using var client = scoped.CreateClient();

        var response = await client.SendAsync(request, cancellationToken);

        Assert.Empty(calls);
        return response;
    }

    // The launcher prints payload.detail, so every refusal it can show carries one.
    private static async Task<string> AssertDetailAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var detail = payload.RootElement.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrWhiteSpace(detail));
        return detail;
    }

    private static string Methods(IEnumerable<DaemonCall> calls)
    {
        return string.Join(',', calls.Select(call => call.Method));
    }

    private static JsonObject BodyOf(string signature)
    {
        return new JsonObject { ["signature"] = signature, ["reason"] = Reason };
    }

    private static ParsedFinding Finding(string signature)
    {
        return new ParsedFinding(
            signature, "{}", "relay-svc", "n_plus_one_sql", "warning", "GET /api/orders/{id}", "hash", null, "high", 3,
            null);
    }

    /// <summary>
    ///     What both routes ask of the store: the finding at that source, filed
    ///     by a poll so the producer version is known, and its ack in the mirror.
    /// </summary>
    internal static async Task SeedAsync(HubDatabase database, string sourceId, string signature)
    {
        await SeedFindingAsync(database, sourceId, signature, "0.24.0");
        await SeedAckAsync(database, sourceId, signature);
    }

    private static Task SeedFindingAsync(
        HubDatabase database,
        string sourceId,
        string signature,
        string producerVersion)
    {
        return database.UpsertBatchAsync(
            new SourceSnapshot(sourceId, sourceId, "test", producerVersion),
            new ParsedBatch([Finding(signature)], 0),
            NowMs,
            TestContext.Current.CancellationToken);
    }

    private static Task SeedAckAsync(HubDatabase database, string sourceId, string signature)
    {
        return database.ReplaceSourceAcksAsync(
            sourceId,
            [new ParsedAck(signature, "daemon", "robin", "known", "2026-09-20T10:00:00Z", null, null)],
            AckReadStates.Ok,
            NowMs,
            TestContext.Current.CancellationToken);
    }

    internal static HttpRequestMessage Relay(
        string sourceId,
        string body,
        string? identity = "alice",
        bool revoke = false)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/sources/{sourceId}/acks{(revoke ? "/revoke" : "")}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (identity is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-User", identity);
        return request;
    }

    /// <summary>
    ///     A daemon that files every request it gets and answers a write with
    ///     the status the test chose, and a listing with the acks it chose.
    /// </summary>
    internal static Task<FakeDaemon> DaemonAsync(
        ConcurrentQueue<DaemonCall> calls,
        int writeStatus,
        string listing = "[]")
    {
        return FakeDaemon.StartAsync(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            calls.Enqueue(new DaemonCall(
                context.Request.Method,
                context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget,
                context.Request.Headers["X-API-Key"].ToString(),
                context.Request.Headers["X-User-Id"].ToString(),
                context.Request.ContentType,
                await reader.ReadToEndAsync(context.RequestAborted)));
            if (context.Request.Method == HttpMethods.Get)
                await context.Response.WriteAsync(listing, context.RequestAborted);
            else
                context.Response.StatusCode = writeStatus;
        }, TestContext.Current.CancellationToken);
    }

    // The read key sits under the header name the ack key uses, the way a real
    // daemon is configured, so a test tells them apart by value alone.
    internal static SourceOptions Relayed(string id, FakeDaemon daemon)
    {
        return new SourceOptions
        {
            Id = id,
            Name = id,
            Environment = "test",
            BaseUrl = daemon.BaseUrl,
            AuthHeaderName = "X-API-Key",
            AuthHeaderValue = "read-key", // gitleaks:allow -- synthetic test credential
            AckHeaderName = "X-API-Key",
            AckHeaderValue = AckKey
        };
    }

    // The proxy header is trusted here: these tests run without Hub:Auth, and
    // AuthenticationTests owns what happens when nobody opted in.
    private static WebApplicationFactory<Program> Scoped(
        WebApplicationFactory<Program> host,
        params SourceOptions[] sources)
    {
        return Scoped(host, true, TimeSpan.FromSeconds(10), sources);
    }

    internal static WebApplicationFactory<Program> Scoped(
        WebApplicationFactory<Program> host,
        bool trustIdentityHeader,
        TimeSpan httpTimeout,
        params SourceOptions[] sources)
    {
        return host.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                options.HttpTimeout = httpTimeout;
                options.AckRelay = new AckRelayOptions { TrustIdentityHeader = trustIdentityHeader };
                options.Sources =
                [
                    .. options.Sources,
                    .. sources,
                    new SourceOptions
                    {
                        Id = "never",
                        Name = "Never",
                        Environment = "test",
                        Kind = SourceKinds.Tempo,
                        BaseUrl = new Uri("http://127.0.0.1:2")
                    }
                ];
            })));
    }

    internal sealed record DaemonCall(
        string Method,
        string Target,
        string ApiKey,
        string UserId,
        string? ContentType,
        string Body);
}
