<p align="center">
    <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Frobintra%2FPerfSentinelHub%2Fmain%2Fglobal.json&query=%24.sdk.version&label=.NET&color=512BD4&logo=dotnet&logoColor=white" alt=".NET" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml/badge.svg" alt="Security Audit" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml/badge.svg" alt="CodeQL" /></a>
    <a href="https://sonarcloud.io/summary/overall?id=robintrassard_PerfSentinelHub"><img src="https://sonarcloud.io/api/project_badges/measure?project=robintrassard_PerfSentinelHub&metric=coverage" alt="Coverage" /></a>
    <a href="https://sonarcloud.io/summary/overall?id=robintrassard_PerfSentinelHub"><img src="https://sonarcloud.io/api/project_badges/measure?project=robintrassard_PerfSentinelHub&metric=alert_status" alt="Quality Gate" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/release.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/release.yml/badge.svg" alt="Release" /></a>
</p>

# PerfSentinelHub

**One durable endpoint for the findings your
[perf-sentinel](https://github.com/robintra/perf-sentinel) daemons produce, and a browser
interface that launches an analysis without a terminal.** A NativeAOT service backed by
SQLite. Daemon push is the primary path, polling is a recovery safety net, and finding
envelopes stay read-compatible for 180 days by default.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/hub-integration_dark.svg">
  <img alt="How the Hub, the fleet, the browser and the engine fit together" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/hub-integration.svg">
</picture>

## Run locally in five minutes

Requirements: .NET SDK 10.0.400 and a reachable perf-sentinel daemon.

```bash
Hub__DatabasePath=/tmp/perf-sentinel-hub.db \
Hub__Sources__0__Id=local \
Hub__Sources__0__Name='Local daemon' \
Hub__Sources__0__Environment=development \
Hub__Sources__0__BaseUrl=http://localhost:4318 \
Hub__Sources__0__ImportApiKey="$(openssl rand -hex 16)" \
ASPNETCORE_URLS=http://localhost:5080 \
dotnet run --project PerfSentinelHub

curl http://localhost:5080/health/ready
curl http://localhost:5080/api/findings
```

The first poll starts immediately. The launcher is at `http://localhost:5080/`, and the
SQLite file survives restarts at the configured path.

## Install with Helm

The source chart deploys one replica and a persistent volume. Supply at least one source
and an immutable image digest:

```bash
helm upgrade --install perf-sentinel-hub deploy/helm/perf-sentinel-hub \
  --set image.repository=ghcr.io/robintra/perf-sentinel-hub \
  --set image.digest=sha256:IMAGE_DIGEST \
  --set 'sources[0].id=production' \
  --set 'sources[0].name=Production' \
  --set 'sources[0].environment=production' \
  --set 'sources[0].baseUrl=http://perf-sentinel.observability:4318' \
  --set persistence.size=5Gi
```

Credentials never go in Helm values. For an authenticated poll, put the value in a Secret
and set `sources[].authHeaderName`, `authSecretName` and `authSecretKey`. For daemon push,
set `sources[].importSecretName` and `importSecretKey`, with at least 32 characters.

For a public release, install by digest rather than by tag. A version tag is a discovery
hint, never a deployment identity:

```bash
IMAGE_DIGEST="$(jq -r .image.digest release/release-manifest.json)"
docker pull "ghcr.io/robintra/perf-sentinel-hub@$IMAGE_DIGEST"

CHART=ghcr.io/robintra/charts/perf-sentinel-hub
CHART_DIGEST="$(oras resolve "$CHART:0.1.0")"
helm pull "oci://$CHART@$CHART_DIGEST"
```

These registry commands work only once the public rehearsal and publication succeed.

## Documentation

| Document                                       | Covers                                                                                                |
|------------------------------------------------|-------------------------------------------------------------------------------------------------------|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)   | Five diagrams: the topology, push against poll, what a run does, the retention clocks, the run states |
| [docs/CONFIGURATION.md](docs/CONFIGURATION.md) | Every setting, where the Hub connects, and an https source behind a private CA                        |
| [docs/API.md](docs/API.md)                     | The import, read and analysis APIs                                                                    |
| [docs/LAUNCHER.md](docs/LAUNCHER.md)           | The browser interface, its printed commands and its live reports                                      |
| [docs/OPERATIONS.md](docs/OPERATIONS.md)       | Freshness, recovery, backup and restore                                                               |
| [RELEASING.md](RELEASING.md)                   | What a release contains, how it is signed, and how to verify one publicly                             |
| [CONTRIBUTING.md](CONTRIBUTING.md)             | Local gates, the pinned toolchain, and the pull request rules                                         |

## What this is not

No ingress, no user authentication, no CI or SARIF import, no acknowledgment writer, and
no remote backup. The local `backup` command snapshots the database, but shipping that
file off the cluster stays the operator's job.

Network exposure and authentication belong to the next independent design.
Acknowledgments remain in the repository perf-sentinel consumes.

Every badge above reports something observed. The container image and Helm chart badges
are deliberately absent until the first release publishes their package pages, because a
badge that links nowhere is a promise rather than evidence.

## License

[GNU Affero General Public License v3.0](LICENSE). Applications and IDE plugins
communicate with the Hub over HTTP rather than linking it. If you modify the Hub and offer
that modified version over a network, AGPL section 13 applies. This is a practical
summary, not legal advice.
