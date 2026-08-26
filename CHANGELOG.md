# Changelog

All notable changes to PerfSentinelHub are recorded here.

## [0.1.0] - 2026-08-12

### Added

- Initial NativeAOT Hub release with durable SQLite findings, authenticated push ingestion, recovery polling, retention, and Helm deployment support.
- `first_seen` comes from the daemon envelope (`first_seen_ms`) instead of the Hub's poll clock, clamped to the observation time and to a Unix-ms sanity floor so neither a clock running ahead nor a seconds-unit bug can distort the irreversible MIN. `last_seen` deliberately stays the Hub's own observation clock, since retention, ordering and the freshness guard compare it and must read one monotonic clock. A finding's age now reflects when the daemon first detected it, not when the Hub first heard about it.
- The test suite runs on xunit.v3 4 over Microsoft.Testing.Platform. The .NET 10 SDK removed the VSTest bridge that xunit 3 relied on, so `global.json` opts into the platform runner, the test project builds as an executable, and `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` and `coverlet.collector` give way to the platform's own coverage and TRX extensions. The coverage engine changed with it, so the exclusion moved rather than disappeared: coverlet dropped generated sources through its `**/obj/**` rule, and the Microsoft collector has no working equivalent, so ReportGenerator now applies the same filter to the report before the gate reads it. Without it the figure fell to 75.19%, not through any lost test but because 1,345 of 2,237 measured lines were source-generated (minimal-API route builders, the configuration binder, the JSON context); with it the baseline sits at 90.81%, next to the 91.84% coverlet recorded. Separately, the coverage run no longer carries a `--filter` allowlist of suite names, so a suite added later is measured by construction rather than by remembering to extend a list, which is how `BackupTests` had escaped it.
- A `backup` CLI command (`PerfSentinelHub backup <destination>`) snapshots the live database with SQLite `VACUUM INTO`, refuses to overwrite an existing destination, cleans up its partial file on failure, and rejects a wrong arity with a usage message instead of falling through to the server. Comes with a `make backup` wrapper and a documented backup and restore procedure. The database volume is the only non-reconstructible state the Hub holds.
