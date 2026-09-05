# The launcher

The Hub serves a browser interface at `/`, from the same origin as the reports it opens.
Plain HTML, CSS and JavaScript. No framework, no build step, no network fetch: the two
typefaces are base64 in `wwwroot/fonts.css` and every icon is inline SVG.

Five screens: start an analysis, follow one run, list recent runs, read fleet health, read
the incidents the daemons recorded.

## The form follows the source

The form adapts to the selected source's `kind` rather than offering an independent
live-or-historical switch. A switch would let an operator compose impossible states, such
as a three-hour window against a daemon that keeps ten minutes.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-sources-dark.png">
  <img alt="The fleet health screen with a daemon row unfolded" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-sources.png">
</picture>

## Gauges

A gauge is toned once it is close to a cap it published. Red from 90 %, the engine's own
advisor line and the same one that turns a row's verdict to near capacity. Amber from
75 %, the Hub's own step ahead of it.

Each read shows what moved since the one before, rising off the figure it belongs to and
fading out: red for a rise, green for a fall. Every one of these counts toward a cap, so
up is the direction that costs something. Uptime is neither toned nor tracked, having no
cap and only one direction to go in.

## What the browser remembers

Which folds a reader has opened, in that browser's `localStorage`, under one key and as
open folds only. A row, its terminal block, its settings and the groups inside them come
back the way they were left, and a row left open reads its daemon again on the next visit
without being clicked.

Nothing but those names is stored. A browser that refuses storage simply starts everything
folded.

The theme is tri-state: system, light, dark. Only the resolved light or dark ever reaches
the DOM, so stylesheets see two values and never three. The position is stored under
`perf-sentinel:theme` in both `localStorage` and `sessionStorage`, the second because the
rendered dashboard reads that exact key from this origin. That handoff is why the launcher
and the reports must share an origin.

## Printed commands

Every printed command carries a tab per shell, because the difference is not cosmetic. A
POSIX shell continues a line with a backslash and escapes a quote by closing and
reopening. PowerShell continues with a backtick and doubles the quote, and its bare-word
set is narrower since a comma is its array operator.

The tab a first visit opens on follows the platform, Windows getting PowerShell. The
reader's own choice is remembered from then on and applies to every command on the page at
once.

Neither command carries a placeholder. The endpoint is the source's own configured
`BaseUrl`, and the monitor command carries the re-read interval the reader picked on that
row, so a copied line is runnable as it stands and does not contradict the screen it came
from. The one thing an operator still types is the service name, which is theirs to choose
and is shown empty rather than guessed.

Both commands say where to get the engine, since neither runs through the Hub. The note
links the release of the exact version this Hub runs, which is the version the flags are
spelled for. Without a probed version the link falls back to the release list rather than
inventing a tag.

### The run as a command line

The launcher prints the run as an engine command line, so an operator can take it to a
terminal instead. It is built from the very object the form posts, never from the form, so
the printed command and the submitted run cannot drift.

It is one command and not the two the Hub runs: the JSON output and the render step exist
so the Hub can build a dashboard, and a terminal needs neither.

Values are quoted for a POSIX shell with single quotes, the only form that holds for a
service name carrying `$` or a quote. An authenticated source prints `--auth-header-env`
rather than its token, which the Hub holds and never discloses.

Detection overrides have no command-line flag, so a run that changed one carries
`-c perf-sentinel.toml`, and the file is printed beside the command, ready to copy or to
download. The name is undotted because the engine only discovers the dotted
`.perf-sentinel.toml`, which a download may not preserve, so the command names the file
rather than relying on that discovery.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-report-dark.png">
  <img alt="A rendered report opened inside the launcher" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-report.png">
</picture>

## Live reports

A report rendered from a daemon source goes live when the daemon's `BaseUrl` is a bare
origin. The render passes `--daemon-url`, and the dashboard's own Refresh and
acknowledgment controls then talk to that daemon from the viewer's browser.

Two conditions sit outside the Hub: the daemon's `[daemon.cors] allowed_origins` must
carry the origin this Hub serves reports from, and the viewer must be able to reach the
daemon directly.

A daemon behind a path-based ingress gets a static report instead, because the engine's
flag takes an origin and nothing else. So does every daemon source when the configured
binary does not take `--daemon-url` at all: the engine declares it inside its `daemon`
feature, so a binary built without that feature refuses the argument rather than ignoring
it, and a run passing it would render nothing. The Hub asks the binary once at startup,
through `report --help`, and renders static when the answer is no or unreadable.

Report links share within network reach of the Hub and die with the retention window.

## The fleet health row

A daemon's row unfolds into the gauges it reports against their caps and the hints it
writes about its own tuning, neither of which `/metrics` carries.

The row re-reads on an interval the reader picks, the same knob `query monitor --refresh`
carries plus an off position. A read replaces only the gauges and the hints: settings do
not change without a restart, so rebuilding them would throw away open groups for nothing.
Folding the row stops the reads.

The settings themselves are one click further in, grouped and folded, each group showing
how many of its values depart from the engine's own defaults. The row ends with the
`perf-sentinel query monitor` command for the same view in a terminal.

## The incidents screen

Each row is an incident a daemon recorded when the operator's alerting posted it, in the
order the daemon's own monitor prints the columns: started, service, kind, ended,
findings, capture, source. The daemon is the author and the Hub copies its record on
every poll, so the screen reads what the daemon froze and re-derives nothing.

A row unfolds into the findings the daemon froze for that incident, each placed before
the incident or after the restart from its own stamp against the incident's. The capture
column carries the daemon's reading of how far back its ring still reached: complete,
partial, or empty. Times are relative, never dates, and the exact stamp sits in the
tooltip. A service select narrows the list, and a button loads the next hundred older
rows. A daemon that refused the Hub's key on that route gets a banner naming the setting
to fix, and its findings stay collected.

## Safety

Nothing from the server is ever written with `innerHTML`. Every displayed string is a text
node.
