using Microsoft.Extensions.Options;

namespace PerfSentinelHub.Configuration;

public sealed record HubOptions
{
    public const string SectionName = "Hub";

    public string DatabasePath { get; set; } = "/data/hub.db";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxConcurrentPolls { get; set; } = 4;
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(180);
    // Window behind the per-finding status: seen within it = active; older,
    // with the endpoint still heartbeating from a reachable source =
    // likely_resolved; anything else = not_observed.
    public TimeSpan ResolutionGrace { get; set; } = TimeSpan.FromDays(7);
    public int DefaultReadLimit { get; set; } = 1000;
    public int MaxReadLimit { get; set; } = 10_000;
    public AnalysisOptions Analysis { get; set; } = new();
    public IReadOnlyList<SourceOptions> Sources { get; set; } = [];
}

public sealed record AnalysisOptions
{
    // Absent means the Hub keeps collecting findings but cannot run an
    // analysis: the launcher reads a null engine version and says so.
    public string? EngineBinaryPath { get; set; }
    // Where rendered reports live. Must be writable: the container is
    // read-only everywhere else.
    public string ReportDirectory { get; set; } = "/data/reports";
    // Header a reverse proxy sets with the established identity. The Hub has
    // no account surface and records the value as a claim, never verifies it.
    public string IdentityHeader { get; set; } = "X-Forwarded-User";
    public int Workers { get; set; } = 2;
    public int MaxTracesCap { get; set; } = 2000;
    // Span trees embedded in the rendered report. Passing this at all opts the
    // sink out of size targeting, which is why it is set: without it a wide
    // sweep loses findings to the budget, and the finding list is the thing an
    // operator came for. The trees are what gets capped instead.
    public int MaxTracesEmbedded { get; set; } = 50;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(300);
    public TimeSpan ReportRetention { get; set; } = TimeSpan.FromHours(24);
}

public static class SourceKinds
{
    public const string Daemon = "daemon";
    public const string Tempo = "tempo";
    public const string JaegerQuery = "jaeger_query";

    public static bool IsKnown(string kind) =>
        kind is Daemon or Tempo or JaegerQuery;
}

public sealed record SourceOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Environment { get; set; } = "";
    // A daemon detects its own findings and is polled. A trace backend stores
    // traces and detects nothing, so it is never polled and only ever read by
    // an analysis run.
    public string Kind { get; set; } = SourceKinds.Daemon;
    // How far back this backend keeps traces, declared here because no backend
    // API exposes it. Bounds the launcher's time-range picker. Declared and not
    // measured, so it carries the same caveat as the environment: it keeps a
    // stale claim until someone edits it.
    public int? RetentionHours { get; set; }
    public Uri? BaseUrl { get; set; }
    public string? AuthHeaderName { get; set; }
    public string? AuthHeaderValue { get; set; }
    // Trimmed on binding: the daemon trims its key file, so a secret mounted from a file with a
    // trailing newline must hash to the same bytes on both halves of the contract.
    public string? ImportApiKey
    {
        get;
        set => field = value?.Trim();
    }
}

