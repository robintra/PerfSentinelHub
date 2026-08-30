# How the Hub fits together

Five diagrams, each answering a question the README states in prose but does not draw.
Sources live in [`diagrams/mmd/`](diagrams/mmd), rendered SVGs in
[`diagrams/svg/`](diagrams/svg). Editing a diagram means editing the `.mmd` and
re-exporting both themes, never touching the SVG by hand.

## The whole thing at once

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/hub-integration_dark.svg">
  <img alt="How the Hub, the fleet, the browser and the engine fit together" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/hub-integration.svg">
</picture>

One Hub serves two audiences that never overlap. The browser gets the launcher and
nothing else. An IDE plugin or a CI job gets `/api/findings` and never opens a screen.
That split is not enforced by authentication, it is simply what each client asks for,
and it is worth knowing before reading either surface.

The engine appears twice on this board and it is the same binary both times: once as a
subprocess the Hub spawns to produce a report, and once as the daemon it collects from.
Nothing about the Hub is resident in the engine, and nothing about the engine is
resident in the Hub.

## Push and poll, and which one owns reachability

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/push-and-poll_dark.svg">
  <img alt="The push path and the poll path, and the fact only one of them owns" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/push-and-poll.svg">
</picture>

Both paths write findings. Only one writes reachability.

A daemon pushing successfully proves that the daemon can reach the Hub. It proves
nothing about whether the Hub can reach the daemon, which is a different route through
a different set of firewalls, and that is the direction an operator needs when a source
goes quiet. So the import handler never touches `source_state`: a source whose push
lands while its poll fails keeps reporting `unreachable_since`, and that is correct
rather than stale.

The two paths also differ in back-pressure, deliberately. The poll blocks on the write
lock because it is the Hub's own scheduled work and can wait. An import gives up after
five seconds with `503 Retry-After: 1`, because a slow uploader must not be able to
stall the whole fleet's collection.

## What a launched analysis actually does

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/analysis-run_dark.svg">
  <img alt="A run from submission to the embedded report" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/analysis-run.svg">
</picture>

Two things on this sequence are easy to miss.

The first is that validation happens **before** the run is queued. An impossible pair,
a three-hour window against a daemon that keeps ten minutes, is refused while the
operator is still looking at the form, not discovered three minutes later as a failed
run.

The second is that a run is two spawns of the engine, not one. The query subcommands
emit text, JSON or SARIF, and only `report` writes HTML, so the source is read into a
report JSON and that JSON is then rendered. A daemon source skips the first spawn
entirely, because its own `/api/export/report` already returns exactly that JSON.

## Three retention clocks

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/retention-clocks_dark.svg">
  <img alt="Findings, status window and rendered reports each expire on their own clock" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/retention-clocks.svg">
</picture>

None of the three is derivable from the others, and they are enforced by three
different mechanisms.

Findings expire on a worker that runs once a day and purges in chunks, so a long purge
cannot reject imports for its whole duration. The status window is not a worker at all,
it is a `CASE` evaluated at read time, which is why a finding's status can change
without anything having been written. Rendered reports expire on a sweep that runs
every sixty seconds, finer than the lifetime it enforces, so the countdown a reader
sees never outlives the file it counts down to.

The trap the diagram exists to prevent: a poll that omits a finding does not resolve
it. The daemon's ring buffer may simply have evicted it, and missing is not the same as
resolved. Only retention removes a row.

## A run always reaches a terminal state

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/run-states_dark.svg">
  <img alt="The states a run passes through and who moves each edge" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/run-states.svg">
</picture>

Whatever happens, a row does not stay `running`. A process that dies mid-run leaves an
orphaned row, and the next startup marks it `interrupted` rather than leaving it to be
wondered about.

Nothing is retried. A silent replay would fire a second heavy query at a backend nobody
asked to query twice, so an interrupted run waits for a human to decide. `expired` is
not a failure either: the report was deleted on schedule and the run keeps its
parameters, so it can be launched again as it was.

## Editing a diagram

The `.mmd` sources are the source of truth. They carry no `%%{init}%%` theme block on
purpose: the theme is an export argument, which is what lets one source produce both
the light and the dark SVG.

The export is manual, through [mermaid.live](https://mermaid.live): paste the source,
pick the theme, use the SVG export button. That is how the perf-sentinel family was
produced and the two match, which matters because the SVGs sit side by side in the same
kind of document.

`mermaid-cli` is not a drop-in replacement for that. Measured on this repository's own
sources, `mmdc` with no background flag writes `hsl(80, 100%, 96.2%)` where the existing
family has `rgb(255, 255, 255)`, and `-t dark -b '#232030'` still writes
`hsl(20, 1.6%, 12.4%)` rather than the family's `rgb(35, 32, 48)`, because the theme's
own background wins over the flag. It is useful for checking that a source parses at
all, which is worth doing before opening a browser:

```bash
npx -y @mermaid-js/mermaid-cli@latest -i docs/diagrams/mmd/<name>.mmd -o /tmp/check.svg
```

That invocation needs a Chrome or Chromium that Puppeteer can find, and says so plainly
when it cannot.

Both files of a pair must exist. A diagram shipped with only the light variant is a
white slab for every reader in dark mode, which is exactly the drift the naming
convention exists to make visible.

## Every arrow, against the code

The first diagram claims a topology. This is where that claim is checked: each arrow on
it corresponds to a real call. Named by symbol rather than by line, because a line number
is wrong the first time anyone edits above it.

| Arrow                                     | Where it lives                                                                                     |
|-------------------------------------------|----------------------------------------------------------------------------------------------------|
| Browser to Hub, the launcher reads        | `Api/ApiEndpoints.Analysis.cs`, the four routes it maps                                            |
| Plugin or CI to Hub                       | `Api/ApiEndpoints.cs`, `GetFindingsAsync`                                                          |
| Daemon to Hub, push                       | `Api/ApiEndpoints.cs`, `ImportFindingsAsync`, storing via `TryUpsertBatchAsync`                    |
| Hub to daemon, poll                       | `Collection/DaemonClient.cs`, `FetchStatusAsync` and `FetchFindingsAsync`                          |
| Hub to daemon, export for a run           | `Collection/DaemonClient.cs`, `FetchReportSnapshotAsync`                                           |
| Hub to daemon, config for an unfolded row | `Collection/DaemonClient.cs`, `FetchConfigAsync`                                                   |
| Reachability set, and cleared             | `Collection/SourcePoller.cs`, `MarkSourceAttemptAsync` and `MarkSourceFailureAsync`, nowhere else  |
| Hub to SQLite                             | `Storage/Schema.cs`                                                                                |
| Hub spawns the engine                     | `Analysis/AnalysisRunner.cs`, twice per run                                                        |
| Engine writes the report                  | `Analysis/AnalysisRunner.cs`                                                                       |
| Report served into the iframe             | `Api/ApiEndpoints.Analysis.cs`, `GetReportAsync`                                                   |
| Hub to api.github.com                     | `Collection/UpdateChecker.cs`, `ReadAsync`, the only outbound call that is not a configured source |
