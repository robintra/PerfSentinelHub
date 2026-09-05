# Changelog

All notable changes to PerfSentinelHub are recorded here.

## [0.1.6] - 2026-09-05

### Added

- The Hub mirrors the incidents a perf-sentinel 0.20.0 daemon records when the operator's
  alerting posts one. Every poll copies the daemon's `/api/incidents` ring, frozen findings
  included, into a table the daemon's own restart cannot empty. A copy is one daemon's
  capture, keyed on the incident's id and the source together, because the id hashes
  nothing of the daemon and two daemons fed the same alert would otherwise collapse into
  one row and lose one ring's findings, and of two captures by the same daemon the richer
  one is kept, because Alertmanager repeats a firing alert and a daemon restarted in
  between would freeze a window its ring no longer reaches. The ring is read a page at a
  time under a 4 MiB body cap, and a page that overflows it is re-read at half the size
  from the same offset down to a single incident, since a daemon embeds up to a thousand
  findings per incident and a full page of a busy daemon would otherwise be filed as an
  error on every poll and mirror nothing. The read carries the source's auth header, so a
  daemon's `[daemon] read_api_key` goes in `AuthHeaderValue` under
  `AuthHeaderName=X-API-Key`, and its outcome lives in an `incident_reads` row of its own
  rather than in the source's reachability: a wrong key, or a daemon before 0.20.0
  answering 404, never demotes the findings that daemon reported on the same poll, and a
  page that is not a JSON array is filed as `invalid_incidents`, apart from the findings
  leg's own code. `GET /api/incidents` lists the copies newest first without their
  findings, which are stored apart so the listing never reads them, and with a `capture`
  verdict derived from the daemon's `oldest_finding_ms`, `GET /api/incidents/{id}` returns
  one whole, the richest copy when several sources hold the id, `/api/sources` gains
  `incidents_state`, `/api/status` publishes `max_read_limit` among its limits, and the
  copies expire with the findings retention on the Hub's clock. The launcher gets a fifth
  screen listing them in the daemon monitor's column order, its pages sized under the
  operator's read limit, each row unfolding into the findings it froze, placed before the
  incident or after the restart from their own stamp.
- The incidents screen reads the daemons when it is opened, rather than serving whatever the
  last poll left behind. An operator paged about an OOM kill opens that screen within the
  minute, and `Hub:PollInterval` defaults to an hour, so a poll-only screen would be empty
  exactly when it matters. `POST /api/incidents/refresh` reads every daemon's ring now and
  answers with the same body and the same parameters as the listing, so the screen costs one
  round trip, and the poll is the floor rather than the only path. A POST because the route
  writes to the store, and because a GET would be cached and prefetched, which a fleet read
  must never be. The
  read leg itself is one class shared by the poll and the route, so the two cannot drift
  on how a refused key, a missing route or an oversized page is filed, and it carries the
  same isolation: those outcomes live in the source's own `incidents_read` row and
  never in its reachability, since a wrong read key says nothing about the findings that
  daemon reports. What bounds it, given every screen open triggers one: the fan-out is
  capped by `Hub:MaxConcurrentPolls` the way the poll worker is, two refreshes run at once
  and a third gets a `503` with `Retry-After`, and a source read in the last ten seconds is
  skipped with its stored copy served instead, so a reload loop or five people watching the
  same screen cannot storm the fleet. That floor is a constant rather than a setting,
  because it protects the daemons from this Hub and is not an operator's preference.
  `/api/sources` gains `incidents_read_ms` beside `incidents_state`, and the screen prints
  one line per daemon saying how long ago its copy was read, or that it was never read: a
  fleet with nothing to report and a copy nobody refreshed would otherwise look identical,
  which is the second half of the same problem.
- The incidents screen shows the namespace an incident carries and filters on service,
  kind, namespace, environment and daemon. The namespace is the alert label a perf-sentinel
  0.20.0 daemon writes on an incident when the alert named one, relayed as the daemon wrote
  it and absent when it did not, so two
  deployments of one service name in two namespaces read apart: a tag for reading and
  filtering, never part of the copy's key. The daemon's own monitor prints namespace and
  service in one cell, the Hub gives the namespace a column of its own so the screen reads
  and narrows on either. The filters are applied by the Hub rather than by the page,
  `GET /api/incidents` and `POST /api/incidents/refresh` take `kind`, `namespace` and
  `environment` beside `service` and `source_id`, so the older rows a button loads next
  follow the same filter instead of the screen narrowing the one page it holds. `service`
  and `namespace` are free strings matched exactly, and an unknown one is an empty page,
  since only the daemons know the set. `kind`, `environment` and `source_id` are closed
  sets, the daemon's five kinds and the Hub's configured sources, and a value outside them
  is a `400` rather than an empty page: an empty incidents screen is the answer an operator
  hopes for, so a typo in a filter that configuration could have checked must not be able
  to produce it. `environment` resolves to every source configured with it, and given
  together with `source_id` the two intersect, so a pair that excludes itself lists
  nothing rather than one of the two quietly ceasing to apply. The
  filters narrow the answer, never the read: a refresh still reads the whole fleet whatever
  the query asks for.
