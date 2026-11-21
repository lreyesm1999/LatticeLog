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
  namespaces: [],     // names of the sources that hold data (can be opened)
  sources: [],        // every source as /v1/sources describes it
  roots: [],          // registered folders, also the ones without sources yet
  fallback: false,    // talking to a server or an older service without /v1/sources
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

// Counts (/v1/stats) are reused for a short while, so going back and forth between views does not count again.
// Search, Refresh and anything that changes the query bypass it (a different query is a different key).
const statsCache = new Map();
const STATS_TTL = 20e3;
function cachedApi(path, params = {}) {
  const key = JSON.stringify([state.ns, path, Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== "")]);
  const hit = statsCache.get(key);
  if (hit && Date.now() - hit.at < STATS_TTL) return hit.promise;
  const promise = api(path, params);
  statsCache.set(key, { at: Date.now(), promise });
  promise.catch(() => statsCache.delete(key));
  if (statsCache.size > 80) statsCache.delete(statsCache.keys().next().value);
  return promise;
}
const dropStatsCache = () => statsCache.clear();

const toMs = (iso) => Date.parse(String(iso).replace(/(\.\d{3})\d+/, "$1"));
const NUM = new Intl.NumberFormat(); // one formatter: toLocaleString() builds a new one on every call
const num = (n) => NUM.format(Number(n ?? 0));
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
  $("source-btn").hidden = true;
  closePalette();
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
  $("source-btn").hidden = state.sources.length === 0;
}

/** Every source with where it comes from (/v1/sources); a server or an older service only lists names. */
async function loadSources(refresh = false) {
  try {
    const res = await api("/v1/sources", refresh ? { refresh: 1, ns: "" } : { ns: "" });
    state.sources = Array.isArray(res.sources) ? res.sources : [];
    state.roots = Array.isArray(res.roots) ? res.roots : [];
    state.fallback = false;
  } catch (err) {
    if (err.status !== 404) throw err;
    const { namespaces } = await api("/v1/namespaces", { ns: "" });
    state.sources = namespaces.map((n) => ({ name: n, kind: "store", status: "ok", origin: "" }));
    state.roots = [];
    state.fallback = true;
  }
  state.namespaces = state.sources.filter(hasData).map((s) => s.name);
  buildTree();
}

