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
    public IReadOnlyList<SourceOptions> Sources { get; set; } = [];
}

public sealed record SourceOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Environment { get; set; } = "";
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
    }

    private static void ValidateSource(SourceOptions source, HashSet<string> ids, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(source.Id) ||
            source.Id.Length > 64 ||
            source.Id.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '.' && character != '_' && character != '-') ||
            !ids.Add(source.Id))
            errors.Add("Source IDs must be unique and contain 1-64 ASCII letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Environment))
            errors.Add($"Source '{source.Id}' requires a name and environment.");
        ValidateBaseUrl(source, errors);
        ValidateAuthHeader(source, errors);
        if (source.ImportApiKey is { } importApiKey && IsInvalidImportApiKey(importApiKey))
            errors.Add($"Source '{source.Id}' import API key must contain at least 32 characters and no controls.");
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
}
