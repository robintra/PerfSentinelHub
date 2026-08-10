using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Collection;

public sealed class DaemonClient(HttpClient httpClient, IOptions<HubOptions> options)
{
    private const int MaxBodyBytes = 16 * 1024 * 1024;
    private readonly TimeSpan _timeout = options.Value.HttpTimeout;

    public async Task<string> FetchStatusAsync(
        SourceOptions source,
        CancellationToken cancellationToken)
    {
        var body = await SendAsync(source, "/api/status", cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(version.GetString()))
                return version.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new InvalidStatusException(exception);
        }

        throw new InvalidStatusException();
    }

    public Task<byte[]> FetchFindingsAsync(
        SourceOptions source,
        CancellationToken cancellationToken) =>
        SendAsync(source, "/api/findings?limit=10000&include_acked=true", cancellationToken);

    private async Task<byte[]> SendAsync(
        SourceOptions source,
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(source.BaseUrl, path));
        if (source.AuthHeaderName is not null)
            request.Headers.TryAddWithoutValidation(source.AuthHeaderName, source.AuthHeaderValue);

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                throw new ResponseTooLargeException();

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    break;
                if (output.Length + read > MaxBodyBytes)
                    throw new ResponseTooLargeException();
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DaemonTimeoutException(exception);
        }
    }
}

public sealed class DaemonTimeoutException(Exception innerException)
    : IOException("The daemon request timed out.", innerException);

public sealed class ResponseTooLargeException()
    : IOException("The daemon response exceeds 16 MiB.");

public sealed class InvalidStatusException : IOException
{
    public InvalidStatusException() : base("The daemon status response is invalid.") { }
    public InvalidStatusException(Exception innerException)
        : base("The daemon status response is invalid.", innerException) { }
}
