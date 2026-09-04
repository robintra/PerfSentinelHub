# Deployment shapes

One question the rest of this documentation does not answer: when the
environments are walled off from one another, does each cluster get its own
Hub, or does one central Hub collect them all. This page states the flows each
shape needs and what the cheapest one gives up, so the answer can be read
before any of it is deployed.

## Every flow, and which way it goes

| Flow                          | Direction | What it carries                                                           |
|-------------------------------|-----------|---------------------------------------------------------------------------|
| daemon to Hub                 | inbound   | the primary findings path, `POST /api/import/findings` with `X-API-Key`   |
| Hub to daemon                 | outbound  | the poll, reachability, and `api/export/report` when a run starts         |
| Hub to trace backend          | outbound  | during a run, and never otherwise                                         |
| browser to Hub                | inbound   | the launcher and the reports it opens                                     |
| IDE plugin or CI job to Hub   | inbound   | `GET /api/findings`, nothing else                                         |
| Hub to api.github.com         | outbound  | the version check, which `Hub:UpdateCheck:Enabled` turns off              |

The Hub never initiates anything toward a CI system. A build runs the engine in
batch, there is no daemon in it, so there is nothing to poll. The same topology
is drawn in [ARCHITECTURE.md](ARCHITECTURE.md).

## Three shapes, and what each one costs

| Shape                       | Network to open       | What works                                                                              | URL for machine clients |
|-----------------------------|-----------------------|-----------------------------------------------------------------------------------------|-------------------------|
| one Hub per cluster         | nothing               | everything                                                                              | one per environment     |
| central Hub, both ways      | two flows per cluster | everything                                                                              | a single one            |
| central Hub, push only      | one flow per cluster  | findings yes, reachability and daemon row unfolding and runs on a daemon source no      | a single one            |

A Hub deployed in the cluster it collects reaches its daemons and its trace
backends over ClusterIP, so the first shape opens no firewall rule at all. It
pays for that in URLs: every machine client has to know one address per
environment, and there is no screen that shows the whole fleet at once.

A central Hub inverts that trade. One address, one fleet screen, and one set of
firewall rules per cluster to maintain. Opening both directions keeps every
function. Opening only the inbound one is cheaper and costs the functions
listed below, which is a real choice rather than a degraded install.

## What a poll catches up, and what it cannot

The Hub polls `api/findings?limit=1000&include_acked=true` immediately at
startup and then on every `Hub:PollInterval`. That resynchronises the current
contents of the daemon's buffer. It is not a journal. Whatever the buffer
evicted while the Hub was away is gone, and no limit brings it back. A poll
that returns exactly the cap is logged as possibly truncated, because the
snapshot may be short of what the daemon holds. See
[LIMITATIONS.md](LIMITATIONS.md).

## What push alone gives up

Four things need the Hub to reach the daemon rather than the reverse.
Reachability is written by the poll and by nothing else, so a push-only source
reports no last success. Unfolding a daemon row in the fleet screen reads that
source on demand. Launching a run against a daemon source starts by fetching
`api/export/report` from it. And a finding that stopped recurring during an
outage is never pushed again, because only recurrence pushes it.

One thing bounds how much any of that matters. The daemon's exporter pushes a
signature the moment it is discovered or its severity worsens, then refreshes a
still-active one at most once an hour, so a problem that persists comes back on
its own within the hour, poll or no poll. What the poll
would have recovered is the finding that went quiet, and the window between a
restart and the next natural refresh.

## When a central Hub cannot reach a backend

Nothing becomes unanalysable. The launcher prints every run as an engine
command line, so a backend the central Hub cannot reach stays analysable from a
terminal that can reach it. See [LAUNCHER.md](LAUNCHER.md).

## What has to be true of the address

The Hub must be served at the root of an origin, and it authenticates none of
its readers. Both constraints shape the reverse proxy in front of it rather
than the Hub itself, so read them before writing the ingress. See
[LIMITATIONS.md](LIMITATIONS.md).

## The CI reads, it does not feed

A build produces SARIF and JSON, and neither belongs in the Hub. `last_seen` is
the Hub's own observation clock, `status` is derived from an endpoint that
still heartbeats, and a build is a synthetic shot against a branch. Importing
one would make `status` false and would let a branch's findings read as
production's. A CI job that wants the fleet's state calls `GET /api/findings`
like any other reader. See [API.md](API.md).
