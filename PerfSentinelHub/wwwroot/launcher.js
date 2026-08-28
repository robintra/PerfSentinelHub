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
    timeout: "the run passed the 300-second ceiling and was killed. Halve the trace cap or shorten the lookback before resubmitting. The same request will time out again unchanged.",
    output_too_large: "the source returned more than one run is allowed to hold. Narrow the window or lower the trace cap so less comes back, then run it again.",
    binary_failed: "the analysis binary failed for a reason none of the other codes covers, and nothing was stored. Run it once more. If it repeats, send this analysis ID to whoever operates the Hub.",
    invalid_request: "the arguments were rejected before the run started, so nothing was read and nothing was spent. Fix the trace ID or the lookback value and submit again.",
    internal: "the Hub itself failed and never touched the source. Retry now. If it fails the same way, the Hub needs attention rather than your request."
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
   * @param {number} n
   * @returns {{key: import("../types").WeightBand, label: string, fg: string, bg: string, bd: string, body: string, needsAck: boolean}}
   */
  function weightBand(n) {
    if (!Number.isFinite(n) || n < 1) {
      return {
        key: "invalid", label: "not a count",
        fg: "var(--crit-fg)", bg: "var(--crit-bg)", bd: "var(--crit-bd)", needsAck: false,
        body: "A run needs at least one trace. Drag the handle or type a number between 1 and 2 000."
      };
    }
    if (n <= 500) {
      return {
        key: "safe", label: "comfortable",
        fg: "var(--ok-fg)", bg: "var(--ok-bg)", bd: "var(--ok-bd)", needsAck: false,
        body: "Well inside what the report sink returns whole. Nothing is trimmed at this size unless individual traces are unusually large."
      };
    }
    if (n <= 1200) {
      return {
        key: "heavy", label: "heavy",
        fg: "var(--warn-fg)", bg: "var(--warn-bg)", bd: "var(--warn-bd)", needsAck: false,
        body: "The sink targets a 5 MiB standalone file. Whether it has to trim at this count depends on how heavy your traces are, and the launcher cannot know that before the run. Expect a whole report, plan for a trimmed one."
      };
    }
    if (n <= 2000) {
      return {
        key: "ceiling", label: "at the ceiling",
        fg: "var(--crit-fg)", bg: "var(--crit-bg)", bd: "var(--crit-bd)", needsAck: true,
        body: "At this count the report will almost certainly be trimmed to fit 5 MiB. It will still open, still look complete, and will not be. The snapshot_scope warnings on the result are the only place that difference is stated."
      };
    }
    return {
      key: "over", label: "above the hard cap",
      fg: "var(--crit-fg)", bg: "var(--crit-bg)", bd: "var(--crit-bd)", needsAck: false,
      body: "The service rejects this before the run starts. Nothing is read and nothing is spent."
    };
  }

  global.PSL = {
    setVersions,
    get ENGINE() { return ENGINE; },
    get HUB() { return HUB; },
    ERRORS, ERROR_TITLES, KIND_LABEL,
    dur, durParts, clock, parseDur, humanDur, dtLocal, dtHuman,
    vparts, vcmp, minorGap, skew, detector, statusKey, argsLine, weightBand
  };
})(globalThis);
