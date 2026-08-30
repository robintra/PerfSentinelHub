# Drawing brief: the Hub integration board

This describes a hand-drawn board, not a rendered diagram. `mmd/hub-integration.mmd`
carries the same topology and renders, but mermaid cannot draw a glow, a logo, a pill
badge or a legend, so the `.mmd` is the skeleton and this file is the style.

The board belongs to the family in `perf-sentinel-simulation-lab/docs/diagrams/svg/`,
drawn in Figma at 2000x2000. Every value below is measured from
`perf-sentinel-production.svg` so the two families read as one set.

## Canvas

- `viewBox="0 0 2000 2000"`, and **no `width` or `height` attribute**. They were
  removed deliberately from the existing boards (commit `070fa36`) so the SVG scales
  to its container.
- Background: one `rect` 2000x2000, `rx=16`, fill `#0A0E1A`.
- Two decorative orbit circles centred on the hub, `#1A72E8` at 25% opacity,
  `stroke-width=3`, `stroke-dasharray="3 8"`. On the reference they sit at `r=1023.5`
  and `r=603.5`.

## Colour, and what each one means

The legend is not decoration, it is the key to reading the board. Keep the four roles
and their colours exactly as the perf-sentinel boards use them.

| Role                         | Hex       | Shape                                  |
|------------------------------|-----------|----------------------------------------|
| Hub or perf-sentinel surface | `#1A72E8` | card, or fully rounded pill for an API |
| Human actor                  | `#D4A017` | pill with a **dashed** border          |
| Output, report, export       | `#34A853` | card                                   |
| Infrastructure, datastore    | `#7C4DFF` | card                                   |

Everything else:

| Usage                                       | Hex                            |
|---------------------------------------------|--------------------------------|
| Page background                             | `#0A0E1A`                      |
| Every card, pill and the legend bar         | `#1A1F2E`                      |
| Edge-label chip                             | `#0D0A14`, `rx=8`              |
| Legend bar border                           | `#4A5668`, `stroke-width=1.25` |
| Arrows out of the product, and their labels | `#7BB4FF`                      |
| Card title                                  | white                          |
| Card body copy                              | `#C7D6EB`                      |
| Legend labels                               | `#D9E0ED`                      |
| Page subtitle                               | `#A0AFC4`                      |
| Zone heading, top right                     | `#FF7B7B`                      |
| Central disc gradient                       | `#103C84` to `#0A142E`         |

## Card grammar

Every card is two rectangles: a fill rect, then a stroke rect inset by 2 px with
`stroke-width=4`. That is Figma's inside-stroke idiom, and it is what gives the family
its even 2 px visual border.

- Radius `22.5` on the outer rect, `20.5` on the inner.
- A **status dot** at the top left of the title, `r` between 9.4 and 11.25, filled with
  the card's accent colour. Actor pills carry a hollow ring instead: fill at 25%
  opacity plus a stroke, both `#D4A017`.
- Title in bold white, about 32.
- An **all-caps badge** immediately after the title, about 23, in the accent colour.
- Two or three lines of body copy, about 25, in `#C7D6EB`.
- Third-party products carry their real logo inside the card.

## The centre

A glowing disc, not a card. Stack, from the outside in:

1. Radial glow, `r=304.8`, `#1A72E8` fading 75% to 0.
2. Disc, `r=232.4`, linear gradient `#103C84` to `#0A142E`.
3. Ring, `r=229.4`, `#1A72E8`, `stroke-width=6`, plus an outer glow filter (blur 12,
   `#1A72E8` at 0.45).
4. Inner ring, `r=172.2`, `#1A72E8` at 45%, `stroke-width=2`,
   `stroke-dasharray="6 7"`.

Text inside, centred: **PerfSentinelHub** in white at about 44, then
*one for the fleet* in `#C7D6EB`.

## Arrows

Connectors are **filled polygons 10 units wide**, not strokes, and the arrowhead is a
filled triangle roughly 50 long on a 57.7 base, baked into the same path. There is no
`<marker>` element anywhere in the family.

Labels sit in a `#0D0A14` chip with `rx=8`, text about 30.

**The dash carries meaning, and it is the easiest thing to lose:**

- **Solid** — a flow that is always on.
- **Dashed, green, with a 25% to 100% opacity gradient along its length** — an
  on-demand pull. Dash pattern `20 20`, width 10.
- **Dashed border** — a human, not a system.
- **Dashed circle** — a zone boundary, or a decorative orbit.

## What to draw

### Centre

The glowing disc: **PerfSentinelHub**, *one for the fleet*.

### Left, in gold with dashed borders

- **QA / SRE / On-call**, sub-line *watches the fleet, reads Fleet health*.
- **Developer**, sub-line *launches an analysis, reads the report*.

### Bottom, in blue: the browser

One card per screen, named as the UI names them, not as the routes spell them:

