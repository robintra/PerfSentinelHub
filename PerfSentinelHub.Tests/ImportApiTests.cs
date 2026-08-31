using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Tests;

public sealed class ImportApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential
    private readonly HttpClient _client = factory.CreateClient();


    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-findings-0.11.2.json");

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Import_requires_the_configured_source_secret(string? apiKey)
    {
        using var request = await RequestAsync(apiKey, 1);

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Import_commits_an_idempotent_batch_before_acknowledging_it()
    {
        using var first = await RequestAsync(ApiKey, 1);
        using var firstResponse = await _client.SendAsync(first, TestContext.Current.CancellationToken);
        using var second = await RequestAsync(ApiKey, 1);
        using var secondResponse = await _client.SendAsync(second, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using var responseBody = JsonDocument.Parse(await secondResponse.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(1, responseBody.RootElement.GetProperty("accepted").GetInt32());
        var rows = await factory.Database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 1000),
            TestContext.Current.CancellationToken);
        Assert.Single(rows, row => row.Signature == "blocking_wait:rider-smoke:checkout:slow-path");
    }

    [Fact]
    public async Task Import_rejects_more_than_one_hundred_findings()
    {
        using var request = await RequestAsync(ApiKey, 101);

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Import_commits_one_hundred_distinct_signatures_idempotently()
    {
        using var first = await RequestAsync(ApiKey, 100, "load-test");
        using var firstResponse = await _client.SendAsync(first, TestContext.Current.CancellationToken);
        using var second = await RequestAsync(ApiKey, 100, "load-test");
        using var secondResponse = await _client.SendAsync(second, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var rows = await factory.Database.QueryFindingsAsync(
            new FindingQuery(null, null, null, 1000),
            TestContext.Current.CancellationToken);
        Assert.Equal(100, rows.Count(row => row.Signature.StartsWith("load-test-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Import_gate_bounds_the_number_of_buffered_bodies()
    {
        var gate = factory.Services.GetRequiredService<ImportGate>();

        var entered = Enumerable.Range(0, ImportGate.MaxImports).Count(_ => gate.TryEnter());
        try
        {
            Assert.Equal(ImportGate.MaxImports, entered);
            Assert.False(gate.TryEnter());
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++)
                gate.Exit();
        }
    }

    [Fact]
    public async Task Busy_import_is_retryable_without_entering_storage()
    {
        var gate = factory.Services.GetRequiredService<ImportGate>();
        var entered = Enumerable.Range(0, ImportGate.MaxImports).Count(_ => gate.TryEnter());
        try
        {
            using var request = await RequestAsync(ApiKey, 1);
            using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture) ??
                              response.Headers.GetValues("Retry-After").Single());
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++)
                gate.Exit();
        }
    }

    [Fact]
    public async Task Import_does_not_clear_the_poll_unreachable_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await factory.Database.MarkSourceFailureAsync("test", 500, "network_error", cancellationToken);

        using var request = await RequestAsync(ApiKey, 1);
        using var response = await _client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var connection = await factory.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT unreachable_since_ms, last_error_code, producer_version
                              FROM source_state WHERE source_id = 'test';
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.False(reader.IsDBNull(0));
        Assert.Equal("network_error", reader.GetString(1));
        Assert.Equal("0.11.2", reader.GetString(2));
    }

    private static async Task<HttpRequestMessage> RequestAsync(
        string? apiKey,
        int count,
        string? signaturePrefix = null)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            FixturePath,
            TestContext.Current.CancellationToken));
        var finding = fixture.RootElement[0].GetRawText();
        var findings = Enumerable.Range(0, count).Select(index => signaturePrefix is null
            ? finding
            : finding.Replace(
                "blocking_wait:rider-smoke:checkout:slow-path",
                $"{signaturePrefix}-{index}",
                StringComparison.Ordinal));
        var payload = $$"""
                        {"producer_version":"0.11.2","findings":[{{string.Join(',', findings)}}]}
                        """;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/findings?source_id=test")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (apiKey is not null)
            request.Headers.Add("X-API-Key", apiKey);
        return request;
    }
}
