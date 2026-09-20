using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int MaxAckBodyBytes = 8 * 1024;

    private static Task<IResult> CreateAckAsync(
        string sourceId,
        HttpRequest request,
        AckRelay relay,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return RelayAckAsync(sourceId, false, request, relay, options.Value, timeProvider, cancellationToken);
    }

    // A POST of its own rather than a DELETE on the route above: the signature
    // travels in a body, which a DELETE is not sure to keep through a proxy.
    private static Task<IResult> RevokeAckAsync(
        string sourceId,
        HttpRequest request,
        AckRelay relay,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return RelayAckAsync(sourceId, true, request, relay, options.Value, timeProvider, cancellationToken);
    }

    /// <summary>
    ///     Writes an ack, or revokes one, at the daemon behind a source, with
    ///     that daemon's own write key. Every check comes before the daemon is
    ///     called, the caller first: nobody unnamed learns which sources relay.
    /// </summary>
    private static async Task<IResult> RelayAckAsync(
        string sourceId,
        bool revoke,
        HttpRequest request,
        AckRelay relay,
        HubOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (RelayIdentity(request, options) is not { } identity)
            return Problem(
                StatusCodes.Status403Forbidden,
                "The Hub does not know who is asking, and an ack is taken in somebody's name.");
        // One answer for an unknown source, a trace backend and a daemon with
        // no ack credential, so the route tells nothing /api/sources does not.
        if (options.Sources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, sourceId, StringComparison.Ordinal))
            is not { Kind: SourceKinds.Daemon, HasAckCredential: true } source)
            return Problem(StatusCodes.Status404NotFound, "This source relays no acknowledgments.");
        if (RefuseForeignRequest(request) is { } refusal)
            return refusal;
        if (!relay.Gate.TryEnter())
        {
            request.HttpContext.Response.Headers.RetryAfter = "1";
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var payload = await ReadBodyAsync(request, cancellationToken, MaxAckBodyBytes);
            if (payload is null)
                return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (AckWrite.TryParse(payload, revoke, nowMs, out var error) is not { } write)
                return Problem(StatusCodes.Status400BadRequest, error ?? "The request is invalid.");
            // The daemon acks any canonical signature. The Hub only spends the
            // key on one it holds at that source, which bounds what a caller
            // can grow the daemon's ack store by.
            if (!await relay.KnowsAsync(source, write, cancellationToken))
                return Problem(
                    StatusCodes.Status404NotFound,
                    revoke
                        ? "The Hub holds no such finding at this source and mirrors no ack of it."
                        : "The Hub holds no such finding at this source.");

            var answer = await relay.SendAsync(source, write, identity, cancellationToken);
            return answer.Detail is null ? TypedResults.NoContent() : Problem(answer.Status, answer.Detail);
        }
        finally
        {
            relay.Gate.Exit();
        }
    }

    /// <summary>
    ///     A Hub:Auth session, or the header a proxy sets once the relay opted
    ///     in to it. POST /api/analyses records that header as an unverified
    ///     claim, and a claim is not enough to spend a daemon's write key on.
    /// </summary>
    private static string? RelayIdentity(HttpRequest request, HubOptions options)
    {
        return Sanitized(
            SessionName(request) ??
            (options.AckRelay.TrustIdentityHeader ? ProxyIdentity(request, options.Analysis) : null));
    }

    /// <summary>
    ///     The Hub has no antiforgery token and no CORS policy. A browser names
    ///     the site a request comes from, and a page on another origin cannot
    ///     send JSON without a preflight nothing here answers.
    /// </summary>
    private static IResult? RefuseForeignRequest(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin")
            return Problem(StatusCodes.Status403Forbidden, "A request from another site is refused.");
        return request.HasJsonContentType()
            ? null
            : TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    }
}

/// <summary>
///     Each relay holds a daemon connection for up to three client timeouts, and
///     every one of them spends that daemon's write key, so a burst is refused
///     rather than queued.
/// </summary>
public sealed class AckRelayGate() : RequestGate(MaxRelays)
{
    // Public because AckRelayApiTests pins it, as IncidentRefreshGate.MaxReads is.
    public const int MaxRelays = 2;
}