- An unfolded incident carries an Analyse this window button, which opens New analysis
  with the mode set to a service, the incident's service filled in and the range set
  absolute to the window the daemon froze, under a banner naming the incident and, while
  the incidents screen still holds its row, how far the window reaches either side of it,
  otherwise its length. Without it the operator paged about an incident reads the window
  off the row and retypes it into the form, four numbers and a name copied by hand at the
  worst moment to copy anything. The source stays the operator's choice and is
  not part of the link: a daemon takes no window, so the incident's own daemon would run
  its in-memory snapshot rather than the window, and the window has to go to a trace
  backend the operator picks on the left. Under a daemon the banner says so and asks for
  one, instead of the form pretending the pair can run. The window's end is clamped to
  now, in the link when it is built and again when it is read: the daemon's window reaches
  past the alert, so a young incident still has a `window_to_ms` in the future, and the
  Hub refuses a range that ends there, which means the link built the moment the alert
  fires would otherwise open a form that cannot run. Clamping on the read side as well
  holds a hash that reaches the form by any other path to the same rule rather than
  refusing it. The handoff travels as
  `#/new?from=<ms>&to=<ms>&service=<name>&incident=<id>`, so the link keeps its parameters
  and can be shared or reloaded while the New tab itself carries none, and a query on any
  other route leaves the form alone.

### Changed

- The image ships perf-sentinel `0.20.0` as its analysis engine, repinned by digest from
  `0.19.0`. That is the binary the Hub runs for a backend analysis, so a run gets the
  0.20.0 detectors, and it is also the version the launcher compares a polled daemon's
  `producer_version` against, so a fleet still on `0.19.0` now reads one minor behind
  where it read level. The engine is copied from the published image rather than
  downloaded, so the build reaches no host outside the registry, and
  `config/supply-chain.json` carries the same digest as the `Dockerfile`.

### Documentation

- A new page, [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md), answers the question the rest of the
  documentation left to be reconstituted from the code: whether walled-off environments get a
  Hub each or one central Hub, which way every flow goes, and what opening only the push
  direction gives up. It states what a poll actually resynchronises, that the daemon's
  exporter pushes a signature on discovery or on a severity escalation and refreshes a
  still-active one at most hourly, and that a backend a central
  Hub cannot reach stays analysable from the command line the launcher prints.
- `docs/LIMITATIONS.md` now carries two boundaries an operator used to meet at the first page
  load. The Hub must be served at the root of an origin, the launcher calling the API in
  absolute paths, which is the cousin of the daemon constraint already described beside it.
  And a single endpoint asks for a credential, `POST /api/import/findings`, so whoever
  reaches the port reads every finding of every tenant and can start a run. `docs/OPERATIONS.md`
  adds the consequence for `/metrics`, which shares its origin with the launcher.
- The two reachability claims in `docs/ARCHITECTURE.md` say what the code does. The import
  handler touches no reachability column rather than never touching `source_state`, since it
  may refresh an existing row's `producer_version`, and the poll writes reachability through
  its upsert as well as through the two `MarkSource` calls.

## [0.1.5] - 2026-09-03

### Changed

