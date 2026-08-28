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

  // Every storage access is wrapped: sessionStorage throws in Safari private
  // mode and under some enterprise policies, and a theme is not worth an error.
  function store(area, key, value) {
    try {
      if (value === undefined) return globalThis[area].getItem(key);
      globalThis[area].setItem(key, value);
    } catch (error) { return null; }
    return null;
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
    if (state.screen === "sources") main.replaceChildren(renderSourcesScreen());
    else if (state.screen === "new") main.replaceChildren(renderNewScreen());
    else if (state.screen === "run") main.replaceChildren(renderRunScreen(currentRunId()));
    else if (state.screen === "report") main.replaceChildren(renderReportScreen(currentRunId()));
    else main.replaceChildren(renderRecentScreen());
  }

  /** Loads whatever the route needs, then renders it. */
  function onRoute() {
    const screen = currentScreen();
    clearTimeout(state.runTimer);
    render();
    if (state.loading) return;
    if (screen === "recent") loadRuns();
    else if (screen === "run" || screen === "report") {
      const id = currentRunId();
      if (id && (!state.run || state.run.id !== id)) loadRun(id);
      else if (id) render();
    }
  }

  // -------------------------------------------------------- screen: sources

  function renderSourcesScreen() {
    const section = el("section", {}, [
      el("p", { class: "overline", text: "// fleet health" }),
      el("h1", { class: "page-title", text: "Sources" }),
      el("p", {
        class: "page-sub",
        text: "Every source this Hub is configured to read. The set is closed, bounded by "
          + "configuration, so this is a table rather than cards."
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
    row.appendChild(el("td", { text: source.last_success_ms ? PSL.dtHuman(source.last_success_ms) : "never" }));
    row.appendChild(el("td", {
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
          text: "n/a",
          title: "A trace backend stores traces and detects nothing, so it reports no producer version."
        });
    }

    const cell = el("td", { class: "table-mono" }, [el("span", { text: source.producer_version })]);
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

  function setMaxTraces(value) {
    state.form.maxTraces = value;
    // Dropping back below the ceiling withdraws the question that was asked
    // about it.
    if (!PSL.weightBand(value).needsAck) state.form.ackHeavy = false;
    render();
  }

  function renderNewScreen() {
    const section = el("section", {}, [
      el("p", { class: "overline", text: "// new analysis" }),
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

    section.appendChild(el("div", { class: "new-grid" }, [sourcePanel(), parametersPanel()]));
    section.appendChild(costBand());
    section.appendChild(submitRow());
    return section;
  }

  function hubUnreachableBanner() {
    return el("div", { class: "banner", "data-tone": "crit" }, [
      svg([["circle", { cx: "12", cy: "12", r: "9" }], ["path", { d: "M12 7.5v5M12 15.8v.2" }]], 16),
      el("div", {
        text: "The Hub is not answering. This is the Hub itself and not any one source, so nothing "
          + "can be launched from here until it is back."
      })
    ]);
  }

  function sourcePanel() {
    const list = el("div", { class: "card source-panel", role: "radiogroup", "aria-label": "Source" },
      state.sources.map(sourceRadio));
    return el("div", {}, [
      list,
      el("p", {
        class: "panel-note",
        text: "The environment is declared by the source's own configuration, never measured. A "
          + "misconfigured deployment can label production as staging."
      })
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
    return source.kind === "daemon" ? "producer unknown" : "engine " + (state.status.engine_version || "none");
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
    if (!source.reachable) panel.appendChild(unreachableAck(source));
    return panel;
  }

  function daemonNotice() {
    return el("div", { class: "notice" }, [
      svg([["circle", { cx: "12", cy: "12", r: "9" }], ["path", { d: "M12 11v5M12 8.2v.2" }]], 16),
      el("div", {}, [
        el("p", { text: "No parameters. A daemon snapshot is whatever it holds in memory right now." }),
        el("p", {
          class: "notice-sub",
          text: "The window is the daemon's own ring buffer, and asking for more would be a request "
            + "the source cannot answer. What comes back is the slice it still holds."
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
    nodes.push(field("Time range", rangeControl(source)));
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
    return el("div", { class: "field" }, [
      group,
      el("p", { class: "field-note", text: "One or the other, never both." })
    ]);
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

  function field(label, control) {
    return el("div", { class: "field" }, [el("label", { class: "field-label", text: label }), control]);
  }

  function windowLabel() {
    if (state.form.rangeMode === "absolute") {
      return PSL.dtHuman(state.form.fromMs) + " → " + PSL.dtHuman(state.form.toMs);
    }
    return "Last " + PSL.humanDur(state.form.lookback);
  }

  function windowSpanMs() {
    return state.form.rangeMode === "absolute"
      ? state.form.toMs - state.form.fromMs
      : PSL.parseDur(state.form.lookback);
  }

  function rangeControl(source) {
    const button = el("button", { type: "button", class: "pill-button", "aria-expanded": String(state.form.pickerOpen) }, [
      svg([["circle", { cx: "12", cy: "12", r: "9" }], ["path", { d: "M12 7v5l3.5 2" }]], 14),
      el("span", { text: windowLabel() }),
      el("span", { class: "pill-button-span", text: PSL.dur(windowSpanMs()) })
    ]);
    button.addEventListener("click", function () {
      state.form.pickerOpen = !state.form.pickerOpen;
      render();
    });

    const wrap = el("div", { class: "range" }, [button]);
    if (state.form.pickerOpen) wrap.appendChild(rangePicker());
    wrap.appendChild(el("p", {
      class: "field-note",
      text: state.form.rangeMode === "absolute"
        ? "Absolute, fixed at submission. It does not drift while the job waits in the queue."
        : "Relative to the moment the run starts, so it drifts while the job waits in the queue."
    }));
    rangeConsequences(source).forEach(function (node) { wrap.appendChild(node); });
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
    return el("p", { class: "consequence", "data-tone": tone || "muted", text: text });
  }

  function rangePicker() {
    const backdrop = el("div", { class: "picker-backdrop" });
    backdrop.addEventListener("click", function () { state.form.pickerOpen = false; render(); });

    const from = el("input", { type: "datetime-local", class: "input", value: PSL.dtLocal(state.form.fromMs) });
    const to = el("input", { type: "datetime-local", class: "input", value: PSL.dtLocal(state.form.toMs) });
    const span = el("p", { class: "picker-span" });
    const apply = el("button", { type: "button", class: "pill-button pill-primary", text: "Apply" });

    function readAbsolute() {
      const start = Date.parse(from.value);
      const end = Date.parse(to.value);
      const ordered = Number.isFinite(start) && Number.isFinite(end) && start < end;
      const past = Number.isFinite(end) && end <= Date.now();
      span.textContent = !ordered
        ? "The start must come before the end."
        : !past
          ? "The end cannot be in the future."
          : "Span " + PSL.dur(end - start);
      span.setAttribute("data-invalid", ordered && past ? "false" : "true");
      apply.disabled = !(ordered && past);
      return { start: start, end: end, valid: ordered && past };
    }
    from.addEventListener("input", readAbsolute);
    to.addEventListener("input", readAbsolute);
    apply.addEventListener("click", function () {
      const read = readAbsolute();
      if (!read.valid) return;
      state.form.rangeMode = "absolute";
      state.form.fromMs = read.start;
      state.form.toMs = read.end;
      state.form.pickerOpen = false;
      render();
    });

    const quick = el("div", { class: "picker-quick" }, QUICK_RANGES.map(function (value) {
      const button = el("button", { type: "button", class: "picker-quick-item", text: "Last " + PSL.humanDur(value) });
      button.addEventListener("click", function () {
        state.form.rangeMode = "relative";
        state.form.lookback = value;
        state.form.pickerOpen = false;
        render();
      });
      return button;
    }));

    const left = el("div", { class: "picker-pane" }, [
      el("p", { class: "overline", text: "// absolute" }),
      field("From", from),
      field("To", to),
      span,
      apply,
      el("p", { class: "overline picker-sep", text: "// relative" }),
      customRelativeRow()
    ]);

    const panel = el("div", { class: "picker" }, [left, el("div", { class: "picker-pane picker-right" }, [
      el("p", { class: "overline", text: "// quick" }), quick
    ])]);
    readAbsolute();
    return el("div", {}, [backdrop, panel]);
  }

  function customRelativeRow() {
    const qty = el("input", { type: "number", class: "input input-narrow", min: "1", value: String(state.form.customQty) });
    const units = el("div", { class: "segmented segmented-sm", role: "radiogroup", "aria-label": "Unit" });
    [["m", "m"], ["h", "h"], ["d", "d"]].forEach(function (entry) {
      const button = el("button", {
        type: "button",
        role: "radio",
        "aria-checked": state.form.customUnit === entry[0] ? "true" : "false",
        text: entry[1]
      });
      button.addEventListener("click", function () {
        state.form.customUnit = entry[0];
        state.form.customQty = Math.max(1, Number(qty.value) || 1);
        state.form.rangeMode = "relative";
        state.form.lookback = state.form.customQty + state.form.customUnit;
        state.form.pickerOpen = false;
        render();
      });
      units.appendChild(button);
    });
    return el("div", { class: "picker-custom" }, [qty, units]);
  }

  function maxTracesBlock() {
    const band = PSL.weightBand(state.form.maxTraces);
    const number = el("input", { type: "number", class: "input input-narrow", min: "1", value: String(state.form.maxTraces) });
    number.addEventListener("input", function () { setMaxTraces(Number(number.value)); });

    const head = el("div", { class: "traces-head" }, [
      number,
      el("span", { class: "band-chip", style: bandStyle(band), text: band.label }),
      el("span", { class: "traces-cap", text: "hard cap " + state.status.limits.max_traces_cap })
    ]);

    const slider = el("input", {
      type: "range",
      "data-band": "true",
      min: "1",
      max: String(state.status.limits.max_traces_cap),
      value: String(Math.min(Math.max(state.form.maxTraces, 1), state.status.limits.max_traces_cap)),
      "aria-label": "Maximum traces"
    });
    slider.addEventListener("input", function () { setMaxTraces(Number(slider.value)); });

    const track = el("div", { class: "band-track" }, [
      el("span", { class: "band-seg", "data-seg": "ok" }),
      el("span", { class: "band-seg", "data-seg": "warn" }),
      el("span", { class: "band-seg", "data-seg": "crit" }),
      slider
    ]);

    const scale = el("div", { class: "band-scale" }, [
      el("span", { text: "1" }), el("span", { text: "500 safe" }),
      el("span", { text: "1 200 heavy" }), el("span", { text: "2 000 cap" })
    ]);

    const block = el("div", { class: "field" }, [
      el("label", { class: "field-label" }, [
        el("span", { text: "Max traces" }),
        el("span", { class: "field-gloss", text: "how much comes back, not how far back" })
      ]),
      head, track, scale,
      el("p", { class: "band-body", text: band.body })
    ]);
    if (band.needsAck) block.appendChild(heavyAck());
    block.appendChild(sinkPanel());
    return block;
  }

  /**
   * What the report sink actually does with a run this size. The numbers are
   * the sink's own constants, not predictions: the byte size depends on span
   * counts and SQL template lengths, which the launcher cannot know.
   */
  function sinkPanel() {
    const rows = [
      ["5 MiB", "The size the standalone report aims for. It is a target the sink trims towards, "
        + "not a hard refusal."],
      ["70 %", "Share of that budget reserved for findings. Over it, findings are dropped "
        + "critical-first, so the ones you most wanted to see survive longest."],
      ["lowest waste", "Order in which embedded traces are dropped once findings have taken their "
        + "share. The worst offenders stay."],
      ["25", "Hard cap on the top offenders embedded for the Explain tab, whatever the run size. "
        + "The full ranking is still computed, only the embed is capped."],
      ["0 findings", "Traces carrying no finding are never embedded at all, at any size."]
    ];
    return el("div", { class: "sink" }, [
      el("p", { class: "overline", text: "// what comes back, and what it drops first" }),
      el("dl", { class: "sink-rows" }, rows.flatMap(function (row) {
        return [el("dt", { text: row[0] }), el("dd", { text: row[1] })];
      }))
    ]);
  }

  function bandStyle(band) {
    return "color:" + band.fg + ";background:" + band.bg + ";border-color:" + band.bd;
  }

  function heavyAck() {
    return checkbox(
      state.form.ackHeavy,
      "I accept a report that may come back trimmed.",
      function (checked) { state.form.ackHeavy = checked; updateSubmit(); });
  }

  function unreachableAck(source) {
    return el("div", { class: "ack-block" }, [
      el("p", {
        class: "ack-title",
        text: "This source has been unreachable for " + PSL.dur(Date.now() - source.unreachable_since_ms) + "."
      }),
      checkbox(
        state.form.ackUnreachable,
        "Run it anyway. The most likely outcome is source_unreachable.",
        function (checked) { state.form.ackUnreachable = checked; updateSubmit(); })
    ]);
  }

  function checkbox(checked, label, onChange) {
    const input = el("input", { type: "checkbox" });
    input.checked = checked;
    input.addEventListener("change", function () { onChange(input.checked); });
    const wrap = el("label", { class: "checkbox" }, [input, el("span", { text: label })]);
    return wrap;
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
    return el("section", { class: "cost" }, [
      el("p", { class: "overline", text: "// what this run costs" }),
      el("p", { class: "cost-sub", text: "Reported by the service, not assumed." }),
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
    const band = PSL.weightBand(state.form.maxTraces);
    if (band.key === "over") return "The trace cap is above what the service accepts.";
    if (band.key === "invalid") return "A run needs at least one trace.";
    return band.needsAck && !state.form.ackHeavy ? "Confirm the report will be trimmed." : null;
  }

  /** Restates the request in a sentence, so the button is not a leap of faith. */
  function submitSentence() {
    const source = selectedSource();
    if (!source) return "";
    if (source.kind === "daemon") {
      return "Takes a snapshot of whatever " + source.name + " holds in memory right now. "
        + queuePhrase();
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
      svg([["path", { d: "M7 5l12 7-12 7z" }]], 16),
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
    if (state.form.mode === "trace") return { trace_id: state.form.traceId.trim() };
    const request = { service: state.form.service.trim(), max_traces: state.form.maxTraces };
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

  const OUTCOME_TONE = {
    succeeded: "ok", empty: "warn", failed: "crit", interrupted: "info",
    expired: "muted", running: "info", queued: "muted"
  };

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
    section.appendChild(el("div", { class: "run-head" }, [
      el("span", { class: "status-pill", "data-status": key, text: key }),
      el("span", { class: "run-id", text: run.id })
    ]));
    section.appendChild(el("h1", { class: "page-title", text: runHeadline(run, key) }));
    section.appendChild(el("p", { class: "page-sub", text: runSubline(run, key) }));
    section.appendChild(el("div", { class: "run-grid" }, [eventLog(run, key), factsRail(run, key)]));
    const outcome = outcomePanel(run, key);
    if (outcome) section.appendChild(outcome);
    return section;
  }

  function backLink() {
    const link = el("a", { class: "back-link", href: "#/new" }, [
      svg([["path", { d: "M14 6l-6 6 6 6" }]], 14),
      el("span", { text: "New analysis" })
    ]);
    return link;
  }

  function runHeadline(run, key) {
    if (key === "queued") return "Queued.";
    if (key === "running") return "Running.";
    if (key === "empty") return "It succeeded, and there is nothing in it.";
    if (key === "succeeded") return "Done.";
    if (key === "interrupted") return "The service restarted while this was running.";
    if (key === "expired") return "This report is gone.";
    return PSL.ERROR_TITLES[run.error_code] || "The run failed.";
  }

  function runSubline(run, key) {
    if (key === "queued") return "Waiting for a worker. The execution ceiling has not started counting yet.";
    if (key === "running") return "The engine reports nothing between start and finish. Expect one more line, not a stream.";
    if (key === "interrupted") return "The Hub never replays an interrupted run on its own: a silent retry could fire a "
      + "second heavy query at the backend that nobody asked for.";
    return run.source_name + " · " + PSL.KIND_LABEL[run.kind];
  }

  /** A receipt, not a feed. Only what the Hub actually recorded. */
  function eventLog(run, key) {
    const rows = [logRow(run.created_at_ms, "accepted", "The request was validated and queued.")];
    if (run.started_at_ms) rows.push(logRow(run.started_at_ms, "started", "A worker picked it up."));
    if (run.finished_at_ms) {
      rows.push(logRow(run.finished_at_ms, run.status,
        run.error_code ? run.error_code : "The run ended."));
    } else {
      rows.push(logRow(null, run.started_at_ms ? "running" : "waiting", ""));
    }

    return el("div", {}, [
      el("p", { class: "overline", text: "// service events" }),
      el("p", { class: "log-sub", text: "Only what the Hub actually recorded." }),
      el("div", { class: "log" }, rows),
      el("p", { class: "log-note", text: logClosing(key) })
    ]);
  }

  function logRow(ms, name, detail) {
    // The name and the detail share the third column: four children over three
    // columns would drop the detail onto a row of its own.
    return el("div", { class: "log-row" }, [
      el("span", { class: "log-time", text: ms ? PSL.clock(ms) : "…" }),
      el("span", { class: "log-dot", "data-event": name }),
      el("span", { class: "log-text" }, [
        el("span", { class: "log-name", text: name }),
        el("span", { class: "log-detail", text: detail })
      ])
    ]);
  }

  function logClosing(key) {
    if (key === "running") {
      return "There is no percentage and no arrival time. The engine emits no intermediate signal, so "
        + "anything of the sort would be invented.";
    }
    if (key === "queued") return "Nothing has started yet. The next line appears when a worker takes it.";
    return "These lines were written as the run went. This is a receipt, and nothing more is coming.";
  }

  function factsRail(run, key) {
    const elapsedMs = (run.finished_at_ms || Date.now()) - (run.started_at_ms || run.created_at_ms);
    const parts = PSL.durParts(run.started_at_ms ? elapsedMs : 0);
    const figure = el("p", { class: "elapsed-figure", "data-running": key === "running" ? "true" : "false" });
    parts.forEach(function (part) {
      figure.appendChild(el("span", { text: part.n }));
      if (part.u) figure.appendChild(el("span", { class: "elapsed-unit", text: part.u }));
    });

    const elapsed = el("div", { class: "card rail-card" }, [
      el("p", { class: "overline", text: "// elapsed" }),
      figure
    ]);
    if (key === "running") elapsed.appendChild(ceilingRule(elapsedMs));
    else if (key === "queued") {
      elapsed.appendChild(el("p", {
        class: "rail-note",
        text: "The " + state.status.limits.analysis_timeout_seconds + "-second ceiling has not started "
          + "counting. That is the whole difference between queued and running."
      }));
    }

    return el("div", { class: "rail" }, [elapsed, requestCard(run)]);
  }

  /**
   * The only bar in this product. It measures elapsed time against a known
   * ceiling, which is a fact, not progress toward an unknown total.
   */
  function ceilingRule(elapsedMs) {
    const ceilingMs = state.status.limits.analysis_timeout_seconds * 1000;
    const ratio = Math.min(1, elapsedMs / ceilingMs);
    const near = elapsedMs > ceilingMs * 0.8;
    const fill = el("span", { class: "ceiling-fill" });
    fill.style.width = (ratio * 100).toFixed(1) + "%";
    if (near) fill.setAttribute("data-near", "true");
    return el("div", {}, [
      el("div", { class: "ceiling" }, [fill]),
      el("p", { class: "rail-note", text: "against the " + state.status.limits.analysis_timeout_seconds + " s ceiling" })
    ]);
  }

  function requestCard(run) {
    const rows = [
      ["source", run.source_name],
      ["type", PSL.KIND_LABEL[run.kind] || run.kind],
      ["arguments", PSL.argsLine(run)],
      ["requested by", run.requested_by],
      [PSL.detector(run.kind) === "producer" ? "detected by producer" : "detected by engine",
        run.producer_version || "not yet known"],
      ["expires", run.expires_at_ms ? PSL.dtHuman(run.expires_at_ms) : "not until it succeeds"]
    ];
    return el("div", { class: "card rail-card" }, [
      el("p", { class: "overline", text: "// request" }),
      el("dl", { class: "facts" }, rows.flatMap(function (row) {
        return [
          el("dt", { text: row[0] }),
          el("dd", { text: row[1], title: row[1] })
        ];
      }))
    ]);
  }

  function outcomePanel(run, key) {
    if (key === "succeeded" || key === "empty") return successPanel(run, key);
    if (key === "failed") return failurePanel(run);
    if (key === "interrupted") return resumePanel(run);
    if (key === "expired") return expiredPanel();
    return null;
  }

  function successPanel(run, key) {
    const result = run.result || {};
    const panel = el("section", { class: "outcome", "data-tone": OUTCOME_TONE[key] }, [
      el("p", { class: "overline", text: "// result" })
    ]);

    if (key === "empty") {
      panel.appendChild(el("p", {
        class: "outcome-body",
        text: "The source answered correctly with zero traces. The report exists and is blank, which "
          + "is the expected outcome here rather than a rendering fault. A quality gate that passes "
          + "on zero traces has not measured anything."
      }));
    } else {
      panel.appendChild(countStrip(result));
    }

    // Above the link on purpose: they change how the numbers should be read.
    (result.warnings || []).forEach(function (warning) {
      panel.appendChild(el("div", { class: "banner", "data-tone": "warn" }, [
        svg([["path", { d: "M12 4l9 16H3z" }], ["path", { d: "M12 10v4M12 17.4v.2" }]], 16),
        el("div", {}, [
          el("p", { class: "warning-kind", text: warning.kind }),
          el("p", { class: "warning-message", text: warning.message })
        ])
      ]));
    });

    panel.appendChild(actionRow(run, key));
    return panel;
  }

  function countStrip(result) {
    const cells = [
      [result.findings, "findings"], [result.critical, "critical"],
      [result.warning, "warning"], [result.info, "info"],
      [result.traces_analyzed, "traces"],
      [result.quality_gate_passed ? "pass" : "fail", "gate"]
    ];
    return el("div", { class: "counts" }, cells.map(function (cell) {
      return el("div", { class: "count" }, [
        el("span", { class: "count-n", text: String(cell[0]) }),
        el("span", { class: "count-l", text: cell[1] })
      ]);
    }));
  }

  function actionRow(run, key) {
    const row = el("div", { class: "outcome-actions" });
    if (key === "empty") {
      const again = el("button", { type: "button", class: "submit", text: "Wait and run it again" });
      again.addEventListener("click", function () { location.hash = "#/new"; });
      row.appendChild(again);
      // The dashboard names the cold start itself, so removing the link would
      // hide the evidence.
      row.appendChild(el("a", { class: "pill-button", href: "#/report/" + run.id, text: "Open the blank dashboard anyway" }));
    } else {
      row.appendChild(el("a", { class: "submit", href: "#/report/" + run.id, text: "Open the dashboard" }));
    }
    if (run.expires_at_ms) {
      row.appendChild(el("span", {
        class: "outcome-lifetime",
        text: "deleted in " + PSL.dur(run.expires_at_ms - Date.now())
      }));
    }
    return row;
  }

  function failurePanel(run) {
    return el("section", { class: "outcome", "data-tone": "crit" }, [
      el("p", { class: "overline", text: "// " + run.error_code }),
      el("p", { class: "outcome-body", text: "The Hub asked, and " + (PSL.ERRORS[run.error_code] || "it failed.") }),
      el("p", { class: "outcome-foot", text: "Raw output from the engine is never shown here, by design. This code is "
        + "the whole vocabulary, and it is what to quote when asking for help." })
    ]);
  }

  function resumePanel(run) {
    const panel = el("section", { class: "outcome", "data-tone": "info" }, [
      el("p", { class: "overline", text: "// interrupted" }),
      el("p", {
        class: "outcome-body",
        text: "Nothing was stored and nothing was retried. Resubmitting is a decision, not a recovery."
      })
    ]);
    const resume = el("button", { type: "button", class: "submit", text: "Resume with the same parameters" });
    resume.addEventListener("click", function () { resubmit(run); });
    panel.appendChild(el("div", { class: "outcome-actions" }, [
      resume,
      el("span", { class: "outcome-lifetime", text: PSL.argsLine(run) })
    ]));
    return panel;
  }

  function expiredPanel() {
    return el("section", { class: "outcome", "data-tone": "muted" }, [
      el("p", { class: "overline", text: "// expired" }),
      el("p", {
        class: "outcome-body",
        text: "Reports live " + state.status.limits.report_retention_hours + " hours and this one is "
          + "gone. Running the same parameters again produces a new report, not this one back."
      })
    ]);
  }

  function resubmit(run) {
    fetch("/api/analyses", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ source_id: run.source_id, request: run.request || {} })
    }).then(function (response) { return response.json(); })
      .then(function (payload) {
        if (payload.id) location.hash = "#/run/" + payload.id;
      });
  }

  function loadRun(id) {
    return getJson("/api/analyses/" + id).then(function (run) {
      state.run = run;
      state.runError = false;
      render();
      // A finished run needs no poll, and the job screen has no push channel
      // to rely on.
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

  // ---------------------------------------------------- screen: recent runs

  function renderRecentScreen() {
    const section = el("section", {}, [
      el("p", { class: "overline", text: "// recent analyses" }),
      el("h1", { class: "page-title", text: "Recent" }),
      el("p", {
        class: "page-sub",
        text: "Reports are deleted " + state.status.limits.report_retention_hours + " hours after they "
          + "succeed. This is not an audit trail, and a link shared yesterday is already dead."
      })
    ]);

    if (!state.runs) {
      section.appendChild(el("div", { class: "card skeleton", style: "height:120px;margin-top:18px" }));
      return section;
    }
    if (state.runs.length === 0) {
      section.appendChild(el("div", { class: "empty-state", text: "No analysis has been run yet." }));
      return section;
    }

    const binaries = new Set(state.runs.map(function (run) { return run.producer_version; }).filter(Boolean));
    if (binaries.size > 1) {
      section.appendChild(el("div", { class: "banner", "data-tone": "warn" }, [
        svg([["path", { d: "M12 4l9 16H3z" }], ["path", { d: "M12 10v4M12 17.4v.2" }]], 16),
        el("div", {
          text: "These runs come from " + binaries.size + " different binaries, so their counts are not "
            + "directly comparable. A detector added between minors changes what gets found, not only how much."
        })
      ]));
    }

    section.appendChild(legendStrip());
    section.appendChild(el("div", { class: "run-list" }, state.runs.map(runCard)));
    return section;
  }

  function legendStrip() {
    const keys = ["queued", "running", "succeeded", "empty", "failed", "interrupted", "expired"];
    return el("div", { class: "legend" }, keys.map(function (key) {
      return el("span", { class: "status-pill", "data-status": key, text: key });
    }));
  }

  function runCard(run) {
    const key = PSL.statusKey(run);
    const card = el("a", { class: "run-card", "data-status": key, href: "#/run/" + run.id });

    card.appendChild(el("div", { class: "run-card-line" }, [
      el("span", { class: "status-pill", "data-status": key, text: key }),
      el("span", { class: "run-card-name", text: run.source_name }),
      el("span", { class: "chip", text: PSL.KIND_LABEL[run.kind] || run.kind }),
      el("span", { class: "chip chip-declared", text: run.environment }),
      el("span", { class: "run-card-id", text: run.id })
    ]));
    card.appendChild(el("p", { class: "run-card-args", text: PSL.argsLine(run), title: PSL.argsLine(run) }));

    const facts = [
      ["by", run.requested_by],
      ["ran", run.finished_at_ms ? PSL.dur(run.finished_at_ms - (run.started_at_ms || run.created_at_ms)) : "—"],
      [PSL.detector(run.kind), run.producer_version || "—"],
      ["started", PSL.dtHuman(run.started_at_ms || run.created_at_ms)],
      ["expires", run.expires_at_ms ? PSL.dtHuman(run.expires_at_ms) : "—"]
    ];
    if (run.error_code) facts.push(["error", run.error_code]);
    card.appendChild(el("div", { class: "run-card-facts" }, facts.map(function (fact) {
      return el("span", {}, [
        el("span", { class: "fact-k", text: fact[0] }),
        el("span", { class: "fact-v", text: fact[1] })
      ]);
    })));
    return card;
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
      el("span", { class: "report-path", text: "/reports/" + id + ".html" }),
      el("span", { class: "report-engine", text: reportLifetime(id) })
    ]);
    return el("div", { class: "report-shell" }, [bar, frame]);
  }

  function reportLifetime(id) {
    const run = state.run && state.run.id === id ? state.run : null;
    const engine = "engine " + (state.status && state.status.engine_version ? state.status.engine_version : "unknown");
    if (!run || !run.expires_at_ms) return engine;
    return engine + " · deleted in " + PSL.dur(run.expires_at_ms - Date.now());
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
