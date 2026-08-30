# Configuration

Environment variables use the .NET `Hub__...` form. Helm exposes the same settings under
`hub` and `sources`.

[`examples/appsettings.reference.json`](../examples/appsettings.reference.json) sets every
setting to the value the Hub already uses, annotated. Copying it whole changes nothing, it
is the inventory rather than a starting point. A test keeps it exhaustive, which matters
here because .NET ignores an unrecognised configuration key in silence: a mistyped name
produces no error and reads like a bug in the Hub rather than a typo in your file.

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
| `Hub:Analysis:MaxTracesEmbedded` | `50`                                               | 0 to 10000. Span trees embedded in the report. Setting it opts the sink out of size targeting   |
| `Hub:Analysis:Timeout`           | `00:05:00`                                         | Positive, at most one hour                                                                      |
| `Hub:Analysis:ReportRetention`   | `1.00:00:00` (24 hours)                            | Positive duration                                                                               |
| `Hub:Analysis:RunRetention`      | `30.00:00:00` (30 days)                            | Positive, longer than `ReportRetention`. When a finished run's row is deleted                   |
| `Hub:UpdateCheck:Enabled`        | `true`                                             | Whether the Hub asks GitHub for the newest published release                                    |
| `Hub:UpdateCheck:Interval`       | `1.00:00:00` (1 day)                               | At least 15 minutes                                                                             |
| `Hub:UpdateCheck:EngineEndpoint` | GitHub releases API for `robintra/perf-sentinel`   | Absolute HTTPS, no credentials, query, or fragment                                              |
| `Hub:UpdateCheck:HubEndpoint`    | GitHub releases API for `robintra/PerfSentinelHub` | Absolute HTTPS, no credentials, query, or fragment                                              |
| `Hub:Sources`                    | none                                               | At least one source                                                                             |

## Per-source settings

| Setting                          | Default  | Validation                                                                                                   |
|----------------------------------|----------|--------------------------------------------------------------------------------------------------------------|
| `Sources[].Id`                   | none     | Unique, 1 to 64 ASCII letters, digits, `.`, `_` or `-`                                                                                         |
| `Sources[].Name`                 | none     | Non-empty                                                                                                    |
| `Sources[].Environment`          | none     | Non-empty                                                                                                    |
| `Sources[].Kind`                 | `daemon` | One of `daemon`, `tempo`, `jaeger_query`. Only a daemon is polled, and only a daemon may carry an import key |
| `Sources[].RetentionHours`       | none     | Trace backends only, 1 hour to 10 years                                                                      |
| `Sources[].BaseUrl`              | none     | Required. Absolute HTTP(S), no credentials, query, or fragment                                               |
| `Sources[].AuthHeaderName/Value` | none     | Both absent or both present, no newlines                                                                     |
| `Sources[].ImportApiKey`         | none     | Optional push credential, at least 32 characters, supplied through a Secret                                  |

`Hub:DatabasePath` and `Hub:Analysis:ReportDirectory` default to `/data/hub.db` and
`/data/reports`, which are the container's paths. Both are validated with
`Path.IsPathFullyQualified`, which rejects a leading slash with no drive on Windows, so a
Windows or macOS host must set both or the Hub refuses to start and names the offending key.

`RetentionHours` is declared rather than measured, because no backend API exposes it. It
carries the same caveat as `Environment`: it keeps a stale claim until someone edits it.

`BaseUrl` keeps a path prefix, so `https://gw/perf-sentinel/` polls
`https://gw/perf-sentinel/api/status`.

## What a source is, and what is measured

The list is configuration, never discovery. Nothing is auto-detected, the launcher cannot
add a source, and the Hub refuses to start with none.

That splits every row on the fleet screen in two. `Id`, `Name`, `Environment`, `Kind`,
`BaseUrl` and `RetentionHours` are declared: taken from this file as written and never
checked against anything. `reachable`, `last_success`, `unreachable_since`,
`producer_version` and `last_error` are observed, written by the poll. A dashed outline in
the launcher marks the declared half, which is why a misconfigured deployment can label
production as staging and nothing will contradict it.

Only a daemon is polled. Its `api/status` supplies `producer_version`, and its
`api/findings` is the safety net behind the push.

Where it goes: `Hub:Sources` in `appsettings.json`, the `Hub__Sources__N__*` environment
variables, or `sources[]` in the Helm values. The three are the same setting, and `Kind`
is what decides which half of the screen a row lands in.

```yaml
sources:
  - id: checkout-prod
    name: Checkout production
    environment: production
    kind: daemon
    baseUrl: http://perf-sentinel.observability:4318
    importSecretName: hub-import-keys    # the push credential, never inline
    importSecretKey: checkout-prod
  - id: victoria-eu
    name: Victoria Traces EU
    environment: staging
    kind: jaeger_query                   # Victoria Traces speaks the Jaeger query API
    baseUrl: http://victoria-traces.observability:10428
    retentionHours: 72
```

The same pair as environment variables, one index per source:

```bash
Hub__Sources__0__Id=checkout-prod
Hub__Sources__0__Kind=daemon
Hub__Sources__0__BaseUrl=http://perf-sentinel.observability:4318
Hub__Sources__1__Id=victoria-eu
Hub__Sources__1__Kind=jaeger_query
Hub__Sources__1__BaseUrl=http://victoria-traces.observability:10428
Hub__Sources__1__RetentionHours=72
```

A trace backend is never contacted at all until someone runs an analysis: no route in the
Hub reads a Tempo. The Hub only launches the engine against it, with the subcommand its
kind implies, `tempo` for `tempo` and `jaeger-query` otherwise. That is why such a source
shows no producer version and no last success, and it is not a fault.

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