- The image carries the perf-sentinel 0.19.0 engine instead of 0.18.0. That release adds a
  `grouping` label beside `service` on `perf_sentinel_findings_total`,
  `perf_sentinel_slow_duration_seconds` and the three `perf_sentinel_service_*_io_ops_total`
  counters, which is breaking for an unaggregated alert on any of them the way 0.18.0 was for
  `service`: `sum()` or `sum by (service)` around one keeps what it matched before. The value is
  the finding's effective grouping, the namespace on Kubernetes, and `[daemon]
  per_grouping_labels = false` restores the 0.18.0 shape. Nothing the Hub itself reads moved.
  The report JSON keeps its shape, the new per-pair split is `serde(skip)` and in-process only,
  and the engine adds no `[detection]` key, so the ten defaults `GET
  /api/sources/{sourceId}/daemon` compares against still hold and the launcher offers the same
  knobs. The Hub's own dashboard reads `perf_sentinel_hub_*` and is untouched.

### Fixed

- The daemon view carries a default for `per_service_labels` and `per_grouping_labels`, and the
  settings panel draws them under a `metrics labels` group. A daemon publishes the first since
  engine 0.18.0 and the second since 0.19.0, and a setting the panel has no group for is not
  drawn at all, so both were invisible rather than marked as changed.
- The two buttons on a run's outcome panel had no ground of their own, so on the five toned
  panels they took the panel's tone as their face and read as prose with a ring around it. They
  now sit on the ground the insets already use, which separates by lightness on all five tones
  and in both themes. Their hover was `filter: brightness()`, which on the light theme fades a
  pale border towards the panel behind it: the hovered `Check the source` lost its outline where
  it was meant to gain one. Both outlined buttons state a darker edge instead, and the filled
  one keeps the brightening its opaque face was written for.

## [0.1.4] - 2026-09-02

### Fixed

- The launcher's `sanitizer_aware_classification` control was a native select, whose menu the
  OS draws over the button and aligns on the selected item. It now opts into the base
  appearance the refresh select already used, so the list hangs under the button and is
  painted by the page. Two details were measured rather than assumed. The picker asks for
  `max-height: stretch`, so it claims whatever room the axis offers instead of the height its
  four options need, and down the page that reads as too little room below and flips the list
  back over the button. Bounding the height does not change that decision, only dropping the
  position fallbacks does. And without the flex the base appearance leaves behind, the label
  sat at the top-left of the button with the arrow adrift in the corner.
- The four values `sanitizer_aware_classification` accepts, `auto`, `strict`, `always` and
  `never`, are marked as code in its description, where they read as ordinary words of the
  sentence before. The same treatment reaches `strict` and `auto` where
  `sanitizer_aware_min_cv` names them.
- `ParseEngineVersion` splits the probed version on separators passed as an array. As loose
  arguments they partially matched `Split(char, int, StringSplitOptions)`, which is what a
  reader has to rule out before trusting the line.

## [0.1.3] - 2026-09-02

### Changed

- The image carries the perf-sentinel 0.18.0 engine instead of 0.17.0. That release labels
  `perf_sentinel_findings_total` and `perf_sentinel_slow_duration_seconds` by service, which
  is breaking for an unaggregated alert on either, and it resolves a span with no service name
  to `unknown` on every ingestion path, so an acknowledgment taken on a Zipkin or Jaeger
  finding with a blank service has to be re-taken. It also adds `[detection]
  sanitizer_aware_min_cv`, which is the knob the launcher withholds below this version, so the
  advanced panel now offers it. The engine gains that one `[detection]` default and leaves the
  other nine unchanged, so the table `GET /api/sources/{sourceId}/daemon` compares against
  still holds for them.

### Fixed

- Two visual faults on a run's result panel. The `tuning` inset sat on
  `--surface-2`, close enough in lightness to `--crit-bg` that it dissolved into
  the panel behind it in the dark theme. It now darkens through whatever tone is
  behind it, which takes the lightness ratio between the two from 1.27 to 2.42.
  The light theme keeps its opaque white ground, which already sat above the
  pastel panels, and gains only the firmer border. And the count strip wrapped into a second row on a narrow
  viewport, where the cells ending each line are square while the container is
  round, so its outline broke at the top right and the bottom left. It stacks
  instead, each cell carrying its label beside the figure.

### Added

- The launcher's advanced panel offers the engine's two sanitizer settings on a backend
  run: `sanitizer_aware_classification` as a choice among `auto`, `strict`, `always` and
  `never`, and `sanitizer_aware_min_cv`, the timing-variance threshold that decides
  whether a run of identical parameterised queries reads as an N+1 or as a cached repeat,
  as a decimal from `0.01` to `10`. Both are recorded on the run and written to the
  per-run config like the eight thresholds before them. `detection_knobs` in
  `GET /api/status` now names each knob's `kind` (`integer`, `decimal` or `choice`) and,
  for a choice, its `choices`, with the default relayed as a JSON value rather than typed.
  A knob the probed engine's `[detection]` does not read is withheld from the list and
  refused on submission with a 400 naming both versions, rather than written to the run
  config and refused by the engine at run time with a stderr the Hub never returns. The
  mode has been read since engine 0.5.7 and is offered to every engine, the variance
  threshold appears only once the embedded binary is 0.18.0 or later.

## [0.1.2] - 2026-08-31

### Fixed

- The chart refuses to render while `image.digest` is still the all-zero release placeholder, and
  names the flag to pass. The placeholder satisfies the digest shape guard, so an install from a
  checkout that forgot `--set image.digest` used to reach the cluster and fail there as
  `ImagePullBackOff` rather than at `helm template`. A test now also asserts the committed chart
  carries the image helper `scripts/verify-release.py` pins, which until now was compared only at
  release time, against the packaged chart.

## [0.1.1] - 2026-08-30

### Changed

- The image carries the perf-sentinel 0.17.0 engine instead of 0.16.0. That release fixes
  `tempo` and `jaeger-query` dropping the spans they had already correlated, which are the two
  subcommands a run against a trace backend invokes, so such a report now carries span trees
  where 0.1.0 rendered none. It also bounds the cross-trace correlator's refused-pair set, which
  could OOM a daemon on a wide topology. The engine's own defaults are unchanged between the two
  versions, so the table `GET /api/sources/{sourceId}/daemon` compares against still holds.

### Fixed

- `/api/status` and the `perf_sentinel_hub_build_info` metric report `0.1.1` rather than `0.1.1.0`.
  Both read `AssemblyVersion`, which .NET pads to four components, so the version the Hub published
  about itself matched neither its own tag, its chart `appVersion`, nor its image label, and a
  dashboard joining the metric's `version` label against a release tag never matched. Both now read
  the informational version, through one accessor rather than two copies. The launcher already
  ignored the fourth component when comparing, so nothing downstream changes.

- `scripts/release.sh` runs the lab validation gate itself instead of leaving it to the operator's
  memory, and takes `--skip-lab` to bypass that one gate for a release the lab cannot tell apart
  from the last it validated. The bypass never writes the ledger and warns twice, so a skipped lab
  leaves a trace rather than a false PASS. `release-gate/check-lab-validation.sh` had existed since
  the first release without a single caller.

- `scripts/check-badges.py` runs in CI, validates `README-FR.md` alongside `README.md`, and reads
  each badge's evidence file instead of only checking that it exists. It had none of the three: no
  job ran it, so a README and a canonical block that disagreed passed every pull request, and the
  only job that touched it ran unit tests building their own README from their own copy of the
  badge set. That copy is now imported from the checker, so the two cannot drift.

- `image.repository` defaults to `ghcr.io/robintra/perf-sentinel-hub` rather than the bare name
  `perf-sentinel-hub`. The published chart already carries a stamped `image.digest` naming a
  GitHub Container Registry image, so a bare repository left half the identity unresolved and an
  install that did not override it ended in `ImagePullBackOff` against Docker Hub.

### Documentation

- The README carries its latest-release, container image and Helm chart badges again, all three
  having been removed while nothing was published for them to point at. The release badge takes the
  `512BD4` of the .NET one rather than the shields default orange.

- The launcher screenshots and both tour GIFs are regenerated against this version, so the version
  chip they show matches the release rather than the one before it.

## [0.1.0] - 2026-08-30

### Added

- Initial NativeAOT Hub release with durable SQLite findings, authenticated push ingestion, recovery polling, retention, and Helm deployment support.

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

- The launcher is checked against the real engine: a new `tests/launcher-e2e.test.js` writes the
  generated config, runs the printed command, and asserts every threshold reaches the engine under
  the name it calls it back. It skips itself where there is no engine build.

- The generated config is named `perf-sentinel.toml`, without the leading dot, and the printed
  command names it with `-c`. A downloaded file may not keep a leading dot, so the dotted name
  the command used to ask for was one the reader could not count on having. Its caption now says
  where to put the file and that `-c` makes it required, instead of naming a second, nearly
  identical file the reader has nothing to do with.

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

- JSON responses use snake_case property names, matching the envelope perf-sentinel itself emits.
  Every pre-existing field is a single word and serialises identically under either policy, so no
  response changed shape.

- `GET /metrics` serves the Prometheus text format, eight metric families over data the Hub already
  holds. `Api/MetricsEndpoint.cs` writes that format by hand rather than pulling a client library,
  which would be a third package in a NativeAOT service whose only two are SQLite. Cardinality is
  bounded by configuration rather than by requests: `source` takes the ids in `Hub:Sources`, fixed
  at startup and restricted to 1 to 64 ASCII letters, digits, `.`, `_` or `-`, and `status` takes
  the six constants in `AnalysisStatuses`. Nothing a caller sends reaches a label. Label values are
  escaped for backslash, quote and newline anyway, so that loosening the source id rule later
  cannot silently produce a scrape Prometheus rejects. The endpoint carries no authentication,
  exactly like `/api/status`, so keep it behind whatever fronts the rest of the Hub.

- A daemon the Hub has never observed publishes nothing rather than a value that reads healthy.
  `perf_sentinel_hub_source_reachable`, `perf_sentinel_hub_source_unreachable_seconds` and
  `perf_sentinel_hub_source_last_success_seconds` are written only for a source that has a
  `source_state` row, and only for a daemon at all: a trace backend is never polled, so calling one
  reachable would assert something the Hub has not seen. A 1 for a source never attempted reads as
  a poll that succeeded, and a 0 for one never polled successfully reads as "succeeded just now",
  which is the opposite of never. Retention drops the `source_state` row of a source the Hub
  stopped attempting, so a long forgotten daemon goes silent rather than turning green. Each family
  is its own loop over the sources, because every sample of a family has to be contiguous and one
  pass would interleave them.

- `perf_sentinel_hub_source_last_import_seconds` reports how long ago a daemon last pushed, from a
  `source_imports(source_id, last_import_ms)` table added as schema `V4` and written only by
  `TryUpsertBatchAsync`. It is kept out of `source_state` on purpose: a push must never look like a
  successful poll, and a push-only source would need every poll column there nullable to own a row.
  It is not a heartbeat, and its HELP text says so, because the daemon exporter sends nothing at all
  while it has no findings, so an old value means "no new finding since" and not "the push path is
  broken". `PurgeAsync` deletes nothing from `source_imports`, so a daemon that stopped pushing
  keeps a climbing age instead of losing its series, which would read like a daemon never observed.

- `perf_sentinel_hub_import_rejected_total{reason}` counts the imports the Hub refused, over the
  five bounded reasons of the `ImportRejection` enum: `bad_request`, `unauthorized`, `gate_full`,
  `write_timeout` and `too_large`. `gate_full` is a fifth import arriving while `ImportGate` already
  holds four in flight, and `write_timeout` is the write gate not coming free in five seconds, kept
  apart because they name two different knobs, one on the uploader's spacing and one on a writer
  held by the poll or by retention. The counts live in process rather than in SQLite, since a
  refusal happens on the very paths that could not take the write gate, and resetting on restart is
  what a Prometheus counter is allowed to do. Every reason is published from startup, zeros
  included, because a series that appears only on the first failure reads as a scrape gap and an
  alert cannot tell the two apart. The label mapping keeps no working catch-all, so a reason added
  without a label throws instead of publishing refusals under a name that says nothing.

- `perf_sentinel_hub_analysis_queue_depth` counts the runs accepted and not yet claimed by a worker,
  read from the same `CountRunsByStatusAsync` snapshot as `perf_sentinel_hub_analysis_runs{status}`
  rather than from a second query, so two counts of the same rows taken a moment apart cannot
  disagree inside one scrape. `analysis_runs` is a gauge and not a `_total`: a run moves between
  statuses and a finished one ages out on `Hub:Analysis:RunRetention`, so every series falls as well
  as rises, while a run still `pending` or `running` is never purged however old its row looks.
  Every status is emitted even at zero, a gauge that vanishes reading as a scrape failure rather
  than as nothing being in that state. One scrape takes one connection for both source tables
  through `QuerySourceObservationsAsync`, two statements rather than a join because SQLite grew
  FULL OUTER JOIN only in 3.39 and a LEFT JOIN would drop exactly the push-only sources.

- Every age the endpoint publishes clamps at zero. A stored timestamp ahead of the Hub's clock, from
  skew or from a restored backup, would otherwise expose a negative value that breaks every
  threshold comparing against it. Zero reads as "just now", which understates an age rather than
  breaking the alert.

- `Hub:Analysis:RunRetention`, thirty days by default, ages a finished run out of `analysis_runs`.
  Nothing deleted those rows before, so a Hub that had ever run an analysis carried every run it had
  ever accepted. The knob is validated as strictly longer than `Hub:Analysis:ReportRetention` rather
  than merely positive, because the two are one mechanism: the report is deleted after 24 hours and
  the run is marked expired, and the row has to outlive it so an expired run can still be read and
  relaunched with the parameters it was given. It also gets its own clock instead of riding on
  `Hub:Retention`, whose 180 days would keep six months of run history in a table nothing reads back
  that far. Once the row goes, `GET /api/analyses/{id}` answers 404. This is not an audit trail, and
  a link shared yesterday is already dead.

- The purge never removes a run whose `status` is `pending` or `running`, however old its row looks,
  since a worker is about to write to it or already is. Age is read as
  `COALESCE(finished_at_ms, created_at_ms)`, so a row that reached a terminal status without a
  finish time is still aged out on when it was created rather than kept forever. `PurgeAsync` takes
  the run cutoff as a second argument beside the findings cutoff, and both still delete in bounded
  chunks, since holding the write gate for one whole delete would answer 503 to every daemon import
  for the duration.

- `examples/grafana-dashboard.json` imports as it stands: nine panels over the eight metric
  families, on a `$job` variable read from `label_values(perf_sentinel_hub_build_info, job)` so one
  dashboard serves several Hubs. Every panel carries a description saying what its silence means,
  because most of these series are absent rather than zero on purpose: a daemon the Hub has never
  once reached publishes no `perf_sentinel_hub_source_reachable` row at all, and one never polled
  successfully has no `perf_sentinel_hub_source_last_success_seconds` series, zero there reading as
  "succeeded just now". `perf_sentinel_hub_analysis_runs` is a gauge, so `increase()` over it means
  nothing. Alongside it, `examples/prometheus-scrape.yml` names its targets for a docker compose, VM
  or discovery-less Prometheus, the chart already carrying the annotations an
  annotation-discovering one reads. The engine ships its own dashboard and the two do not overlap:
  no panel here reads a daemon series, none there reads a Hub one.

- `examples/prometheus-alerts.yml` carries one rule, `HubDown` on `up{job="perf-sentinel-hub"} == 0`
  for 30m, and says in the file why it is alone. The Hub sits in no production request path and push
  is the primary path, daemons POSTing findings and retrying coalesced batches, so a rule that fires
  on something a panel already draws is noise. Four were written and cut, listed in
  `docs/OPERATIONS.md` with where each condition shows instead: unreachable and stale sources both
  watch the poll safety net rather than the push path, so they go red on a fleet whose daemons are
  all delivering, a queue depth of 20 is 20 people clicking run and `GET /api/status` shows it to
  the person who queued them, and interrupted runs fire on a restart catching a queued run and never
  clear since nothing deletes those rows. What survives is the condition no panel can show, a dead
  Hub publishing no series looking exactly like a broken scrape config. The rule is a ticket, not a
  page, the loss horizon being the daemons' retry buffers filling, which is hours.

- `docs/OPERATIONS.md` and its French mirror describe the eight families and say why the import
  counter still earns no alert rule, which is worth stating because it looks like it should.
  `unauthorized` rises when a key expires and also when anyone at all posts an unknown `source_id`,
  and the only label that would separate the two is the caller's own `source_id`, the unbounded
  value that must never reach a label, so alerting on it would hand a stranger the ability to page
  you. `bad_request` has the same problem, its query-string half being checked before the key is.
  Two gaps are named rather than papered over: a push blocked before it arrives, by a network policy
  for instance, produces no request and therefore no rejection, and Prometheus holds no list of the
  sources that ought to exist.

- The chart's Service can carry annotations, through `service.annotations` in `values.yaml`. It
  ships empty because a scrape is opted into rather than assumed: set `prometheus.io/scrape`,
  `prometheus.io/port` and `prometheus.io/path` there, or point a ServiceMonitor at the Service
  instead.

- `examples/appsettings.reference.json` sets every setting to the value the Hub already uses,
  annotated, so copying it whole changes nothing. It is the inventory rather than a starting point,
  and it exists because the .NET JSON configuration provider ignores a key it does not recognise in
  silence: a name recalled from memory produces no error and reads like a bug in the Hub rather than
  a typo in the file. `Sources` is the one exception, it has no default and the Hub refuses to start
  without it, so its entry is an example. `ReferenceConfigurationTests` keeps the file exhaustive by
  walking `HubOptions` with reflection and failing on any writable property with no key here,
  descending into a list through one populated entry and asserting out loud when it meets a
  container shape the walk does not model, rather than skipping settings in the one test whose job
  is catching what the reference forgot.

- `Hub:Analysis:MaxTracesEmbedded` is documented, default `50`, valid from 0 to 10000. It has been
  in `HubOptions` and passed on every `report` invocation since analysis runs existed, and appeared
  in no document until the reference file forced the inventory. `AnalysisRunner` always passes
  `--max-traces-embedded`, and passing it at all opts the sink out of size targeting: without it a
  wide sweep loses the tail of the finding list to the 5 MiB budget, and the finding list is what an
  operator came for, so the span trees get capped instead.

- The README is a landing page again, 524 lines down to 115, and everything it used to inline lives
  in a document named for the task it serves: `docs/ARCHITECTURE.md`, `docs/CONFIGURATION.md`,
  `docs/API.md`, `docs/LAUNCHER.md`, `docs/OPERATIONS.md`, `docs/LIMITATIONS.md`, plus
  `RELEASING.md` and `CONTRIBUTING.md` at the root. The filenames are upper case the way the
  engine's are, so a reader moving between the two repositories does not have to remember which one
  shouts. Every one of them has its French mirror under `docs/FR/`, `README-FR.md` included, and the
  README's own table says what each of the eight covers rather than leaving the reader to open them
  all.

- Five mermaid diagrams in `docs/diagrams/mmd/` explain the topology, push against poll, what a
  launched run does, the three retention clocks and the states a run passes through, each rendered
  into a light and a dark SVG under `docs/diagrams/svg/` and served through a `<picture>` so a
  dark-mode reader is not handed a white slab. The `.mmd` sources carry no `%%{init}%%` theme block
  on purpose, because the theme is an export argument and that is what lets one source produce both
  files. `docs/ARCHITECTURE.md` says the export goes through mermaid.live rather than `mmdc`, and
  measures why: on this repository's own sources `mmdc` with no background flag writes
  `hsl(80, 100%, 96.2%)` where the perf-sentinel family has `rgb(255, 255, 255)`, and
  `-t dark -b '#232030'` still writes `hsl(20, 1.6%, 12.4%)` rather than `rgb(35, 32, 48)`. It
  closes with a table checking every arrow of the topology diagram against the symbol that draws it,
  `FetchReportSnapshotAsync`, `MarkSourceAttemptAsync`, `UpdateChecker.ReadAsync` and the rest,
  named by symbol rather than by line so an edit above them does not make the table lie.

- `docs/LIMITATIONS.md` gathers the caveats that were scattered next to the features they constrain,
  so the boundaries can be read in one pass before deploying instead of one at a time afterwards:
  the 1,000-row cap the 0.11.x daemon puts on `/api/findings`, that a poll omitting a finding does
  not resolve it, that reachability is one-directional and only a successful poll clears
  `unreachable_since_ms`, that `status` is a presumption derived at read time, that the sink's 5 MiB
  report target is a private constant with no flag or config key, and that counts from runs with
  different detection thresholds are not comparable. `docs/CONFIGURATION.md` states the same split
  at its source: `Id`, `Name`, `Environment`, `Kind`, `BaseUrl` and `RetentionHours` are declared
  and never checked against anything, while `reachable`, `last_success`, `unreachable_since`,
  `producer_version` and `last_error` are written by the poll, which is why a deployment can label
  production as staging and nothing contradicts it. It also says a trace backend is contacted by
  nothing but a launched run, so a Tempo or Jaeger source showing no producer version is the design
  rather than a fault.

- The fleet table and the launcher's source list both group daemons apart from trace backends,
  under a `daemons` band and a `trace backends` band. The two kinds behave differently enough to be
  worth the split: a daemon is polled and pushes on its own, a trace backend is only read when a run
  asks it for traces. A fleet holding a single kind draws no bands at all, because the label would
  name the only thing on screen. Both surfaces partition through the same `PSL.splitByKind`, which
  keeps every source's position in the original array: a fleet row's fold ids are built from that
  index, so a grouped row still addresses the source it belongs to. An unknown kind counts as a
  backend, matching the engine, where only a daemon is polled.

- Every duration measured against the current moment now ticks once a second from one
  `state.durationTicker` over a `state.liveDurations` registry, instead of a timer per screen. A
  node registers through `live(node, compute)` with the same function that built its text, and the
  ticker rewrites the text and, where a cell carries that same string as its tooltip, the `title`
  with it. Re-rendering to refresh them was not available on the report screen: rebuilding it
  replaces the iframe and reloads the report under the reader. Each entry computes inside its own
  `try`, so one that throws cannot freeze every countdown behind it in the list for the rest of the
  screen's life, and a fact line borrowed for six seconds by a failed resubmission is left alone
  until its error clears.

- Countdowns carry seconds and uptimes carry minutes, through `PSL.durPrecise` and `PSL.durMinutes`
  over a shared `durDown(ms, floor)` that keeps every unit between the largest that applies and the
  floor, zeros included. `PSL.dur` stops at two units, which reads well for an age skimmed in a
  table and badly for a figure someone watches: 86,399,000 ms of remaining report life read as
  `23 h 59 m` and never appeared to move, and an uptime of 888,120,000 ms read as `10 d 6 h`, hiding
  the 42 minutes that were the only part changing between reads. A report already gone reads
  `expired` rather than counting on past zero, and a negative interval floors at `0 s` instead of
  reading as one second from expiry.

- The run result strip takes the treatment the rendered dashboard gives its overview tiles, so the
  two surfaces read as one product. The quality gate leads the strip as `PASS` or `FAIL`, the
  findings count is the one cell filled as a solid block and takes the dominant severity found,
  `crit` over `warn` over `info` and `ok` when there is nothing, and the severities beside it take
  their own pastel through `data-grad`. The dark theme's filled backgrounds are darker than the
  light theme's so white text on them stays legible. The daemon gauges pass no options and keep the
  neutral strip, because their tone means near a cap, which is not a severity.

- No version is tagged without a recent PASS from the simulation lab.
  `release-gate/check-lab-validation.sh --version vX.Y.Z` reads the append-only
  `release-gate/lab-validations.txt`, four tab-separated fields per line,
  `<version>\t<lab_commit_sha>\t<YYYY-MM-DD>\t<PASS|FAIL>`, and exits non-zero when there is no PASS
  for that version, when the newest one is more than thirty days old, when the date is in the
  future, or when the ledger is missing, empty or off schema. The lab runs the Hub image against a
  real daemon through `hub-ingestion`, `hub-derived-status`, `hub-lineage-mutation`,
  `hub-retention-purge`, `hub-source-reachability` and `hub-plugin-contract`, which is coverage no
  unit or integration test in this repository reaches: ingestion, polling, the derived status, the
  lineage columns and the plugin's envelope contract, all against a daemon that really produced the
  findings. The gate is operator-driven by design, since CI cannot reproduce a run that needs a
  Kubernetes cluster, a workload fleet and an image built from the commit under test. A ledger line
  names the Hub commit the image was built from and not only the version, because the version in
  column one is the one about to be tagged and so identifies nothing yet. `RELEASING.md` carries the
  procedure, and step 1 of the publish list now asserts the gate. The script is a copy of
  perf-sentinel's, which is the original and holds the full test suite, so a fix to the date
  handling or the ledger schema belongs in both. The ledger's first entry records `v0.1.0` as PASS
  on 2026-08-29 against Hub commit `d7f5889`, validating a version that has never been published,
  which is the whole point: the lab has to see the build before the tag exists.

- A `.gitleaks.toml` that extends the bundled default ruleset and adds one narrow allowlist, so
  `generic-api-key` stops flagging the engine's finding signatures carried by the demo fixtures. A
  signature is `<finding_type>:<service>:<sanitized_endpoint>:<32-hex>`, a deterministic SHA-256
  prefix operators copy out of `analyze --format json` into PR-reviewed acknowledgment files, public
  by design and not a credential. The allowlist is scoped to
  `tests/browser/demo/fixtures/.*\.json` under `condition = "AND"` rather than the default OR,
  without which the path list and the regex are two independent ways to be allowlisted and the regex
  applies everywhere. `regexTarget = "line"` silences the whole source line, so confining it to
  machine-generated capture output keeps that bypass out of hand-written code. The leading token is
  pinned to the engine's twelve finding types, from `n_plus_one_sql` through `slow_messaging` to
  `serialized_calls`, because a bare `[a-z_]+:` prefix would have accepted a real credential
  formatted as `aws_secret_key:account:region:<hex>`. A rule allowlist rather than `.gitleaksignore`
  fingerprints, which are bound to a commit and a line number: the fixtures are regenerated by
  `tests/browser/demo/capture-fixtures.sh` at every engine bump, and each regeneration would leave
  four stale entries and eleven new findings behind.

- The result panel takes its tone from the quality gate's verdict rather than the run's. A run that
  completed with a failing gate drew a green panel over its own sentence, "The quality gate did not
  pass."

- A run card sits on `--surface` rather than `--surface-2`, and its `.run-card-args` line steps back
  to `--bg`. `--surface-2` means raised above a grey panel, so it is lighter than its ground and
  resolves to white in the light theme, which left the card indistinguishable from the page it sits
  on. `.source-row` and `.cost-cell` do sit on a panel and keep the token.

- `HubDatabase` is no longer `IDisposable`, and `_writeGate` and `_initializeGate` are never
  disposed. The host disposed the database at shutdown, which disposed a `SemaphoreSlim` that a poll
  still in flight was waiting on, arming an `ObjectDisposedException` on a `WaitAsync` already in
  progress. A `SemaphoreSlim` only holds a wait handle once `AvailableWaitHandle` has been read,
  which nothing here does, so the dispose freed nothing and bought only the race. The tests dropped
  their own `using` and `Dispose` on it with it.

- `scripts/check-supply-chain.py` skips `node_modules` when walking for structured files. The
  browser demo suite's dependencies are gitignored and never reach CI, so the workflows vendored
  inside them are not this repository's supply chain to declare, and scanning them only failed the
  check on a machine where someone had run `npm install`.

- `first_seen` comes from the daemon envelope (`first_seen_ms`) instead of the Hub's poll clock, clamped to the observation time and to a Unix-ms sanity floor so neither a clock running ahead nor a seconds-unit bug can distort the irreversible MIN. `last_seen` deliberately stays the Hub's own observation clock, since retention, ordering and the freshness guard compare it and must read one monotonic clock. A finding's age now reflects when the daemon first detected it, not when the Hub first heard about it.

- The test suite runs on xunit.v3 4 over Microsoft.Testing.Platform. The .NET 10 SDK removed the VSTest bridge that xunit 3 relied on, so `global.json` opts into the platform runner, the test project builds as an executable, and `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` and `coverlet.collector` give way to the platform's own coverage and TRX extensions. The coverage engine changed with it, so the exclusion moved rather than disappeared: coverlet dropped generated sources through its `**/obj/**` rule, and the Microsoft collector has no working equivalent, so ReportGenerator now applies the same filter to the report before the gate reads it. Without it the figure fell to 75.19%, not through any lost test but because 1,345 of 2,237 measured lines were source-generated (minimal-API route builders, the configuration binder, the JSON context); with it the baseline sits at 90.81%, next to the 91.84% coverlet recorded. Separately, the coverage run no longer carries a `--filter` allowlist of suite names, so a suite added later is measured by construction rather than by remembering to extend a list, which is how `BackupTests` had escaped it.

- A `backup` CLI command (`PerfSentinelHub backup <destination>`) snapshots the live database with SQLite `VACUUM INTO`, refuses to overwrite an existing destination, cleans up its partial file on failure, and rejects a wrong arity with a usage message instead of falling through to the server. Comes with a `make backup` wrapper and a documented backup and restore procedure. The database volume is the only non-reconstructible state the Hub holds.
