# Changelog

All notable changes to PerfSentinelHub are recorded here.

## [Unreleased]

### Added

- `GET /api/sources/{sourceId}/daemon` reads one daemon's applied settings and its own account of
  its state: the `[daemon]` section relayed verbatim, the detection thresholds and carbon scoring
  its export carries, three gauges against their caps, and the tuning hints the daemon writes
  about itself. None of that is in `/metrics`, which carries the counters behind the hints but
  neither the settings nor the sentences. Read on demand rather than polled, because settings
  never change without a restart the Hub has no signal for and a stale gauge would defeat the
  point. The Hub derives one thing, whether a gauge crossed 90 % of its cap, on the same line the
  daemon's own monitor draws. Every recommendation is the daemon's.
- `--brand-text` moves from `#11845d` to `#107a56` in the light theme. The brand green fell to
  4.09 against the active tab's own background, 4.12 on `--surface-3` and 4.37 on `--surface`, so
  the tab label, the settings disclosure and the footer link all sat under AA. The new value's
  worst case is 4.65, and the perceived shift is one step of lightness on the same green. The
  engine's dashboard template carries the same token and moves with it.
- The declared-environment chip takes `--text-2` on an unreachable row. `--text-3` is calibrated
  against the grey surfaces and lands at 4.49 on that row's red one.
- `--text-3` moves from `#7d8c83` to `#95a199` in the dark theme, which finishes an accessibility
  pass that had only been done on the light one: the old value fell to 3.97 against `--surface-2`
  and 3.50 against `--surface-3`, under the 4.5 WCAG AA asks of text this size. Measured on the
  rendered pages with translucent layers composited, every screen now passes in dark. The same
  value is corrected in the engine's dashboard template, which is where these tokens come from.
- The generated config is named `perf-sentinel.toml`, without the leading dot, and the printed
  command names it with `-c`. A downloaded file may not keep a leading dot, so the dotted name
  the command used to ask for was one the reader could not count on having.
- The generated `perf-sentinel.toml` can be downloaded, not only copied. Copying left the
  reader to create the file themselves, which is the one step between the thresholds they set
  here and a run that honours them.
- The fleet version summary counts only the sources running behind the Hub's engine. A fleet
  ahead of it, which is a normal moment during a rollout, drew an amber warning whose arrow
  pointed from the newer version down to the older one.
- The Hub says "update available" when a newer release exists. It asks the GitHub releases API once a day what the
  newest published engine and Hub versions are, and the version chip names whichever of the two is
  behind, with a link to its release notes. A source running behind the Hub's own engine now says
  where to get the newer one, and for a daemon that is the chart it is deployed from rather than a
  binary. This is the Hub's only outbound request that does not go to a configured source, so it
  has its own configuration section, its own endpoints, and `Hub:UpdateCheck:Enabled` turns it off.
  Off, or when the request fails, nothing is shown rather than a claim of being current.
- Native controls follow the theme: `color-scheme` is declared per theme, so the number steppers
  stop rendering in the browser's light scheme on a dark page.
- The re-read interval opens below its control instead of over it. A native select's menu is
  placed by the OS and no CSS moves it, so the control opts into `appearance: base-select` and the
  page paints and positions the picker itself. Browsers without it keep the native menu.
- Static assets answer `no-cache`. Nothing under `wwwroot` carries a fingerprint, so a browser
  choosing its own freshness could run an old `app.js` against a new API after an upgrade.
- `Hub:Analysis:MaxTracesCap` is bounded by the engine's own limit on `--max-traces` rather than
  by an unrelated 100000. A Hub configured in the gap accepted a count, drew it, printed it in the
  copyable command, and only then failed the run on an engine argument error.
- The dashed outline that marks a declared value reads at 3.5:1 rather than 1.2:1, on its own
  `--dash` token so the two places that draw it cannot drift apart.
- Max traces carries a "?" saying what it does, and the number itself takes the band's colour
  once the count stops being comfortable.
- The auth-header step shows the line that sets the variable, in the chosen shell's own syntax,
  rather than naming a variable and leaving the reader to work out the rest. A source the Hub
  reaches without a header shows none of it.
- Printed commands carry a tab per shell, POSIX or PowerShell, each with its own quoting and line
  continuation. The first visit opens on the platform's own, and the choice is remembered.
- Scrollbars follow the theme, using the dashboard's own three rules rather than the browser's
  default grey.
- A detection threshold's field carries the engine's default rather than showing it as a
  placeholder, so the spinner steps from 10 to 11 instead of jumping to the minimum. Each one can
  be put back on its own, and a button puts every one back at once.
- The printed command's prerequisites are a numbered "what you need first" block in the product's
  info tone, separate from the prose that only explains.
- The launcher remembers which source was last chosen and whether the advanced panel was open.
- The launcher's sink blocks carry the `//` overline every other heading on that screen has, and
  the one listing what a run hands back folds, closed until the reader opens it.
- A daemon row's re-read has its own deadline instead of waiting for the one-second countdown
  ticker to notice it is due, so the read lands when the disc closes rather than up to a second
  after it.
- The gauge strip no longer clips what rises out of it, so a move badge is not cut in half on its
  way up. Its rounded corners come from the cells at each end instead.
- The gauge move badge rises at a constant speed, its travel and its fade being two animations
  rather than one set of keyframes carrying both, and it starts clear of all but the top of the
  figure instead of halfway down it.
- A daemon row's gauges float what moved since the previous read over the figure itself, out of
  the flow and unselectable, so it neither widens a cell nor lands in a copied selection, and they take a tone once
  they are within 75 % or 90 % of the cap they publish.
