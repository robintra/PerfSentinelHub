# Browser demo suite

Captures the launcher screens that `docs/` and the README show. It produces
artefacts, not assertions: nothing here gates CI, and `make verify-fast` does
not run it.

```bash
npm install
npx playwright install chromium
npm run demo
```

Output lands in `docs/img/hub/`: six screens as `<name>.png` (light) and
`<name>-dark.png` (dark), plus `launcher_light.gif` and `launcher_dark.gif`.

## What global-setup has to build first

The Hub has no seeder, no fixture loader and no demo mode. Its validation
refuses to start without a source, and a daemon view is read live rather than
from storage. A populated screenshot therefore needs a Hub that is really
running against daemons that really answer, so `global-setup.ts` stands up:

- two fake daemons (`demo/fake-daemon.js`) replaying the captures in
  `demo/fixtures/`,
- the Hub, built from this checkout and run from its own binary,
- four analysis runs submitted through the API, chosen for the states they end
  in: two succeed, one hits an unreachable source, one is refused before it is
  queued because the window outruns what the backend keeps.

## The fixtures are captures, not inventions

`demo/fixtures/` holds what a real daemon answered. `daemon-config.json` is its
`api/config` verbatim. The findings come from
`perf-sentinel analyze --format json` on the engine's own demo trace file, so
every detector, severity and service in the screenshots is one the engine really
produced.

`demo/capture-fixtures.sh <engine>` refreshes all of it against a live daemon.
Run it when the engine version moves, so the screenshots keep showing what the
engine currently answers.

Only the gauge values in `daemon-status-*.json` are chosen: an idle daemon
reports zeros, and a screenshot of zeros teaches nothing. One daemon sits near
its cap so the toning shows, the other is comfortable.

The incidents are captured the same way, from a daemon fed five Alertmanager
deliveries carrying three `namespace` labels and one alert without, so the
column shows both a value and the empty cell, and the fake daemon slides their
stamps forward to the present as it serves them. The screen prints every time as an age, so a body captured last
quarter would put "3 months ago" on an OOM kill and read as a broken screen.
One delta over every millisecond field, so the windows, the frozen findings and
the before-or-after-the-restart reading keep the distances the daemon measured.

## Two things it needs from outside

**A perf-sentinel binary.** Without one the Hub answers `503` to
`POST /api/analyses` and three of the six screens are dead. The setup looks
for a sibling `perf-sentinel` checkout with a release build, or takes
`HUB_ENGINE_BINARY`.

**The pinned SDK.** `global.json` pins 10.0.400 with `rollForward: disable`,
which is usually not the `dotnet` on `PATH`. The setup runs
`/usr/local/share/dotnet/dotnet` by name when it is there, rather than putting
that directory in front of `PATH`: search order is a poor way to decide which
toolchain builds the thing you are about to photograph.

`ffmpeg` is needed for the GIFs only. `build-gif.sh` exits non-zero when it
finds no recording, so a no-op cannot pass for a success and ship the previous
run's files.
