// LatticeLog console (P23): a thin client of the read API. Log content is untrusted: everything is built with
// createElement/textContent, never parsed as markup. The token lives only in sessionStorage of this tab.
"use strict";
const $ = (id) => document.getElementById(id);

// "latticelog service ui" opens /ui#token=...: the fragment never reaches the server; it is moved to sessionStorage and
// removed from the address bar.
function takeHashToken() {
  const token = new URLSearchParams(location.hash.slice(1)).get("token");
  if (!token) return null;
  sessionStorage.setItem("ll-token", token);
  history.replaceState(null, "", location.pathname + location.search);
  return token;
}
takeHashToken();

function loadPrefs() { try { return JSON.parse(localStorage.getItem("ll-prefs") || "{}"); } catch { return {}; } }
function savePrefs() { try { localStorage.setItem("ll-prefs", JSON.stringify({ range: state.range, order: $("order").value, by: state.statsBy })); } catch { /* private mode */ } }
const prefs = loadPrefs();

const state = {
  token: sessionStorage.getItem("ll-token") || "",
  ns: sessionStorage.getItem("ll-ns") || "",
  namespaces: [],
  view: "search",
  cursor: null,
  events: [],
  selected: -1,
  level: "",
  chips: [],          // extra filters: { expr }
  range: prefs.range || "24h",
  zoom: null,         // { from, to } in ms, set by clicking the histogram
  span: null,         // what the source holds: { min, max, records, bytes, segments, sealed, services }
  statsBy: prefs.by || "event",
  seq: 0,
};

// ------------------------------------------------------------------ helpers

function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === null || v === undefined || v === false) continue;
    if (k === "class") el.className = v;
    else if (k === "text") el.textContent = v;
    else if (k === "title") el.title = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat()) if (c !== null && c !== undefined && c !== false) el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  return el;
}

async function api(path, params = {}) {
  const url = new URL(path, location.origin);
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") url.searchParams.set(k, v);
  if (state.ns && !url.searchParams.has("ns")) url.searchParams.set("ns", state.ns);
  const resp = await fetch(url, { headers: { Authorization: "Bearer " + state.token } });
  const body = await resp.json().catch(() => ({}));
  if (!resp.ok) {
    const err = new Error(body.message || body.error || `HTTP ${resp.status}`);
    err.status = resp.status;
    err.code = body.error || "";
    throw err;
  }
  return body;
}

const toMs = (iso) => Date.parse(String(iso).replace(/(\.\d{3})\d+/, "$1"));
const num = (n) => Number(n ?? 0).toLocaleString();
const pad = (n, w = 2) => String(n).padStart(w, "0");
const baseLevel = (s) => String(s || "").replace(/\d+$/, "");
const DAY = 864e5;

function isToday(d) { const n = new Date(); return d.getFullYear() === n.getFullYear() && d.getMonth() === n.getMonth() && d.getDate() === n.getDate(); }
function fmtDate(d) { return d.toLocaleDateString(undefined, { month: "short", day: "numeric", year: d.getFullYear() === new Date().getFullYear() ? undefined : "numeric" }); }
function fmtClock(d, ms = true) { return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}` + (ms ? `.${pad(d.getMilliseconds(), 3)}` : ""); }
function fmtShort(ms) { const d = new Date(ms); return isToday(d) ? fmtClock(d, false).slice(0, 5) : `${fmtDate(d)} ${fmtClock(d, false).slice(0, 5)}`; }
function fmtFull(ms) { const d = new Date(ms); return `${fmtDate(d)} ${fmtClock(d)}`; }
function ago(ms) {
  const s = Math.round((Date.now() - ms) / 1000);
  if (s < 0) return "in the future";
  if (s < 45) return "just now";
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  const d = Math.round(s / 86400);
  return d === 1 ? "yesterday" : `${d} days ago`;
}
function duration(ms) {
  const s = ms / 1000;
  if (s < 60) return `${Math.round(s)} s`;
  if (s < 3600) return `${Math.round(s / 60)} min`;
  if (s < 2 * 86400) return `${Math.round(s / 3600)} hours`;
  return `${Math.round(s / 86400)} days`;
}
function bytes(n) {
  if (n === undefined || n === null) return "";
  const u = ["B", "KB", "MB", "GB", "TB"];
  let i = 0; let v = n;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v < 10 && i ? v.toFixed(1) : Math.round(v)} ${u[i]}`;
}
function toast(text) {
  const t = $("toast");
  t.textContent = text;
  t.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { t.hidden = true; }, 1800);
}

// ------------------------------------------------------------------ filters

