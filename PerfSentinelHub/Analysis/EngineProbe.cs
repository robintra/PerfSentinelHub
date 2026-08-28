using System.Diagnostics;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Analysis;

/// <summary>
/// Reads the version of the perf-sentinel binary the Hub runs, once, at
/// startup. A missing or unusable binary leaves the version null and the Hub
/// starts anyway: collection and the read API do not depend on it.
/// </summary>
public sealed partial class EngineProbe(
    IOptions<HubOptions> options,
    ILogger<EngineProbe> logger) : IHostedService
{
    // A `--version` line is a dozen bytes. Anything beyond this is not one,
    // and reading to the end would let a wrong binary size the buffer.
    private const int MaxOutputChars = 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly AnalysisOptions _analysis = options.Value.Analysis;

    /// <summary>Null until probed, and null forever if the probe failed.</summary>
    public string? Version { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_analysis.EngineBinaryPath is not { } path)
        {
            LogNoBinaryConfigured(logger);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            Version = ParseVersion(await RunVersionAsync(path, timeout.Token));
            if (Version is null)
                LogUnreadableVersion(logger, path);
            else
                LogEngineVersion(logger, Version, path);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogProbeTimedOut(logger, path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogProbeFailed(logger, exception, path);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<string?> RunVersionAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--version");

        using var process = Process.Start(startInfo);
        if (process is null)
            return null;

        var buffer = new char[MaxOutputChars];
        var read = await process.StandardOutput.ReadAsync(buffer, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return process.ExitCode == 0 ? new string(buffer, 0, read) : null;
    }

    // clap prints `perf-sentinel 0.16.0`. The version is the last token of the
    // first line. It must start with a digit: a pre-release carries letters
    // (0.16.0-rc.1) so letters alone cannot be the test, and without the
    // leading digit the last word of any sentence would pass as a version.
    private static string? ParseVersion(string? output)
    {
        var firstLine = output?.Split('\n', 2)[0].Trim();
        if (string.IsNullOrEmpty(firstLine))
            return null;

        var candidate = firstLine[(firstLine.LastIndexOf(' ') + 1)..];
        return candidate.Length is > 0 and <= 64 &&
               char.IsAsciiDigit(candidate[0]) &&
               candidate.All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+')
            ? candidate
            : null;
    }

    [LoggerMessage(1101, LogLevel.Information,
        "No Hub:Analysis:EngineBinaryPath configured, analysis runs are unavailable.")]
    private static partial void LogNoBinaryConfigured(ILogger logger);

    [LoggerMessage(1102, LogLevel.Information, "Engine version {Version} from {Path}.")]
    private static partial void LogEngineVersion(ILogger logger, string version, string path);

    [LoggerMessage(1103, LogLevel.Warning, "Engine binary {Path} did not report a usable version.")]
    private static partial void LogUnreadableVersion(ILogger logger, string path);

    [LoggerMessage(1104, LogLevel.Warning, "Engine binary {Path} did not answer within the probe timeout.")]
    private static partial void LogProbeTimedOut(ILogger logger, string path);

    [LoggerMessage(1105, LogLevel.Warning, "Engine binary {Path} could not be run.")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception, string path);
}