async function connect() {
  try {
    await loadSources();
    const n = state.namespaces;
    if (!n.includes(state.ns)) state.ns = n.length === 1 ? n[0] : "";
    sessionStorage.setItem("ll-ns", state.ns);
    if (state.ns) touchRecent(state.ns);
    const total = state.sources.length;
    setStatus(total === 1 ? "Connected · 1 source" : `Connected · ${num(total)} sources`, "ok");
    showApp();
    renderSourceButton();
    // Several sources and none chosen in this tab: start in the explorer instead of guessing one.
    if (!state.ns && n.length > 1) state.view = "sources";
    syncTabs();
    if (state.view === "sources") renderExplorer();
    else await sourceChanged();
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
  $("notice").hidden = true;
  $("more").hidden = true;
  $("loading").hidden = true;
  if (state.namespaces.length) {
    showEmpty("Choose a source", `${num(state.namespaces.length)} sources have logs. Pick one in the explorer, or press Ctrl+K and type its name.`,
      [["Browse sources", () => switchView("sources"), true], ["Jump to one…", openPalette]]);
    return;
  }
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
    const res = await cachedApi("/v1/stats", { where, ...scope.params, bucket: param });
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
  const by = state.statsBy;
  // Three counts in parallel, each drawn as soon as it arrives: level and event counts usually come from the
  // segment indexes in a few ms, the per-service count may have to read every event.
  const byLevelP = cachedApi("/v1/stats", { by: "level", where, ...scope.params });
  const byServiceP = cachedApi("/v1/stats", { by: "service", where, ...scope.params, top: 1000 });
  const mainP = by === "level" ? byLevelP : by === "service" ? byServiceP : cachedApi("/v1/stats", { by, where, ...scope.params });
  byServiceP.catch(() => { /* reported below with the others */ });
  mainP.catch(() => { /* reported below */ });
  try {
    const byLevel = await byLevelP;
    if (seq !== state.seq) return;
    const lv = {};
    for (const g of byLevel.groups) lv[baseLevel(g.key)] = (lv[baseLevel(g.key)] || 0) + g.count;
    const errors = (lv.ERROR || 0) + (lv.FATAL || 0);
    const goSearch = (fn) => () => { fn(); switchView("search"); };
    const servicesCard = statCard("Services", "…", "counting");
    cards.replaceChildren(
      statCard("Events", num(byLevel.total), rangeText()),
      statCard("Errors", num(errors), byLevel.total ? `${((100 * errors) / byLevel.total).toFixed(1)}% of events` : "", errors ? "error" : "", errors ? goSearch(() => setLevel("ERROR")) : null),
      statCard("Warnings", num(lv.WARN || 0), "", lv.WARN ? "warn" : "", lv.WARN ? goSearch(() => setLevel("WARN")) : null),
      servicesCard);
    const main = await mainP;
    if (seq !== state.seq) return;
    statBars(box, main, by);
    const byService = await byServiceP;
    if (seq !== state.seq) return;
    servicesCard.replaceWith(statCard("Services", num(byService.groups.length), byService.groups.slice(0, 3).map((g) => g.key).join(", ")));
  } catch (err) {
    if (seq !== state.seq) return;
    cards.replaceChildren();
    box.replaceChildren(h("div", { class: "none", text: err.message }));
  }
}

function statBars(box, res, by) {
  const groups = by === "service" ? res.groups.slice(0, 50) : res.groups;
  if (!groups.length) { box.replaceChildren(h("div", { class: "none", text: `No events in ${rangeText()}.` })); return; }
  const max = Math.max(1, ...groups.map((g) => g.count));
  box.replaceChildren(...groups.map((g) => {
    const fill = h("span", { class: `fill ${by === "level" ? baseLevel(g.key) : ""}` });
    fill.style.width = `${(100 * g.count) / max}%`;
    return h("div", { class: "row", title: `Show these events`, onclick: () => drill(g.key) },
      h("span", { class: "key", title: g.key, text: g.key }),
      h("span", { class: "track" }, fill),
      h("span", { class: "n", text: num(g.count) }),
      h("span", { class: "pct", text: res.total ? `${((100 * g.count) / res.total).toFixed(1)}%` : "" }));
  }));
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

// ------------------------------------------------------------------ source explorer

const SVG_NS = "http://www.w3.org/2000/svg";
const ICONS = {
  folder: "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z",
  drive: "M3 13h18v5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1zM5 13l2.5-7h9L19 13M7 16h.01M10 16h.01",
  project: "M5 3h9l5 5v13H5zM14 3v5h5M8 13h8M8 17h5",
  all: "M4 6h.01M8 6h12M4 12h.01M8 12h12M4 18h.01M8 18h12",
  star: "M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.9l-5.2 2.7 1-5.8-4.3-4.1 5.9-.9z",
  clock: "M12 7v5l3 2M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0z",
  python: "M4 5h16v14H4zM8 10l2.5 2L8 14M13 14h3",
  dotnet: "M12 3l8 4.5v9L12 21l-8-4.5v-9zM4 7.5l8 4.5 8-4.5M12 12v9",
  migrated: "M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8zM14 3v5h5M9 15h6M13 12l3 3-3 3",
  chevron: "M9 6l6 6-6 6",
  copy: "M9 9h10v10H9zM5 15V5h10",
  open: "M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5",
};
function icon(name, cls = "") {
  const svg = document.createElementNS(SVG_NS, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("class", ("ic " + cls).trim());
  const p = document.createElementNS(SVG_NS, "path");
  p.setAttribute("d", ICONS[name] || ICONS.folder);
  svg.append(p);
  return svg;
}

const KINDS = {
  "python-local": { icon: "python", label: "Service (Python)" },
  "dotnet-store": { icon: "dotnet", label: "Service (.NET)" },
  migrated: { icon: "migrated", label: "Migrated log" },
  store: { icon: "dotnet", label: "Source" },
};
const kindOf = (s) => KINDS[s.kind] || KINDS.store;
const kindLabel = (s) => (s.kind === "migrated" && s.follow ? "Migrated log (following)" : kindOf(s).label);
const hasData = (s) => s.status === "ok";
const lastMs = (s) => { const d = ex.derived.get(s.name); return d && d.time === s.last_time ? d.last : (s.last_time ? toMs(s.last_time) : null); };
const COMPACT = (() => { try { return new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 }); } catch { return null; } })(); // older engines: plain numbers
const compact = (n) => (COMPACT ? COMPACT.format(n || 0) : num(n));
const byName = (name) => state.sources.find((s) => s.name === name);

/** live: new events in the last 15 minutes; idle: older; none: nothing indexed yet; bad: the service failed to index it. */
function activity(s) {
  if (s.last_error) return ["bad", `Indexing failed: ${s.last_error}`];
  if (!hasData(s)) return ["none", s.status && s.status !== "ok" ? cap(s.status) : "No events yet"];
  const newest = Math.max(lastMs(s) || 0, s.last_new_records ? toMs(s.last_new_records) : 0);
  if (newest && Date.now() - newest < 15 * 60e3) return ["live", "Active: new events in the last 15 minutes"];
  return ["idle", newest ? `Idle: newest event ${ago(newest)}` : "Idle"];
}
const cap = (t) => String(t).charAt(0).toUpperCase() + String(t).slice(1);

// Pinned and recently opened sources are per-browser conveniences: localStorage, best effort.
function loadLib() {
  try {
    const v = JSON.parse(localStorage.getItem("ll-sources") || "{}");
    return { pins: Array.isArray(v.pins) ? v.pins.filter((x) => typeof x === "string") : [], recent: Array.isArray(v.recent) ? v.recent.filter((x) => typeof x === "string") : [] };
  } catch { return { pins: [], recent: [] }; } // unreadable or blocked storage: start empty
}
const lib = loadLib();
function saveLib() { try { localStorage.setItem("ll-sources", JSON.stringify(lib)); } catch { /* private mode: not remembered */ } }
function touchRecent(name) { lib.recent = [name, ...lib.recent.filter((x) => x !== name)].slice(0, 12); saveLib(); }
function togglePin(name) {
  lib.pins = lib.pins.includes(name) ? lib.pins.filter((x) => x !== name) : [...lib.pins, name];
  saveLib();
  renderExplorerParts();
}

// Paths come from Windows or POSIX hosts; compare case-insensitively (as the catalog does on Windows).
const pathParts = (p) => String(p || "").split(/[\\/]+/).filter(Boolean);
const trimSep = (p) => String(p || "").replace(/[\\/]+$/, "");
function dirOf(p) { const s = trimSep(p); const i = Math.max(s.lastIndexOf("\\"), s.lastIndexOf("/")); return i > 0 ? s.slice(0, i) : s; }
function leafOf(p) { const parts = pathParts(p); return parts.length ? parts[parts.length - 1] : String(p || ""); }
function isUnder(path, base) {
  const a = trimSep(path).toLowerCase(); const b = trimSep(base).toLowerCase();
  return a === b || a.startsWith(b + "\\") || a.startsWith(b + "/");
}
const relParts = (path, base) => pathParts(trimSep(path).slice(trimSep(base).length));

const ex = { nodes: new Map(), locOf: new Map(), derived: new Map(), listKey: "", node: null, sel: null, filter: "", sort: { key: "name", dir: 1 }, open: new Set(), rows: [], stats: new Map(), seq: 0 };

/**
 * The folder tree: one location per registered root (and latticelog.toml project), its subfolders down to each
 * source folder, migrated logs grouped by the folder of the original file, plus the virtual All, Pinned and Recent.
 */
function buildTree() {
  const nodes = new Map();
  const locOf = new Map();
  const make = (id, label, iconName, parent, path, title) => {
    const n = { id, label, icon: iconName, parent, path, title: title || path || label, children: new Map(), sources: [], agg: null };
    nodes.set(id, n);
    return n;
  };
  const child = (parent, name, path) => {
    const key = name.toLowerCase();
    let c = parent.children.get(key);
    if (!c) {
      const p = path || (parent.path ? trimSep(parent.path) + (String(parent.path).includes("\\") ? "\\" : "/") + name : null);
      c = make(parent.id + "/" + key, path ? leafOf(path) : name, "folder", parent, p);
      parent.children.set(key, c);
    }
    return c;
  };
  for (const v of [["all", "All sources", "all"], ["pinned", "Pinned", "star"], ["recent", "Recent", "clock"]]) make(v[0], v[1], v[2], null, null).virtual = v[0];
  const location = (id, path, iconName, label) => nodes.get(id) || Object.assign(make(id, label || leafOf(path) || path, iconName, null, path), { top: true });
  for (const r of state.roots) location("loc:" + trimSep(r).toLowerCase(), r, "drive");
  for (const s of state.sources) {
    const origin = String(s.origin || "");
    const m = /^(root|toml-root):(.+)$/.exec(origin);
    let folder;
    if (m && isUnder(s.path, m[2])) {
      folder = location("loc:" + trimSep(m[2]).toLowerCase(), m[2], m[1] === "root" ? "drive" : "project");
      for (const part of relParts(s.path, m[2]).slice(0, -1)) folder = child(folder, part);
    } else if (origin.startsWith("toml:")) {
      const dir = dirOf(origin.slice(5));
      folder = location("proj:" + dir.toLowerCase(), dir, "project");
      if (isUnder(s.path, dir)) for (const part of relParts(s.path, dir).slice(0, -1)) folder = child(folder, part);
    } else if (s.kind === "migrated") {
      folder = location("migrated", null, "migrated", "Migrated logs");
      const from = (s.migrated_from || [])[0];
      if (from) folder = child(folder, dirOf(from).toLowerCase(), dirOf(from));
    } else {
      folder = location("other", null, "folder", state.fallback ? "Namespaces" : "Other sources");
    }
    folder.sources.push(s);
    locOf.set(s.name, folder);
  }
  ex.nodes = nodes;
  ex.locOf = locOf;
  // Per-source text used by every sort, search and row, computed once per source list instead of per call.
  ex.derived = new Map();
  for (const s of state.sources) {
    const parts = [];
    for (let n = locOf.get(s.name); n; n = n.parent) parts.unshift(n.label);
    const loc = parts.join(" › ");
    ex.derived.set(s.name, { loc, time: s.last_time, last: s.last_time ? toMs(s.last_time) : null, hay: [s.name, s.service, s.environment, s.path, kindLabel(s), loc, ...(s.migrated_from || [])].filter(Boolean).join(" ").toLowerCase() });
  }
  if (!ex.node || !nodes.has(ex.node)) {
    const home = state.ns && locOf.get(state.ns);
    ex.node = home ? home.id : "all";
    if (home && !ex.sel) ex.sel = { type: "source", key: state.ns };
  }
  for (let n = nodes.get(ex.node); n; n = n.parent) ex.open.add(n.id);
  for (const n of nodes.values()) if (n.top) ex.open.add(n.id);
}

function aggregate(node) {
  if (node.agg) return node.agg;
  const a = { sources: 0, records: 0, bytes: 0, newest: null, live: 0 };
  const addSource = (s) => {
    a.sources++; a.records += s.records || 0; a.bytes += s.bytes || 0;
    const t = lastMs(s);
    if (t && (a.newest === null || t > a.newest)) a.newest = t;
    if (activity(s)[0] === "live") a.live++;
  };
  for (const s of virtualSources(node) || node.sources) addSource(s);
  for (const c of node.children.values()) {
    const b = aggregate(c);
    a.sources += b.sources; a.records += b.records; a.bytes += b.bytes; a.live += b.live;
    if (b.newest && (a.newest === null || b.newest > a.newest)) a.newest = b.newest;
  }
  return (node.agg = a);
}

function virtualSources(node) {
  if (node.virtual === "all") return state.sources;
  if (node.virtual === "pinned") return lib.pins.map(byName).filter(Boolean);
  if (node.virtual === "recent") return lib.recent.map(byName).filter(Boolean);
  return null;
}

/** Where a source lives, as a short path: "logs › payments". */
function locationText(s) { return ex.derived.get(s.name)?.loc ?? ""; }

function matchesFilter(s, words) {
  const hay = ex.derived.get(s.name)?.hay ?? s.name.toLowerCase();
  return words.every((w) => hay.includes(w));
}

/** What the list shows: a folder's subfolders and sources, or a flat list (virtual folders and searches). */
function currentItems() {
  const words = ex.filter.trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length) return { flat: true, folders: [], sources: state.sources.filter((s) => matchesFilter(s, words)) };
  const node = ex.nodes.get(ex.node) || ex.nodes.get("all");
  const virt = virtualSources(node);
  if (virt) return { flat: true, folders: [], sources: virt, keepOrder: node.virtual !== "all" };
  return { flat: false, folders: [...node.children.values()], sources: node.sources };
}