const RANGES = { "15m": 15 * 60e3, "1h": 3600e3, "6h": 6 * 3600e3, "24h": DAY, "7d": 7 * DAY, "30d": 30 * DAY };
const RANGE_LABEL = { "15m": "the last 15 minutes", "1h": "the last hour", "6h": "the last 6 hours", "24h": "the last 24 hours", "7d": "the last 7 days", "30d": "the last 30 days", all: "all time" };

function parseSince(text) {
  const m = /^(\d+(?:\.\d+)?)([smhd])$/.exec(text.trim());
  if (m) return Date.now() - Number(m[1]) * { s: 1e3, m: 60e3, h: 3600e3, d: DAY }[m[2]];
  const t = toMs(text.trim());
  return Number.isNaN(t) ? null : t;
}

/** The time window as API params plus its bounds in ms (from may be null = since the beginning). */
function timeScope() {
  if (state.zoom) return { params: { since: new Date(state.zoom.from).toISOString(), until: new Date(state.zoom.to).toISOString() }, from: state.zoom.from, to: state.zoom.to };
  if (state.range === "all") return { params: {}, from: state.span?.min ?? null, to: null };
  if (state.range === "custom") {
    const text = $("since-custom").value.trim();
    return { params: { since: text }, from: text ? parseSince(text) : null, to: null };
  }
  return { params: { since: state.range }, from: Date.now() - RANGES[state.range], to: null };
}

function rangeText() {
  if (state.zoom) return `${fmtShort(state.zoom.from)} – ${fmtShort(state.zoom.to)}`;
  if (state.range === "custom") return `since ${$("since-custom").value.trim() || "the beginning"}`;
  return RANGE_LABEL[state.range];
}

