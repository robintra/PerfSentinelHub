/**
 * perf-sentinel Hub launcher — pure logic.
 *
 * Classic script, no module syntax, no build step: the page loads it with a plain
 * <script src> and reads it off `window.PSL`. Brief §3.5 forbids a build stage in
 * the shipped app, so this file is authored as runnable JavaScript and type-checked
 * separately with `tsc --noEmit` against `types.d.ts`.
 *
 * Everything here is a pure function or a frozen table. Rendering, state and DOM
 * live in the page.
 */
(function (global) {
  "use strict";

  /**
   * The analysis binary embedded in the Hub, and the Hub service itself. Both
   * come from `/api/status` at load time rather than being baked in: a value
   * frozen here would go stale the first time either side is upgraded, and
   * `skew()` would then compare against a version nobody is running.
   */
  let ENGINE = null;
  let HUB = null;

  /**
   * @param {string | null} hub
   * @param {string | null} engine
   * @returns {void}
   */
  function setVersions(hub, engine) { HUB = hub; ENGINE = engine; }

  /**
   * One actionable sentence per code, naming the next action. The service refuses to
   * expose raw stderr, so this table is the entire failure vocabulary the operator gets.
   * @type {Record<import("../types").ErrorCode, string>}
   */
  const ERRORS = {
    source_unreachable: "nothing answered at its address. Check the daemon or backend is up and that the Hub still has a route to it, then run this again.",
    source_auth_failed: "it answered and refused the Hub's credentials. Rotate the configured auth header or API key in the source's Secret. Nothing here will work until that is done.",
    source_rejected_request: "it answered and refused these arguments, usually an unknown service name or a window it does not keep. Check the service against the backend and try a shorter lookback.",
    timeout: "the run passed this Hub's time ceiling, or the backend took too long to answer. A wide window is the usual cause, halve the trace cap or shorten it. If it repeats instantly whatever the window, the address is probably not answering at all, check the endpoint.",
    output_too_large: "the source returned more than one run is allowed to hold. Narrow the window or lower the trace cap so less comes back, then run it again.",
    binary_failed: "the analysis binary failed for a reason none of the other codes covers, and nothing was stored. Run it once more. If it repeats, send this analysis ID to whoever operates the Hub.",
    invalid_request: "the arguments were rejected before the run started, so nothing was read and nothing was spent. Fix the trace ID or the lookback value and submit again.",
    internal: "the Hub itself failed and never touched the source. Retry now. If it fails the same way, the Hub needs attention rather than your request."
  };

  /**
   * What a failed read of a source means. A different vocabulary from ERRORS,
   * which describes a failed analysis run: these are the codes the collector
   * records, and only the ones a settings read can actually produce.
   */
  const READ_ERRORS = {
    network_error: "nothing answered at its address. Check the daemon is up and that the Hub still has a route to it.",
    http_error: "it answered with an error status, so it is running and reachable. This is the daemon refusing or failing the request rather than a network problem.",
    timeout: "it did not answer inside this Hub's HTTP timeout. Busy is as likely as down.",
    invalid_status: "it answered, but not with a status this Hub can read. Its /api/status has to carry a version string.",
    response_too_large: "it answered with more than this Hub reads in one go. A [daemon] section past that cap is worth reporting.",
    hub_busy: "the Hub capped how many daemon reads run at once and this one hit the cap. It clears in about a second."
  };

  /** @type {Record<import("../types").ErrorCode, string>} */
  const ERROR_TITLES = {
    source_unreachable: "No connection could be opened.",
    source_auth_failed: "The source rejected the Hub's credentials.",
    source_rejected_request: "The source refused the request.",
    timeout: "The run exceeded the execution ceiling.",
    output_too_large: "The source returned too much data.",
    binary_failed: "The analysis binary failed.",
    invalid_request: "The arguments were rejected before the run started.",
    internal: "The Hub failed on its own side."
  };

  /** @type {Record<import("../types").SourceKind, string>} */
  const KIND_LABEL = { daemon: "daemon", tempo: "tempo", jaeger_query: "victoria traces" };

  const UNIT_MS = { m: 60000, h: 3600000, d: 86400000 };
  const UNIT_WORD = { m: "minute", h: "hour", d: "day" };

  /**
   * Coarse duration. Drops a zero remainder so an exact hour reads "1 h", not "1 h 0 m".
   * @param {number | null | undefined} ms
   * @returns {string}
   */
  function dur(ms) {
    if (ms == null) return "n/a";
    const s = Math.max(0, Math.round(ms / 1000));
    if (s < 60) return s + " s";
    const m = Math.floor(s / 60);
    if (m < 60) return s % 60 ? m + " m " + (s % 60) + " s" : m + " m";
    const h = Math.floor(m / 60);
    if (h < 24) return m % 60 ? h + " h " + (m % 60) + " m" : h + " h";
    const d = Math.floor(h / 24);
    return h % 24 ? d + " d " + (h % 24) + " h" : d + " d";
  }

  /**
   * Same duration, split into figure and unit so a caller can set them at different
   * sizes. A 40px "m" beside a 40px digit reads as two glyphs, not one duration.
   * @param {number | null | undefined} ms
   * @returns {{n: string, u: string}[]}
   */
  function durParts(ms) {
    const s = dur(ms);
    if (s === "n/a") return [{ n: s, u: "" }];
    /** @type {{n: string, u: string}[]} */
    const out = [];
    const re = /(\d+)\s*([a-z]+)/g;
    let m;
    while ((m = re.exec(s)) !== null) out.push({ n: m[1] ?? "", u: m[2] ?? "" });
    return out;
  }

  /**
   * Local wall clock with milliseconds, for the event log.
   * @param {number} ms
   * @returns {string}
   */
  function clock(ms) {
    const d = new Date(ms);
    const p = (/** @type {number} */ n, /** @type {number} */ w) => String(n).padStart(w, "0");
    return p(d.getHours(), 2) + ":" + p(d.getMinutes(), 2) + ":" + p(d.getSeconds(), 2) + "." + p(d.getMilliseconds(), 3);
  }

  /**
   * Relative window string, e.g. `15m`, `6h`, `90d`. Unparseable input falls back to
   * one hour rather than throwing: this feeds a live preview, not a submission.
   * @param {string} s
   * @returns {number}
   */
  function parseDur(s) {
    const m = /^(\d+)([mhd])$/.exec(s || "");
    return m ? Number(m[1]) * UNIT_MS[/** @type {"m"|"h"|"d"} */ (m[2])] : 3600000;
  }

  /**
   * @param {string} s
   * @returns {string}
   */
  function humanDur(s) {
    const m = /^(\d+)([mhd])$/.exec(s || "");
    if (!m) return s;
    const n = Number(m[1]);
    return n + " " + UNIT_WORD[/** @type {"m"|"h"|"d"} */ (m[2])] + (n > 1 ? "s" : "");
  }

  /**
   * Value for an `<input type="datetime-local">`, in local time as that control expects.
   * @param {number} ms
   * @returns {string}
   */
  function dtLocal(ms) {
    const d = new Date(ms);
    const p = (/** @type {number} */ n) => String(n).padStart(2, "0");
    return d.getFullYear() + "-" + p(d.getMonth() + 1) + "-" + p(d.getDate()) + "T" + p(d.getHours()) + ":" + p(d.getMinutes());
  }

  /**
   * @param {number} ms
   * @returns {string}
   */
  function dtHuman(ms) {
    const d = new Date(ms);
    const p = (/** @type {number} */ n) => String(n).padStart(2, "0");
    return d.getFullYear() + "-" + p(d.getMonth() + 1) + "-" + p(d.getDate()) + " " + p(d.getHours()) + ":" + p(d.getMinutes());
  }

  /**
   * @param {string | null | undefined} v
   * @returns {number[]}
   */
  function vparts(v) { return String(v || "").split(".").map(n => Number(n) || 0); }

  /**
   * Ordering over `major.minor.patch`. Extra segments are ignored: the Hub's own
   * version has four, and only the first three ever carry meaning here.
   * @param {string | null | undefined} a
   * @param {string | null | undefined} b
   * @returns {-1 | 0 | 1}
   */
  function vcmp(a, b) {
    const A = vparts(a), B = vparts(b);
    for (let i = 0; i < 3; i++) {
      const x = A[i] ?? 0, y = B[i] ?? 0;
      if (x !== y) return x < y ? -1 : 1;
    }
    return 0;
  }

  /**
   * @param {string | null | undefined} a
   * @param {string | null | undefined} b
   * @returns {number}
   */
  function minorGap(a, b) { return Math.abs((vparts(b)[1] ?? 0) - (vparts(a)[1] ?? 0)); }

  /**
   * How a producer sits against the Hub's embedded binary.
   *
   * This compares two version strings and nothing more. It cannot know whether a
   * given minor actually changed detection, so callers must word the result as
   * "may not be comparable", never as "incompatible". perf-sentinel is pre-1.0,
   * which is what makes a single minor worth surfacing at all.
   *
   * @param {string | null | undefined} producer
   * @returns {{dir: "behind" | "ahead", label: string, fg: string, bg: string, bd: string} | null}
   */
  function skew(producer) {
    // With no engine version there is nothing to compare against, and a
    // comparison against null would read every producer as ahead.
    if (!producer || !ENGINE) return null;
    const c = vcmp(producer, ENGINE);
    if (c === 0) return null;
    const g = minorGap(producer, ENGINE);
    return c < 0
      ? { dir: "behind", label: g + " minor behind", fg: "var(--warn-fg)", bg: "var(--warn-bg)", bd: "var(--warn-bd)" }
      : { dir: "ahead", label: g + " minor ahead", fg: "var(--info-fg)", bg: "var(--info-bg)", bd: "var(--info-bd)" };
  }

  /**
   * Which version string a result of this kind actually carries.
   *
   * A daemon detects its own findings, so it carries `producer`. A trace backend
   * detects nothing: the Hub's embedded binary does, so a historical read carries
   * `engine`. Labelling both "engine" is the §7.6 confusion.
   *
   * @param {import("../types").SourceKind | import("../types").Analysis} kindOrAnalysis
   * @returns {"producer" | "engine"}
   */
  function detector(kindOrAnalysis) {
    const k = typeof kindOrAnalysis === "string" ? kindOrAnalysis : kindOrAnalysis.kind;
    return k === "daemon" ? "producer" : "engine";
  }

  /**
   * Presentation status. `empty` is derived here and never stored: it must not
   * become a seventh value of `analysis_runs.status`.
   * @param {import("../types").Analysis} a
   * @returns {import("../types").DisplayStatus}
   */
  function statusKey(a) {
    if (a.status === "succeeded" && a.result && a.result.empty) return "empty";
    if (a.status === "pending") return "queued";
    return a.status;
  }

  /**
   * One-line restatement of what was asked for. Long values are truncated by CSS,
   * never here: the full string stays available in a `title`.
   * @param {import("../types").Analysis} a
   * @returns {string}
   */
  function argsLine(a) {
    const r = /** @type {Record<string, unknown>} */ (a.request || {});
    /** @type {string[]} */
    const parts = [];
    if (r["service"]) parts.push("service = " + r["service"]);
    if (r["trace_id"]) parts.push("trace_id = " + r["trace_id"]);
    if (r["lookback"]) parts.push("lookback = " + r["lookback"]);
    if (r["from_ms"]) parts.push("from_ms = " + r["from_ms"]);
    if (r["to_ms"]) parts.push("to_ms = " + r["to_ms"]);
    if (r["max_traces"] != null) parts.push("max_traces = " + r["max_traces"]);
    const detection = /** @type {Record<string, number>} */ (r["detection"] || {});
    Object.keys(detection).forEach(function (name) { parts.push(name + " = " + detection[name]); });
    return parts.length ? parts.join("   ·   ") : "no parameters  ·  daemon in-memory snapshot";
  }

  /**
   * Report-weight advice for a requested trace count.
   *
   * Bands come from the report sink (`crates/sentinel-core/src/report/html/mod.rs`):
   * it targets `DEFAULT_SIZE_TARGET_BYTES` (5 MiB) and trims to fit, findings
   * critical-first past `FINDINGS_BUDGET_SHARE_PCT` (70 %) of the budget, then
   * embedded traces lowest-waste-first. The byte size cannot be predicted before the
   * run because it depends on span counts and SQL template lengths, so these are
   * advice and not a bound. Only `ceiling` asks the operator to confirm; only `over`
   * is refused, and that refusal is the service's.
   *
   * The 500 and 1 200 boundaries come from the sink's own 5 MiB target and do
   * not move. The refusal boundary does: it is the service's configured cap,
   * and hardcoding 2 000 would refuse runs a differently configured Hub accepts.
   *
   * @param {number} n
   * @param {number} [cap]
   * @returns {{key: import("../types").WeightBand, label: string, fg: string, bg: string, bd: string, body: string, needsAck: boolean}}
   */
  function weightBand(n, cap) {
    const hardCap = typeof cap === "number" && cap > 0 ? cap : 2000;
    if (!Number.isFinite(n) || n < 1) {
      return {
        key: "invalid", label: "not a count",
        fg: "var(--crit-fg)", bg: "var(--crit-bg)", bd: "var(--crit-bd)", needsAck: false,
        body: "A run needs at least one trace. Drag the handle or type a number between 1 and "
          + hardCap + "."
      };
    }
    if (n <= Math.min(500, hardCap)) {
      return {
        key: "safe", label: "comfortable",
        fg: "var(--ok-fg)", bg: "var(--ok-bg)", bd: "var(--ok-bd)", needsAck: false,
        body: "Well inside what the sink returns whole. At this count the report is bounded by how much your traffic is doing wrong, not by the sink."
      };
    }
    if (n <= Math.min(1200, hardCap)) {
      return {
        key: "heavy", label: "heavy",
        fg: "var(--warn-fg)", bg: "var(--warn-bg)", bd: "var(--warn-bd)", needsAck: false,
        body: "More traces means more findings, and every one of them reaches the report. The span trees stop at the embed cap, so the extra weight here is the list itself, not the trees."
      };
    }
    if (n <= hardCap) {
      return {
        key: "ceiling", label: "at the ceiling",
        fg: "var(--crit-fg)", bg: "var(--crit-bg)", bd: "var(--crit-bd)", needsAck: true,
        body: "At this count the report still keeps every finding, and that is what makes it heavy: the file has no fixed ceiling and takes a moment to open. The run is also long enough to be worth watching against this Hub's time limit."
      };
    }
    return {
      key: "over", label: "above the hard cap",
      fg: "var(--crit-fg)", bg: "var(--crit-bg)", bd: "var(--crit-bd)", needsAck: false,
      body: "The service rejects this before the run starts. Nothing is read and nothing is spent."
    };
  }

  /**
   * A file size in the unit an operator reads. One decimal at most: the second
   * would claim a precision a report does not have, its weight moves with what
   * the run found.
   * @param {number} n
   */
  function bytes(n) {
    if (!Number.isFinite(n) || n < 0) return "";
    if (n < 1024) return n + " B";
    const kb = Math.round(n / 1024);
    // Rounding can carry into the next unit: 1023.5 KiB must read
    // "1.0 MB", never "1024 KB".
    if (kb < 1024) return kb + " KB";
    return (n / (1024 * 1024)).toFixed(1) + " MB";
  }

  /**
   * A value the way a POSIX shell reads it back, byte for byte.
   *
   * Single quotes and never double: inside double quotes a shell still expands
   * `$`, backticks and backslashes, so a service name carrying one would run as
   * something other than what is displayed. Nothing expands inside single
   * quotes, and the only character needing care is the quote itself, closed and
   * reopened around an escaped one.
   *
   * The bare form is kept for values drawn entirely from the set no shell
   * treats specially, because quoting `order-service` would only make the
   * common line harder to read.
   *
   * @param {string} value
   * @returns {string}
   */
  function shq(value) {
    const text = String(value);
    if (text !== "" && /^[A-Za-z0-9_@%+=:,./-]+$/.test(text)) return text;
    return "'" + text.replace(/'/g, "'\\''") + "'";
  }

  /**
   * Whole seconds, the way the Hub writes them into its own invocation. The
   * printed window and the launched one have to be the same window.
   * @param {number} ms
   * @returns {string}
   */
  function isoUtc(ms) {
    return new Date(ms).toISOString().replace(/\.\d{3}Z$/, "Z");
  }

  /**
   * The request as the engine's own command line.
   *
   * Takes the very object the launcher posts, never the form, so the printed
   * command and the submitted run cannot drift: one shape feeds both, and a
   * field added to one without the other goes missing here rather than wrong.
   * Returns null for a daemon, which takes no arguments at all.
   *
   * The break follows the engine's own examples: the subcommand, the endpoint
   * and the selector on the first line, everything else on the second.
   *
   * @param {import("../types").Source} source
   * @param {Record<string, unknown>} request
   * @returns {string | null}
   */
  function analysisCommand(source, request) {
    if (!source.engine_subcommand) return null;
    const head = ["perf-sentinel " + source.engine_subcommand, "--endpoint " + shq(source.base_url)];
    /** @type {string[]} */
    const tail = [];
    if (request["trace_id"] != null) {
      head.push("--trace-id " + shq(String(request["trace_id"])));
    } else {
      head.push("--service " + shq(String(request["service"] == null ? "" : request["service"])));
      if (request["from_ms"] != null) {
        tail.push("--from " + isoUtc(Number(request["from_ms"])));
        tail.push("--to " + isoUtc(Number(request["to_ms"])));
      } else {
        tail.push("--lookback " + shq(String(request["lookback"])));
      }
      tail.push("--max-traces " + String(request["max_traces"]));
    }
    if (source.auth_header_name) tail.push("--auth-header-env PERF_SENTINEL_SOURCE_TOKEN");
    if (Object.keys(request["detection"] || {}).length > 0) tail.push("-c .perf-sentinel.toml");
    return tail.length === 0 ? head.join(" ") : head.join(" ") + " \\\n  " + tail.join(" ");
  }

  /**
   * The live view of a daemon in a terminal. `--daemon` sits on `query` and not
   * on `monitor`, so the order is not interchangeable.
   * @param {import("../types").Source} source
   * @returns {string}
   */
  function monitorCommand(source) {
    return "perf-sentinel query --daemon " + shq(source.base_url) + " monitor";
  }

  /**
   * The overridden thresholds as the file `-c` expects. Only the ones a run
   * actually changed: a value equal to the engine's own default is dropped
   * before it reaches here, so every key present is a real departure.
   * @param {Record<string, number>} detection
   * @returns {string}
   */
  function detectionToml(detection) {
    return ["[detection]"].concat(Object.keys(detection).sort().map(function (name) {
      return name + " = " + detection[name];
    })).join("\n");
  }

  /** True when a value had to be quoted, so the block can name the shell. */
  function quotedForShell(command) { return command.indexOf("'") >= 0; }

  /**
   * The state a light refresh yields, mirroring DaemonView.Classify with the
   * hints of the page's last full read: a status-only body carries the gauges
   * but not the daemon's hints, and once a minute a full read re-syncs both.
   *
   * @param {{traces: any, analysis_queue: any, findings: any}} view
   * @param {number} warningCount
   * @returns {string}
   */
  function lightState(view, warningCount) {
    const gauges = [view.traces, view.analysis_queue, view.findings];
    if (gauges.some(function (g) { return g && g.at_capacity; })) return "near_capacity";
    if (warningCount > 0) return "advised";
    return gauges.every(function (g) { return !g || g.pct === null; }) ? "unknown" : "ok";
  }

  /**
   * The last view a light refresh can merge onto, or null when there is none:
   * undefined before the first read, the "loading" sentinel during it, an error
   * body, or a light body that never carried the rest. Null is the caller's
   * signal to read in full rather than light.
   *
   * @param {any} view
   * @returns {any}
   */
  function mergeableView(view) {
    return view && typeof view === "object" && !view.error_code && Array.isArray(view.warnings)
      ? view
      : null;
  }

  /**
   * What the next re-read of an open row should cost.
   *
   *   full   the whole view: the only read that renders a panel, so it is what
   *          a row starts with and what it returns to once a minute
   *   light  the gauges alone, laid over the view already on screen
   *   probe  the gauges alone again, but to find out whether a row that failed
   *          can be read at all: an answer is followed by a full read
   *
   * @param {any} previous the view currently on screen, if any
   * @param {number} sinceFullMs since the last full read
   * @param {number} everyMs how often a full read is due
   * @returns {"full" | "light" | "probe"}
   */
  function refreshPlan(previous, sinceFullMs, everyMs) {
    if (mergeableView(previous)) return sinceFullMs >= everyMs ? "full" : "light";
    // Only a view that failed is worth probing. Everything else, nothing read
    // yet or a read in flight, starts over with the read that renders.
    return previous && typeof previous === "object" && previous.error_code ? "probe" : "full";
  }

  /**
   * A light body laid over the last full view: the gauges and the version are
   * the light read's, the settings and the daemon's hints stay the full read's,
   * and the state is derived from both.
   *
   * @param {any} previous a view mergeableView returned
   * @param {any} light
   * @returns {any}
   */
  function mergeLight(previous, light) {
    return Object.assign({}, previous, {
      observed_at_ms: light.observed_at_ms,
      version: light.version,
      uptime_seconds: light.uptime_seconds,
      traces: light.traces,
      analysis_queue: light.analysis_queue,
      findings: light.findings,
      state: lightState(light, previous.warnings.length)
    });
  }

  global.PSL = {
    setVersions,
    get ENGINE() { return ENGINE; },
    get HUB() { return HUB; },
    ERRORS, READ_ERRORS, ERROR_TITLES, KIND_LABEL,
    dur, durParts, clock, parseDur, humanDur, dtLocal, dtHuman, bytes,
    vparts, vcmp, minorGap, skew, detector, statusKey, argsLine, weightBand,
    shq, analysisCommand, monitorCommand, detectionToml, quotedForShell,
    lightState, mergeableView, mergeLight, refreshPlan
  };
})(globalThis);
