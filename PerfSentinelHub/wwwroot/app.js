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
    loading: true
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
      renderShell();
      render();
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
    return ["new", "recent", "sources"].indexOf(hash) >= 0 ? hash : "new";
  }

  function render() {
    state.screen = currentScreen();
    Array.prototype.forEach.call(document.querySelectorAll(".shell-tab"), function (tab) {
      if (tab.getAttribute("data-screen") === state.screen) tab.setAttribute("aria-current", "page");
      else tab.removeAttribute("aria-current");
    });

    const main = document.getElementById("main");
    if (state.screen === "sources") main.replaceChildren(renderSourcesScreen());
    else main.replaceChildren(renderPlaceholder());
  }

  function renderPlaceholder() {
    return el("section", {}, [
      el("p", { class: "overline", text: "// " + state.screen }),
      el("h1", { class: "page-title", text: "Not built yet" }),
      el("p", { class: "page-sub", text: "This screen lands in the next slice." })
    ]);
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

  // ------------------------------------------------------------------ boot

  initTheme();
  render();
  loadShell();
  globalThis.addEventListener("hashchange", render);
})();
