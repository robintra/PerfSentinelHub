/**
 * perf-sentinel Hub launcher — shell, state and rendering.
 *
 * Classic script, no build step. Pure logic lives in launcher.js as `PSL`.
 *
 * Nothing from the server is ever written with innerHTML: every displayed
 * string is a text node, and nothing in this design needs rich HTML from data.
 */
(function () {
  "use strict";

  const PSL = globalThis.PSL;
  const THEME_KEY = "perf-sentinel:theme";
  const THEME_POSITIONS = ["auto", "light", "dark"];
  const THEME_LABELS = { auto: "System", light: "Light", dark: "Dark" };

  /** Glyph paths lifted from the dashboard's themeIcon(), not redrawn. */
  const THEME_GLYPHS = {
    auto: [["rect", { x: "3", y: "4", width: "18", height: "13", rx: "2" }], ["path", { d: "M8 21h8M12 17v4" }]],
    light: [["circle", { cx: "12", cy: "12", r: "4" }], ["path", {
      d: "M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"
    }]],
    dark: [["path", { d: "M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" }]]
  };

  const state = {
    themePosition: document.documentElement.getAttribute("data-theme-position") || "auto",
    screen: "new",
    status: null,
    sources: null,
    sourcesError: false,
    loading: true,
    run: null,
    runError: false,
    runTimer: null,
    noteTimer: null,
    runs: null,
    form: {
      sourceId: null,
      mode: "service",
      service: "",
      traceId: "",
      rangeMode: "relative",
      lookback: "1h",
      fromMs: Date.now() - 3600000,
      toMs: Date.now(),
      customQty: 90,
      customUnit: "m",
      detection: {},
      pickerOpen: false,
      maxTraces: 100,
      ackUnreachable: false,
      ackHeavy: false
    }
  };

  // ------------------------------------------------------------ DOM helpers

  function el(tag, attrs, children) {
    const node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (key) {
      if (key === "class") node.className = attrs[key];
      else if (key === "text") node.textContent = attrs[key];
      else if (attrs[key] != null) node.setAttribute(key, String(attrs[key]));
    });
    (children || []).forEach(function (child) {
      if (child) node.appendChild(child);
    });
    return node;
  }

  function svg(paths, size) {
    const node = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    node.setAttribute("viewBox", "0 0 24 24");
    node.setAttribute("fill", "none");
    node.setAttribute("stroke", "currentColor");
    node.setAttribute("stroke-width", "1.9");
    node.setAttribute("stroke-linecap", "round");
    node.setAttribute("stroke-linejoin", "round");
    node.setAttribute("aria-hidden", "true");
    if (size) { node.setAttribute("width", String(size)); node.setAttribute("height", String(size)); }
    paths.forEach(function (spec) {
      const shape = document.createElementNS("http://www.w3.org/2000/svg", spec[0]);
      Object.keys(spec[1]).forEach(function (key) { shape.setAttribute(key, spec[1][key]); });
      node.appendChild(shape);
    });
    return node;
  }

  // Write-only: the read happens in the inline script, before this file loads.
  // sessionStorage throws in Safari private mode and under some enterprise
  // policies, and a theme is not worth an error.
  function store(area, key, value) {
    try {
      globalThis[area].setItem(key, value);
    } catch (error) {
      // Nothing to do: the position still applies to this page.
    }
  }

  // ---------------------------------------------------------------- theme

  function resolveTheme(position) {
    if (position !== "auto") return position;
    return matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
  }

  function applyTheme(animate) {
    const root = document.documentElement;
    root.setAttribute("data-theme", resolveTheme(state.themePosition));
    root.setAttribute("data-theme-position", state.themePosition);
    // Both stores: localStorage so the position survives the tab, sessionStorage
    // because the rendered dashboard reads that exact key from this origin.
    store("localStorage", THEME_KEY, state.themePosition);
    store("sessionStorage", THEME_KEY, state.themePosition);

    const button = document.getElementById("theme-toggle");
    const glyph = document.getElementById("theme-glyph");
    document.getElementById("theme-label").textContent = THEME_LABELS[state.themePosition];
    button.setAttribute("aria-label", "Theme: " + THEME_LABELS[state.themePosition] + ". Click to cycle.");
    glyph.replaceChildren(svg(THEME_GLYPHS[state.themePosition], 15));
    if (!animate) return;
    // Two identical keyframes alternated, to force the animation to restart.
    button.setAttribute("data-spin", button.getAttribute("data-spin") === "a" ? "b" : "a");
  }

  function initTheme() {
    document.getElementById("theme-toggle").addEventListener("click", function () {
      const next = (THEME_POSITIONS.indexOf(state.themePosition) + 1) % THEME_POSITIONS.length;
      state.themePosition = THEME_POSITIONS[next];
      applyTheme(true);
    });
    // An OS change re-resolves live and never animates, or the theme would
    // spin by itself at sunset.
    matchMedia("(prefers-color-scheme: light)").addEventListener("change", function () {
      if (state.themePosition === "auto") applyTheme(false);
    });
    applyTheme(false);
  }

  // ----------------------------------------------------------------- data

  function getJson(path) {
    return fetch(path, { headers: { accept: "application/json" } }).then(function (response) {
      if (!response.ok) throw new Error(path + " answered " + response.status);
      return response.json();
    });
  }

  function loadShell() {
    return Promise.all([
      getJson("/api/status").catch(function () { return null; }),
      getJson("/api/sources").catch(function () { return "error"; })
    ]).then(function (results) {
      state.status = results[0];
      state.sourcesError = results[1] === "error";
      state.sources = state.sourcesError ? null : results[1];
      state.loading = false;
      if (state.sources && state.form.sourceId === null) {
        const usable = state.sources.find(function (source) { return source.reachable; });
        state.form.sourceId = (usable || state.sources[0] || {}).id || null;
      }
      renderShell();
      onRoute();
    });
  }

  function renderShell() {
    const status = state.status;
    document.getElementById("version-hub").textContent = status ? status.version : "unknown";
    document.getElementById("version-engine").textContent =
      status && status.engine_version ? status.engine_version : "none";
    if (status) PSL.setVersions(status.version, status.engine_version);

    // The identity comes from the reverse proxy. With no proxy in front there
    // is nothing to show, and an empty chip is better than a fake name.
    const identity = document.getElementById("identity");
    identity.hidden = !status || !status.identity;
    document.getElementById("identity-name").textContent = status && status.identity ? status.identity : "";

    renderFleetSkew();
    renderSourcesBadge();
  }

  /**
   * The third version segment appears only when producers disagree with the
   * engine. It names the spread across the fleet, not one source.
   */
  function renderFleetSkew() {
    const chip = document.getElementById("version-chip");
    let existing = chip.querySelector(".shell-version-skew");
    if (existing) existing.remove();
    if (!state.sources || !state.status || !state.status.engine_version) return;

    const behind = state.sources
      .map(function (source) { return source.producer_version; })
      .filter(function (version) { return version && PSL.skew(version); });
    if (behind.length === 0) return;

    const oldest = behind.sort(PSL.vcmp)[0];
    chip.appendChild(el("span", { class: "shell-version-rule", "aria-hidden": "true" }));
    chip.appendChild(el("span", { class: "shell-version-skew" }, [
      svg([["path", { d: "M12 4l9 16H3z" }], ["path", { d: "M12 10v4M12 17.4v.2" }]], 12),
      el("span", { text: "fleet " + oldest + " → " + state.status.engine_version })
    ]));
  }

  function renderSourcesBadge() {
    const badge = document.getElementById("sources-badge");
    const unreachable = (state.sources || []).filter(function (source) { return !source.reachable; });
    badge.hidden = unreachable.length === 0;
    badge.textContent = String(unreachable.length);
  }

  // ------------------------------------------------------------ navigation

  function currentScreen() {
    const hash = (location.hash || "#/new").replace("#/", "");
    if (hash.indexOf("run/") === 0) return "run";
    if (hash.indexOf("report/") === 0) return "report";
    return ["new", "recent", "sources"].indexOf(hash) >= 0 ? hash : "new";
  }

  function currentRunId() {
    const match = /^#\/(?:run|report)\/([0-9a-f]{16})$/.exec(location.hash || "");
    return match ? match[1] : null;
  }

  function render() {
    state.screen = currentScreen();
    Array.prototype.forEach.call(document.querySelectorAll(".shell-tab"), function (tab) {
      if (tab.getAttribute("data-screen") === state.screen) tab.setAttribute("aria-current", "page");
      else tab.removeAttribute("aria-current");
    });

    const main = document.getElementById("main");
    document.body.setAttribute("data-screen", state.screen);
    if (state.loading && state.screen !== "report") {
      main.replaceChildren(el("div", { class: "card skeleton", style: "height:220px" }));
      return;
    }
    // Every screen reads limits, workers and the engine version off the status.
    // Without it there is nothing truthful to draw, and reaching for it would
    // throw on the first field.
    if (!state.status) {
      main.replaceChildren(hubUnreachableBanner());
      return;
    }
    if (state.screen === "sources") main.replaceChildren(renderSourcesScreen());
    else if (state.screen === "new") main.replaceChildren(renderNewScreen());
    else if (state.screen === "run") main.replaceChildren(renderRunScreen(currentRunId()));
    else if (state.screen === "report") main.replaceChildren(renderReportScreen(currentRunId()));
    else main.replaceChildren(renderRecentScreen());
  }

  /** The label with a rule running out to its right, as every screen head has. */
  function ruledOverline(text) {
    return el("div", { class: "overline-ruled" }, [
      el("span", { class: "overline", text: text }),
      el("span", { class: "overline-rule", "aria-hidden": "true" })
    ]);
  }

  /** Loads whatever the route needs, then renders it. */
  function onRoute() {
    const screen = currentScreen();
    clearTimeout(state.runTimer);
    // The note this timer restores belongs to a panel the next render replaces.
    clearTimeout(state.noteTimer);
    render();
    if (state.loading) return;
    if (screen === "recent") loadRuns();
    // The launcher shows what past runs weighed, so it needs the same list.
    // Reloaded on every entry, not once: coming back from a run that just
    // finished must show that run's weight. onRoute fires on hashchange
    // alone, so the render inside loadRuns cannot bring us back here.
    else if (screen === "new") loadRuns();
    else if (screen === "run" || screen === "report") {
      const id = currentRunId();
      if (id && (!state.run || state.run.id !== id)) loadRun(id);
      else if (id) render();
    }
  }

  // -------------------------------------------------------- screen: sources

  function renderSourcesScreen() {
    const section = el("section", {}, [
      ruledOverline("// sources"),
      el("h1", { class: "page-title", text: "Fleet health" }),
      el("p", {
        class: "page-sub",
        text: "Everything on this screen is an observation except the environment column, which is "
          + "declared. Sources are configured at deploy time, and the launcher cannot add one."
      })
    ]);

    if (state.loading) {
      section.appendChild(el("div", { class: "sources-wrap" }, [skeletonTable()]));
      return section;
    }
    if (state.sourcesError) {
      // Showing the last known values here would be worse than showing none:
      // a stale health table is the one thing this page must never be.
      section.appendChild(el("div", { class: "banner", "data-tone": "crit" }, [
        svg([["circle", { cx: "12", cy: "12", r: "9" }], ["path", { d: "M12 7.5v5M12 15.8v.2" }]], 16),
        el("div", {
          text: "The Hub is not answering, so fleet health is unknown. This is the Hub itself, "
            + "not any one source. Nothing below is shown rather than showing values that may be stale."
        })
      ]));
      return section;
    }

    section.appendChild(el("div", { class: "sources-wrap" }, [sourcesTable(state.sources)]));
    section.appendChild(el("p", {
      class: "sources-note",
      text: "The environment column is declared by each source's own configuration and is never "
        + "measured. A misconfigured deployment can label production as staging."
    }));
    return section;
  }

  const SOURCE_COLUMNS = [
    "Source", "Type", "Env (declared)", "Health", "Last success", "Unreachable for", "Producer", "Last error"
  ];

  function sourcesTable(sources) {
    const head = el("tr", {}, SOURCE_COLUMNS.map(function (name) {
      return el("th", { text: name, scope: "col" });
    }));
    const body = sources.map(sourceRow);
    return el("table", { class: "table" }, [
      el("thead", {}, [head]),
      el("tbody", {}, body)
    ]);
  }

  function sourceRow(source) {
    const now = Date.now();
    const row = el("tr", source.reachable ? {} : { "data-unreachable": "true" });
    row.appendChild(el("td", { class: "table-strong", text: source.name }));
    row.appendChild(el("td", {}, [el("span", { class: "chip", text: PSL.KIND_LABEL[source.kind] || source.kind })]));
    row.appendChild(el("td", {}, [el("span", { class: "chip chip-declared", text: source.environment })]));
    row.appendChild(el("td", {}, [healthCell(source, now)]));
    row.appendChild(el("td", {
      "data-align": "right",
      text: source.last_success_ms ? PSL.dur(now - source.last_success_ms) + " ago" : "never"
    }));
    row.appendChild(el("td", {
      "data-align": "right",
      text: source.unreachable_since_ms ? PSL.dur(now - source.unreachable_since_ms) : "—"
    }));
    row.appendChild(producerCell(source));
    row.appendChild(el("td", { class: "table-mono", text: source.last_error_code || "—" }));
    return row;
  }

  function healthCell(source, now) {
    if (source.reachable) {
      return el("span", { class: "health", "data-health": "ok" }, [
        el("span", { class: "health-dot" }),
        el("span", { text: source.last_attempt_ms == null ? "not yet observed" : "reachable" })
      ]);
    }
    return el("span", { class: "health", "data-health": "crit" }, [
      el("span", { class: "health-dot" }),
      el("span", { text: "unreachable " + PSL.dur(now - source.unreachable_since_ms) })
    ]);
  }

  function producerCell(source) {
    if (!source.producer_version) {
      // Two different absences. A backend has no producer at all, and saying
      // so about a daemon nobody has reached yet would be a false statement
      // about a source that does have one.
      return source.kind === "daemon"
        ? el("td", {
          class: "table-muted",
          text: "unknown",
          title: "This daemon reports a producer version, but the Hub has not had a successful "
            + "response from it yet."
        })
        : el("td", {
          class: "table-muted",
          "data-align": "right",
          text: "n/a",
          title: "A trace backend stores traces and detects nothing, so it reports no producer version."
        });
    }

    const cell = el("td", { class: "table-mono", "data-align": "right" }, [el("span", { text: source.producer_version })]);
    const gap = PSL.skew(source.producer_version);
    if (gap) {
      cell.appendChild(el("span", {
        class: "skew-pill",
        "data-dir": gap.dir,
        text: gap.label,
        title: "perf-sentinel is pre-1.0, so detectors change between minors. The Hub compares two "
          + "version strings and cannot know whether this minor changed detection."
      }));
    }
    return cell;
  }

  function skeletonTable() {
    const rows = [];
    for (let index = 0; index < 4; index++) rows.push(el("div", { class: "skeleton skeleton-row" }));
    return el("div", { class: "skeleton-stack" }, rows);
  }


  // ---------------------------------------------------- screen: new analysis

  const QUICK_RANGES = [
    "15m", "30m", "1h", "3h", "6h", "12h", "24h", "2d", "7d", "30d", "90d", "180d"
  ];

  function selectedSource() {
    return (state.sources || []).find(function (source) { return source.id === state.form.sourceId; }) || null;
  }

  /** Changing source clears both acknowledgements and closes the picker: they
      were answers about a different source. */
  function selectSource(id) {
    state.form.sourceId = id;
    state.form.ackUnreachable = false;
    state.form.ackHeavy = false;
    state.form.pickerOpen = false;
    state.form.detection = {};
    render();
  }

  function setMode(mode) {
    state.form.mode = mode;
    // Switching clears the other field, and a trace ID takes no window at all,
    // so the picker cannot stay open behind a hidden control.
    if (mode === "trace") {
      state.form.service = "";
      state.form.pickerOpen = false;
    } else {
      state.form.traceId = "";
    }
    render();
  }

  /**
   * Updated in place, never re-rendered. A full render replaces the range and
   * the number field mid-interaction, which drops the browser's pointer
   * capture (the handle stops following the mouse) and the text caret.
   */
  function setMaxTraces(value) {
    state.form.maxTraces = value;
    // Dropping back below the ceiling withdraws the question that was asked
    // about it.
    if (!PSL.weightBand(value, tracesCap()).needsAck) state.form.ackHeavy = false;
    refreshTraces();
    updateSubmit();
  }

  /** Everything on screen that reads maxTraces, refreshed without a re-render. */
  function refreshTraces() {
    const cap = tracesCap();
    const band = PSL.weightBand(state.form.maxTraces, cap);
    const over = band.key === "over" || band.key === "invalid";
    const value = String(state.form.maxTraces);

    const number = document.getElementById("traces-number");
    const slider = document.getElementById("traces-slider");
    // Assigned only when it differs, so the element the operator is dragging or
    // typing into is left alone.
    if (number && number.value !== value) number.value = value;
    if (number) number.toggleAttribute("data-over", over);
    if (slider) {
      const clamped = String(Math.min(Math.max(state.form.maxTraces, 1), cap));
      if (slider.value !== clamped) slider.value = clamped;
    }

    const chip = document.getElementById("traces-band");
    if (chip) {
      chip.textContent = band.label;
      chip.setAttribute("style", bandStyle(band));
    }

    const note = document.getElementById("traces-cap");
    if (note) {
      note.textContent = capNote(band, cap);
      note.setAttribute("data-over", over ? "true" : "false");
    }

    const body = document.getElementById("traces-body");
    if (body) {
      body.textContent = band.body;
      body.setAttribute("style", "color:" + band.fg);
    }

    const slot = document.getElementById("traces-ack");
    if (!slot) return;
    if (band.needsAck) slot.replaceChildren(heavyAck());
    else slot.replaceChildren();
  }

  function renderNewScreen() {
    const section = el("section", {}, [
      ruledOverline("// new analysis"),
      el("h1", { class: "page-title", text: "Run an analysis" })
    ]);

    if (state.loading) {
      section.appendChild(el("div", { class: "new-grid" }, [
        el("div", { class: "card skeleton", style: "height:280px" }),
        el("div", { class: "card skeleton", style: "height:280px" })
      ]));
      return section;
    }
    if (state.sourcesError) {
      section.appendChild(hubUnreachableBanner());
      return section;
    }
    if (!state.sources || state.sources.length === 0) {
      section.appendChild(el("div", { class: "empty-state", text: "This Hub has no configured source." }));
      return section;
    }

    const source = selectedSource();
    const skew = source && PSL.skew(source.producer_version);
    const right = el("div", { class: "new-column" }, [parametersPanel(), costBand()]);
    const advanced = source && source.kind !== "daemon" ? advancedPanel() : null;
    if (advanced) right.appendChild(advanced);
    if (skew) right.appendChild(skewNotice(source, skew));
    if (source && !source.reachable) right.appendChild(unreachableNotice(source));
    right.appendChild(submitRow());

    section.appendChild(el("div", { class: "new-grid" }, [sourcePanel(), right]));
    return section;
  }

  function hubUnreachableBanner() {
    return el("div", { class: "banner", "data-tone": "crit" }, [
      svg([["circle", { cx: "12", cy: "12", r: "9" }], ["path", { d: "M12 7.5v5M12 15.8v.2" }]], 16),
      el("div", {
        text: "The Hub is not answering. This is the Hub itself and not any one source, so nothing "
          + "can be launched from here until it is back. Reload once it responds again."
      })
    ]);
  }

  function sourcePanel() {
      return el("div", {class: "card source-panel"}, [
        el("div", {class: "panel-head"}, [
            el("span", {class: "overline", text: "// source"}),
            el("span", {class: "panel-head-source", text: state.sources.length + " configured"})
        ]),
        el("div", {class: "source-list", role: "radiogroup", "aria-label": "Source"},
            state.sources.map(sourceRadio)),
        el("p", {class: "panel-note"}, [
            el("span", {class: "panel-note-rule", "aria-hidden": "true"}),
            el("span", {
                text: "A dashed outline marks a value the source declares about itself. The Hub never "
                    + "measures it. A misconfigured deployment can label production as staging."
            })
        ])
    ]);
  }

  function sourceRadio(source) {
    const selected = source.id === state.form.sourceId;
    const now = Date.now();
    const line1 = el("div", { class: "source-line" }, [
      el("span", { class: "source-name", text: source.name }),
      el("span", { class: "health", "data-health": source.reachable ? "ok" : "crit" }, [
        el("span", { class: "health-dot" }),
        el("span", {
          text: source.reachable
            ? "reachable"
            : "unreachable " + PSL.dur(now - source.unreachable_since_ms)
        })
      ])
    ]);

    const line2 = el("div", { class: "source-line source-meta" }, [
      el("span", { class: "chip", text: PSL.KIND_LABEL[source.kind] || source.kind }),
      el("span", { class: "chip chip-declared", text: source.environment }),
      el("span", { class: "source-version", text: producerLabel(source) })
    ]);
    const gap = PSL.skew(source.producer_version);
    if (gap) line2.appendChild(el("span", { class: "skew-pill", "data-dir": gap.dir, text: gap.label }));

    const button = el("button", {
      type: "button",
      class: "source-row",
      role: "radio",
      "aria-checked": selected ? "true" : "false"
    }, [el("span", { class: "source-dot" }), el("span", {}, [line1, line2])]);
    button.addEventListener("click", function () { selectSource(source.id); });
    return button;
  }

  function producerLabel(source) {
    if (source.producer_version) return "producer " + source.producer_version;
    return source.kind === "daemon" ? "producer unknown" : "no producer version";
  }

  function parametersPanel() {
    const source = selectedSource();
    if (!source) {
      return el("div", { class: "card params-panel" }, [
        el("div", { class: "empty-state", text: "Pick a source to see what it takes." })
      ]);
    }

    const head = el("div", { class: "panel-head" }, [
      el("span", { class: "overline", text: source.kind === "daemon" ? "// parameters" : "// query" }),
      el("span", { class: "panel-head-source", text: source.name })
    ]);

    const panel = el("div", { class: "card params-panel" }, [head]);
    if (source.kind === "daemon") panel.appendChild(daemonNotice());
    else backendControls(source).forEach(function (node) { panel.appendChild(node); });
    return panel;
  }

  function daemonNotice() {
    return el("div", { class: "notice" }, [
      svg([["circle", { cx: "12", cy: "12", r: "9" }], ["path", { d: "M12 11v5M12 8.2v.2" }]], 16),
      el("div", {}, [
        el("p", { text: "No parameters. A daemon snapshot is whatever it holds in memory right now." }),
        el("p", {
          class: "notice-sub",
          text: "The window is the daemon's own ring buffer. There is nothing to widen: asking for "
            + "three hours from a process that keeps ten minutes would be a request the source "
            + "cannot answer, so the launcher does not offer it."
        })
      ])
    ]);
  }

  function backendControls(source) {
    const nodes = [modeSwitch()];
    if (state.form.mode === "trace") {
      nodes.push(field("Trace ID", traceInput()));
      nodes.push(el("p", {
        class: "field-note",
        text: "An ID resolves to exactly one trace, so neither the window nor the trace cap applies."
      }));
      return nodes;
    }

    nodes.push(field("Service name", serviceInput()));
    nodes.push(field("Time range", rangeControl(source), state.form.rangeMode === "absolute"
      ? "absolute, fixed at submission"
      : "relative to the moment the run starts"));
    nodes.push(maxTracesBlock());
    return nodes;
  }

  function modeSwitch() {
    const group = el("div", { class: "segmented", role: "radiogroup", "aria-label": "Selection mode" });
    [["service", "Service"], ["trace", "Trace ID"]].forEach(function (entry) {
      const button = el("button", {
        type: "button",
        role: "radio",
        "aria-checked": state.form.mode === entry[0] ? "true" : "false",
        text: entry[1]
      });
      button.addEventListener("click", function () { setMode(entry[0]); });
      group.appendChild(button);
    });
    return field("Select traces by", group, "one or the other, never both");
  }

  function serviceInput() {
    const input = el("input", {
      type: "text",
      class: "input",
      value: state.form.service,
      placeholder: "order-service",
      spellcheck: "false"
    });
    input.addEventListener("input", function () {
      state.form.service = input.value;
      updateSubmit();
    });
    return input;
  }

  function traceInput() {
    const input = el("input", {
      type: "text",
      class: "input",
      value: state.form.traceId,
      placeholder: "4bf92f3577b34da6a3ce929d0e0e4736",
      spellcheck: "false"
    });
    input.addEventListener("input", function () {
      state.form.traceId = input.value;
      updateSubmit();
    });
    return input;
  }

  function field(label, control, gloss) {
    const heading = el("span", { class: "field-label" }, [el("span", { text: label })]);
    if (gloss) heading.appendChild(el("span", { class: "field-gloss", text: gloss }));
    return el("div", { class: "field" }, [heading, control]);
  }

  function windowLabel() {
    if (state.form.rangeMode === "absolute") {
      return PSL.dtHuman(state.form.fromMs) + " → " + PSL.dtHuman(state.form.toMs);
    }
    return "Last " + PSL.humanDur(state.form.lookback);
  }

  /** The span, and the argument the run will actually carry. */
  function rangeWire() {
    const span = PSL.dur(windowSpanMs());
    return state.form.rangeMode === "absolute"
      ? span + " · from_ms/to_ms"
      : span + " · lookback = " + state.form.lookback;
  }

  function windowSpanMs() {
    return state.form.rangeMode === "absolute"
      ? state.form.toMs - state.form.fromMs
      : PSL.parseDur(state.form.lookback);
  }

  function rangeControl(source) {
    const button = el("button", { type: "button", class: "range-pill", "aria-expanded": String(state.form.pickerOpen) }, [
      svg([["circle", { cx: "12", cy: "12", r: "9" }], ["path", { d: "M12 7v5l3.2 2" }]], 14),
      el("span", { class: "range-pill-label", text: windowLabel() }),
      svg([["path", { d: "M6 9l6 6 6-6" }]], 11)
    ]);
    button.addEventListener("click", function () {
      state.form.pickerOpen = !state.form.pickerOpen;
      render();
    });

    const wrap = el("div", { class: "range" }, [
      el("div", { class: "range-row" }, [
        button,
        el("span", { class: "range-wire", text: rangeWire() })
      ])
    ]);
    if (state.form.pickerOpen) wrap.appendChild(rangePicker());
    const notes = rangeConsequences(source);
    if (notes.length > 0) wrap.appendChild(el("div", { class: "consequences" }, notes));
    return wrap;
  }

  /** Consequences appear under the control, not after the run. */
  function rangeConsequences(source) {
    const notes = [];
    const spanMs = windowSpanMs();
    if (spanMs > 86400000) {
      notes.push(consequence("A wider window returns no more data. The run still stops at the trace "
        + "cap, so the result is a sample spread over the period rather than the period itself."));
    }
    if (spanMs > 7 * 86400000) {
      notes.push(consequence("The whole scan has to finish inside the "
        + (state.status.limits.analysis_timeout_seconds) + "-second ceiling, which is usually the "
        + "limit met first. Expect a timeout rather than a result."));
    }
    if (source.retention_hours != null && spanMs > source.retention_hours * 3600000) {
      notes.push(consequence("This source declares it keeps " + PSL.dur(source.retention_hours * 3600000)
        + " of traces. A window beyond that comes back short, or is refused as "
        + "source_rejected_request.", "warn"));
    } else if (source.retention_hours == null && spanMs > 86400000) {
      notes.push(consequence("Nobody declared how far back this source keeps traces, so the Hub "
        + "cannot tell whether it can answer this window at all."));
    }
    return notes;
  }

  function consequence(text, tone) {
    return el("span", { class: "consequence", "data-tone": tone || "muted" }, [
      el("span", { class: "consequence-dot" }),
      el("span", { text: text })
    ]);
  }

  function rangePicker() {
    const backdrop = el("div", { class: "picker-backdrop" });
    backdrop.addEventListener("click", function () { state.form.pickerOpen = false; render(); });

    const from = el("input", { type: "datetime-local", class: "input-date", value: PSL.dtLocal(state.form.fromMs) });
    const to = el("input", { type: "datetime-local", class: "input-date", value: PSL.dtLocal(state.form.toMs) });
    const note = el("span", { class: "picker-note" });
    const apply = el("button", { type: "button", class: "picker-apply", text: "Apply range" });

    function readAbsolute() {
      const start = Date.parse(from.value);
      const end = Date.parse(to.value);
      const ordered = Number.isFinite(start) && Number.isFinite(end) && start < end;
      const past = Number.isFinite(end) && end <= Date.now();
      const valid = ordered && past;
      note.textContent = !ordered
        ? "The start must come before the end."
        : !past
          ? "The end cannot be in the future."
          : PSL.dur(end - start) + " selected";
      note.setAttribute("data-invalid", valid ? "false" : "true");
      apply.disabled = !valid;
      return { start: start, end: end, valid: valid };
    }
    from.addEventListener("input", readAbsolute);
    to.addEventListener("input", readAbsolute);
    apply.addEventListener("click", function () {
      const read = readAbsolute();
      if (!read.valid) return;
      applyRange("absolute", { fromMs: read.start, toMs: read.end });
    });

    const left = el("div", { class: "picker-pane" }, [
      dateField("From", from),
      dateField("To", to),
      el("div", { class: "picker-apply-row" }, [apply, note]),
      el("div", { class: "picker-rule" }),
      el("p", { class: "overline", text: "Custom relative" }),
      customRelativeRow()
    ]);

    const right = el("div", { class: "picker-pane picker-right" }, [
      el("p", { class: "overline picker-quick-head", text: "Quick ranges" }),
      el("div", { class: "picker-quick" }, QUICK_RANGES.map(function (value) {
        const active = state.form.rangeMode === "relative" && state.form.lookback === value;
        const button = el("button", {
          type: "button",
          class: "picker-quick-item",
          "aria-current": active ? "true" : null,
          text: "Last " + PSL.humanDur(value)
        });
        button.addEventListener("click", function () { applyRange("relative", { lookback: value }); });
        return button;
      }))
    ]);

    readAbsolute();
    return el("div", {}, [backdrop, el("div", { class: "picker" }, [left, right])]);
  }

  function applyRange(mode, values) {
    state.form.rangeMode = mode;
    Object.keys(values).forEach(function (key) { state.form[key] = values[key]; });
    state.form.pickerOpen = false;
    render();
  }

  function dateField(label, control) {
    return el("label", { class: "picker-field" }, [
      el("span", { class: "picker-field-label", text: label }),
      control
    ]);
  }

  function customRelativeRow() {
    const qty = el("input", {
      type: "number",
      class: "input-qty",
      min: "1",
      value: String(state.form.customQty)
    });
    const units = el("div", { class: "segmented segmented-sm", role: "radiogroup", "aria-label": "Unit" });
    ["m", "h", "d"].forEach(function (unit) {
      const button = el("button", {
        type: "button",
        role: "radio",
        "aria-checked": state.form.customUnit === unit ? "true" : "false",
        text: unit
      });
      // Picking a unit selects it. Applying is a separate, deliberate click,
      // so a half-typed quantity is never submitted by choosing a unit.
      button.addEventListener("click", function () {
        state.form.customUnit = unit;
        state.form.customQty = Math.max(1, Number(qty.value) || 1);
        render();
      });
      units.appendChild(button);
    });

    const apply = el("button", { type: "button", class: "pill-button pill-sm", text: "Apply" });
    apply.addEventListener("click", function () {
      const quantity = Math.max(1, Number(qty.value) || 1);
      state.form.customQty = quantity;
      applyRange("relative", { lookback: quantity + state.form.customUnit });
    });

    return el("div", { class: "picker-custom" }, [
      el("span", { class: "picker-custom-lead", text: "Last" }),
      qty,
      units,
      apply
    ]);
  }

  function maxTracesBlock() {
    const cap = state.status.limits.max_traces_cap;
    const band = PSL.weightBand(state.form.maxTraces, cap);
    const over = band.key === "over" || band.key === "invalid";

    const number = el("input", {
      type: "number",
      id: "traces-number",
      class: "input input-traces",
      min: "1",
      max: String(cap),
      value: String(state.form.maxTraces)
    });
    if (over) number.setAttribute("data-over", "true");
    number.addEventListener("input", function () { setMaxTraces(Number(number.value)); });

    const head = el("div", { class: "traces-head" }, [
      number,
      el("span", { id: "traces-band", class: "band-chip", style: bandStyle(band), text: band.label }),
      el("span", {
        id: "traces-cap",
        class: "traces-cap",
        "data-over": over ? "true" : "false",
        text: capNote(band, cap)
      })
    ]);

    const slider = el("input", {
      type: "range",
      id: "traces-slider",
      min: "1",
      max: String(cap),
      step: "1",
      value: String(Math.min(Math.max(state.form.maxTraces, 1), cap)),
      "aria-label": "Max traces"
    });
    slider.addEventListener("input", function () { setMaxTraces(Number(slider.value)); });

    // The container carries the pill radius and clips its children, so only the
    // outer ends are rounded and the two inner joins stay square.
    const segments = el("div", { class: "band-segs", "aria-hidden": "true" },
      bands(cap).map(function (band) {
        const segment = el("span", { class: "band-seg", "data-seg": band.tone });
        segment.style.width = band.width;
        return segment;
      }));

    const block = el("div", { class: "field" }, [
      el("label", { class: "field-label" }, [
        el("span", { text: "Max traces" }),
        el("span", { class: "field-gloss", text: "how much comes back, not how far back" })
      ]),
      head,
      el("div", { class: "band-track" }, [segments, slider]),
      bandScale(cap),
      el("p", { id: "traces-body", class: "band-body", style: "color:" + band.fg, text: band.body }),
      // A slot rather than a conditional child: the acknowledgement appears and
      // disappears as the count crosses the ceiling, and refreshing it in place
      // keeps the rest of the block untouched.
      el("div", { id: "traces-ack" }, band.needsAck ? [heavyAck()] : [])
    ]);
    block.appendChild(sinkPanel());
    const history = weightHistory();
    if (history) block.appendChild(history);
    return block;
  }

  /**
   * What this source's own runs weighed, at the count they asked for. A
   * measurement rather than a model: a report's size follows how many spans
   * its traces carry, which differs per service and which the launcher cannot
   * know before the run. Absent until this source has a measured run.
   */
  function weightHistory() {
    const source = selectedSource();
    if (!source || !state.runs) return null;
    const past = state.runs.filter(function (run) {
      const result = run.result || {};
      const request = run.request || {};
      return run.source_id === source.id &&
        run.status === "succeeded" &&
        typeof result.report_bytes === "number" &&
        typeof request.max_traces === "number";
    }).slice(0, 3);
    if (past.length === 0) return null;

    return el("div", { class: "sink" }, [
      el("div", { class: "sink-head" }, [
        el("span", { class: "overline", text: "What your own runs weighed" }),
        el("span", { class: "sink-sub", text: "Measured on this source, not predicted." })
      ]),
      el("dl", { class: "sink-rows" }, past.flatMap(function (run) {
        return [
          el("dt", { text: PSL.bytes(run.result.report_bytes) }),
          el("dd", {
            text: run.request.max_traces + " traces, " + run.result.findings + " findings, "
              + PSL.dur(Date.now() - (run.finished_at_ms || run.created_at_ms)) + " ago"
          })
        ];
      }))
    ]);
  }

  function capNote(band, cap) {
    if (band.key === "invalid") return "at least 1";
    if (band.key === "over") return "above the hard cap of " + cap + ", the service will reject this";
    return "hard cap " + cap;
  }

  /**
   * The bands that exist at this cap, each with the boundary it ends at and
   * its share of the rule. A cap at or below a boundary means that band never
   * occurs: it is dropped rather than drawn at zero width under a label
   * repeating the cap.
   */
  function bands(cap) {
    const inner = [["ok", 500, "safe"], ["warn", 1200, "heavy"]]
      .filter(function (band) { return band[1] < cap; });
    // The stripe that ends at the cap takes the tone of whichever band the cap
    // itself falls in, which is the one after the last inner boundary kept.
    // Always painting it crit would turn a Hub capped at 500 into an entirely
    // red rule over a range that is all comfortable.
    const TONES = ["ok", "warn", "crit"];
    const kept = inner.concat([[TONES[inner.length], cap, "cap"]]);

    let previous = 0;
    return kept.map(function (band) {
      const share = (band[1] - previous) / cap;
      previous = band[1];
      return {
        tone: band[0],
        label: group(band[1]) + " " + band[2],
        width: (share * 100).toFixed(2) + "%"
      };
    });
  }

  /** Each label sits at the right edge of its own band, so it marks a boundary. */
  function bandScale(cap) {
    const current = bands(cap);
    const grid = el("div", { class: "band-scale-grid" }, current.map(function (band) {
      return el("span", { text: band.label });
    }));
    grid.style.gridTemplateColumns = current.map(function (band) { return band.width; }).join(" ");
    return el("div", { class: "band-scale" }, [
      el("span", { class: "band-scale-start", text: "1" }),
      grid
    ]);
  }

  function group(value) {
    return String(value).replace(/\B(?=(\d{3})+(?!\d))/g, "\u202f");
  }

  // render() returns early when the status is missing, so every caller runs
  // with one in hand.
  function tracesCap() {
    return state.status.limits.max_traces_cap;
  }

  /**
   * What the sink guarantees, measured rather than predicted. The design bans
   * predicting a byte size, and its reason still holds: SQL template lengths
   * move a fixed-count report by tens of kilobytes. Both ends of the range are
   * fixed, though, so they can be stated as facts.
   */
  function sinkPanel() {
    const rows = [
      ["550 KB", "The floor. Fonts, styles and the dashboard itself, present in every report "
        + "whether it found one problem or none."],
      ["5 MiB", "The size the sink targets. Findings and span trees share it, so a run that "
        + "finds more of both sits closer to the ceiling."],
      ["70 %", "Share of the budget reserved for findings. Over it, findings are dropped "
        + "critical-first, so the ones you most wanted to see survive longest."],
      ["25", "Hard cap on the top offenders embedded for the Carbon tab, whatever the run size. "
        + "The full ranking is still computed, only the embed is capped."],
      ["span trees", "One tree per finding, for as many as fit the budget. Past it the rest "
        + "open without one, and the dashboard says so on the finding."]
    ];
    return el("div", { class: "sink" }, [
      el("div", { class: "sink-head" }, [
        el("span", { class: "overline", text: "What comes back, and what it drops first" }),
        el("span", { class: "sink-sub", text: "Constants from the sink, not predictions." })
      ]),
      el("dl", { class: "sink-rows" }, rows.flatMap(function (row) {
        return [el("dt", { text: row[0] }), el("dd", { text: row[1] })];
      }))
    ]);
  }

  function bandStyle(band) {
    return "color:" + band.fg + ";background:" + band.bg;
  }

  function heavyAck() {
    const node = checkbox(
      state.form.ackHeavy,
      "I accept a report that may come back trimmed.",
      function (checked) { state.form.ackHeavy = checked; updateSubmit(); });
    node.classList.add("checkbox-pill");
    return node;
  }

  /**
   * A producer behind the engine is worth saying out loud: perf-sentinel is
   * pre-1.0, so a detector added between minors does not run on the older
   * binary at all, and its absence looks exactly like a clean service.
   */
  function skewNotice(source, skew) {
    const engine = state.status.engine_version;
    const behind = skew.dir === "behind";
    return el("section", { class: "notice-block", "data-tone": behind ? "warn" : "info" }, [
      warningGlyph(16),
      el("div", { class: "notice-block-text" }, [
        el("p", {
          class: "notice-block-title",
          text: source.name + " runs " + source.producer_version + ", " + skew.label + " the "
            + engine + " binary embedded in the Hub."
        }),
        el("p", {
          class: "notice-block-body",
          text: behind
            ? "perf-sentinel is pre-1.0, so detectors change between minors. A detector added in "
              + engine + " does not run on this producer at all, and its absence looks exactly like "
              + "a clean service. Read a low finding count from this source as unmeasured, not as healthy."
            : "Envelopes are additive, so nothing breaks. Findings from a detector this Hub does not "
              + "know about arrive unnamed. The Hub compares two version strings and cannot know "
              + "whether this minor changed detection at all."
        })
      ])
    ]);
  }

  function unreachableNotice(source) {
    const text = el("div", { class: "notice-block-text" }, [
      el("p", {
        class: "notice-block-title",
        text: source.name + " has been unreachable for " + PSL.dur(Date.now() - source.unreachable_since_ms) + "."
      })
    ]);
    if (source.last_success_ms) {
      text.appendChild(el("p", {
        class: "notice-block-body",
        text: "Last successful contact " + PSL.dur(Date.now() - source.last_success_ms) + " ago."
      }));
    }
    if (source.last_error_code) {
      text.appendChild(el("p", { class: "notice-block-body" }, [
        el("span", { text: "The last attempt returned " }),
        el("span", { class: "code-inline", text: source.last_error_code }),
        el("span", { text: ": " + (PSL.ERRORS[source.last_error_code] || "the Hub could not reach it.") })
      ]));
    }
    text.appendChild(el("p", {
      class: "notice-block-body",
      text: "Running now will consume a worker slot and will almost certainly end with the same code."
    }));
    text.appendChild(checkbox(
      state.form.ackUnreachable,
      "Run it anyway",
      function (checked) { state.form.ackUnreachable = checked; updateSubmit(); }));

    return el("section", { class: "notice-block", "data-tone": "warn" }, [warningGlyph(17), text]);
  }

  function warningGlyph(size) {
    return svg([
      ["path", { d: "M10.3 3.9 1.9 18a2 2 0 0 0 1.7 3h16.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" }],
      ["path", { d: "M12 9v4M12 17h.01" }]
    ], size);
  }

  function checkbox(checked, label, onChange) {
    const input = el("input", { type: "checkbox" });
    input.checked = checked;
    input.addEventListener("change", function () { onChange(input.checked); });
      return el("label", {class: "checkbox"}, [input, el("span", {text: label})]);
  }

  /** Reported by the service, not assumed: the button is a promise of cost. */
  function costBand() {
    const limits = state.status.limits;
    const queue = state.status.queue_depth;
    const cells = [
      [String(limits.max_traces_cap), "traces", "Hard cap per run", "The service rejects anything above it."],
      [String(limits.analysis_timeout_seconds), "s", "Timeout", "Then the run is killed, marked timeout."],
      [String(state.status.workers), "workers",
        queue === 1 ? "1 job queued now" : queue + " jobs queued now",
        "That many runs at a time across the whole Hub."],
      [String(limits.report_retention_hours), "h", "Report retention", "Then the file is deleted. Links die."]
    ];
    return el("section", { class: "card cost" }, [
      el("div", { class: "cost-head" }, [
        el("span", { class: "overline", text: "// what this run costs" }),
        el("span", { class: "cost-sub", text: "Reported by the service, not assumed." })
      ]),
      el("div", { class: "cost-grid" }, cells.map(function (cell) {
        return el("div", { class: "cost-cell" }, [
          el("p", { class: "cost-figure" }, [
            el("span", { text: cell[0] }),
            el("span", { class: "cost-unit", text: cell[1] })
          ]),
          el("p", { class: "cost-label", text: cell[2] }),
          el("p", { class: "cost-note", text: cell[3] })
        ]);
      }))
    ]);
  }

  /**
   * What blocks the run, or null when nothing does. Mirrors the server's own
   * rules so the operator is told before spending a round trip.
   */
  function submitBlocker() {
    const source = selectedSource();
    if (!source) return "Pick a source.";
    if (!state.status.engine_version) return "This Hub has no analysis engine configured.";
    if (!source.reachable && !state.form.ackUnreachable) return "Confirm you want to run against an unreachable source.";
    if (source.kind === "daemon") return null;
    if (state.form.mode === "trace") {
      return state.form.traceId.trim() ? null : "Enter a trace ID.";
    }
    if (!state.form.service.trim()) return "Enter a service name.";
    const band = PSL.weightBand(state.form.maxTraces, tracesCap());
    if (band.key === "over") return "The trace cap is above what the service accepts.";
    if (band.key === "invalid") return "A run needs at least one trace.";
    return band.needsAck && !state.form.ackHeavy ? "Confirm the report will be trimmed." : null;
  }

  /** Restates the request in a sentence, so the button is not a leap of faith. */
  function submitSentence() {
    const source = selectedSource();
    if (!source) return "";
    if (source.kind === "daemon") {
      return "Takes a snapshot of what " + source.name + " holds in memory. No query is sent to a "
        + "trace backend. " + queuePhrase();
    }
    if (state.form.mode === "trace") {
      return "Fetches one trace by ID from " + source.name + ". " + queuePhrase();
    }
    return "Reads up to " + state.form.maxTraces + " traces for "
      + (state.form.service.trim() || "a service") + " across "
      + (state.form.rangeMode === "absolute" ? "the selected window" : "the last " + PSL.humanDur(state.form.lookback))
      + " of " + source.name + ". " + queuePhrase();
  }

  function queuePhrase() {
    const queue = state.status.queue_depth;
    if (queue === 0) return "Nothing is queued ahead of it.";
    return queue === 1 ? "Queued behind 1 job." : "Queued behind " + queue + " jobs.";
  }

  function submitRow() {
    const button = el("button", { type: "button", class: "submit", id: "submit" }, [
      playGlyph(),
      el("span", { text: "Run analysis" })
    ]);
    button.addEventListener("click", submit);
    const row = el("div", { class: "submit-row" }, [
      button,
      el("p", { class: "submit-sentence", id: "submit-sentence" })
    ]);
    queueMicrotask(updateSubmit);
    return row;
  }

  function playGlyph() {
    const node = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    node.setAttribute("viewBox", "0 0 24 24");
    node.setAttribute("width", "15");
    node.setAttribute("height", "15");
    node.setAttribute("fill", "currentColor");
    node.setAttribute("aria-hidden", "true");
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", "M7 4.5v15l13-7.5z");
    node.appendChild(path);
    return node;
  }

  function updateSubmit() {
    const button = document.getElementById("submit");
    const sentence = document.getElementById("submit-sentence");
    if (!button || !sentence) return;
    const blocker = submitBlocker();
    button.disabled = blocker !== null;
    button.title = blocker || "";
    sentence.textContent = blocker || submitSentence();
    sentence.setAttribute("data-blocked", blocker ? "true" : "false");
  }

  function buildRequest(source) {
    if (source.kind === "daemon") return {};
    if (state.form.mode === "trace") {
      const trace = { trace_id: state.form.traceId.trim() };
      if (Object.keys(state.form.detection).length > 0) trace.detection = state.form.detection;
      return trace;
    }
    const request = { service: state.form.service.trim(), max_traces: state.form.maxTraces };
    if (Object.keys(state.form.detection).length > 0) request.detection = state.form.detection;
    if (state.form.rangeMode === "absolute") {
      request.from_ms = state.form.fromMs;
      request.to_ms = state.form.toMs;
    } else {
      request.lookback = state.form.lookback;
    }
    return request;
  }

  function submit() {
    const source = selectedSource();
    if (!source || submitBlocker()) return;
    const button = document.getElementById("submit");
    button.disabled = true;

    fetch("/api/analyses", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ source_id: source.id, request: buildRequest(source) })
    }).then(function (response) {
      return response.json().then(function (payload) { return { ok: response.ok, payload: payload }; });
    }).then(function (result) {
      if (!result.ok) throw new Error(result.payload.detail || "The Hub refused the request.");
      location.hash = "#/run/" + result.payload.id;
    }).catch(function (error) {
      const sentence = document.getElementById("submit-sentence");
      if (sentence) {
        sentence.textContent = String(error.message || error);
        sentence.setAttribute("data-blocked", "true");
      }
      updateSubmit();
    });
  }


  // ------------------------------------------------------- screen: one run

  function renderRunScreen(id) {
    const run = state.run;
    const section = el("section", {}, [backLink()]);
    if (state.runError) {
      section.appendChild(el("div", { class: "empty-state", text: "No analysis with that ID." }));
      return section;
    }
    if (!run || run.id !== id) {
      section.appendChild(el("div", { class: "card skeleton", style: "height:220px;margin-top:16px" }));
      return section;
    }

    const key = PSL.statusKey(run);
    const view = runView(run, key);
    section.appendChild(el("div", { class: "run-head" }, [
      el("span", { class: "status-pill", "data-status": key, text: key }),
      el("span", { class: "run-id", text: run.id })
    ]));
    section.appendChild(el("h1", { class: "page-title", text: view.headline }));
    section.appendChild(el("p", { class: "page-sub", text: view.sub }));

    const left = el("div", { class: "run-left" }, [eventLog(run, key)]);
    const outcome = outcomePanel(run, key, view);
    if (outcome) left.appendChild(outcome);
    section.appendChild(el("div", { class: "run-grid" }, [left, factsRail(run, key)]));
    return section;
  }

  function backLink() {
      return el("a", {class: "back-pill", href: "#/recent"}, [
        svg([["path", {d: "M15 18l-6-6 6-6"}]], 13),
        el("span", {text: "All analyses"})
    ]);
  }

  /** Headline and sub-line per state, in the source's own terms. */
  function runView(run, key) {
    if (key === "queued") {
      return {
        headline: "Waiting for a worker.",
        sub: "Every worker is busy. Nothing has been read from " + run.source_name
          + " yet, so nothing has been spent."
      };
    }
    if (key === "running") {
      return {
        headline: "Reading " + run.source_name + ".",
        sub: "A worker holds this job. The next thing that happens is a result or a failure, with "
          + "nothing in between."
      };
    }
    if (key === "empty") {
      return {
        headline: "It succeeded, and there is nothing in it.",
        sub: "This is not a failure and not an error. The source answered correctly, and the answer "
          + "was zero traces."
      };
    }
    if (key === "succeeded") {
      const result = run.result || {};
      const caveats = (result.warnings || []).length;
      return {
        headline: caveats > 0
          ? result.findings + " findings, and " + (caveats === 1 ? "a caveat" : caveats + " caveats")
            + " you should read first."
          : result.findings + " findings.",
        sub: "The report is ready and will be deleted in " + PSL.dur(run.expires_at_ms - Date.now()) + "."
      };
    }
    if (key === "interrupted") {
      return {
        headline: "The service restarted while this was running.",
        sub: "It stopped after " + PSL.dur((run.finished_at_ms || 0) - (run.started_at_ms || run.created_at_ms))
          + " of work. This is a resumption, not an error to investigate."
      };
    }
    if (key === "expired") {
      return {
        headline: "This report was deleted.",
        sub: "Reports live " + state.status.limits.report_retention_hours + " hours. This one expired "
          + PSL.dur(Date.now() - run.expires_at_ms) + " ago and the file is gone."
      };
    }
    return {
      headline: "Failed: " + String(run.error_code || "internal").replace(/_/g, " ") + ".",
      sub: "The Hub does not expose the process's error output by design. It gives one code out of "
        + "eight, and this is what that code means."
    };
  }

  /**
   * A receipt, not a feed. Every line is a timestamp the Hub wrote: the design
   * calls for a `dequeued` line too, but this service records one instant for
   * dequeue and start, and inventing a second would be interpolation.
   */
  function eventLog(run, key) {
    const rows = [logRow(run.created_at_ms, "accepted", "the request was validated and queued", "muted")];
    if (run.started_at_ms) {
      rows.push(logRow(run.started_at_ms, "started",
        run.kind === "daemon" ? "reading the daemon's in-memory store" : "reading " + PSL.KIND_LABEL[run.kind],
        "brand"));
    }
    if (key === "running") rows.push(logRow(null, "running", "no further event until the engine returns", "brand"));
    if (key === "queued") rows.push(logRow(null, "waiting", "every worker is busy, nothing has been read yet", "muted"));
    if (key === "succeeded" || key === "empty") {
      rows.push(logRow(run.finished_at_ms, "succeeded",
        "report written, retained " + state.status.limits.report_retention_hours + " h", "ok"));
    }
    if (key === "failed") rows.push(logRow(run.finished_at_ms, "failed", run.error_code, "crit"));
    if (key === "interrupted") {
      rows.push(logRow(run.finished_at_ms, "interrupted",
        "the Hub restarted, the run was abandoned and not replayed", "info"));
    }
    if (key === "expired") {
      rows.push(logRow(run.finished_at_ms, "succeeded", "report written", "muted"));
      rows.push(logRow(run.expires_at_ms, "deleted",
        "retention reached, the report no longer exists", "muted"));
    }

    return el("section", { class: "card log-card", "aria-label": "Service events" }, [
      el("div", { class: "log-head" }, [
        el("span", { class: "overline", text: "// service events" }),
        el("span", { class: "log-head-note", text: "Only what the Hub actually recorded." })
      ]),
      el("div", { class: "log" }, rows),
      el("div", { class: "log-foot" }, [el("p", { text: logClosing(run, key) })])
    ]);
  }

  function logRow(ms, name, detail, tone) {
    return el("div", { class: "log-row" }, [
      el("span", { class: "log-time", text: ms ? PSL.clock(ms) : "…" }),
      el("span", { class: "log-dot", "data-tone": tone }),
      el("span", { class: "log-text" }, [
        el("span", { class: "log-name", "data-tone": tone, text: name }),
        el("span", { class: "log-detail", text: detail })
      ])
    ]);
  }

  function logClosing(run, key) {
    if (key === "running" || key === "queued") {
      return "The engine reports nothing between start and finish. There is no percentage to show "
        + "and no arrival time to predict, so this screen shows neither. Only the events above, the "
        + "time spent, and the ceiling at which the service gives up. Expect one more line, not a stream.";
    }
    const instant = run.finished_at_ms && (run.finished_at_ms - run.created_at_ms) < 10000;
    return instant
      ? "This run was read and finished in one step, so every line above was written at once. It is "
        + "a receipt of what happened, not a feed."
      : "Every line above is a timestamp the Hub wrote. Nothing here is interpolated.";
  }

  function factsRail(run, key) {
    const elapsedMs = key === "queued"
      ? Date.now() - run.created_at_ms
      : (run.finished_at_ms || Date.now()) - (run.started_at_ms || run.created_at_ms);
    const figure = el("div", { class: "elapsed", "data-running": key === "running" ? "true" : "false" });
    PSL.durParts(elapsedMs).forEach(function (part) {
      figure.appendChild(el("span", { class: "elapsed-part" }, [
        el("span", { class: "elapsed-n", text: part.n }),
        el("span", { class: "elapsed-u", text: part.u })
      ]));
    });

    const elapsed = el("section", { class: "card rail-card" }, [
      el("p", { class: "overline", text: "// elapsed" }),
      figure
    ]);
    // The only bar in the product, and only while running: it measures a known
    // ceiling, not progress toward an unknown total.
    if (key === "running") elapsed.appendChild(ceilingRule(elapsedMs));
    elapsed.appendChild(el("p", { class: "rail-note", text: ceilingNote(key, elapsedMs) }));

    return el("aside", { class: "rail" }, [elapsed, requestCard(run)]);
  }

  function ceilingRule(elapsedMs) {
    const ceilingMs = state.status.limits.analysis_timeout_seconds * 1000;
    const fill = el("span", { class: "ceiling-fill" });
    fill.style.width = Math.min(100, (elapsedMs / ceilingMs) * 100).toFixed(1) + "%";
    if (elapsedMs > ceilingMs * 0.8) fill.setAttribute("data-near", "true");
    return el("div", { class: "ceiling", "aria-hidden": "true" }, [fill]);
  }

  function ceilingNote(key, elapsedMs) {
    const seconds = state.status.limits.analysis_timeout_seconds;
    if (key === "queued") {
      return "The " + seconds + "-second ceiling starts when a worker picks the job up, not now.";
    }
    if (key !== "running") return "Total time the run occupied a worker.";
    const left = seconds * 1000 - elapsedMs;
    return left <= 0
      ? "Past the " + seconds + " s ceiling. The run should already have been killed and marked timeout."
      : "Hard stop at " + seconds + " s, then the run is killed and marked timeout. "
        + PSL.dur(left) + " of ceiling left.";
  }

  function requestCard(run) {
    const request = run.request || {};
    const facts = [["source", run.source_name, "ui"], ["type", PSL.KIND_LABEL[run.kind] || run.kind, "mono"]];
    ["service", "trace_id", "lookback", "max_traces"].forEach(function (name) {
      if (request[name] != null) facts.push([name, String(request[name]), "mono"]);
    });
    if (request.from_ms) facts.push(["window", PSL.dtHuman(request.from_ms) + " → " + PSL.dtHuman(request.to_ms), "mono"]);
    Object.keys(request.detection || {}).forEach(function (name) {
      facts.push([name, String(request.detection[name]), "warn"]);
    });
    if (!request.service && !request.trace_id) facts.push(["parameters", "none", "muted"]);
    facts.push(["requested by", run.requested_by, "mono"]);
    facts.push(["detected by", run.producer_version
      ? PSL.detector(run.kind) + " " + run.producer_version
      : "not yet known", PSL.skew(run.producer_version) ? "warn" : "mono"]);
    facts.push(["expires", expiryText(run), run.expires_at_ms && run.expires_at_ms < Date.now() ? "crit" : "mono"]);

    return el("section", { class: "request" }, [
      el("p", { class: "overline", text: "// request" }),
      el("div", { class: "request-grid" }, facts.map(function (fact) {
        return el("div", { class: "fact-card" }, [
          el("span", { class: "fact-card-k", text: fact[0] }),
          el("span", { class: "fact-card-v", "data-tone": fact[2], text: fact[1], title: fact[1] })
        ]);
      }))
    ]);
  }

  function expiryText(run) {
    if (!run.expires_at_ms) return "not until it succeeds";
    const delta = run.expires_at_ms - Date.now();
    return delta > 0 ? "in " + PSL.dur(delta) : PSL.dur(-delta) + " ago";
  }

  function outcomePanel(run, key, _) {
    if (key === "running" || key === "queued") return null;
    const spec = outcomeSpec(run, key);
    const panel = el("section", { class: "outcome", "data-tone": spec.tone }, [
      el("p", { class: "overline", text: "// " + spec.title }),
      el("p", { class: "outcome-body", text: spec.body })
    ]);
    if (spec.counts) panel.appendChild(countStrip(spec.counts));
    const trimmed = trimNotice(run);
    if (trimmed) panel.appendChild(trimmed);
    (spec.warnings || []).forEach(function (warning) {
      panel.appendChild(el("div", { class: "outcome-warning" }, [
        el("span", { class: "outcome-warning-kind", text: warning.kind }),
        el("span", { class: "outcome-warning-message", text: warning.message })
      ]));
    });
    panel.appendChild(actionRow(run, spec));
    return panel;
  }

  function outcomeSpec(run, key) {
    const result = run.result || {};
    if (key === "succeeded") {
      return {
        tone: "ok", title: "result",
        body: result.quality_gate_passed
          ? "The quality gate passed. The dashboard holds the full detail."
          : "The quality gate did not pass. The dashboard holds the full detail.",
        counts: [
          [String(result.findings), result.kept_findings == null ? "findings" : "found", "text"],
          [String(result.critical), "critical", "crit"],
          [String(result.warning), "warning", "warn"],
          [String(result.info), "info", "info"],
          [String(result.traces_analyzed), "traces read", "text"],
          [result.quality_gate_passed ? "pass" : "fail", "quality gate",
            result.quality_gate_passed ? "ok" : "crit"]
        ],
        warnings: result.warnings,
        primary: { label: "Open the dashboard", href: "#/report/" + run.id, filled: true },
        note: "Opens on this origin. The link dies in " + PSL.dur(run.expires_at_ms - Date.now()) + "."
      };
    }
    if (key === "empty") {
      return {
        tone: "warn", title: "empty result",
        body: run.source_name + " had nothing for the engine to analyse. The report exists, and it is "
          + "blank. Opening it will show an empty dashboard. That is the expected outcome, not a "
          + "rendering fault.",
        counts: [
          [String(result.findings), "findings", "warn"],
          [String(result.traces_analyzed), "traces read", "warn"],
          [result.quality_gate_passed ? "pass" : "fail", "quality gate", "muted"]
        ],
        warnings: result.warnings,
        primary: { label: "Wait and run it again", href: "#/new", filled: false },
        secondary: { label: "Open the blank dashboard anyway", href: "#/report/" + run.id },
        note: "A quality gate that passes on zero traces has not measured anything."
      };
    }
    if (key === "failed") {
      return {
        tone: "crit", title: run.error_code || "internal",
        body: run.source_name + ": " + (PSL.ERRORS[run.error_code] || "it failed for an unnamed reason."),
        primary: { label: "Run it again", href: "#/new", filled: false },
        secondary: { label: "Check the source", href: "#/sources" },
        note: "Nothing was stored, so nothing expires."
      };
    }
    if (key === "interrupted") {
      return {
        tone: "info", title: "resume",
        body: "The Hub never replays an interrupted run on its own. A silent retry could fire a second "
          + "heavy query at " + run.source_name + " without anyone asking for it, so the decision stays "
          + "yours. The parameters are unchanged and ready to send again.",
        primary: {
          label: "Resume with the same parameters",
          action: function (button) { resubmit(run, button); },
          filled: true
        },
        note: PSL.argsLine(run)
      };
    }
    return {
      tone: "muted", title: "expired",
      body: "Retention is not configurable from here. Running the same analysis again produces a new "
        + "report with a new clock. It will not reproduce the old one, because the source has moved "
        + "on since then.",
      primary: { label: "Run it again", href: "#/new", filled: false },
      note: PSL.argsLine(run)
    };
  }

  /**
   * The sink drops findings to fit its budget, and the count on the card is
   * what the engine found, not what the report holds. Said above the link,
   * because it changes how the numbers should be read.
   */
  function trimNotice(run) {
    const result = run.result || {};
    if (result.kept_findings == null || result.kept_findings >= result.findings) return null;
    return el("div", { class: "outcome-warning" }, [
      el("span", { class: "outcome-warning-kind", text: "trimmed" }),
      el("span", {
        class: "outcome-warning-message",
        text: result.findings + " findings were found and " + result.kept_findings
          + " are in the report. The sink dropped the rest to fit, critical last, so what "
          + "survived is what mattered most."
      })
    ]);
  }

  function countStrip(counts) {
    return el("div", { class: "counts" }, counts.map(function (cell) {
      return el("div", { class: "count" }, [
        el("span", { class: "count-n", "data-tone": cell[2], text: cell[0] }),
        el("span", { class: "count-l", text: cell[1] })
      ]);
    }));
  }

  function actionRow(run, spec) {
    const row = el("div", { class: "outcome-actions" }, [actionButton(spec.primary, true)]);
    if (spec.secondary) row.appendChild(actionButton(spec.secondary, false));
    if (spec.note) row.appendChild(el("span", { class: "outcome-note", text: spec.note }));
    return row;
  }

  function actionButton(spec, primary) {
    const className = "action" + (primary && spec.filled ? " action-filled" : "")
      + (primary ? "" : " action-secondary");
    if (spec.href) return el("a", { class: className, href: spec.href, text: spec.label });
    const button = el("button", { type: "button", class: className, text: spec.label });
    button.addEventListener("click", function () { spec.action(button); });
    return button;
  }

  function resubmit(run, button) {
    fetch("/api/analyses", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ source_id: run.source_id, request: run.request || {} })
    }).then(function (response) {
      return response.json().then(function (payload) { return { ok: response.ok, payload: payload }; });
    }).then(function (result) {
      if (!result.ok || !result.payload.id) {
        throw new Error(result.payload.detail || "The Hub refused the resubmission.");
      }
      location.hash = "#/run/" + result.payload.id;
    }).catch(function (error) {
      // Silence here reads as a broken button: the operator clicked and
      // nothing moved. The note carries the run's arguments, so the error
      // borrows the line and hands it back.
      const note = button.parentNode && button.parentNode.querySelector(".outcome-note");
      if (!note) return;
      // Stashed on the node, not in a closure: a second failure inside the
      // window would otherwise capture the first error as the text to restore
      // and pin it there for good.
      if (note.dataset.restore === undefined) note.dataset.restore = note.textContent;
      clearTimeout(state.noteTimer);
      note.textContent = String(error.message || error);
      note.setAttribute("data-error", "true");
      state.noteTimer = setTimeout(function () {
        note.textContent = note.dataset.restore;
        delete note.dataset.restore;
        note.removeAttribute("data-error");
      }, 6000);
    });
  }

  function loadRun(id) {
    return getJson("/api/analyses/" + id).then(function (run) {
      state.run = run;
      state.runError = false;
      render();
      if (run.status === "pending" || run.status === "running") scheduleRunPoll(id);
    }).catch(function () {
      state.runError = true;
      render();
    });
  }

  function scheduleRunPoll(id) {
    clearTimeout(state.runTimer);
    state.runTimer = setTimeout(function () {
      if (currentRunId() === id) loadRun(id);
    }, 1000);
  }


  // ------------------------------------------------------- advanced: detection

  /**
   * One sentence per knob, saying what the detector stops seeing when the
   * number goes up. Written in the terms of what is looked for, never in terms
   * of file size: raising a threshold does not shorten a report, it decides
   * that a smaller pattern is no longer a problem.
   */
  const DETECTION_COPY = {
    n_plus_one_min_occurrences: "How many near-identical queries in one trace count as an N+1. "
      + "Raise it and smaller loops stop being reported at all.",
    window_duration_ms: "How close together those queries have to be. A shorter window splits one "
      + "slow loop into several groups that each fall under the count.",
    slow_query_threshold_ms: "Above this, one operation is called slow.",
    slow_query_min_occurrences: "How many times a slow template has to appear before it is worth "
      + "reporting. One slow query stays invisible below this.",
    max_fanout: "Child spans under one parent before it counts as excessive fanout. The engine "
      + "warns outside 5 to 1 000: too low floods the list, too high hides real fan-outs.",
    chatty_service_min_calls: "Outbound HTTP calls in one trace before a service is called chatty. "
      + "Critical fires at three times this.",
    pool_saturation_concurrent_threshold: "Peak concurrent SQL spans on one service before the "
      + "connection pool is called at risk. Set it to the pool size you actually run.",
    serialized_min_sequential: "Sequential independent calls under one parent before they are "
      + "worth parallelising."
  };

  function detectionKnobs() {
    return (state.status && state.status.detection_knobs) || [];
  }

  function detectionCount() {
    return Object.keys(state.form.detection).length;
  }

  function setDetection(name, raw, knob) {
    const value = Number(raw);
    // An empty field or the engine's own default is not an override: recording
    // it would make the run card claim a departure that never happened.
    if (raw === "" || !Number.isFinite(value) || value === knob.default) delete state.form.detection[name];
    else state.form.detection[name] = value;
    updateSubmit();
    refreshDetectionCount();
  }

  function refreshDetectionCount() {
    const badge = document.getElementById("advanced-count");
    if (!badge) return;
    const count = detectionCount();
    badge.hidden = count === 0;
    badge.textContent = count === 1 ? "1 changed" : count + " changed";
  }

  /**
   * A disclosure, and the only one in the product. It holds settings that
   * change what the analysis looks for, which is a different question from
   * every other control on this screen, so it is folded away rather than
   * mixed in.
   */
  function advancedPanel() {
    const knobs = detectionKnobs();
    if (knobs.length === 0) return null;

    const summary = el("summary", { class: "advanced-summary" }, [
      warningGlyph(14),
      el("span", { class: "overline", text: "// advanced · what the analysis looks for" }),
      el("span", { id: "advanced-count", class: "advanced-count", hidden: "hidden" })
    ]);

    const body = el("div", { class: "advanced-body" }, [
      el("section", { class: "notice-block", "data-tone": "warn" }, [
        warningGlyph(17),
        el("div", { class: "notice-block-text" }, [
          el("p", {
            class: "notice-block-title",
            text: "For operators who know what these thresholds do."
          }),
          el("p", {
            class: "notice-block-body",
            text: "Set one too low and the report fills with noise, set it too high and real problems "
              + "go unreported. If you are not sure which way a number should move, leave it."
          })
        ])
      ]),
      el("p", {
        class: "advanced-lead",
        text: "These are the engine's detection thresholds. They decide what counts as a problem, "
          + "not how the report is written: raising one does not make the run lighter, it makes the "
          + "engine stop reporting the smaller cases. A run records the ones you changed, and the "
          + "recent list flags counts that came from different thresholds, because they are not "
          + "comparable."
      })
    ]);

    knobs.forEach(function (knob) {
      body.appendChild(detectionRow(knob));
    });

    const panel = el("details", { class: "advanced" }, [summary, body]);
    if (detectionCount() > 0) panel.setAttribute("open", "open");
    queueMicrotask(refreshDetectionCount);
    return panel;
  }

  function detectionRow(knob) {
    const current = state.form.detection[knob.name];
    const input = el("input", {
      type: "number",
      class: "input input-knob",
      min: String(knob.min),
      max: String(knob.max),
      placeholder: String(knob.default),
      value: current === undefined ? "" : String(current)
    });
    input.addEventListener("input", function () { setDetection(knob.name, input.value, knob); });

    return el("label", { class: "knob" }, [
      el("span", { class: "knob-head" }, [
        el("span", { class: "knob-name", text: knob.name }),
        el("span", { class: "knob-default", text: "default " + knob.default })
      ]),
      el("span", { class: "knob-body", text: DETECTION_COPY[knob.name] || "" }),
      input
    ]);
  }

  // ---------------------------------------------------- screen: recent runs

  function renderRecentScreen() {
    const section = el("section", {}, [
      ruledOverline("// recent analyses"),
      el("h1", { class: "page-title", text: "The team's short memory" }),
      el("p", {
        class: "page-sub",
        text: "Reports are deleted " + state.status.limits.report_retention_hours + " hours after they "
          + "succeed. This is not an audit trail, and a link you shared yesterday is already dead."
      })
    ]);

    if (!state.runs) {
      section.appendChild(el("div", { class: "card skeleton", style: "height:120px;margin-top:18px" }));
      return section;
    }
    if (state.runs.length === 0) {
      section.appendChild(el("div", { class: "empty-state" }, [
        el("p", { class: "empty-title", text: "Nothing here yet." }),
        el("p", {
          text: "Not “no results”. This list is the team's short memory, and after "
            + state.status.limits.report_retention_hours + " idle hours retention returns it to "
            + "exactly this state. That is normal, so it reads as normal."
        })
      ]));
      return section;
    }

    const binaries = Array.from(new Set(
      state.runs.map(function (run) { return run.producer_version; }).filter(Boolean))).sort(PSL.vcmp);
    if (binaries.length > 1) {
      section.appendChild(el("div", { class: "banner", "data-tone": "warn" }, [
        svg([["path", { d: "M10.3 3.9 1.9 18a2 2 0 0 0 1.7 3h16.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" }],
          ["path", { d: "M12 9v4M12 17h.01" }]], 16),
        el("p", {
          text: "These analyses were produced by " + binaries.join(" and ") + ". Counts from "
            + binaries.length + " binaries are not directly comparable: a detector added between "
            + "minors changes what gets found, not only how much. The label on each card names which "
            + "binary did the detecting."
        })
      ]));
    }

    const tuned = state.runs.filter(function (run) {
      return Object.keys((run.request || {}).detection || {}).length > 0;
    });
    if (tuned.length > 0 && tuned.length < state.runs.length) {
      section.appendChild(el("div", { class: "banner", "data-tone": "warn" }, [
        warningGlyph(16),
        el("p", {
          text: tuned.length + (tuned.length === 1 ? " run" : " runs") + " here changed the "
            + "detection thresholds. Their counts are not comparable with the rest: a threshold "
            + "decides what gets reported, so a lower count can mean a quieter service or simply "
            + "a detector that was told to look for less. Each card names the thresholds it used."
        })
      ]));
    }

    section.appendChild(legendStrip());
    section.appendChild(el("div", { class: "run-list" }, state.runs.map(runCard)));
    return section;
  }

  function legendStrip() {
    const keys = ["queued", "running", "succeeded", "empty", "failed", "interrupted", "expired"];
    const strip = el("div", { class: "legend" }, [el("span", { class: "overline", text: "legend" })]);
    keys.forEach(function (key) {
      strip.appendChild(el("span", {
        class: "status-pill",
        "data-status": key,
        text: key === "empty" ? "succeeded · empty" : key
      }));
    });
    return strip;
  }

  function runCard(run) {
    const key = PSL.statusKey(run);
    const card = el("a", { class: "run-card", "data-status": key, href: "#/run/" + run.id });

    card.appendChild(el("span", { class: "run-card-line" }, [
      el("span", { class: "status-pill", "data-status": key, text: key === "empty" ? "succeeded · empty" : key }),
      el("span", { class: "run-card-name", text: run.source_name }),
      el("span", { class: "chip", text: PSL.KIND_LABEL[run.kind] || run.kind }),
      el("span", {
        class: "chip chip-declared",
        text: run.environment,
        title: "Declared by the source's configuration, not measured."
      }),
      el("span", { class: "run-card-spacer" }),
      el("span", { class: "run-card-id", text: run.id })
    ]));
    card.appendChild(el("span", { class: "run-card-args", text: PSL.argsLine(run), title: PSL.argsLine(run) }));
    card.appendChild(el("span", { class: "run-card-facts" }, cardFacts(run, key).map(function (fact) {
      return el("span", { class: "fact" }, [
        el("span", { class: "fact-k", text: fact[0] }),
        el("span", { class: "fact-v", "data-tone": fact[2] || "mono", text: fact[1] })
      ]);
    })));
    return card;
  }

  /** Durations relative to now, the way an operator reads a list: "3 s", not a clock. */
  function cardFacts(run, key) {
    const now = Date.now();
    const started = run.started_at_ms || run.created_at_ms;
    const ran = run.finished_at_ms
      ? PSL.dur(run.finished_at_ms - started)
      : key === "queued" ? "not started" : PSL.dur(now - started) + " so far";
    const facts = [["by", run.requested_by], ["ran", ran]];
    if (run.producer_version) facts.push([PSL.detector(run.kind), run.producer_version,
      PSL.skew(run.producer_version) ? "warn" : "mono"]);
    facts.push(["started", PSL.dur(now - started) + " ago"]);
    facts.push(["expires", run.expires_at_ms ? expiryText(run) : "n/a",
      run.expires_at_ms && run.expires_at_ms < now ? "crit" : "mono"]);
    const tuned = Object.keys((run.request || {}).detection || {});
    if (tuned.length > 0) {
      facts.push(["thresholds", tuned.length === 1 ? "1 changed" : tuned.length + " changed", "warn"]);
    }
    if (run.error_code) facts.push(["error", run.error_code, "crit"]);
    return facts;
  }

  function loadRuns() {
    return getJson("/api/analyses").then(function (runs) {
      state.runs = runs;
      render();
    }).catch(function () {
      state.runs = [];
      render();
    });
  }

  // ------------------------------------------------ screen: dashboard handoff

  /**
   * The report is served byte for byte as the engine produced it, in a frame of
   * its own. The surface changes visibly so the operator knows they left the
   * launcher, and the single return is always present.
   */
  function renderReportScreen(id) {
    const frame = el("iframe", { class: "report-frame", src: "/reports/" + id + ".html", title: "Analysis report" });
    const bar = el("div", { class: "report-bar" }, [
      el("a", { class: "pill-button", href: "#/run/" + id }, [
        svg([["path", { d: "M14 6l-6 6 6 6" }]], 14),
        el("span", { text: "Back to the launcher" })
      ]),
      el("span", { class: "report-path", text: "report / " + id }),
      el("span", { class: "report-spacer" }),
      el("span", { class: "report-engine", text: reportLifetime(id) })
    ]);
    return el("div", { class: "report-shell" }, [bar, frame]);
  }

  function reportLifetime(id) {
    const run = state.run && state.run.id === id ? state.run : null;
    const version = state.status && state.status.engine_version;
    const rendered = "Rendered by perf-sentinel " + (version || "unknown");
    if (!run || !run.expires_at_ms) return rendered;
    return rendered + " · expires in " + PSL.dur(run.expires_at_ms - Date.now());
  }

  // ------------------------------------------------------------------ boot

  initTheme();
  // Escape closes the picker. Without it the only ways out are Apply, a quick
  // range or a click outside, and a keyboard user has none of them.
  globalThis.addEventListener("keydown", function (event) {
    if (event.key === "Escape" && state.form.pickerOpen) {
      state.form.pickerOpen = false;
      render();
    }
  });
  render();
  loadShell();
  globalThis.addEventListener("hashchange", onRoute);
})();