| Card                        | Badge      | Body                                       |
|-----------------------------|------------|--------------------------------------------|
| **Run an analysis**         | `LAUNCHER` | *picks a source, builds the request*       |
| **Fleet health**            | `LIVE`     | *one row per source, a daemon row unfolds* |
| **The team's short memory** | `RECENT`   | *the last runs and who launched them*      |
| **Run screen**              | `POLLS`    | *pending, running, then a terminal state*  |
| **Report screen**           | `IFRAME`   | *the engine's own dashboard, embedded*     |

### Right, in blue: the fleet

- **perf-sentinel daemons**, badge `ONE PER ENVIRONMENT`.
- **Trace backends**, badge `TEMPO, JAEGER QUERY`. Carry the Grafana Tempo and
  Jaeger logos, as the production board does for its trace store.

**The two arrows between the Hub and the daemons are the point of the board.** Draw
both, in opposite directions, and label them so the asymmetry is visible:

- daemon to Hub, solid: `push, POST /api/import/findings` with a second line
  *the primary path*.
- Hub to daemon, solid: `poll, GET api/status and api/findings` with a second line
  *the safety net*.
- On or beside the poll arrow, the sentence that no other surface states:
  **only a successful poll clears unreachable**. A push that lands while the poll
  fails leaves the source unreachable, because the import handler never touches
  `source_state`.

### Bottom left, in purple

**SQLite**, badge `ONE FILE`, body *findings, lineage, runs*. Three retention lines,
because none of the three is derivable from the others:

- findings, 180 days
- status window, 7 days
- rendered reports, 24 hours

### Bottom right, in green

- **perf-sentinel binary**, badge `SUBPROCESS`, body *two spawns per run, never
  resident*. An arrow to the trace backends labelled `queries`.
- **report.html**, badge `24 HOURS`, with an arrow into the Report screen card.

### Corner, in purple

**api.github.com**, badge `DAILY`, body *the only destination that is not a configured
source*. Reached by a **dashed** arrow from the Hub, since it is a periodic pull and
the whole feature is off behind one key.

### The zone boundary

A dashed arc fencing off what the **IDE plugin and CI** reach from what the browser
reaches. This is worth drawing because it is written nowhere else: the launcher never
calls `/api/findings`, that endpoint exists for the plugin. Put the plugin card outside
the zone the browser occupies, with its own arrow to the Hub labelled
`GET /api/findings`.

Use the same treatment the production board uses for `Local dev`: a large-radius
dashed circle sweeping through one corner, `stroke-width=8`,
`stroke-dasharray="16 22"`, with the zone name in the corner at about 50.

### Legend

Bottom left, a bar of `#1A1F2E` with an `rx=9.375` and a `#4A5668` border at 1.25.
Four hollow rings, `r=13.6`, `stroke-width=4`, with `#D9E0ED` labels at about 32:

`Hub surface` · `actor` · `output / report` · `infra / datastore`

## Typography

There are no `<text>` elements in the reference, every glyph is outlined to a path, so
the family is not recoverable from the file. It renders as an Inter or SF Pro class
humanist grotesque. Sizes, derived from cap heights:

| Role                | Size |
|---------------------|------|
| Page title          | 54   |
| Corner zone heading | 50   |
| Centre node name    | 44   |
| Card title          | 32   |
| Legend label        | 32   |
| Edge label          | 30   |
| Card body           | 25   |
| All-caps badge      | 23   |
| Actor sub-line      | 19   |

## Headings

- Top left, white at 54: **PerfSentinelHub - integration**
- Below it, `#A0AFC4` at about 27: *one Hub for the fleet, push primary and poll as
  the net, analyses launched from the browser*
- Top right, `#FF7B7B` at 50: the zone name for the served surface.

## Every arrow, against the code

The verification the plan asks for: each arrow on the board corresponds to a real call.

| Arrow                                      | Where it lives                                 |
|--------------------------------------------|------------------------------------------------|
| browser to Hub, launcher reads             | `Api/ApiEndpoints.Analysis.cs:21-24`           |
| plugin to Hub, `GET /api/findings`         | `Api/ApiEndpoints.cs:46-47`                    |
| daemon to Hub, push                        | `Api/ApiEndpoints.cs:48`, stores at `:133`     |
| Hub to daemon, poll                        | `Collection/DaemonClient.cs:36` and `:111`     |
| Hub to daemon, export for a run            | `Collection/DaemonClient.cs:106`               |
| Hub to daemon, config for the unfolded row | `Collection/DaemonClient.cs:66`                |
| reachability set and cleared               | `Collection/SourcePoller.cs:30` and `:64` only |
| Hub to SQLite                              | `Storage/Schema.cs`, three migrations          |
| Hub spawns the engine                      | `Analysis/AnalysisRunner.cs:148` and `:201`    |
| engine writes the report                   | `Analysis/AnalysisRunner.cs:51-52`             |
| report served into the iframe              | `Api/ApiEndpoints.Analysis.cs:24`              |
| Hub to api.github.com                      | `Collection/UpdateChecker.cs:53`               |