// One collator: localeCompare() with options builds a new one on every comparison (50 ms to sort 3,000 names).
const COLL = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });
const SORTS = {
  name: (a, b) => COLL.compare(a.name, b.name),
  loc: (a, b) => COLL.compare(locationText(a), locationText(b)) || SORTS.name(a, b),
  type: (a, b) => COLL.compare(kindLabel(a), kindLabel(b)) || SORTS.name(a, b),
  records: (a, b) => (a.records || 0) - (b.records || 0),
  bytes: (a, b) => (a.bytes || 0) - (b.bytes || 0),
  last: (a, b) => (lastMs(a) || 0) - (lastMs(b) || 0),
};
const FOLDER_SORTS = {
  name: (a, b) => COLL.compare(a.label, b.label),
  records: (a, b) => aggregate(a).records - aggregate(b).records,
  bytes: (a, b) => aggregate(a).bytes - aggregate(b).bytes,
  last: (a, b) => (aggregate(a).newest || 0) - (aggregate(b).newest || 0),
};

const COLUMNS = [["name", "Name"], ["loc", "Folder"], ["type", "Type"], ["records", "Events", "num"], ["bytes", "Size", "num"], ["last", "Newest event"]];

function renderHead(flat) {
  $("ex-list").classList.toggle("flat", flat);
  $("ex-head").replaceChildren(...COLUMNS.map(([key, label, cls]) => {
    const active = ex.sort.key === key;
    return h("th", { class: `c-${key} ${cls || ""} sortable${active ? " sorted" : ""}`, "aria-sort": active ? (ex.sort.dir > 0 ? "ascending" : "descending") : null,
      onclick: () => { ex.sort = { key, dir: active ? -ex.sort.dir : (key === "name" || key === "loc" || key === "type" ? 1 : -1) }; renderList(); } },
    label, h("span", { class: "arrow", text: active ? (ex.sort.dir > 0 ? "▲" : "▼") : "" }));
  }));
}

