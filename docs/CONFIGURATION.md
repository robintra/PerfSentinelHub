# Configuration

Environment variables use the .NET `Hub__...` form. Helm exposes the same settings under
`hub` and `sources`.

## Settings

| Setting                          | Default                                            | Validation                                                                                      |
|----------------------------------|----------------------------------------------------|-------------------------------------------------------------------------------------------------|
| `Hub:DatabasePath`               | `/data/hub.db`                                     | Absolute path                                                                                   |
| `Hub:PollInterval`               | `01:00:00`                                         | Positive duration                                                                               |
| `Hub:HttpTimeout`                | `00:00:10`                                         | Positive duration                                                                               |
| `Hub:MaxConcurrentPolls`         | `4`                                                | 1 to 32                                                                                         |
| `Hub:Retention`                  | `180.00:00:00` (180 days)                          | Positive duration                                                                               |
| `Hub:ResolutionGrace`            | `7.00:00:00` (7 days)                              | Positive, below `Retention`                                                                     |
| `Hub:DefaultReadLimit`           | `1000`                                             | 1 to `MaxReadLimit`                                                                             |
| `Hub:MaxReadLimit`               | `10000`                                            | 1 to 10000                                                                                      |
| `Hub:Analysis:EngineBinaryPath`  | none                                               | Optional, absolute path to the perf-sentinel binary. Absent means analysis runs are unavailable |
| `Hub:Analysis:ReportDirectory`   | `/data/reports`                                    | Absolute, writable. Rendered reports live here                                                  |
| `Hub:Analysis:IdentityHeader`    | `X-Forwarded-User`                                 | Header a reverse proxy sets with the requester's identity                                       |
| `Hub:Analysis:Workers`           | `2`                                                | 1 to 16                                                                                         |
| `Hub:Analysis:MaxTracesCap`      | `2000`                                             | 1 to 10000, the engine's own limit on `--max-traces`                                            |
| `Hub:Analysis:Timeout`           | `00:05:00`                                         | Positive, at most one hour                                                                      |
| `Hub:Analysis:ReportRetention`   | `1.00:00:00` (24 hours)                            | Positive duration                                                                               |
| `Hub:UpdateCheck:Enabled`        | `true`                                             | Whether the Hub asks GitHub for the newest published release                                    |
| `Hub:UpdateCheck:Interval`       | `1.00:00:00` (1 day)                               | At least 15 minutes                                                                             |
| `Hub:UpdateCheck:EngineEndpoint` | GitHub releases API for `robintra/perf-sentinel`   | Absolute HTTPS, no credentials, query, or fragment                                              |
| `Hub:UpdateCheck:HubEndpoint`    | GitHub releases API for `robintra/PerfSentinelHub` | Absolute HTTPS, no credentials, query, or fragment                                              |
| `Hub:Sources`                    | none                                               | At least one source                                                                             |

## Per-source settings

| Setting                          | Default  | Validation                                                                                                   |
|----------------------------------|----------|--------------------------------------------------------------------------------------------------------------|
| `Sources[].Id`                   | none     | Non-empty and unique                                                                                         |
| `Sources[].Name`                 | none     | Non-empty                                                                                                    |
| `Sources[].Environment`          | none     | Non-empty                                                                                                    |
| `Sources[].Kind`                 | `daemon` | One of `daemon`, `tempo`, `jaeger_query`. Only a daemon is polled, and only a daemon may carry an import key |
| `Sources[].RetentionHours`       | none     | Trace backends only, 1 hour to 10 years                                                                      |
| `Sources[].BaseUrl`              | none     | Required. Absolute HTTP(S), no credentials, query, or fragment                                               |
| `Sources[].AuthHeaderName/Value` | none     | Both absent or both present, no newlines                                                                     |
| `Sources[].ImportApiKey`         | none     | Optional push credential, at least 32 characters, supplied through a Secret                                  |

`RetentionHours` is declared rather than measured, because no backend API exposes it. It
carries the same caveat as `Environment`: it keeps a stale claim until someone edits it.

`BaseUrl` keeps a path prefix, so `https://gw/perf-sentinel/` polls
`https://gw/perf-sentinel/api/status`.

## Where the Hub connects

Every outbound request goes to a configured `Sources[].BaseUrl`, with one exception. Once
a day the Hub asks the GitHub releases API for the newest published version of the engine
and of itself, so the version chip can say that what you are running is no longer current.
The request is an unauthenticated GET and carries no identifier of your deployment.

For a deployment with no egress, set `Hub:UpdateCheck:Enabled` to `false`. The chip then
shows nothing rather than claiming you are current, which is also what it shows when the
request fails.

## An https source with a private CA

The Hub validates a source's certificate against the container's trust store. An
in-cluster daemon with a self-signed or internally-issued certificate is refused with
`PartialChain` until its CA is in there.

The runtime image is chiseled and has no shell, so `update-ca-certificates` cannot run in
it. Point `SSL_CERT_FILE` at a bundle mounted from a ConfigMap instead:

```yaml
env:
  - name: SSL_CERT_FILE
    value: /etc/perf-sentinel-hub/certs/bundle.crt
```

The bundle must be the public roots and your CA concatenated, in that order, not the CA
alone. The variable replaces the default file rather than adding to it, and a Hub that
trusts only your CA cannot reach anything on the public internet.

Verified against the runtime this image is built on: with no variable the private
certificate is refused and public TLS works, with the concatenated bundle both work.
