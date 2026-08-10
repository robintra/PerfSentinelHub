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
    public string? ImportApiKey { get; set; }
}

public sealed class HubOptionsValidator : IValidateOptions<HubOptions>
{
    public ValidateOptionsResult Validate(string? name, HubOptions options)
    {
        var errors = new List<string>();

        if (!Path.IsPathFullyQualified(options.DatabasePath))
            errors.Add("Hub:DatabasePath must be absolute.");
        if (options.PollInterval <= TimeSpan.Zero)
            errors.Add("Hub:PollInterval must be positive.");
        if (options.HttpTimeout <= TimeSpan.Zero)
            errors.Add("Hub:HttpTimeout must be positive.");
        if (options.Retention <= TimeSpan.Zero)
            errors.Add("Hub:Retention must be positive.");
        if (options.MaxConcurrentPolls is < 1 or > 32)
            errors.Add("Hub:MaxConcurrentPolls must be between 1 and 32.");
        if (options.MaxReadLimit is < 1 or > 10_000)
            errors.Add("Hub:MaxReadLimit must be between 1 and 10000.");
        if (options.DefaultReadLimit is < 1 || options.DefaultReadLimit > options.MaxReadLimit)
            errors.Add("Hub:DefaultReadLimit must be between 1 and MaxReadLimit.");
        if (options.Sources.Count == 0)
            errors.Add("Hub:Sources must contain at least one source.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in options.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) ||
                source.Id.Length > 64 ||
                source.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-') ||
                !ids.Add(source.Id))
                errors.Add("Source IDs must be unique and contain 1-64 ASCII letters, digits, '.', '_' or '-'.");
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Environment))
                errors.Add($"Source '{source.Id}' requires a name and environment.");
            if (source.BaseUrl is not { IsAbsoluteUri: true } baseUrl ||
                (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(baseUrl.UserInfo) ||
                !string.IsNullOrEmpty(baseUrl.Query) ||
                !string.IsNullOrEmpty(baseUrl.Fragment))
                errors.Add(
                    $"Source '{source.Id}' requires an absolute HTTP(S) URL " +
                    "without credentials, query, or fragment.");

            ValidateAuthHeader(source, errors);
            if (source.ImportApiKey is { } importApiKey &&
                (importApiKey.Length < 32 || string.IsNullOrWhiteSpace(importApiKey) ||
                 importApiKey.Any(char.IsControl)))
                errors.Add($"Source '{source.Id}' import API key must contain at least 32 characters and no controls.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateAuthHeader(SourceOptions source, List<string> errors)
    {
        if ((source.AuthHeaderName is null) != (source.AuthHeaderValue is null))
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
}
