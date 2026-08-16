# Changelog

All notable changes to PerfSentinelHub are recorded here.

## [0.1.0] - 2026-08-12

### Added

- Initial NativeAOT Hub release with durable SQLite findings, authenticated push ingestion, recovery polling, retention, and Helm deployment support.
- `first_seen` and `last_seen` come from the daemon envelope (`first_seen_ms`, `stored_at_ms`) instead of the Hub's poll clock, clamped to the observation time so a daemon clock running ahead cannot mint future observations. A finding's age now reflects when the daemon first detected it, not when the Hub first heard about it.
- A `backup` CLI command (`PerfSentinelHub backup <destination>`) snapshots the live database with SQLite `VACUUM INTO`, plus a `make backup` wrapper and a documented restore procedure. The database volume is the only non-reconstructible state the Hub holds.
