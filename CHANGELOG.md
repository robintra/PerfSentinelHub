# Changelog

All notable changes to PerfSentinelHub are recorded here.

## [Unreleased]

### Added

- `GET /api/sources` joins each configured source to its last known collection state, and
  `GET /api/status` now also reports the engine version, worker count, queue depth and the trace
  cap, timeout and report retention a run is held to. Together they are what a human interface
  needs to show a source list and state the cost of a run before it starts.
- Sources declare a `Kind` of `daemon`, `tempo` or `jaeger_query`. A trace backend serves no
  findings endpoint, so it is never polled and cannot carry an import key. Polling one would have
  marked a healthy source unreachable on every interval.
- A trace backend can declare `RetentionHours`, how far back it keeps traces. No backend API
  exposes it, so a client asking for a window beyond it waits for a full run before getting
  nothing useful back. A daemon cannot carry one, since it takes no window at all and the value
  would have no reader.
- `Hub:Analysis` configures the perf-sentinel binary the Hub runs, along with the worker count and
  the limits a run is held to. The binary's version is read once at startup, and a missing or
  unusable binary leaves it null rather than stopping the Hub, since collection and the read API
  do not depend on it.

- Analysis runs. `POST /api/analyses` queues a run against a configured source, a worker executes
  the perf-sentinel binary in a subprocess under a timeout, and `GET /reports/{id}.html` serves the
  HTML the engine rendered, from the same origin. A run is two invocations, because the query
  subcommands emit text, JSON or SARIF and only `report` writes HTML. A daemon skips the first one
  and its `/api/export/report` provides the input directly.
- Failures are reported as one of eight bounded codes and raw stderr never leaves the process. It
  is read only to tell a backend that refused us from a binary that broke, which have different
  owners.
- Reports expire after `Hub:Analysis:ReportRetention` and their run is marked expired while keeping
  its parameters. A run interrupted by a restart comes back as such and is never replayed on its
  own, since a silent retry would fire a second heavy query nobody asked for.

- The engine ships inside the Hub image, copied by digest from the published perf-sentinel image
  rather than downloaded, so the build reaches no host outside the registry. The Helm chart renders
  the `Hub:Analysis` section and a source's `Kind` and `RetentionHours`, which it previously
  dropped, and reports live under the existing `/data` volume since the container root is
  read-only.
- `scripts/check-supply-chain.py` accepts a GHCR container pin, restricted to this account's own
  namespace the way the Docker Hub branch is restricted to jetbrains.

- A browser interface at `/`, served from the same origin as the reports. Four screens covering the
  form, one run, recent runs and fleet health, in plain HTML, CSS and JavaScript with no framework
  and no build step. The theme is tri-state and hands over to the rendered dashboard through
  `sessionStorage`, which is why the two share an origin.
- The launcher is excluded from Sonar coverage and `fonts.css` from analysis: browser code with no
  test runner in this repository would otherwise be counted at zero and sink the new-code gate.

### Changed

- JSON responses use snake_case property names, matching the envelope perf-sentinel itself emits.
  Every pre-existing field is a single word and serialises identically under either policy, so no
  response changed shape.

## [0.1.0] - 2026-08-12

### Added

- Initial NativeAOT Hub release with durable SQLite findings, authenticated push ingestion, recovery polling, retention, and Helm deployment support.
- `first_seen` comes from the daemon envelope (`first_seen_ms`) instead of the Hub's poll clock, clamped to the observation time and to a Unix-ms sanity floor so neither a clock running ahead nor a seconds-unit bug can distort the irreversible MIN. `last_seen` deliberately stays the Hub's own observation clock, since retention, ordering and the freshness guard compare it and must read one monotonic clock. A finding's age now reflects when the daemon first detected it, not when the Hub first heard about it.
- The test suite runs on xunit.v3 4 over Microsoft.Testing.Platform. The .NET 10 SDK removed the VSTest bridge that xunit 3 relied on, so `global.json` opts into the platform runner, the test project builds as an executable, and `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` and `coverlet.collector` give way to the platform's own coverage and TRX extensions. The coverage engine changed with it, so the exclusion moved rather than disappeared: coverlet dropped generated sources through its `**/obj/**` rule, and the Microsoft collector has no working equivalent, so ReportGenerator now applies the same filter to the report before the gate reads it. Without it the figure fell to 75.19%, not through any lost test but because 1,345 of 2,237 measured lines were source-generated (minimal-API route builders, the configuration binder, the JSON context); with it the baseline sits at 90.81%, next to the 91.84% coverlet recorded. Separately, the coverage run no longer carries a `--filter` allowlist of suite names, so a suite added later is measured by construction rather than by remembering to extend a list, which is how `BackupTests` had escaped it.
- A `backup` CLI command (`PerfSentinelHub backup <destination>`) snapshots the live database with SQLite `VACUUM INTO`, refuses to overwrite an existing destination, cleans up its partial file on failure, and rejects a wrong arity with a usage message instead of falling through to the server. Comes with a `make backup` wrapper and a documented backup and restore procedure. The database volume is the only non-reconstructible state the Hub holds.
