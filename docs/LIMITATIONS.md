# Limitations

Everything here is stated somewhere else in these documents, next to the feature
it constrains. This page gathers it so you can read the boundaries in one pass
before deploying, rather than discovering one at a time.

## What the Hub declares rather than measures

`Environment` and `RetentionHours` are taken from configuration as written and
never checked against anything. A misconfigured deployment can label production
as staging, and nothing contradicts it. The launcher marks the declared half of
a row with a dashed outline. See [CONFIGURATION.md](CONFIGURATION.md).

A trace backend is never polled. Only a daemon is, so a Tempo or Jaeger query
source shows no producer version and no last success. That is the design, not a
fault.

## What a poll can and cannot tell you

The perf-sentinel 0.11.x daemon caps `/api/findings` at 1,000 rows. The Hub uses
that exact cap and warns whenever it is reached, because the snapshot may be
incomplete. High-volume coverage needs the bounded push exporter, not the poll.

A poll that omits a finding does not resolve it. The daemon's ring buffer may
have evicted it, so missing is not the same as gone. Only retention removes a
row.

Reachability is one-directional. A daemon pushing successfully proves it can
reach the Hub, not that the Hub can reach it, and only a successful poll clears
`unreachable_since_ms`. A source whose push arrives while its poll fails still
reports `unreachable_since`. See [OPERATIONS.md](OPERATIONS.md).

## What `status` and `lineage` claim

`status` is a presumption, not a verdict. It is derived at read time from data
the Hub already holds, and `likely_resolved` means the endpoint still heartbeats
without the finding, not that someone fixed it.

Lineage links a mutated signature to its predecessor only when exactly one
stored finding matches. Ambiguity records nothing, because naming one of several
candidates would be a guess. See [API.md](API.md).

## What a run does not promise

Reports are deleted after `Hub:Analysis:ReportRetention`, 24 hours by default.
This is not an audit trail, and a link shared yesterday is already dead. The run
keeps its parameters, so it can be launched again as it stands.

A run still going when the service stops comes back `interrupted` and is never
replayed on its own. A silent retry would fire a second heavy query at a backend
nobody asked to query twice.

Report size is not a knob. The sink's 5 MiB target is a private constant with no
flag, no environment variable and no config key. When it drops findings to fit,
the run records how many survived, read back from the rendered file.

The eight failure codes come from a heuristic on a bounded set of markers in the
engine's stderr, not from a contract. Raw stderr never leaves the process.

Counts from runs with different detection thresholds are not comparable. Raising
a threshold does not make a run lighter, it stops the detector from reporting
the smaller cases.

## What sits outside the Hub

A live report needs two things the Hub does not control: the daemon's
`[daemon.cors] allowed_origins` must carry the origin the Hub serves reports
from, and the viewer must be able to reach that daemon directly. A daemon behind
a path-based ingress gets a static report instead, because the engine's
`--daemon-url` takes an origin and nothing else. See [LAUNCHER.md](LAUNCHER.md).

## Scale and observability

One replica, and not a knob. SQLite has a single writer and the volume is
`ReadWriteOnce`, so the chart sets `replicas: 1`.

`GET /metrics` covers reachability, the analysis queue and run counts, and
nothing else. There is no series for stored findings, for retention purge
duration, or for import throughput, so an alert on those has to read
`/api/findings` or the logs. See [OPERATIONS.md](OPERATIONS.md#metrics).