function renderList() {
  const items = currentItems();
  renderHead(items.flat);
  const dir = ex.sort.dir;
  const sources = items.keepOrder && ex.sort.key === "name" && ex.sort.dir === 1 ? [...items.sources] : [...items.sources].sort((a, b) => dir * (SORTS[ex.sort.key] || SORTS.name)(a, b));
  const fsort = FOLDER_SORTS[ex.sort.key] || FOLDER_SORTS.name;
  const folders = [...items.folders].sort((a, b) => (FOLDER_SORTS[ex.sort.key] ? dir : 1) * fsort(a, b));
  ex.rows = [...folders.map((f) => ({ type: "folder", key: f.id, node: f })), ...sources.map((s) => ({ type: "source", key: s.name, source: s }))];
  // Another folder or search starts at the top; a re-sort or refresh keeps the scroll position.
  const listKey = `${ex.node}|${ex.filter.trim().toLowerCase()}`;
  if (listKey !== ex.listKey) { ex.listKey = listKey; $("ex-wrap").scrollTop = 0; }
  renderRows(true);
  const empty = ex.rows.length === 0;
  $("ex-empty").hidden = !empty;
  if (empty) {
    const node = ex.nodes.get(ex.node);
    const [title, text] = ex.filter.trim()
      ? [`No source matches “${ex.filter.trim()}”`, "The search looks at names, services, environments and folders."]
      : state.sources.length === 0
        ? ["No sources yet", "Register the folder your services log into (llog roots add <folder>) or convert an old log file (llog migrate <file> --name <name>). Then press the rescan button."]
        : node?.virtual === "pinned" ? ["Nothing pinned", "Select a source and press Pin to keep it here."]
          : node?.virtual === "recent" ? ["Nothing opened yet", "Sources you open appear here."]
            : ["No sources in this folder yet", "Services that log into this folder with LatticeLog appear here on the next rescan."];
    $("ex-empty-title").textContent = title;
    $("ex-empty-text").textContent = text;
  }
  const total = ex.rows.length;
  const recs = sources.reduce((n, s) => n + (s.records || 0), 0) + folders.reduce((n, f) => n + aggregate(f).records, 0);
  const size = sources.reduce((n, s) => n + (s.bytes || 0), 0) + folders.reduce((n, f) => n + aggregate(f).bytes, 0);
  $("ex-status").textContent = total ? `${num(total)} item${total === 1 ? "" : "s"} · ${num(recs)} events · ${bytes(size)}` : "";
}

/**
 * Virtual list: only the rows in (and just around) the visible part of the list are in the DOM, between two spacer
 * rows that keep the scrollbar true to the full list. Thousands of sources cost the same as forty.
 */
const vlist = { h: 46, start: -1, end: -1, raf: 0 };
function renderRows(force = false) {
  const wrap = $("ex-wrap");
  const n = ex.rows.length;
  const head = $("ex-list").tHead.offsetHeight;
  const view = wrap.clientHeight || 800;
  const top = Math.max(0, wrap.scrollTop - head);
  const start = Math.max(0, Math.floor(top / vlist.h) - 8);
  const end = Math.min(n, Math.ceil((top + view) / vlist.h) + 8);
  if (!force && start === vlist.start && end === vlist.end) return;
  vlist.start = start; vlist.end = end;
  const spacer = (px) => { const td = h("td", { colspan: "6" }); td.style.height = `${px}px`; return h("tr", { class: "spacer", "aria-hidden": "true" }, td); };
  const rows = [];
  if (start > 0) rows.push(spacer(start * vlist.h));
  for (let i = start; i < end; i++) { const r = ex.rows[i]; rows.push(r.type === "folder" ? folderRow(r.node, i) : sourceRow(r.source, i)); }
  if (end < n) rows.push(spacer((n - end) * vlist.h));
  $("ex-list").tBodies[0].replaceChildren(...rows);
  markSelection();
  // The CSS sets the row height; measure it once it is laid out (zoom and fonts change it slightly).
  const first = rows.find((r) => r.dataset.i !== undefined);
  const real = first && wrap.clientHeight ? first.getBoundingClientRect().height : 0;
  if (real > 10 && Math.abs(real - vlist.h) > 0.5) { vlist.h = real; renderRows(true); }
}

/** Scrolls the list so row i is visible below the sticky header. */
function scrollToRow(i) {
  const wrap = $("ex-wrap");
  const head = $("ex-list").tHead.offsetHeight;
  const top = i * vlist.h;
  if (top < wrap.scrollTop) wrap.scrollTop = top;
  else if (top + vlist.h + head > wrap.scrollTop + wrap.clientHeight) wrap.scrollTop = top + vlist.h + head - wrap.clientHeight;
  renderRows();
}

function nameCell(iconEl, name, sub, dotKind, dotTitle) {
  return h("td", { class: "n" },
    h("div", { class: "nm" },
      h("span", { class: "dot " + (dotKind || "blank"), title: dotTitle || null }),
      iconEl,
      h("div", { class: "nt" }, h("span", { class: "name", text: name }), sub ? h("span", { class: "sub", text: sub }) : null)));
}

function folderRow(f, i) {
  const a = aggregate(f);
  return h("tr", { class: "folder", "data-i": i, onclick: () => selectRow(i), ondblclick: () => goFolder(f.id), title: f.title },
    nameCell(icon(f.icon, "k-folder"), f.label, `${num(a.sources)} source${a.sources === 1 ? "" : "s"}${a.live ? ` · ${a.live} active` : ""}`, a.live ? "live" : null, a.live ? `${a.live} active source(s)` : null),
    h("td", { class: "loc" }),
    h("td", { class: "type muted", text: "Folder" }),
    h("td", { class: "num", text: a.records ? num(a.records) : "" }),
    h("td", { class: "num", text: a.bytes ? bytes(a.bytes) : "" }),
    h("td", { class: "last", title: a.newest ? fmtFull(a.newest) : null, text: a.newest ? ago(a.newest) : "" }));
}

