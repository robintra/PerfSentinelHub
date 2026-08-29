using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Analysis;

/// <summary>
/// The bounded vocabulary a run may report. Raw stderr never leaves the
/// process, so this is all the operator gets, and every code needs one
/// actionable sentence in the interface.
/// </summary>
public static class AnalysisErrorCodes
{
    public const string SourceUnreachable = "source_unreachable";
    public const string SourceAuthFailed = "source_auth_failed";
    public const string SourceRejectedRequest = "source_rejected_request";
    public const string Timeout = "timeout";
    public const string OutputTooLarge = "output_too_large";
    public const string BinaryFailed = "binary_failed";
    public const string InvalidRequest = "invalid_request";
    public const string Internal = "internal";
}

public sealed record RunOutcome(
    string Status,
    string? ErrorCode,
    string? ProducerVersion,
    ReportSummary? Summary);

/// <summary>
/// Executes one run: obtain the engine's report JSON, then render it to a
/// self-contained HTML file. Two steps, because the query subcommands emit
/// text, JSON or SARIF and only `report` writes HTML.
/// </summary>
public sealed partial class AnalysisRunner(
    DaemonClient daemonClient,
    EngineProbe engine,
    IOptions<HubOptions> options,
    ILogger<AnalysisRunner> logger)
{
    // The intermediate report JSON. Well past any real run, small enough that
    // a runaway engine cannot exhaust the container's memory.
    private const long MaxReportJsonBytes = 256L * 1024 * 1024;
    private static readonly string[] ScratchFilePatterns = ["*.input.json", "*.config.toml"];

    private readonly AnalysisOptions _analysis = options.Value.Analysis;

    public string ReportPath(string runId) =>
        Path.Combine(_analysis.ReportDirectory, $"{runId}.html");

    /// <summary>Removes a report whose lifetime ran out. Missing is fine.</summary>
    public void DeleteReport(string runId) => TryDelete(ReportPath(runId));

    /// <summary>
    /// Deletes scratch input files a previous process left behind. The finally
    /// in RenderAsync cannot run when the container is killed, and nothing else
    /// ever removes them: report expiry only knows about the .html.
    /// </summary>
    public int SweepScratchFiles()
    {
        if (!Directory.Exists(_analysis.ReportDirectory))
            return 0;

        // The listing is materialised first: deleting while the directory walk
        // is still open lets entries be skipped. Counted on the delete, not on
        // the listing, so a read-only volume cannot have the log announce
        // removals that never happened.
        return ScratchFilePatterns
            .SelectMany(pattern => Directory.GetFiles(_analysis.ReportDirectory, pattern))
            .Count(TryDelete);
    }

    public async Task<RunOutcome> RunAsync(
        AnalysisRun run,
        SourceOptions source,
        AnalysisRequest request,
        CancellationToken cancellationToken)
    {
        if (_analysis.EngineBinaryPath is null)
            return Failed(AnalysisErrorCodes.Internal);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_analysis.Timeout);
        var configPath = WriteRunConfig(run.Id, request);
        try
        {
            var reportJson = source.Kind == SourceKinds.Daemon
                ? await daemonClient.FetchReportSnapshotAsync(source, _analysis.Timeout, timeout.Token)
                : await QueryBackendAsync(request, source, configPath, timeout.Token);
            if (reportJson is null)
                return Failed(AnalysisErrorCodes.BinaryFailed);

            var summary = ReportSummary.TryParse(reportJson, out var binaryVersion);
            // Short-circuit kept on purpose: output that is not a report must
            // never reach the renderer.
            if (summary is null ||
                !await RenderAsync(
                    run.Id,
                    reportJson,
                    configPath,
                    LiveDaemonUrl(source, engine.SupportsDaemonUrl),
                    timeout.Token))
                return Failed(AnalysisErrorCodes.BinaryFailed);

            summary.ReportBytes = RenderedBytes(run.Id);
            return new RunOutcome(AnalysisStatuses.Succeeded, null, binaryVersion, summary);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(AnalysisErrorCodes.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = Classify(exception);
            LogRunFailed(logger, exception, run.Id, code);
            return Failed(code);
        }
        finally
        {
            if (configPath is not null)
                TryDelete(configPath);
        }
    }

    /// <summary>
    /// Writes the run's own `[detection]` section, or returns null when the
    /// operator changed nothing. Handed to both subprocesses through `-c`, so
    /// the query and the render agree on what counts as a problem.
    /// </summary>
    private string? WriteRunConfig(string runId, AnalysisRequest request)
    {
        if (request.Detection.IsEmpty)
            return null;

        Directory.CreateDirectory(_analysis.ReportDirectory);
        var path = Path.Combine(_analysis.ReportDirectory, $"{runId}.config.toml");
        File.WriteAllText(path, request.Detection.ToToml());
        return path;
    }

    /// <summary>
    /// Returns the engine's report JSON, or null when the engine refused. The
    /// stderr it wrote is used to name an owner and is never returned.
    /// </summary>
    private async Task<byte[]?> QueryBackendAsync(
        AnalysisRequest request,
        SourceOptions source,
        string? configPath,
        CancellationToken cancellationToken)
    {
        var result = await EngineProcess.RunAsync(
            _analysis.EngineBinaryPath!,
            request.ToEngineArguments(source, configPath),
            MaxReportJsonBytes,
            _analysis.ReportDirectory,
            cancellationToken,
            source.AuthHeaderName is null
                ? null
                : new Dictionary<string, string>
                {
                    // Curl form, the shape --auth-header-env documents.
                    [AnalysisRequest.AuthTokenVariable] =
                        $"{source.AuthHeaderName}: {source.AuthHeaderValue}"
                });
        return result.Succeeded
            ? result.StandardOutput
            : throw new EngineFailedException(ClassifyEngineFailure(result.StandardError));
    }

    /// <summary>
    /// The URL a daemon-source report can go live against, or null. Three ways
    /// to get null, and all three render a static report rather than fail a
    /// run: a source that is not a daemon, a daemon reached through a
    /// path-based ingress (the engine takes an origin and rejects path, query,
    /// userinfo and trailing slash at parse), and an engine binary built
    /// without its `daemon` feature, which does not know the flag at all.
    /// </summary>
    public static string? LiveDaemonUrl(SourceOptions source, bool engineTakesTheFlag) =>
        engineTakesTheFlag && source.Kind == SourceKinds.Daemon && source.BaseUrl!.AbsolutePath == "/"
            ? source.EndpointArgument
            : null;

    private async Task<bool> RenderAsync(
        string runId,
        byte[] reportJson,
        string? configPath,
        string? daemonUrl,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_analysis.ReportDirectory);
        // The engine reads a path rather than stdin here: the same bytes are
        // parsed twice otherwise, once by the Hub for the summary and once by
        // the renderer, and a file keeps the two independent.
        var inputPath = Path.Combine(_analysis.ReportDirectory, $"{runId}.input.json");
        try
        {
            await File.WriteAllBytesAsync(inputPath, reportJson, cancellationToken);
            var arguments = new List<string>
            {
                "report", "--input", inputPath, "--output", ReportPath(runId),
                // Always passed. The flag opts the sink out of size targeting,
                // so every finding reaches the report and only the span trees
                // are capped. A wide sweep otherwise loses the tail of the list.
                // No --sort: impact is the engine's own default from 0.17.0,
                // and the released 0.16.0 rejects the flag outright, so
                // passing it would break every run on the binary most
                // deployments hold today for a value it already gets.
                "--max-traces-embedded",
                _analysis.MaxTracesEmbedded.ToString(CultureInfo.InvariantCulture)
            };
            if (daemonUrl is not null)
            {
                // The report's own Refresh and acknowledgment controls talk to
                // this daemon from the viewer's browser. That only works when
                // the daemon's [daemon.cors] allowed_origins carries the origin
                // this Hub serves reports from; without it the dashboard shows
                // itself disconnected and stays a static report.
                arguments.Add("--daemon-url");
                arguments.Add(daemonUrl);
            }

            if (configPath is not null)
            {
                arguments.Add("-c");
                arguments.Add(configPath);
            }

            var result = await EngineProcess.RunAsync(
                _analysis.EngineBinaryPath!,
                arguments,
                MaxReportJsonBytes,
                _analysis.ReportDirectory,
                cancellationToken);
            // A refused render is deterministic (bad flag, unreadable input),
            // and its stderr names the cause. Without this line the run is
            // stored as binary_failed with zero diagnostic anywhere.
            if (!result.Succeeded)
                LogRenderFailed(logger, runId, result.ExitCode, result.StandardError);
            return result.Succeeded;
        }
        finally
        {
            TryDelete(inputPath);
        }
    }

    /// <summary>The rendered file's size, null when it cannot be read.</summary>
    private long? RenderedBytes(string runId)
    {
        try
        {
            var info = new FileInfo(ReportPath(runId));
            return info.Exists ? info.Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A weight is context on the result, not the result.
            return null;
        }
    }


    private static RunOutcome Failed(string errorCode) =>
        new(AnalysisStatuses.Failed, errorCode, null, null);

    private static string Classify(Exception exception) => exception switch
    {
        EngineFailedException engineFailed => engineFailed.ErrorCode,
        DaemonTimeoutException => AnalysisErrorCodes.Timeout,
        ResponseTooLargeException or EngineOutputTooLargeException => AnalysisErrorCodes.OutputTooLarge,
        HttpRequestException http => ClassifyHttp(http.StatusCode),
        _ => AnalysisErrorCodes.Internal
    };

    private static string ClassifyHttp(HttpStatusCode? statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AnalysisErrorCodes.SourceAuthFailed,
        // No status at all means the request never reached a server.
        null => AnalysisErrorCodes.SourceUnreachable,
        >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError =>
            AnalysisErrorCodes.SourceRejectedRequest,
        _ => AnalysisErrorCodes.SourceUnreachable
    };

    /// <summary>
    /// Names an owner from what the engine printed. A heuristic on purpose:
    /// the engine has no exit code per failure kind, and "the backend refused
    /// us" and "the binary broke" have different owners and different next
    /// steps. Anything unrecognised stays <c>binary_failed</c>.
    /// </summary>
    private static string ClassifyEngineFailure(string standardError)
    {
        if (Contains(standardError, "401") || Contains(standardError, "403") ||
            Contains(standardError, "unauthorized") || Contains(standardError, "forbidden"))
            return AnalysisErrorCodes.SourceAuthFailed;
        // A timed-out query is a window too wide for the engine's own request
        // deadline, not a backend that is down. Classing it as unreachable sent
        // the operator to check a healthy backend.
        if (Contains(standardError, "timed out"))
            return AnalysisErrorCodes.Timeout;
        // The engine's own body cap (0.17.0 wording): the launcher's
        // output_too_large gloss already says exactly the right remedy.
        if (Contains(standardError, "byte cap perf-sentinel applies"))
            return AnalysisErrorCodes.OutputTooLarge;
        if (Contains(standardError, "connection refused") || Contains(standardError, "dns error") ||
            Contains(standardError, "error sending request"))
            return AnalysisErrorCodes.SourceUnreachable;
        return Contains(standardError, "400") || Contains(standardError, "bad request")
            ? AnalysisErrorCodes.SourceRejectedRequest
            : AnalysisErrorCodes.BinaryFailed;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Best effort: false when the file is still there.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run's outcome does not depend on the scratch file going away.
            return false;
        }
    }

    [LoggerMessage(1600, LogLevel.Warning, "Analysis run {RunId} failed with {ErrorCode}.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string runId, string errorCode);

    [LoggerMessage(
        1601,
        LogLevel.Warning,
        "Render for run {RunId} exited {ExitCode}: {StandardError}")]
    private static partial void LogRenderFailed(
        ILogger logger, string runId, int exitCode, string standardError);
}

internal sealed class EngineFailedException(string errorCode)
    : IOException($"The engine refused the request: {errorCode}.")
{
    public string ErrorCode { get; } = errorCode;
}
