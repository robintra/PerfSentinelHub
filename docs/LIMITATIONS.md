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

The Hub records which days each source observed a finding, and that record
starts the day the version that writes it is deployed. Nothing is backfilled:
for an earlier day the Hub holds only a finding's `first_seen` and `last_seen`,
not the days in between. A source's own copy of a finding starts the same way,
at its first observation after the upgrade.

A `from` and `to` window on `/api/findings` matches whole days, cut at midnight
UTC on the Hub's clock, so a window narrower than a day still lists whatever a
source observed at any hour of that day. Presence is assumed where nothing was
recorded: between a source's `first_seen` of a finding and the first day the Hub
recorded for it, and up to its `last_seen` for a pair with no recorded day at
all, which is every pair of a database that predates the record. Without that
rule an upgraded Hub would read as empty over its whole past. With it, a finding
that went away and came back before its first recorded day reads as continuous.

Retention (`Hub:Retention`) removes a recorded day while the finding itself can
stay, and the first recorded day does not move. A window that falls wholly on
removed days after that first one therefore lists nothing, even for a finding
present all along.

The Hub mirrors each daemon's active acknowledgments and never owns them. The
mirror is as fresh as the last poll of that daemon, unless an ack relayed
through the Hub refreshed it since, so an ack taken or revoked at the daemon
itself shows at the next poll. A daemon below 0.24.0 is never asked, because it
cannot list its CI baseline, and its findings are judged on their envelope
alone, as before.

A daemon listing 1,000 acks or more has reached its own cap, and the Hub files
that read `truncated`. The acks it did list appear in `acks`, but a listing with
a missing tail cannot say a finding is not acknowledged, so it does not decide
`include_acked=false` and that source falls back to the envelope.

The ack relay writes a runtime ack at one daemon, the one behind the source
named in the route. A finding three sources carry takes three acks, and nothing
fans one out. An ack held by a daemon's CI baseline cannot be revoked through
the relay, nor through the daemon's own API: the daemon answers `404`, and the
baseline changes by editing its file under review. A revoke is relayed for a
finding the Hub holds at that source, or for an ack it mirrors from it. Once
retention has removed the finding, its ack can be revoked from the Hub only
after a poll has listed it, which never happens with a daemon below 0.24.0. See
[API.md](API.md#ack-relay).

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

## What the Hub does not authenticate

This section describes the Hub with `Hub:Auth:Enabled` off, the default. On, the
Hub signs browser users in itself and only the machine routes stay open, see
[AUTHENTICATION.md](AUTHENTICATION.md).

One endpoint asks for a credential, `POST /api/import/findings`, whose
`X-API-Key` is compared by fingerprint. Every other one asks for nothing:
`/api/status`, `/api/sources`, `/api/findings`, `/api/analyses` for both the
listing and the request that starts a run, `/reports/`, `/metrics`, and the
launcher itself. Whoever reaches the port reads every finding of every tenant,
SQL query shapes and endpoint names included.

The `by` and `reason` of a mirrored ack are readable on the open read route,
`/api/findings`, which stays open to machines with `Hub:Auth` on. That is who
acknowledged a finding and why, in the words typed at the daemon, as
`acknowledged_by` already was.

The identity header attributes, it does not authenticate.
`Hub:Analysis:IdentityHeader` is recorded on a run as a claim some proxy made,
and the Hub verifies nothing about it. That is the right behaviour behind an
authenticating proxy and no defence at all without one. If the network is not
the boundary, put that proxy in front or turn on `Hub:Auth`. See
[DEPLOYMENT.md](DEPLOYMENT.md).

The ack relay knows who asks and has no roles. Any caller the Hub can name, a
signed-in user or, once `Hub:AckRelay:TrustIdentityHeader` is set, whoever the
proxy header names, may ack or revoke on every source that carries an ack
credential. The daemon has no per-user roles either: its ack key is one key, and
the Hub holds it. Restrict who signs in on the provider side, and leave the
credential off a source nobody should ack from the Hub. The name sent along
feeds the daemon's audit trail and authorizes nothing there. The daemon keeps
256 bytes of it, so a long name that had to be percent-encoded may be cut.

The relay has no antiforgery token and no CORS policy, so it reads
`Sec-Fetch-Site` and refuses every value but `same-origin`, `cross-site` and
`same-site` and the `none` a typed address carries alike. A caller that sends no
such header at all passes, since curl, a CI job and an IDE plugin do not set it.
What defends that case is the content type alone: the Hub takes a JSON content
type and nothing else, which a cross-origin form cannot produce and a
cross-origin fetch cannot send without a preflight the Hub never answers. That
is a floor and not a boundary, and the floor is the relay's alone:
`POST /api/analyses` and `POST /api/incidents/refresh` judge neither the site
nor the content type. A Hub reachable from a browser session therefore belongs
behind a proxy that does not forward arbitrary origins.

## What sits outside the Hub

A live report needs two things the Hub does not control: the daemon's
`[daemon.cors] allowed_origins` must carry the origin the Hub serves reports
from, and the viewer must be able to reach that daemon directly, at its `PublicUrl`
when the Hub reads it by a name only the cluster resolves. A daemon behind
a path-based ingress gets a static report instead, because the engine's
`--daemon-url` takes an origin and nothing else. See [LAUNCHER.md](LAUNCHER.md).

The Hub has the cousin of that constraint and must itself be served at the root
of an origin. The launcher calls `/api/status`, `/api/sources`, `/api/analyses`
and `/reports/` as absolute paths, nothing rewrites them, and a path-based
ingress leaves the browser asking for a prefix the Hub never answers. See
[DEPLOYMENT.md](DEPLOYMENT.md).

## Scale and observability

One replica, and not a knob. SQLite has a single writer and the volume is
`ReadWriteOnce`, so the chart sets `replicas: 1`.

`GET /metrics` covers reachability, the analysis queue, run counts and the
stored findings of each environment, and nothing else. Findings are counted per
environment, type, severity and status, never per service or per endpoint, and
the counts are up to 15 seconds old. There is no series for retention purge
duration or for import throughput, so an alert on those, or on one service's
findings, has to read `/api/findings` or the logs. See
[OPERATIONS.md](OPERATIONS.md#metrics).