function sourceRow(s, i) {
  const [act, actText] = activity(s);
  const sub = [s.service && s.service !== s.name ? s.service : null, s.environment].filter(Boolean).join(" · ");
  const t = lastMs(s);
  return h("tr", { class: "source" + (hasData(s) ? "" : " nodata") + (s.name === state.ns ? " current" : ""), "data-i": i, onclick: () => selectRow(i), ondblclick: () => hasData(s) && openSource(s.name), title: s.path || s.name },
    nameCell(icon(kindOf(s).icon, "k-" + (s.kind || "store")), s.name, sub || null, act, actText),
    h("td", { class: "loc", text: locationText(s) }),
    h("td", { class: "type", text: kindLabel(s) }),
    h("td", { class: "num", text: hasData(s) ? num(s.records) : "–" }),
    h("td", { class: "num", text: s.bytes ? bytes(s.bytes) : "" }),
    h("td", { class: "last", title: t ? fmtFull(t) : null, text: t ? ago(t) : (hasData(s) ? "" : "no events yet") }));
}

function selectRow(i, scroll = false) {
  const r = ex.rows[i];
  if (!r) return;
  ex.sel = { type: r.type, key: r.key };
  markSelection(scroll);
  renderPreview();
}

function selectedIndex() { return ex.sel ? ex.rows.findIndex((r) => r.type === ex.sel.type && r.key === ex.sel.key) : -1; }

function markSelection(scroll = false) {
  const i = selectedIndex();
  if (scroll && i >= 0) scrollToRow(i);
  for (const tr of $("ex-list").tBodies[0].rows) {
    if (tr.dataset.i === undefined) continue;
    const on = Number(tr.dataset.i) === i;
    tr.classList.toggle("selected", on);
    tr.setAttribute("aria-selected", String(on));
  }
}

function goFolder(id) {
  if (!ex.nodes.has(id)) return;
  ex.node = id;
  ex.filter = "";
  $("ex-filter").value = "";
  $("ex-clear").hidden = true;
  for (let n = ex.nodes.get(id); n; n = n.parent) ex.open.add(n.id);
  ex.sel = null;
  renderExplorerParts();
}

function goUp() {
  if (ex.filter) { goFolder(ex.node); return; }
  const n = ex.nodes.get(ex.node);
  if (n?.parent) { const from = n.id; goFolder(n.parent.id); ex.sel = { type: "folder", key: from }; markSelection(true); renderPreview(); }
}

function renderCrumbs() {
  const box = $("ex-crumbs");
  if (ex.filter.trim()) {
    box.replaceChildren(h("span", { class: "crumb here" }, `Search results for “${ex.filter.trim()}”`));
    $("ex-up").disabled = false;
    return;
  }
  const chain = [];
  for (let n = ex.nodes.get(ex.node); n; n = n.parent) chain.unshift(n);
  const parts = [];
  chain.forEach((n, i) => {
    if (i) parts.push(icon("chevron", "sep"));
    const last = i === chain.length - 1;
    parts.push(h("button", { type: "button", class: "crumb" + (last ? " here" : ""), title: n.title, onclick: () => goFolder(n.id) }, i === 0 ? icon(n.icon) : null, n.label));
  });
  box.replaceChildren(...parts);
  $("ex-up").disabled = !chain.length || !chain[chain.length - 1].parent;
}

function renderTree() {
  const tree = $("ex-tree");
  const item = (node, depth) => {
    const kids = [...node.children.values()].sort(FOLDER_SORTS.name);
    const open = ex.open.has(node.id);
    const a = aggregate(node);
    const row = h("div", { class: "tn" + (ex.node === node.id && !ex.filter.trim() ? " active" : ""), role: "treeitem", tabindex: "0", title: node.title, "aria-expanded": kids.length ? String(open) : null,
      onclick: () => goFolder(node.id), onkeydown: (ev) => { if (ev.key === "Enter") goFolder(node.id); else if (ev.key === "ArrowRight" && kids.length && !open) toggleOpen(node.id); else if (ev.key === "ArrowLeft" && open) toggleOpen(node.id); } },
    kids.length ? h("button", { type: "button", class: "tw" + (open ? " open" : ""), tabindex: "-1", "aria-label": open ? "Collapse" : "Expand", onclick: (ev) => { ev.stopPropagation(); toggleOpen(node.id); } }, icon("chevron")) : h("span", { class: "tw" }),
    icon(node.icon, "k-" + node.icon), h("span", { class: "tl", text: node.label }), h("span", { class: "tc", text: a.sources ? num(a.sources) : "" }));
    row.style.paddingLeft = `${6 + depth * 14}px`;
    const out = [row];
    if (open) for (const k of kids) out.push(...item(k, depth + 1));
    return out;
  };
  const tops = [...ex.nodes.values()].filter((n) => n.top);
  const order = (n) => (n.id.startsWith("loc:") ? 0 : n.id.startsWith("proj:") ? 1 : n.id === "migrated" ? 2 : 3);
  tops.sort((a, b) => order(a) - order(b) || COLL.compare(a.label, b.label));
  const section = (title, nodes) => nodes.length ? [h("div", { class: "tsec", text: title }), ...nodes.flatMap((n) => item(n, 0))] : [];
  tree.replaceChildren(
    ...section("Quick access", ["all", "pinned", "recent"].map((id) => ex.nodes.get(id))),
    ...section("Folders", tops.filter((n) => order(n) < 2)),
    ...section("Other", tops.filter((n) => order(n) >= 2)));
}

function toggleOpen(id) {
  if (ex.open.has(id)) ex.open.delete(id); else ex.open.add(id);
  renderTree();
}

function kv(rows) {
  return h("dl", { class: "kv pv-kv" }, ...rows.filter((r) => r && r[1] !== undefined && r[1] !== null && r[1] !== "").flatMap(([k, v, extra]) => [h("dt", { text: k }), h("dd", {}, h("span", { text: String(v) }), extra || null)]));
}

function copyButton(text) {
  return h("button", { type: "button", class: "ghost small", title: "Copy", onclick: async () => {
    try { await navigator.clipboard.writeText(text); toast("Copied"); } catch { toast("Copy is not available here"); }
  } }, "Copy");
}