- The footer's credit sits in the middle of the bar now that it is alone in it.
- The footer's step strip is gone. It was static markup shown on every screen, it described only
  the analysis form, and one of the four steps it named had no section on that form either.
- Folds are remembered between visits in the browser's own storage, and a fold closed over open
  children keeps them open for the next time it is opened.
- The daemon row's terminal block folds, closed by default, and sits one surface step above the
  row it is in so it reads as a block rather than as more of the same field.
- An unfolded daemon row says who it is written for, and that its settings are changed where the
  daemon is deployed rather than from the Hub.
- The terminal blocks' prose reads at 12px like the rest of the screen rather than at the 11.5px
  floor, and an inline code chip takes the size of the sentence it sits in instead of a fixed one.
- Clicking the interval select no longer leaves a focus ring behind. Tabbing to it still draws one,
  and so does the first key pressed after a click.
- The monitor command carries `--refresh` at the interval the daemon row is re-reading on, and
  follows the reader's choice as they change it. Off drops the flag rather than printing a zero.
- Every printed command now says it needs the `perf-sentinel` binary on the machine it is typed
  into, and links the release of the version this Hub runs. The analysis block asks "prefer your
  terminal?" rather than offering itself as an afterthought.
- An open daemon row re-reads itself on a status-only request and takes the full export at most
  once a minute, and a row whose read failed keeps asking with that same cheap request until the
  daemon answers, then reads in full. A row left open now recovers on its own.
- A report rendered from a daemon source is rendered with `--daemon-url` when the daemon's base
  URL is a bare origin and the configured engine binary takes that flag, which the Hub reads from
  `report --help` at startup rather than assuming from the version, so its Refresh and acknowledgment controls work from the viewer's browser
  once the daemon's CORS allows the Hub's origin. A backend run against an authenticated source
  now works at all: the source's auth header reaches the engine through the environment variable
  the printed command names, never through an argument a process list could show.
- The daemon view also publishes the engine's own defaults, so the fold marks what a daemon
  changed and shows the default beside it, the way the engine's `query monitor` does in its
  Config tab. The defaults belong to the binary this Hub embeds, which is the same approximation
  the monitor makes, so the response names the version they came from and the view says so when
  the daemon runs a different one.
- A daemon's row re-reads on an interval the reader picks, off or 5 to 60 seconds, with the
  countdown to the next read beside it, and a disc that fills over the interval and carries
  straight into the next one. The same knob `query monitor --refresh` carries, plus an
  off position the command has no use for: a terminal session is opened to watch, a table row is
  often opened to read one setting. A read only replaces the gauges and the hints, never the
  settings, which do not change without a restart, so open groups and focus survive it. Folding
  the row stops the reads.
- Each settings group folds on its own and starts folded, with the number of settings it holds and
  how many of them depart from the engine default. Folded is the useful state: eight headings fit
  on a glance and the counts say which one to open.
- Setting names the daemon writes in backticks render as code rather than as prose, the way the
  dashboard does. The terminal monitor strips those backticks because a terminal cannot draw them,
  and a browser can. Inline code now carries the dashboard's own chip treatment, a background and
  a border rather than a font change alone, which is what makes it read as code at all.
- Type sizes follow the dashboard's own scale: prose never drops below 11.5px, and anything
  smaller is a glyph, a badge or a short value. Several labels and countdown lines had been set at
  10.5px, which that scale reserves for things that are not sentences.
- Fleet health's headings align with their own cells. The last-success, unreachable-for and
  producer columns right-align their values while their headings stayed left, and the producer
  column disagreed with itself: two of its three cases aligned right and the third did not.
- Line length is one value across the stylesheet, 120ch, replacing limits between 58ch and 80ch
  that each resolved to about half the room their column had. Measured on the rendered screens,
  every block now wraps to the same number of lines it would with no limit at all, while a line
  still stops short of 170 characters. Six blocks were costing a line for nothing, among them the
  sentence under the run button and the gloss under each detection threshold.
- A link that follows prose inside a notice is spaced from it, rather than reading as one more
  line of the sentence above.
- The daemon panel no longer inherits the sources table's monospace face. A table of values is
  right to be monospaced and a panel of prose is not, and the two had been the same thing since
  the panel is a cell of that table. Names and values stay mono by asking for it.
- The fleet health screen unfolds a daemon's row onto that view, and the launcher prints the run
  it would submit as an engine command line for an operator who would rather use a terminal. The
  command is built from the object the form posts rather than from the form, so the two cannot
  drift, and values are quoted for a POSIX shell. Detection overrides have no flag, so a run that
  changed one prints the `.perf-sentinel.toml` it needs beside the command. An authenticated
  source prints `--auth-header-env` and never its token.
- `GET /api/sources` now also carries `base_url`, `engine_subcommand` and `auth_header_name`, the
  three things a command line needs. The header's name and never its value: the value stays in
  the Hub's configuration.

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

- A run may override the engine's detection thresholds, exposed behind an advanced disclosure in
  the launcher and published with their bounds and defaults on `GET /api/status`. They are written
  to a per-run TOML handed to both engine invocations through `-c`. They change what counts as a
  problem rather than how large the report is, so a run records the ones it used and the recent list
  flags counts that are not comparable.
- Both engine invocations now run from a Hub-owned directory. The engine discovers
  `.perf-sentinel.toml` relative to its working directory, so an unset one let a stray file beside
  the Hub's launch directory silently decide detection thresholds. Measured: one threshold moved a
  report from 5,455,307 to 559,514 bytes.
- The result panel reports how many findings survived the sink's budget, read back from the rendered
  report. The summary is parsed from the engine's pre-trim output, so the card previously stated a
  count the artifact did not contain.

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
