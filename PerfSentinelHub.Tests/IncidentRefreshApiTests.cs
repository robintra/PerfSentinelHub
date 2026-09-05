using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     The incidents screen reading the fleet on demand. The poll is the floor
///     rather than the only path, so these cover what the live read adds: rows a
///     poll never fetched, the floor that keeps a reload loop off the daemons,
///     and one daemon's refusal costing the others nothing.
/// </summary>
public sealed class IncidentRefreshApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    // The factory's clock, fixed, and the floor a live read holds each source to.
    private const long NowMs = 10_000;

    [Fact]
    public async Task A_refresh_returns_rows_the_poll_never_fetched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(
            Serving(await IncidentAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "live-svc", cancellationToken)),
            cancellationToken);
        await using var scoped = Scoped(Daemon("live", daemon));
        using var client = scoped.CreateClient();

        // Nothing has ever been polled for this source.
        Assert.Empty(await ListAsync(client, "/api/incidents?source_id=live", HttpMethod.Get));

        var rows = await ListAsync(client, "/api/incidents/refresh?source_id=live", HttpMethod.Post);

        var incident = Assert.Single(rows);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", incident.GetProperty("id").GetString());
        Assert.Equal("live-svc", incident.GetProperty("service").GetString());
        // And the copy was stored, not only relayed.
        Assert.Single(await ListAsync(client, "/api/incidents?source_id=live", HttpMethod.Get));
    }

    [Fact]
    public async Task A_source_read_moments_ago_is_left_alone_and_its_stored_rows_still_answer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var requests = 0;
        await using var daemon = await FakeDaemon.StartAsync(
            async context =>
            {
                Interlocked.Increment(ref requests);
                await Serving(await IncidentAsync("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "fresh-svc", cancellationToken))(
                    context);
            },
            cancellationToken);
        // Stored five seconds ago on the factory's clock, inside the floor. The
        // upsert files the read as ok, which is what the floor reads.
        await StoreAsync("debounced", "cccccccccccccccccccccccccccccccc", "stored-svc", NowMs - 5_000);
        await using var scoped = Scoped(Daemon("debounced", daemon));
        using var client = scoped.CreateClient();

        var rows = await ListAsync(client, "/api/incidents/refresh?source_id=debounced", HttpMethod.Post);

        Assert.Equal(0, requests);
        var incident = Assert.Single(rows);
        Assert.Equal("cccccccccccccccccccccccccccccccc", incident.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "unauthorized", null)]
    [InlineData(StatusCodes.Status404NotFound, "absent", null)]
    [InlineData(StatusCodes.Status500InternalServerError, "error", "http_error")]
    public async Task A_refusing_daemon_keeps_its_own_state_while_the_others_refresh(
        int status,
        string expectedState,
        string? expectedErrorCode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var refusedId = $"refusing-{status}";
        var healthyId = $"healthy-{status}";
        await using var refusing = await FakeDaemon.StartAsync(
            context =>
            {
                context.Response.StatusCode = status;
                return Task.CompletedTask;
            },
            cancellationToken);
        await using var healthy = await FakeDaemon.StartAsync(
            Serving(await IncidentAsync($"dddddddddddddddddddddddddd{status:x6}", healthyId, cancellationToken)),
            cancellationToken);
        await using var scoped = Scoped(Daemon(refusedId, refusing), Daemon(healthyId, healthy));
        using var client = scoped.CreateClient();

        using var response = await client.PostAsync("/api/incidents/refresh", null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The refusal is filed in its own table.
        var read = Assert.Contains(
            refusedId,
            await factory.Database.QueryIncidentReadsAsync(cancellationToken));
        Assert.Equal(expectedState, read.State);
        Assert.Equal(expectedErrorCode, read.LastErrorCode);
        // And never in source_state, which feeds the finding status CASE.
        Assert.DoesNotContain(refusedId, await factory.Database.QuerySourceStatesAsync(cancellationToken));
        // The other daemon was read on the same pass.
        Assert.Single(await ListAsync(client, $"/api/incidents?source_id={healthyId}", HttpMethod.Get));
    }

    [Fact]
    public async Task A_full_gate_refuses_a_refresh_and_says_when_to_come_back()
    {
        // A refresh reads the whole fleet under the client's body cap, so the
        // cap is a memory contract the same way the daemon view's is.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scoped = Scoped();
        using var client = scoped.CreateClient();
        var gate = scoped.Services.GetRequiredService<IncidentRefreshGate>();

        var entered = Enumerable.Range(0, IncidentRefreshGate.MaxReads).Count(_ => gate.TryEnter());
        try
        {
            Assert.Equal(IncidentRefreshGate.MaxReads, entered);
            Assert.False(gate.TryEnter());

            using var refused = await client.PostAsync("/api/incidents/refresh", null, cancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("1", refused.Headers.RetryAfter?.ToString());
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++)
                gate.Exit();
        }
    }

    [Theory]
    [InlineData("/api/incidents/refresh?limit=0")]
    [InlineData("/api/incidents/refresh?limit=10001")]
    [InlineData("/api/incidents/refresh?offset=-1")]
    [InlineData("/api/incidents/refresh?offset=x")]
    [InlineData("/api/incidents/refresh?service=a&service=b")]
    [InlineData("/api/incidents/refresh?service=%FF")]
    [InlineData("/api/incidents/refresh?source_id=nope")]
    public async Task Invalid_query_is_rejected_the_way_the_listing_rejects_it(string path)
    {
        using var response = await factory.CreateClient()
            .PostAsync(path, null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_source_carries_when_its_copy_was_read()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var daemon = await FakeDaemon.StartAsync(
            Serving(await IncidentAsync("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "stamped-svc", cancellationToken)),
            cancellationToken);
        await using var scoped = Scoped(Daemon("stamped", daemon));
        using var client = scoped.CreateClient();
        using var refreshed = await client.PostAsync("/api/incidents/refresh", null, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var sources = await client.GetFromJsonAsync<JsonElement>("/api/sources", cancellationToken);
        var stamped = sources.EnumerateArray()
            .Single(source => source.GetProperty("id").GetString() == "stamped");

        Assert.Equal(IncidentReadStates.Ok, stamped.GetProperty("incidents_state").GetString());
        Assert.Equal(NowMs, stamped.GetProperty("incidents_read_ms").GetInt64());
        // A source nobody has read says so with a null rather than the epoch.
        var never = sources.EnumerateArray().Single(source => source.GetProperty("id").GetString() == "never");
        Assert.Equal(JsonValueKind.Null, never.GetProperty("incidents_read_ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, never.GetProperty("incidents_state").ValueKind);
    }

    /// <summary>The fixture's one incident, re-keyed so each test owns its rows.</summary>
    private static async Task<string> IncidentAsync(string id, string service, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-incidents-0.20.0.json");
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
        var incident = JsonNode.Parse(fixture.RootElement[0].GetRawText())!;
        incident["id"] = id;
        incident["service"] = service;
        return incident.ToJsonString();
    }

    // A single incident on a page of a hundred is a short page, so the reader
    // stops after one request and the count of them is the whole assertion.
    private static RequestDelegate Serving(string incident)
    {
        return async context =>
        {
            if (context.Request.Path != "/api/incidents")
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await context.Response.WriteAsync($"[{incident}]", context.RequestAborted);
        };
    }

    private async Task StoreAsync(string sourceId, string id, string service, long readAtMs)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var page = IncidentParser.Parse(
            Encoding.UTF8.GetBytes($"[{await IncidentAsync(id, service, cancellationToken)}]"));
        await factory.Database.UpsertIncidentsAsync(sourceId, page.Incidents, readAtMs, cancellationToken);
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client, string path, HttpMethod method)
    {
        using var request = new HttpRequestMessage(method, path);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        return [.. document.RootElement.EnumerateArray().Select(item => item.Clone())];
    }

    // The factory's own source is left in place: it points at a closed port, so
    // every pass also proves an unreachable daemon costs the readable ones
    // nothing. "never" is a trace backend, which is never read.
    private WebApplicationFactory<Program> Scoped(params SourceOptions[] daemons)
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
                options.Sources =
                [
                    .. options.Sources,
                    .. daemons,
                    new SourceOptions
                    {
                        Id = "never",
                        Name = "Never",
                        Environment = "test",
                        Kind = SourceKinds.Tempo,
                        BaseUrl = new Uri("http://127.0.0.1:2")
                    }
                ])));
    }

    private static SourceOptions Daemon(string id, FakeDaemon daemon)
    {
        return new SourceOptions
        {
            Id = id,
            Name = id,
            Environment = "test",
            Kind = SourceKinds.Daemon,
            BaseUrl = daemon.BaseUrl
        };
    }
}