const quote = (s) => `'${String(s).replace(/'/g, "''")}'`;
const fieldName = (k) => (/^[A-Za-z_][A-Za-z0-9_]*$/.test(k) ? k : "`" + k.replace(/`/g, "") + "`");
function literal(v) {
  if (typeof v === "number" || typeof v === "boolean") return String(v);
  if (v === null) return "null";
  return quote(typeof v === "string" ? v : JSON.stringify(v));
}

// A filter expression has an operator or an upper-case keyword; anything else is searched as plain text.
function looksLikeFilter(text) {
  return /[=<>~]/.test(text) || /\b(AND|OR|NOT|CONTAINS|STARTSWITH|EXISTS|IN\s*\(|IS\s+(NOT\s+)?NULL|TRUE)\b/.test(text);
}
function textFilter(text) {
  const words = [];
  for (const m of text.matchAll(/"([^"]+)"|(\S+)/g)) words.push(m[1] ?? m[2]);
  return words.map((w) => `text CONTAINS ${quote(w)}`).join(" AND ");
}

function buildWhere({ withLevel = true } = {}) {
  const parts = [];
  const text = $("where").value.trim();
  const typed = text && looksLikeFilter(text);
  if (text) parts.push(typed ? text : textFilter(text));
  if (withLevel && state.level) parts.push(`level >= ${state.level}`);
  for (const c of state.chips) parts.push(c.expr);
  // The typed filter is parenthesized only when combined, so error positions match what the user typed.
  if (typed && parts.length > 1) parts[0] = `(${text})`;
  return parts.join(" AND ");
}
const hasFilters = () => Boolean($("where").value.trim() || state.level || state.chips.length);

function addChip(expr) {
  if (!state.chips.some((c) => c.expr === expr)) state.chips.push({ expr });
  renderChips();
}
function renderChips() {
  const box = $("chips");
  box.replaceChildren();
  if (state.zoom) {
    box.append(h("span", { class: "chip", title: "Time window picked on the chart" }, `time ${rangeText()}`,
      h("button", { type: "button", "aria-label": "Remove time window", onclick: () => { state.zoom = null; renderChips(); runSearch(); } }, "×")));
  }
  for (const c of state.chips) {
    box.append(h("span", { class: "chip" }, c.expr,
      h("button", { type: "button", "aria-label": `Remove filter ${c.expr}`, onclick: () => { state.chips = state.chips.filter((x) => x !== c); renderChips(); refreshView(); } }, "×")));
  }
}
function clearFilters() {
  $("where").value = "";
  state.chips = [];
  state.zoom = null;
  setLevel("");
  renderChips();
  syncClear();
}
function setLevel(level) {
  state.level = level;
  for (const b of $("levels").children) b.classList.toggle("active", b.dataset.level === level);
}
function setRange(range) {
  state.range = range;
  state.zoom = null;
  $("range").value = range;
  $("since-custom").hidden = range !== "custom";
  renderChips();
  savePrefs();
}
function syncClear() { $("clear-where").hidden = !$("where").value; }

// ------------------------------------------------------------------ connection and sources

function setStatus(text, kind) {
  $("status-text").textContent = text;
  $("status").className = "conn" + (kind ? " " + kind : "");
}

function showConnect(message, cancellable = false) {
  $("connect").hidden = false;
  $("app").hidden = true;
  $("tabs").hidden = true;
  $("source-picker").hidden = true;
  $("change-token").hidden = true;
  $("cancel-token").hidden = !cancellable;
  $("connect-error").textContent = message || "";
  closeDetail();
  $("token").focus();
}

function showApp() {
  $("connect").hidden = true;
  $("app").hidden = false;
  $("tabs").hidden = false;
  $("change-token").hidden = false;
  $("source-picker").hidden = state.namespaces.length === 0;
}

async function connect() {
  try {
    const { namespaces } = await api("/v1/namespaces", { ns: "" });
    state.namespaces = namespaces;
    const sel = $("ns");
    sel.replaceChildren(...namespaces.map((n) => h("option", { value: n, text: n })));
    if (!namespaces.includes(state.ns)) state.ns = namespaces[0] || "";
    sel.value = state.ns;
    sessionStorage.setItem("ll-ns", state.ns);
    setStatus(namespaces.length === 1 ? "Connected · 1 source" : `Connected · ${namespaces.length} sources`, "ok");
    showApp();
    await sourceChanged();
  } catch (err) {
    setStatus("Not connected", "bad");
    showConnect(err.status === 401 || err.status === 403 ? "That token was not accepted. Check it and try again." : `Could not reach LatticeLog: ${err.message}`);
  }
}

async function loadSpan() {
  state.span = null;
  $("source-span").textContent = "";
  if (!state.ns) return;
  try {
    const res = await api("/v1/catalog");
    let min = Infinity; let max = -Infinity; let records = 0; let size = 0; let sealed = 0;
    const services = new Set();
    for (const s of res.segments) {
      if (s.sealed) sealed++;
      size += s.bytes || 0;
      records += s.records || 0;
      if (s.min_time) min = Math.min(min, toMs(s.min_time));
      if (s.max_time) max = Math.max(max, toMs(s.max_time));
      for (const v of s.services || []) services.add(v);
    }
    state.span = { min: Number.isFinite(min) ? min : null, max: Number.isFinite(max) ? max : null, records, bytes: size, segments: res.segments.length, sealed, services, raw: res };
    const sp = state.span;
    $("source-span").textContent = sp.max === null
      ? `${state.ns}: no indexed events yet (events still being written appear in searches).`
      : `${state.ns}: ${num(sp.records)} events from ${fmtShort(sp.min)} to ${fmtShort(sp.max)} · newest ${ago(sp.max)} · ${bytes(sp.bytes)} on disk`;
  } catch { /* the catalog is optional context */ }
}

/** When the chosen preset holds nothing, widen it to the smallest one that reaches the newest event. */
function fitRange() {
  const sp = state.span;
  if (!sp || sp.max === null || !(state.range in RANGES)) return null;
  const age = Date.now() - sp.max;
  if (age <= RANGES[state.range]) return null;
  const before = state.range;
  const fit = Object.keys(RANGES).find((k) => RANGES[k] >= age) || "all";
  setRange(fit);
  return `Nothing in ${RANGE_LABEL[before]} (newest event ${ago(sp.max)}), so this shows ${RANGE_LABEL[fit]}.`;
}

async function sourceChanged() {
  state.zoom = null;
  renderChips();
  if (!state.ns) { showNoSource(); return; }
  await loadSpan();
  const note = fitRange();
  await refreshView(note);
}

function showNoSource() {
  $("events").tBodies[0].replaceChildren();
  $("histogram").hidden = true;
  $("summary-count").textContent = "";
  $("summary-meta").textContent = "";
  showEmpty("No logs yet",
    "No source has data. On this PC, register the folder your services log into (llog roots add <folder>) or convert an old log file (llog migrate <file> --name <name>), then reload this page.", []);
}

// ------------------------------------------------------------------ search

function showEmpty(title, text, actions) {
  $("empty").hidden = false;
  $("empty-title").textContent = title;
  $("empty-text").textContent = text;
  $("empty-actions").replaceChildren(...actions.map(([label, fn, primary]) => h("button", { type: "button", class: primary ? "primary" : "", onclick: fn }, label)));
}

function notice(text, kind = "") {
  $("notice").hidden = !text;
  $("notice").className = "notice " + kind;
  $("notice").textContent = text || "";
}

async function runSearch(more = false, note = null) {
  if (!state.ns) { showNoSource(); return; }
  const seq = more ? state.seq : ++state.seq;
  const tbody = $("events").tBodies[0];
  if (!more) {
    state.cursor = null;
    state.events = [];
    state.selected = -1;
    tbody.replaceChildren();
    $("more").hidden = true;
    $("empty").hidden = true;
    notice(note, "info");
    $("summary-count").textContent = "";
    $("summary-meta").textContent = "";
  }
  $("loading").hidden = false;
  const where = buildWhere();
  const scope = timeScope();
  if (!more) histogram(where, scope, seq);
  try {
    const res = await api("/v1/search", { where, ...scope.params, order: $("order").value, limit: 100, cursor: more ? state.cursor : null });
    if (seq !== state.seq) return;
    for (const e of res.events) {
      const i = state.events.push(e) - 1;
      tbody.append(eventRow(e, i));
    }
    const c = res.coverage;
    state.cursor = c.next_cursor || null;
    $("more").hidden = !state.cursor;
    if (!state.histTotal) $("summary-count").textContent = `${num(state.events.length)} event${state.events.length === 1 ? "" : "s"}${state.cursor ? "+" : ""}`;
    $("summary-meta").textContent = `${rangeText()} · ${res.stats.elapsed_ms} ms`;
    const warn = [];
    if (c.partial_results) warn.push(`Results may be incomplete: ${c.partial_reason}.`);
    if (c.storage_issues) warn.push(`${c.storage_issues} storage issue(s) while reading.`);
    if (warn.length) notice(warn.join(" "));
    if (!more && state.events.length === 0) emptyResults();
    if (state.selected >= 0 && more) selectEvent(state.selected + 1);
  } catch (err) {
    if (seq !== state.seq) return;
    if (err.status === 401) { showConnect("The token is no longer accepted."); return; }
    const text = $("where").value.trim();
    const actions = [];
    if (text && looksLikeFilter(text)) actions.push([`Search "${text.length > 40 ? text.slice(0, 40) + "…" : text}" as plain text`, () => { $("where").value = `text CONTAINS ${quote(text)}`; runSearch(); }]);
    actions.push(["Syntax help", () => { $("help").hidden = false; }, true]);
    showEmpty("This search could not run", err.message, actions);
  } finally {
    if (seq === state.seq) $("loading").hidden = true;
  }
}

function emptyResults() {
  const sp = state.span;
  const scope = timeScope();
  const actions = [];
  if (sp && sp.max === null && sp.records === 0) {
    showEmpty("This source has no events yet", "Events appear here as soon as your service writes them.", []);
    return;
  }
  if (sp && sp.max !== null && scope.from !== null && sp.max < scope.from) {
    actions.push(["Search all time", () => { setRange("all"); runSearch(); }, true]);
    if (hasFilters()) actions.push(["Clear filters", () => { clearFilters(); runSearch(); }]);
    showEmpty(`No events in ${rangeText()}`, `The newest event in ${state.ns} is from ${fmtFull(sp.max)} (${ago(sp.max)}).`, actions);
    return;
  }
  if (hasFilters()) actions.push(["Clear filters", () => { clearFilters(); runSearch(); }, true]);
  if (state.range !== "all" || state.zoom) actions.push(["Search all time", () => { setRange("all"); runSearch(); }, !hasFilters()]);
  showEmpty(hasFilters() ? "No events match" : `No events in ${rangeText()}`,
    hasFilters() ? "Try fewer filters or a wider time range." : "Try a wider time range.", actions);
}

function eventText(e) {
  if (e.message) return e.message;
  if (typeof e.body === "string") return e.body;
  if (e.body !== undefined) return JSON.stringify(e.body);
  return "";
}
// Without a message (structlog-style events) the row shows the attributes instead of an empty cell.
function rowText(e) {
  if (eventText(e)) return eventText(e);
  if (e.exception?.message) return e.exception.message;
  return attributePairs(e.attributes).map(([k, v]) => `${k}=${display(v)}`).join("  ");
}

function eventRow(e, i) {
  const t = toMs(e.time);
  const d = new Date(t);
  const lvl = baseLevel(e.severity);
  const clock = fmtClock(d);
  const time = h("td", { class: "t", title: e.time }, isToday(d) ? null : h("span", { class: "d", text: fmtDate(d) + " " }),
    h("b", { text: clock.slice(0, 8) }), h("span", { class: "ms", text: clock.slice(8) }));
  const text = rowText(e);
  const plain = eventText(e) || e.exception?.message;
  const msg = h("td", { class: plain ? "m" : "m dim", title: text.slice(0, 2000) },
    e.exception?.type ? h("span", { class: "exc" }, e.exception.type) : null, text);
  const tr = h("tr", { class: `r-${lvl}`, onclick: () => selectEvent(i) },
    time,
    h("td", {}, h("span", { class: `pill ${lvl}`, text: e.severity })),
    h("td", { class: "s", title: e.service || "" }, e.service || ""),
    h("td", { class: "e", title: e.event || "" }, e.event || ""),
    msg);
  return tr;
}

// ------------------------------------------------------------------ histogram

const BUCKETS = [[1e3, "1s", "1 second"], [5e3, "5s", "5 seconds"], [10e3, "10s", "10 seconds"], [30e3, "30s", "30 seconds"],
  [60e3, "1m", "1 minute"], [300e3, "5m", "5 minutes"], [600e3, "10m", "10 minutes"], [900e3, "15m", "15 minutes"],
  [1800e3, "30m", "30 minutes"], [3600e3, "1h", "1 hour"], [3 * 3600e3, "3h", "3 hours"], [6 * 3600e3, "6h", "6 hours"],
  [12 * 3600e3, "12h", "12 hours"], [DAY, "1d", "1 day"], [7 * DAY, "7d", "7 days"], [30 * DAY, "30d", "30 days"]];

async function histogram(where, scope, seq) {
  state.histTotal = false;
  const box = $("histogram");
  const to = scope.to ?? (state.range === "all" ? state.span?.max : Date.now());
  const from = scope.from ?? state.span?.min;
  if (from === null || from === undefined || to === null || to === undefined || to <= from) { box.hidden = true; return; }
  const [width, param, label] = BUCKETS.find(([w]) => w >= (to - from) / 60) || BUCKETS[BUCKETS.length - 1];
  try {
    const res = await api("/v1/stats", { where, ...scope.params, bucket: param });
    if (seq !== state.seq) return;
    if (!String(res.group_by).startsWith("time")) { box.hidden = true; return; } // an older service without buckets
    const counts = new Map(res.groups.map((g) => [toMs(g.key), g.count]));
    const start = Math.floor(from / width) * width;
    const bars = [];
    for (let t = start; t <= to && bars.length < 400; t += width) bars.push([t, counts.get(t) || 0]);
    const max = Math.max(1, ...bars.map((b) => b[1]));
    $("hist-bars").replaceChildren(...bars.map(([t, n]) => {
      const fill = h("span");
      fill.style.height = n ? `${Math.max(3, (100 * n) / max)}%` : "0";
      return h("div", { class: n ? "b" : "b zero", title: `${fmtShort(t)} – ${fmtShort(t + width)}: ${num(n)} event${n === 1 ? "" : "s"}`, onclick: () => zoomTo(t, t + width) }, fill);
    }));
    $("hist-title").textContent = `Events over time · ${label} per bar`;
    $("hist-from").textContent = fmtShort(start);
    $("hist-to").textContent = fmtShort(Math.min(to, start + bars.length * width));
    box.hidden = false;
    state.histTotal = true;
    $("summary-count").textContent = `${num(res.total)} matching event${res.total === 1 ? "" : "s"}`;
  } catch { if (seq === state.seq) box.hidden = true; }
}

function zoomTo(from, to) {
  state.zoom = { from, to };
  renderChips();
  runSearch();
}

// ------------------------------------------------------------------ detail drawer

function selectEvent(i) {
  if (i < 0) return;
  if (i >= state.events.length) {
    if (state.cursor) { state.selected = i - 1; runSearch(true); }
    return;
  }
  const rows = $("events").tBodies[0].rows;
  rows[state.selected]?.classList.remove("selected");
  state.selected = i;
  rows[i]?.classList.add("selected");
  rows[i]?.scrollIntoView({ block: "nearest" });
  showEvent(state.events[i]);
}

function closeDetail() {
  $("detail").hidden = true;
  $("events").tBodies[0].rows[state.selected]?.classList.remove("selected");
  state.selected = -1;
}

function display(v) { return typeof v === "string" ? v : JSON.stringify(v); }

function kvRow(key, value, filterExpr) {
  const dd = h("dd", {}, h("span", { text: display(value) }));
  if (filterExpr) dd.append(h("button", { type: "button", class: "ghost", title: `Add filter ${filterExpr}`, onclick: () => { addChip(filterExpr); toast("Filter added"); runSearch(); } }, "+ filter"));
  return [h("dt", { text: key }), dd];
}

function attributePairs(attrs) {
  if (!attrs) return [];
  if (Array.isArray(attrs)) {
    return attrs.map((a, i) => (Array.isArray(a) ? [String(a[0]), a[1]] : a && typeof a === "object" && "key" in a ? [String(a.key), a.value] : [String(i), a]));
  }
  return Object.entries(attrs);
}

function showEvent(e) {
  const lvl = baseLevel(e.severity);
  const t = toMs(e.time);
  $("detail").hidden = false;
  $("detail-level").className = `pill ${lvl}`;
  $("detail-level").textContent = e.severity;
  $("detail-title").textContent = e.event || "(no event name)";
  $("detail-time").textContent = `${fmtFull(t)} · ${ago(t)} · ${e.time}`;
  $("detail-message").textContent = eventText(e);

  const ex = e.exception;
  $("detail-exception").hidden = !ex;
  if (ex) {
    $("detail-exc-title").textContent = ex.type + (ex.message ? `: ${ex.message}` : "");
    const parts = [ex.stack || ""];
    const walk = (list, depth) => {
      for (const inner of list || []) {
        parts.push(`${"  ".repeat(depth)}--- inner: ${inner.type}${inner.message ? ": " + inner.message : ""}`, inner.stack || "");
        walk(inner.inner, depth + 1);
      }
    };
    walk(ex.inner, 0);
    $("detail-exc-stack").textContent = parts.filter(Boolean).join("\n");
  }

  const actions = [];
  if (e.trace_id) actions.push(h("button", { type: "button", class: "primary", onclick: () => showTrace(e.trace_id) }, "Show whole trace"));
  if (e.service) actions.push(h("button", { type: "button", onclick: () => { addChip(`service = ${quote(e.service)}`); runSearch(); } }, "Only this service"));
  if (e.event) actions.push(h("button", { type: "button", onclick: () => { addChip(`event = ${quote(e.event)}`); runSearch(); } }, "Only this event"));
  actions.push(h("button", { type: "button", title: "Everything from every service 5 minutes before and after", onclick: () => around(t) }, "What happened around it"));
  $("detail-actions").replaceChildren(...actions);

  const fields = [];
  const f = (key, value, expr) => { if (value !== undefined && value !== null && value !== "") fields.push(...kvRow(key, value, expr)); };
  f("service", e.service, e.service ? `service = ${quote(e.service)}` : null);
  f("event", e.event, e.event ? `event = ${quote(e.event)}` : null);
  f("event_id", e.event_id, e.event_id !== undefined ? `event_id = ${e.event_id}` : null);
  for (const k of ["trace_id", "span_id", "operation_id", "parent_operation_id", "correlation_id"]) f(k, e[k], e[k] ? `${k} = ${quote(e[k])}` : null);
  f("observed_time", e.observed_time);
  f("ref", e.ref);
  f("flags", e.flags);
  $("detail-fields").replaceChildren(...fields);

  const attrs = attributePairs(e.attributes).flatMap(([k, v]) => kvRow(k, v, `${fieldName(k)} = ${literal(v)}`));
  $("attr-heading").hidden = attrs.length === 0;
  $("detail-attrs").replaceChildren(...attrs);
  $("detail-json").textContent = JSON.stringify(e, null, 2);
}

function showTrace(traceId) {
  clearFilters();
  addChip(`trace_id = ${quote(traceId)}`);
  setRange("all");
  $("order").value = "asc";
  closeDetail();
  runSearch(false, "Showing every event of this trace, oldest first.");
}

function around(t) {
  clearFilters();
  state.zoom = { from: t - 5 * 60e3, to: t + 5 * 60e3 };
  renderChips();
  closeDetail();
  runSearch();
}

// ------------------------------------------------------------------ overview

function statCard(label, value, sub, cls, onclick) {
  return h("div", { class: `card stat ${cls || ""} ${onclick ? "click" : ""}`, onclick },
    h("div", { class: "label", text: label }), h("div", { class: "value", text: value }), sub ? h("div", { class: "sub", text: sub }) : null);
}

async function stats() {
  const box = $("bars");
  const cards = $("stat-cards");
  for (const b of $("groupby").children) b.classList.toggle("active", b.dataset.by === state.statsBy);
  if (!state.ns) { box.replaceChildren(h("div", { class: "none", text: "No source has data yet." })); cards.replaceChildren(); return; }
  // The level buttons refine Search; the overview always counts every level so its cards add up.
  const where = buildWhere({ withLevel: false });
  const scope = timeScope();
  $("stats-range").textContent = `${rangeText()}${where ? ` · filter: ${where}` : ""}`;
  box.replaceChildren(h("div", { class: "loading" }, h("span", { class: "spinner" }), "Counting…"));
  const seq = ++state.seq;
  try {
    const [byLevel, byService, main] = await Promise.all([
      api("/v1/stats", { by: "level", where, ...scope.params }),
      api("/v1/stats", { by: "service", where, ...scope.params, top: 1000 }),
      state.statsBy === "level" || state.statsBy === "service" ? null : api("/v1/stats", { by: state.statsBy, where, ...scope.params }),
    ]);
    if (seq !== state.seq) return;
    const lv = Object.fromEntries(byLevel.groups.map((g) => [baseLevel(g.key), 0]));
    for (const g of byLevel.groups) lv[baseLevel(g.key)] += g.count;
    const errors = (lv.ERROR || 0) + (lv.FATAL || 0);
    const goSearch = (fn) => () => { fn(); switchView("search"); };
    cards.replaceChildren(
      statCard("Events", num(byService.total), rangeText()),
      statCard("Errors", num(errors), byLevel.total ? `${((100 * errors) / byLevel.total).toFixed(1)}% of events` : "", errors ? "error" : "", errors ? goSearch(() => setLevel("ERROR")) : null),
      statCard("Warnings", num(lv.WARN || 0), "", lv.WARN ? "warn" : "", lv.WARN ? goSearch(() => setLevel("WARN")) : null),
      statCard("Services", num(byService.groups.length), byService.groups.slice(0, 3).map((g) => g.key).join(", ")));
    const res = state.statsBy === "level" ? byLevel : state.statsBy === "service" ? byService : main;
    const groups = state.statsBy === "service" ? res.groups.slice(0, 50) : res.groups;
    if (!groups.length) { box.replaceChildren(h("div", { class: "none", text: `No events in ${rangeText()}.` })); return; }
    const max = Math.max(1, ...groups.map((g) => g.count));
    box.replaceChildren(...groups.map((g) => {
      const fill = h("span", { class: `fill ${state.statsBy === "level" ? baseLevel(g.key) : ""}` });
      fill.style.width = `${(100 * g.count) / max}%`;
      return h("div", { class: "row", title: `Show these events`, onclick: () => drill(g.key) },
        h("span", { class: "key", title: g.key, text: g.key }),
        h("span", { class: "track" }, fill),
        h("span", { class: "n", text: num(g.count) }),
        h("span", { class: "pct", text: res.total ? `${((100 * g.count) / res.total).toFixed(1)}%` : "" }));
    }));
  } catch (err) {
    if (seq !== state.seq) return;
    cards.replaceChildren();
    box.replaceChildren(h("div", { class: "none", text: err.message }));
  }
}

function drill(key) {
  const by = state.statsBy;
  if (by === "level") addChip(`level = ${key}`);
  else if (key === "(none)" || key === "(missing)") addChip(`${by === "event" || by === "service" ? by : fieldName(by)} IS NULL`);
  else addChip(`${by === "event" || by === "service" ? by : fieldName(by)} = ${quote(key)}`);
  switchView("search");
}

// ------------------------------------------------------------------ storage

async function catalog() {
  await loadSpan();
  const tbody = $("segments").tBodies[0];
  const cards = $("catalog-cards");
  tbody.replaceChildren();
  cards.replaceChildren();
  $("catalog-problems").textContent = "";
  const sp = state.span;
  if (!sp) { $("catalog-problems").textContent = state.ns ? "Could not read the catalog." : "No source has data yet."; return; }
  cards.append(
    statCard("Events stored", num(sp.records), `${state.ns}`),
    statCard("Size on disk", bytes(sp.bytes), sp.records ? `${Math.max(1, Math.round(sp.bytes / sp.records))} bytes per event` : ""),
    statCard("Segments", num(sp.segments), `${sp.sealed} sealed · ${sp.segments - sp.sealed} being written`),
    statCard("Time span", sp.max === null ? "–" : duration(sp.max - sp.min),sp.max === null ? "" : `${fmtShort(sp.min)} → ${fmtShort(sp.max)}`));
  for (const s of [...sp.raw.segments].reverse()) {
    tbody.append(h("tr", {},
      h("td", { class: "mono", text: s.id }),
      h("td", {}, h("span", { class: s.sealed ? "badge" : "badge live", text: s.sealed ? "sealed" : "being written" })),
      h("td", { class: "num", text: s.records === undefined ? "" : num(s.records) }),
      h("td", { title: s.min_time || "", text: s.min_time ? fmtFull(toMs(s.min_time)) : "" }),
      h("td", { title: s.max_time || "", text: s.max_time ? fmtFull(toMs(s.max_time)) : "" }),
      h("td", { class: "num", text: bytes(s.bytes) })));
  }
  const problems = sp.raw.manifest_problems || [];
  if (problems.length) $("catalog-problems").textContent = `Manifest problems: ${problems.join("; ")}`;
}

// ------------------------------------------------------------------ views and events

function switchView(view) {
  state.view = view;
  for (const b of $("tabs").children) b.classList.toggle("active", b.dataset.view === view);
  for (const v of ["search", "stats", "catalog"]) $("view-" + v).hidden = v !== view;
  if (view !== "search") closeDetail();
  refreshView();
}

function refreshView(note) {
  if (state.view === "stats") return stats();
  if (state.view === "catalog") return catalog();
  return runSearch(false, note);
}

const EXAMPLES = [
  ["timeout", "plain text"],
  ["level >= ERROR", "errors and fatals"],
  ["service = 'orders-api'", "one service"],
  ["message CONTAINS 'payment'", "in the message"],
  ["exception.type CONTAINS 'Timeout'", "by exception"],
  ["event IN ('OrderPlaced', 'OrderFailed')", "several events"],
  ["EXISTS(trace_id) AND level >= WARN", "traced warnings"],
  ["NOT service = 'health-check'", "exclude"],
];
$("examples").replaceChildren(...EXAMPLES.map(([q, why]) => h("button", { type: "button", onclick: () => { $("where").value = q; syncClear(); runSearch(); } }, q, h("span", { text: why }))));

$("auth").addEventListener("submit", (ev) => {
  ev.preventDefault();
  const t = $("token").value.trim();
  if (!t) { $("connect-error").textContent = "Paste a token first."; return; }
  state.token = t;
  sessionStorage.setItem("ll-token", t);
  $("token").value = "";
  connect();
});
$("change-token").addEventListener("click", () => showConnect("", true));
// A new /ui#token=... opened in a tab that already shows the console (the service restarted with a new token).
window.addEventListener("hashchange", () => { const t = takeHashToken(); if (t) { state.token = t; connect(); } });
$("cancel-token").addEventListener("click", () => showApp());
$("ns").addEventListener("change", () => { state.ns = $("ns").value; sessionStorage.setItem("ll-ns", state.ns); closeDetail(); sourceChanged(); });
for (const b of $("tabs").children) b.addEventListener("click", () => switchView(b.dataset.view));

$("query").addEventListener("submit", (ev) => { ev.preventDefault(); state.view === "search" ? runSearch() : refreshView(); });
$("where").addEventListener("input", syncClear);
$("clear-where").addEventListener("click", () => { $("where").value = ""; syncClear(); $("where").focus(); runSearch(); });
$("range").addEventListener("change", () => {
  setRange($("range").value);
  if (state.range === "custom") { $("since-custom").focus(); return; }
  runSearch();
});
$("since-custom").addEventListener("keydown", (ev) => { if (ev.key === "Enter") { ev.preventDefault(); runSearch(); } });
$("order").addEventListener("change", () => { savePrefs(); runSearch(); });
for (const b of $("levels").children) b.addEventListener("click", () => { setLevel(b.dataset.level); runSearch(); });
$("help-toggle").addEventListener("click", () => { $("help").hidden = !$("help").hidden; });
$("more").addEventListener("click", () => runSearch(true));

for (const b of $("groupby").children) b.addEventListener("click", () => { state.statsBy = b.dataset.by; savePrefs(); stats(); });
$("stats-refresh").addEventListener("click", () => stats());

$("close").addEventListener("click", closeDetail);
$("prev").addEventListener("click", () => selectEvent(state.selected - 1));
$("next").addEventListener("click", () => selectEvent(state.selected + 1));
$("copy-json").addEventListener("click", async (ev) => {
  ev.preventDefault();
  try { await navigator.clipboard.writeText($("detail-json").textContent); toast("JSON copied"); } catch { toast("Copy is not available here"); }
});

document.addEventListener("keydown", (ev) => {
  const typing = ev.target instanceof HTMLInputElement || ev.target instanceof HTMLSelectElement || ev.target instanceof HTMLTextAreaElement;
  if (ev.key === "Escape") {
    if (typing) { ev.target.blur(); return; }
    if (!$("detail").hidden) closeDetail();
    else if (!$("help").hidden) $("help").hidden = true;
    return;
  }
  if (typing || ev.ctrlKey || ev.metaKey || ev.altKey || $("app").hidden) return;
  if (ev.key === "/") { ev.preventDefault(); if (state.view !== "search") switchView("search"); $("where").focus(); $("where").select(); }
  else if (state.view === "search" && (ev.key === "j" || ev.key === "ArrowDown")) { ev.preventDefault(); selectEvent(state.selected + 1); }
  else if (state.view === "search" && (ev.key === "k" || ev.key === "ArrowUp")) { ev.preventDefault(); if (state.selected > 0) selectEvent(state.selected - 1); }
});

// ------------------------------------------------------------------ start

$("order").value = prefs.order === "asc" ? "asc" : "desc";
setRange(state.range in RANGES || state.range === "all" ? state.range : "24h");
setLevel("");
if (state.token) connect(); else { setStatus("Not connected", "bad"); showConnect(""); }