function renderPreview() {
  const box = $("ex-preview");
  const i = selectedIndex();
  const r = i >= 0 ? ex.rows[i] : null;
  if (r?.type === "source") { sourcePreview(box, r.source); return; }
  const node = r?.type === "folder" ? r.node : ex.nodes.get(ex.node);
  if (!node) { box.replaceChildren(); return; }
  const a = ex.filter.trim() ? null : aggregate(node);
  if (!a) { box.replaceChildren(h("p", { class: "muted small pv-hint", text: "Select a source to see its details." })); return; }
  box.replaceChildren(...[
    h("div", { class: "pv-head" }, h("div", { class: "pv-icon" }, icon(node.icon)), h("div", { class: "pv-title" }, h("h2", { text: node.label }), h("span", { class: "muted small", text: node.virtual ? "Collection" : "Folder" }))),
    r?.type === "folder" ? h("div", { class: "pv-actions" }, h("button", { type: "button", class: "primary", onclick: () => goFolder(node.id) }, "Open folder")) : null,
    h("div", { class: "pv-cards" },
      miniStat("Sources", num(a.sources)), miniStat("Active now", num(a.live)),
      miniStat("Events", compact(a.records)), miniStat("On disk", bytes(a.bytes))),
    kv([["Newest event", a.newest ? `${ago(a.newest)} (${fmtFull(a.newest)})` : null], ["Path", node.path, node.path ? copyButton(node.path) : null]]),
    h("p", { class: "muted small pv-hint", text: "Double-click a source (or select it and press Enter) to search its logs." })].filter(Boolean));
}

function miniStat(label, value) { return h("div", { class: "mini" }, h("span", { class: "mini-v", text: value }), h("span", { class: "mini-l", text: label })); }

function sourcePreview(box, s) {
  const [act, actText] = activity(s);
  const pinned = lib.pins.includes(s.name);
  const can = hasData(s);
  const first = s.first_time ? toMs(s.first_time) : null;
  const last = lastMs(s);
  box.replaceChildren(...[
    h("div", { class: "pv-head" },
      h("div", { class: "pv-icon k-" + (s.kind || "store") }, icon(kindOf(s).icon)),
      h("div", { class: "pv-title" }, h("h2", { text: s.name }), h("span", { class: "muted small", text: kindLabel(s) })),
      h("button", { type: "button", class: "ghost icon-btn pin" + (pinned ? " on" : ""), title: pinned ? "Unpin" : "Pin to Quick access", "aria-pressed": String(pinned), onclick: () => togglePin(s.name) }, icon("star"))),
    h("p", { class: "pv-activity " + act }, h("span", { class: "dot " + act }), actText),
    h("div", { class: "pv-actions" },
      h("button", { type: "button", class: "primary", disabled: !can, onclick: () => openSource(s.name) }, "Open logs"),
      h("button", { type: "button", disabled: !can, onclick: () => openSource(s.name, "search", () => setLevel("ERROR")) }, "Errors"),
      h("button", { type: "button", disabled: !can, onclick: () => openSource(s.name, "stats") }, "Overview")),
    can ? h("div", { class: "pv-chart", id: "pv-chart" }, h("div", { class: "loading small" }, h("span", { class: "spinner" }), "Reading…")) : null,
    kv([
      ["Events", can ? num(s.records) : "none yet"],
      ["Size", s.bytes ? bytes(s.bytes) : null],
      ["From", first ? fmtFull(first) : null],
      ["To", last ? `${fmtFull(last)} (${ago(last)})` : null],
      ["Service", s.service && s.service !== s.name ? s.service : null],
      ["Environment", s.environment],
      ["Processes", s.kind === "python-local" ? s.processes : null],
      ["Retention", s.retention],
      ["Indexed", s.last_indexed ? ago(toMs(s.last_indexed)) : null],
      ["Last error", s.last_error],
      ["Folder", s.path, s.path ? copyButton(s.path) : null],
      ["Migrated from", (s.migrated_from || []).join("\n") || null],
      ["Note", s.records_note],
    ])].filter(Boolean));
  // Holding an arrow key passes many rows: count only for the row the selection stops on (cached ones draw at once).
  clearTimeout(ex.pvTimer);
  if (can) ex.pvTimer = setTimeout(() => previewStats(s), ex.stats.has(`${s.name}|${s.last_time}|${s.records}`) ? 0 : 150);
}

/** All-time level mix and events over time, for the preview. Cached per source and newest event. */
async function previewStats(s) {
  const key = `${s.name}|${s.last_time}|${s.records}`;
  const seq = ++ex.seq;
  let data = ex.stats.get(key);
  if (!data) {
    try {
      const first = s.first_time ? toMs(s.first_time) : null;
      const last = lastMs(s);
      const span = first !== null && last !== null ? Math.max(last - first, 1e3) : null;
      const bucket = span ? (BUCKETS.find(([w]) => w >= span / 48) || BUCKETS[BUCKETS.length - 1]) : null;
      const [levels, time] = await Promise.all([
        api("/v1/stats", { ns: s.name, by: "level" }),
        bucket ? api("/v1/stats", { ns: s.name, bucket: bucket[1] }).catch(() => null) : null,
      ]);
      data = { levels, time, bucket, first, last };
      ex.stats.set(key, data);
    } catch (err) {
      if (seq === ex.seq && $("pv-chart")) $("pv-chart").replaceChildren(h("p", { class: "muted small", text: `Could not read it: ${err.message}` }));
      return;
    }
  }
  if (seq !== ex.seq || !$("pv-chart")) return;
  const mix = new Map();
  for (const g of data.levels.groups || []) { const k = baseLevel(g.key) || "(none)"; mix.set(k, (mix.get(k) || 0) + g.count); }
  const total = [...mix.values()].reduce((a, b) => a + b, 0) || 1;
  const order = ["FATAL", "ERROR", "WARN", "INFO", "DEBUG", "TRACE"];
  const levels = [...mix.entries()].sort((a, b) => (order.indexOf(a[0]) + 1 || 99) - (order.indexOf(b[0]) + 1 || 99));
  const parts = [];
  if (data.time && String(data.time.group_by).startsWith("time")) {
    const width = data.bucket[0];
    const counts = new Map(data.time.groups.map((g) => [toMs(g.key), g.count]));
    const start = Math.floor(data.first / width) * width;
    const bars = [];
    for (let t = start; t <= data.last && bars.length < 60; t += width) bars.push([t, counts.get(t) || 0]);
    const max = Math.max(1, ...bars.map((b) => b[1]));
    parts.push(h("div", { class: "pv-label muted small", text: `Events over time · ${data.bucket[2]} per bar` }),
      h("div", { class: "spark" }, ...bars.map(([t, n]) => { const sp = h("span", { title: `${fmtShort(t)}: ${num(n)} event${n === 1 ? "" : "s"}` }); sp.style.height = n ? `${Math.max(4, (100 * n) / max)}%` : "0"; return sp; })),
      h("div", { class: "hist-axis" }, h("span", { text: fmtShort(start) }), h("span", { text: fmtShort(data.last) })));
  }
  parts.push(h("div", { class: "pv-label muted small", text: "Levels" }),
    h("div", { class: "lvlbar" }, ...levels.map(([k, n]) => { const sp = h("span", { class: k, title: `${k}: ${num(n)}` }); sp.style.width = `${(100 * n) / total}%`; return sp; })),
    h("div", { class: "lvl-legend" }, ...levels.map(([k, n]) => h("button", { type: "button", class: "lg", title: `Search ${k} events`, onclick: () => openSource(s.name, "search", () => setLevel(order.includes(k) ? k : "")) },
      h("span", { class: "sw " + k }), h("span", { text: k }), h("b", { text: compact(n) })))));
  $("pv-chart").replaceChildren(...parts);
}

