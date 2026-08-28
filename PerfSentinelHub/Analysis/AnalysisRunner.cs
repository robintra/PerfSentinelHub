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
    IOptions<HubOptions> options,
    ILogger<AnalysisRunner> logger)
{
    // The intermediate report JSON. Well past any real run, small enough that
    // a runaway engine cannot exhaust the container's memory.
    private const long MaxReportJsonBytes = 256L * 1024 * 1024;

    private readonly AnalysisOptions _analysis = options.Value.Analysis;

    public string ReportPath(string runId) =>
        Path.Combine(_analysis.ReportDirectory, $"{runId}.html");

    /// <summary>Removes a report whose lifetime ran out. Missing is fine.</summary>
    public void DeleteReport(string runId) => DeleteQuietly(ReportPath(runId));

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
        try
        {
            var reportJson = source.Kind == SourceKinds.Daemon
                ? await daemonClient.FetchReportSnapshotAsync(source, _analysis.Timeout, timeout.Token)
                : await QueryBackendAsync(request, source, timeout.Token);
            if (reportJson is null)
                return Failed(AnalysisErrorCodes.BinaryFailed);

            var summary = ReportSummary.TryParse(reportJson, out var binaryVersion);
            if (summary is null)
                return Failed(AnalysisErrorCodes.BinaryFailed);

            return await RenderAsync(run.Id, reportJson, timeout.Token)
                ? new RunOutcome(AnalysisStatuses.Succeeded, null, binaryVersion, summary)
                : Failed(AnalysisErrorCodes.BinaryFailed);
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
    }

    /// <summary>
    /// Returns the engine's report JSON, or null when the engine refused. The
    /// stderr it wrote is used to name an owner and is never returned.
    /// </summary>
    private async Task<byte[]?> QueryBackendAsync(
        AnalysisRequest request,
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var result = await EngineProcess.RunAsync(
            _analysis.EngineBinaryPath!,
            request.ToEngineArguments(source),
            MaxReportJsonBytes,
            cancellationToken);
        if (result.Succeeded)
            return result.StandardOutput;

        throw new EngineFailedException(ClassifyEngineFailure(result.StandardError));
    }

    private async Task<bool> RenderAsync(string runId, byte[] reportJson, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_analysis.ReportDirectory);
        // The engine reads a path rather than stdin here: the same bytes are
        // parsed twice otherwise, once by the Hub for the summary and once by
        // the renderer, and a file keeps the two independent.
        var inputPath = Path.Combine(_analysis.ReportDirectory, $"{runId}.input.json");
        try
        {
            await File.WriteAllBytesAsync(inputPath, reportJson, cancellationToken);
            var result = await EngineProcess.RunAsync(
                _analysis.EngineBinaryPath!,
                ["report", "--input", inputPath, "--output", ReportPath(runId)],
                MaxReportJsonBytes,
                cancellationToken);
            return result.Succeeded;
        }
        finally
        {
            DeleteQuietly(inputPath);
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
        if (Contains(standardError, "connection refused") || Contains(standardError, "dns error") ||
            Contains(standardError, "error sending request") || Contains(standardError, "timed out"))
            return AnalysisErrorCodes.SourceUnreachable;
        return Contains(standardError, "400") || Contains(standardError, "bad request")
            ? AnalysisErrorCodes.SourceRejectedRequest
            : AnalysisErrorCodes.BinaryFailed;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run's outcome does not depend on the scratch file going away.
        }
    }

    [LoggerMessage(1401, LogLevel.Warning, "Analysis run {RunId} failed with {ErrorCode}.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, string runId, string errorCode);
}

internal sealed class EngineFailedException(string errorCode)
    : IOException($"The engine refused the request: {errorCode}.")
{
    public string ErrorCode { get; } = errorCode;
}