public sealed class HubOptionsValidator : IValidateOptions<HubOptions>
{
    public ValidateOptionsResult Validate(string? name, HubOptions options)
    {
        var errors = new List<string>();
        ValidateHubSettings(options, errors);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in options.Sources)
            ValidateSource(source, ids, errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateHubSettings(HubOptions options, List<string> errors)
    {
        if (!Path.IsPathFullyQualified(options.DatabasePath))
            errors.Add("Hub:DatabasePath must be absolute.");
        if (options.PollInterval <= TimeSpan.Zero)
            errors.Add("Hub:PollInterval must be positive.");
        if (options.HttpTimeout <= TimeSpan.Zero)
            errors.Add("Hub:HttpTimeout must be positive.");
        if (options.Retention <= TimeSpan.Zero)
            errors.Add("Hub:Retention must be positive.");
        if (options.ResolutionGrace <= TimeSpan.Zero || options.ResolutionGrace >= options.Retention)
            errors.Add("Hub:ResolutionGrace must be positive and below Hub:Retention.");
        if (options.MaxConcurrentPolls is < 1 or > 32)
            errors.Add("Hub:MaxConcurrentPolls must be between 1 and 32.");
        if (options.MaxReadLimit is < 1 or > 10_000)
            errors.Add("Hub:MaxReadLimit must be between 1 and 10000.");
        if (options.DefaultReadLimit < 1 || options.DefaultReadLimit > options.MaxReadLimit)
            errors.Add("Hub:DefaultReadLimit must be between 1 and MaxReadLimit.");
        if (options.Sources.Count == 0)
            errors.Add("Hub:Sources must contain at least one source.");
        ValidateAnalysisSettings(options.Analysis, errors);
    }

    private static void ValidateAnalysisSettings(AnalysisOptions analysis, List<string> errors)
    {
        if (analysis.EngineBinaryPath is { } path &&
            (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            errors.Add("Hub:Analysis:EngineBinaryPath must be absolute.");
        if (!Path.IsPathFullyQualified(analysis.ReportDirectory))
            errors.Add("Hub:Analysis:ReportDirectory must be absolute.");
        if (string.IsNullOrWhiteSpace(analysis.IdentityHeader) ||
            analysis.IdentityHeader.Any(character => char.IsControl(character) || character == ' '))
            errors.Add("Hub:Analysis:IdentityHeader must be a header name.");
        if (analysis.Workers is < 1 or > 16)
            errors.Add("Hub:Analysis:Workers must be between 1 and 16.");
        if (analysis.MaxTracesCap is < 1 or > 100_000)
            errors.Add("Hub:Analysis:MaxTracesCap must be between 1 and 100000.");
        if (analysis.MaxTracesEmbedded is < 0 or > 10_000)
            errors.Add("Hub:Analysis:MaxTracesEmbedded must be between 0 and 10000.");
        if (analysis.Timeout <= TimeSpan.Zero || analysis.Timeout > TimeSpan.FromHours(1))
            errors.Add("Hub:Analysis:Timeout must be positive and at most one hour.");
        if (analysis.ReportRetention <= TimeSpan.Zero)
            errors.Add("Hub:Analysis:ReportRetention must be positive.");
    }

    private static void ValidateSource(SourceOptions source, HashSet<string> ids, List<string> errors)
    {
        if (!IsValidSourceId(source.Id) || !ids.Add(source.Id))
            errors.Add("Source IDs must be unique and contain 1-64 ASCII letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Environment))
            errors.Add($"Source '{source.Id}' requires a name and environment.");
        if (!SourceKinds.IsKnown(source.Kind))
            errors.Add($"Source '{source.Id}' kind must be 'daemon', 'tempo' or 'jaeger_query'.");
        if (source.Kind != SourceKinds.Daemon && source.ImportApiKey is not null)
            errors.Add($"Source '{source.Id}' is not a daemon and cannot carry an import API key.");
        ValidateRetentionHours(source, errors);
        ValidateBaseUrl(source, errors);
        ValidateAuthHeader(source, errors);
        if (source.ImportApiKey is { } importApiKey && IsInvalidImportApiKey(importApiKey))
            errors.Add($"Source '{source.Id}' import API key must contain at least 32 characters and no controls.");
    }

    private static void ValidateRetentionHours(SourceOptions source, List<string> errors)
    {
        if (source.RetentionHours is not { } retentionHours)
            return;

        // A daemon takes no window, so nothing would ever read the value. A
        // setting with no consumer is worse than a missing one: someone tunes
        // it and nothing happens.
        if (source.Kind == SourceKinds.Daemon)
            errors.Add($"Source '{source.Id}' is a daemon and takes no trace retention.");
        else if (retentionHours is < 1 or > 87_600)
            errors.Add($"Source '{source.Id}' retention must be between 1 hour and 10 years.");
    }

    private static void ValidateBaseUrl(SourceOptions source, List<string> errors)
    {
        var baseUrl = source.BaseUrl;
        if (baseUrl is null ||
            !baseUrl.IsAbsoluteUri ||
            baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseUrl.UserInfo) ||
            !string.IsNullOrEmpty(baseUrl.Query) ||
            !string.IsNullOrEmpty(baseUrl.Fragment))
            errors.Add(
                $"Source '{source.Id}' requires an absolute HTTP(S) URL " +
                "without credentials, query, or fragment.");
    }

    private static void ValidateAuthHeader(SourceOptions source, List<string> errors)
    {
        var hasAuthHeaderName = source.AuthHeaderName is not null;
        var hasAuthHeaderValue = source.AuthHeaderValue is not null;
        if (hasAuthHeaderName != hasAuthHeaderValue)
        {
            errors.Add($"Source '{source.Id}' must provide both auth header name and value.");
            return;
        }

        if (source.AuthHeaderName is null)
            return;

        if (source.AuthHeaderName.Contains('\r', StringComparison.Ordinal) ||
            source.AuthHeaderName.Contains('\n', StringComparison.Ordinal) ||
            source.AuthHeaderValue!.Contains('\r', StringComparison.Ordinal) ||
            source.AuthHeaderValue.Contains('\n', StringComparison.Ordinal))
        {
            errors.Add($"Source '{source.Id}' auth header contains a newline.");
            return;
        }

        using var request = new HttpRequestMessage();
        try
        {
            if (!request.Headers.TryAddWithoutValidation(source.AuthHeaderName, source.AuthHeaderValue))
                errors.Add($"Source '{source.Id}' auth header is invalid.");
        }
        catch (FormatException)
        {
            errors.Add($"Source '{source.Id}' auth header is invalid.");
        }
    }

    private static bool IsInvalidImportApiKey(string value) =>
        value.Length < 32 || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl);

    private static bool IsValidSourceId(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 64 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