function renderExplorerParts() {
  for (const n of ex.nodes.values()) n.agg = null;
  renderTree();
  renderCrumbs();
  renderList();
  renderPreview();
}

/** mode: "local" redraws, "reload" fetches the source list again, "rescan" also rescans the registered folders. */
async function renderExplorer(mode = "local") {
  try {
    if (mode !== "local") {
      if (mode === "rescan") $("ex-refresh").classList.add("spinning");
      await loadSources(mode === "rescan");
      setStatus(state.sources.length === 1 ? "Connected · 1 source" : `Connected · ${num(state.sources.length)} sources`, "ok");
      $("source-btn").hidden = state.sources.length === 0;
    }
  } catch (err) {
    toast(`Could not rescan: ${err.message}`);
  } finally {
    $("ex-refresh").classList.remove("spinning");
  }
  renderExplorerParts();
}

/** Opens a source in Search (or another view); <paramref name="setup"/> runs first, for example to set a level. */
async function openSource(name, view = "search", setup = null) {
  if (!state.namespaces.includes(name)) return;
  closePalette();
  if (state.ns !== name) { state.ns = name; sessionStorage.setItem("ll-ns", name); closeDetail(); }
  touchRecent(name);
  renderSourceButton();
  // Back in the explorer, show where this source lives (the search that found it has done its job).
  ex.sel = { type: "source", key: name };
  ex.filter = "";
  $("ex-filter").value = "";
  $("ex-clear").hidden = true;
  const home = ex.locOf.get(name);
  if (home) { ex.node = home.id; for (let n = home; n; n = n.parent) ex.open.add(n.id); }
  if (setup) setup();
  state.view = view;
  syncTabs();
  await sourceChanged();
}

function renderSourceButton() {
  const s = byName(state.ns);
  $("source-btn").replaceChildren(
    icon(s ? kindOf(s).icon : "folder", "sb-ic"),
    h("span", { class: "sb-name", text: s ? s.name : "Choose a source" }),
    icon("chevron", "sb-chev"));
}

// ------------------------------------------------------------------ quick switcher (Ctrl+K)

const pal = { items: [], sel: 0 };

function openPalette() {
  if ($("app").hidden || !state.sources.length) return;
  $("palette").hidden = false;
  $("pal-input").value = "";
  renderPalette();
  $("pal-input").focus();
}
function closePalette() { $("palette").hidden = true; }

function renderPalette() {
  const q = $("pal-input").value.trim().toLowerCase();
  const words = q.split(/\s+/).filter(Boolean);
  let list;
  if (words.length) {
    const rank = (s) => { const n = s.name.toLowerCase(); return n === q ? 0 : n.startsWith(q) ? 1 : n.includes(q) ? 2 : 3; };
    list = state.sources.filter((s) => matchesFilter(s, words)).sort((a, b) => rank(a) - rank(b) || (hasData(b) - hasData(a)) || SORTS.name(a, b));
  } else {
    const recent = lib.recent.map(byName).filter(Boolean);
    list = [...recent, ...state.sources.filter((s) => !lib.recent.includes(s.name)).sort((a, b) => (lastMs(b) || 0) - (lastMs(a) || 0))];
  }
  pal.items = list.slice(0, 60);
  pal.sel = Math.min(pal.sel, Math.max(0, pal.items.length - 1));
  if (!words.length) pal.sel = pal.items.length > 1 && pal.items[0].name === state.ns ? 1 : 0;
  $("pal-list").replaceChildren(...(pal.items.length ? pal.items.map((s, i) => {
    const t = lastMs(s);
    return h("li", { class: "pal-item" + (hasData(s) ? "" : " dim"), role: "option", id: "pal-" + i, onclick: () => palPick(i), onmousemove: () => { if (pal.sel !== i) { pal.sel = i; markPalette(); } } },
      icon(kindOf(s).icon, "k-" + (s.kind || "store")),
      h("span", { class: "pal-name", text: s.name }),
      h("span", { class: "pal-loc", text: locationText(s) }),
      h("span", { class: "pal-meta", text: hasData(s) ? `${compact(s.records)} · ${t ? ago(t) : ""}` : "no events yet" }),
      !words.length && lib.recent.includes(s.name) ? h("span", { class: "badge", text: s.name === state.ns ? "current" : "recent" }) : null);
  }) : [h("li", { class: "pal-none muted", text: "No source matches." })]));
  markPalette();
}

function markPalette() {
  [...$("pal-list").children].forEach((li, i) => {
    li.classList.toggle("sel", i === pal.sel);
    li.setAttribute("aria-selected", String(i === pal.sel));
    if (i === pal.sel) li.scrollIntoView({ block: "nearest" });
  });
  $("pal-input").setAttribute("aria-activedescendant", pal.items.length ? "pal-" + pal.sel : "");
}

function palPick(i) {
  const s = pal.items[i];
  if (!s) return;
  closePalette();
  if (!hasData(s)) { // nothing to search yet: show it in the explorer instead
    ex.filter = ""; ex.node = ex.locOf.get(s.name)?.id || "all"; ex.sel = { type: "source", key: s.name };
    switchView("sources");
    return;
  }
  openSource(s.name, state.view === "sources" ? "search" : state.view);
}

// ------------------------------------------------------------------ views and events

