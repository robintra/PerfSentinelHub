using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Collection;

/// <summary>
/// Asks GitHub what the newest published release of each product is, so the
/// Hub can say that what it is running may no longer be current.
///
/// This is the Hub's only outbound destination that is not a configured source,
/// which is why it has its own configuration section, its own endpoints and a
/// single key that turns it off. Nothing here blocks or fails a request: a
/// version that was never read stays null, and the front end shows nothing
/// rather than guessing.
/// </summary>
public sealed partial class UpdateChecker(
    IHttpClientFactory clients,
    IOptions<HubOptions> options,
    ILogger<UpdateChecker> logger) : BackgroundService
{
    /// <summary>Its own named client, so this destination never borrows a source's.</summary>
    public const string ClientName = "update-check";

    // A release payload is small. The cap is here because the Hub never reads
    // an unbounded body from anywhere, not because GitHub is expected to send
    // one.
    private const int MaxBodyBytes = 256 * 1024;

    private readonly UpdateCheckOptions _settings = options.Value.UpdateCheck;

    /// <summary>The newest published engine release, or null until one is read.</summary>
    public string? LatestEngineVersion { get; private set; }

    /// <summary>The newest published Hub release, or null until one is read.</summary>
    public string? LatestHubVersion { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        using var timer = new PeriodicTimer(_settings.Interval);
        do
        {
            LatestEngineVersion = await ReadAsync(_settings.EngineEndpoint, "engine", stoppingToken)
                ?? LatestEngineVersion;
            LatestHubVersion = await ReadAsync(_settings.HubEndpoint, "hub", stoppingToken)
                ?? LatestHubVersion;
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The tag of the newest release, with its leading "v" removed, or null.
    /// Null covers every failure alike, including a repository that has never
    /// published a release, which answers 404 and is not a fault.
    /// </summary>
    private async Task<string?> ReadAsync(Uri endpoint, string what, CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(options.Value.HttpTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            // GitHub refuses a request with no user agent, and the version
            // pins what this client understands the body to be.
            request.Headers.UserAgent.ParseAdd("perf-sentinel-hub");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogUnavailable(logger, what, (int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var limited = new MemoryStream();
            await body.CopyToAsync(limited, timeout.Token);
            if (limited.Length > MaxBodyBytes)
            {
                LogUnavailable(logger, what, 0);
                return null;
            }

            limited.Position = 0;
            using var json = await JsonDocument.ParseAsync(limited, cancellationToken: timeout.Token);
            var tag = json.RootElement.TryGetProperty("tag_name", out var element) &&
                      element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
            var version = Normalize(tag);
            if (version is not null) LogRead(logger, what, version);
            return version;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                              or JsonException or IOException)
        {
            // Never louder than a warning. A Hub behind an egress firewall is a
            // supported deployment, not a broken one.
            LogFailed(logger, what, exception.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// A tag as a bare version, or null when it is not one. The same shape the
    /// launcher requires before it builds a release URL out of a version.
    /// </summary>
    public static string? Normalize(string? tag)
    {
        var trimmed = (tag ?? string.Empty).Trim();
        if (trimmed.StartsWith('v')) trimmed = trimmed[1..];
        if (trimmed.Length is 0 or > 64) return null;
        if (!char.IsAsciiDigit(trimmed[0])) return null;
        return trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-') ? trimmed : null;
    }

    [LoggerMessage(EventId = 1500, Level = LogLevel.Information,
        Message = "Update check is off, so the Hub reports no newer version for anything.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Debug,
        Message = "Newest published {What} release is {Version}.")]
    private static partial void LogRead(ILogger logger, string what, string version);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Debug,
        Message = "No published {What} release to compare against, the endpoint answered {Status}.")]
    private static partial void LogUnavailable(ILogger logger, string what, int status);

    [LoggerMessage(EventId = 1503, Level = LogLevel.Warning,
        Message = "Could not read the newest {What} release ({Reason}), so the Hub keeps reporting what it last knew.")]
    private static partial void LogFailed(ILogger logger, string what, string reason);
}
