using System.Net;
using System.Text;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     Freezes the read with no environment and no source scope, and the trace
///     lookup that shares its query and its writer, so a change to the scoped
///     path proves byte for byte that it left them alone. The fixed clock keeps
///     every seeded row active, FindingIngestionTests pins the other two
///     statuses on the same query.
/// </summary>
public sealed class FindingsCompatibilityTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task The_unscoped_response_is_pinned_byte_for_byte()
    {
        await SeedAsync();
        await AssertMatchesGoldenAsync("/api/findings", "hub-findings-unscoped.json");
    }

    [Fact]
    public async Task The_trace_lookup_response_is_pinned_byte_for_byte()
    {
        await SeedAsync();
        await AssertMatchesGoldenAsync("/api/findings/rider-trace-file-line", "hub-findings-by-trace.json");
    }

    private async Task AssertMatchesGoldenAsync(string path, string goldenFile)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await _client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var golden = await File.ReadAllBytesAsync(FixturePath(goldenFile), cancellationToken);
        // The golden file ends with the one LF .editorconfig asks for, the response carries none.
        Assert.Equal((byte)'\n', golden[^1]);
        var expected = golden[..^1];

        // The text comparison only buys a readable failure, the bytes are the contract.
        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(actual));
        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     Two sources on one finding, the second one unreachable, and a template
    ///     mutation so the lineage object shows. Replaying it leaves the same rows,
    ///     so each test seeds for itself.
    /// </summary>
    private async Task SeedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var batch = FindingParser.Parse(await File.ReadAllBytesAsync(
            FixturePath("daemon-findings-0.11.2.json"), cancellationToken));
        var production = new SourceSnapshot("production-a", "Production A", "production", "0.11.2");
        await factory.Database.UpsertBatchAsync(production, batch, 1000, cancellationToken);
        await factory.Database.UpsertBatchAsync(
            new SourceSnapshot("staging-a", "Staging A", "staging", "0.11.2"),
            batch,
            2000,
            cancellationToken);

        var original = batch.Findings[0];
        var successor = original with
        {
            Signature = "blocking_wait:rider-smoke:checkout:mutated-path",
            TemplateHash = "mutated-template-hash",
            TraceId = "rider-trace-mutated",
            EnvelopeJson = original.EnvelopeJson
                .Replace(original.Signature, "blocking_wait:rider-smoke:checkout:mutated-path",
                    StringComparison.Ordinal)
                .Replace("rider-trace-file-line", "rider-trace-mutated", StringComparison.Ordinal)
        };
        await factory.Database.UpsertBatchAsync(
            production, new ParsedBatch([successor], 0), 3000, cancellationToken);

        await factory.Database.MarkSourceFailureAsync("staging-a", 5000, "timeout", cancellationToken);
    }

    private static string FixturePath(string file)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", file);
    }
}