function syncTabs() {
  const view = state.view;
  for (const b of $("tabs").children) b.classList.toggle("active", b.dataset.view === view);
  for (const v of ["sources", "search", "stats", "catalog"]) $("view-" + v).hidden = v !== view;
  $("source-span").hidden = view === "sources";
  $("source-btn").classList.toggle("active", view === "sources");
  if (view !== "search") closeDetail();
}

function switchView(view) {
  state.view = view;
  syncTabs();
  refreshView();
}

function refreshView(note) {
  if (state.view === "sources") return renderExplorer("reload");
  if (!state.ns) {
    if (state.view !== "search") { state.view = "search"; syncTabs(); }
    showNoSource();
    return;
  }
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
$("source-btn").addEventListener("click", () => switchView("sources"));
$("ex-up").addEventListener("click", goUp);
$("ex-refresh").addEventListener("click", () => renderExplorer("rescan"));
$("ex-wrap").addEventListener("scroll", () => {
  if (!vlist.raf) vlist.raf = requestAnimationFrame(() => { vlist.raf = 0; renderRows(); });
}, { passive: true });
window.addEventListener("resize", () => { if (state.view === "sources") renderRows(); });
$("ex-filter").addEventListener("input", () => {
  ex.filter = $("ex-filter").value;
  $("ex-clear").hidden = !ex.filter;
  ex.sel = null;
  renderTree();
  renderCrumbs();
  renderList();
  if (ex.filter.trim() && ex.rows.length) selectRow(0); else renderPreview();
});
$("ex-filter").addEventListener("keydown", (ev) => {
  if (ev.key === "ArrowDown" || ev.key === "ArrowUp") { ev.preventDefault(); moveSelection(ev.key === "ArrowDown" ? 1 : -1); }
  else if (ev.key === "Enter") { ev.preventDefault(); activateSelection(); }
});
$("ex-clear").addEventListener("click", () => { $("ex-filter").value = ""; $("ex-filter").dispatchEvent(new Event("input")); $("ex-filter").focus(); });
$("pal-input").addEventListener("input", () => { pal.sel = 0; renderPalette(); });
$("pal-input").addEventListener("keydown", (ev) => {
  if (ev.key === "ArrowDown" || ev.key === "ArrowUp") {
    ev.preventDefault();
    if (pal.items.length) { pal.sel = (pal.sel + (ev.key === "ArrowDown" ? 1 : -1) + pal.items.length) % pal.items.length; markPalette(); }
  } else if (ev.key === "Enter") { ev.preventDefault(); palPick(pal.sel); }
});
$("palette").addEventListener("mousedown", (ev) => { if (ev.target === $("palette")) closePalette(); });
$("pal-browse").addEventListener("click", () => { closePalette(); switchView("sources"); setTimeout(() => $("ex-filter").focus(), 0); });

function moveSelection(delta) {
  if (!ex.rows.length) return;
  const i = selectedIndex();
  selectRow(i < 0 ? (delta > 0 ? 0 : ex.rows.length - 1) : Math.min(ex.rows.length - 1, Math.max(0, i + delta)), true);
}
function activateSelection() {
  const r = ex.rows[selectedIndex()];
  if (!r) return;
  if (r.type === "folder") goFolder(r.key);
  else if (hasData(r.source)) openSource(r.source.name);
}
for (const b of $("tabs").children) b.addEventListener("click", () => switchView(b.dataset.view));

$("query").addEventListener("submit", (ev) => { ev.preventDefault(); dropStatsCache(); state.view === "search" ? runSearch() : refreshView(); });
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
$("stats-refresh").addEventListener("click", () => { dropStatsCache(); stats(); });

$("close").addEventListener("click", closeDetail);
$("prev").addEventListener("click", () => selectEvent(state.selected - 1));
$("next").addEventListener("click", () => selectEvent(state.selected + 1));
$("copy-json").addEventListener("click", async (ev) => {
  ev.preventDefault();
  try { await navigator.clipboard.writeText($("detail-json").textContent); toast("JSON copied"); } catch { toast("Copy is not available here"); }
});

document.addEventListener("keydown", (ev) => {
  const typing = ev.target instanceof HTMLInputElement || ev.target instanceof HTMLSelectElement || ev.target instanceof HTMLTextAreaElement;
  if ((ev.ctrlKey || ev.metaKey) && !ev.altKey && (ev.key === "k" || ev.key === "K")) {
    ev.preventDefault();
    if ($("palette").hidden) openPalette(); else closePalette();
    return;
  }
  if (ev.key === "Escape" && !$("palette").hidden) { closePalette(); return; }
  if (ev.key === "Escape") {
    if (typing) { ev.target.blur(); return; }
    if (!$("detail").hidden) closeDetail();
    else if (!$("help").hidden) $("help").hidden = true;
    return;
  }
  if (typing || ev.ctrlKey || ev.metaKey || ev.altKey || $("app").hidden) return;
  if (!$("palette").hidden) return;
  if (state.view === "sources") {
    if (ev.key === "/") { ev.preventDefault(); $("ex-filter").focus(); $("ex-filter").select(); }
    else if (ev.key === "ArrowDown" || ev.key === "j") { ev.preventDefault(); moveSelection(1); }
    else if (ev.key === "ArrowUp" || ev.key === "k") { ev.preventDefault(); moveSelection(-1); }
    else if (ev.key === "Enter" && !(ev.target instanceof HTMLButtonElement) && ev.target.getAttribute("role") !== "treeitem") { ev.preventDefault(); activateSelection(); }
    else if (ev.key === "Backspace") { ev.preventDefault(); goUp(); }
    return;
  }
  if (ev.key === "/") { ev.preventDefault(); if (state.view !== "search") switchView("search"); $("where").focus(); $("where").select(); }
  else if (state.view === "search" && (ev.key === "j" || ev.key === "ArrowDown")) { ev.preventDefault(); selectEvent(state.selected + 1); }
  else if (state.view === "search" && (ev.key === "k" || ev.key === "ArrowUp")) { ev.preventDefault(); if (state.selected > 0) selectEvent(state.selected - 1); }
});

// ------------------------------------------------------------------ start

$("order").value = prefs.order === "asc" ? "asc" : "desc";
setRange(state.range in RANGES || state.range === "all" ? state.range : "24h");
setLevel("");
if (state.token) connect(); else { setStatus("Not connected", "bad"); showConnect(""); }
