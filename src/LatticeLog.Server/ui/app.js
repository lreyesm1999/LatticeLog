// LatticeLog console (P23): a thin client of the read API. Log content is untrusted: everything is rendered with
// textContent, never as HTML. The token lives only in sessionStorage of this tab.
"use strict";
const $ = (id) => document.getElementById(id);
const state = { token: sessionStorage.getItem("ll-token") || "", ns: sessionStorage.getItem("ll-ns") || "", cursor: null };

async function api(path, params = {}) {
  const url = new URL(path, location.origin);
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") url.searchParams.set(k, v);
  if (state.ns && !url.searchParams.has("ns")) url.searchParams.set("ns", state.ns);
  const resp = await fetch(url, { headers: { Authorization: "Bearer " + state.token } });
  const body = await resp.json().catch(() => ({}));
  if (!resp.ok) throw new Error(`${resp.status} ${body.error || ""}: ${body.message || ""}`);
  return body;
}

function status(text) { $("status").textContent = text; }

function cell(row, text, cls) {
  const td = document.createElement("td");
  td.textContent = text ?? "";
  if (cls) td.className = cls;
  row.appendChild(td);
}

async function loadNamespaces() {
  const sel = $("ns");
  sel.replaceChildren();
  const { namespaces } = await api("/v1/namespaces", { ns: "" });
  for (const n of namespaces) {
    const o = document.createElement("option");
    o.value = o.textContent = n;
    sel.appendChild(o);
  }
  if (namespaces.includes(state.ns)) sel.value = state.ns; else state.ns = sel.value;
  status(namespaces.length ? `${namespaces.length} namespace(s)` : "no namespaces with data");
}

async function search(more = false) {
  if (!more) { state.cursor = null; $("events").tBodies[0].replaceChildren(); }
  try {
    const res = await api("/v1/search", { where: $("where").value, since: $("since").value, order: $("order").value, limit: 100, cursor: more ? state.cursor : null });
    const tbody = $("events").tBodies[0];
    for (const e of res.events) {
      const tr = document.createElement("tr");
      cell(tr, e.time.replace("T", " ").slice(0, 23));
      cell(tr, e.severity, e.severity);
      cell(tr, e.service);
      cell(tr, e.event);
      cell(tr, e.message ?? (typeof e.body === "string" ? e.body : JSON.stringify(e.body ?? "")));
      tr.addEventListener("click", () => showEvent(e));
      tbody.appendChild(tr);
    }
    const c = res.coverage;
    state.cursor = c.next_cursor || null;
    $("more").hidden = !state.cursor;
    $("coverage").textContent = `${tbody.rows.length} event(s)` + (c.truncated ? " · more available" : "") +
      (c.partial_results ? ` · PARTIAL: ${c.partial_reason}` : "") + (c.storage_issues ? ` · ${c.storage_issues} storage issue(s)` : "") +
      ` · ${res.stats.segments_scanned}/${res.stats.segments_total} segments · ${res.stats.elapsed_ms} ms`;
  } catch (err) { $("coverage").textContent = String(err.message); }
}

function showEvent(e) {
  $("detail").hidden = false;
  $("detail-title").textContent = `${e.severity} ${e.event ?? ""} · ${e.time}`;
  $("detail-json").textContent = JSON.stringify(e, null, 2);
  const traceButton = $("show-trace");
  traceButton.hidden = !e.trace_id;
  traceButton.onclick = async () => {
    try {
      const doc = await api(`/v1/traces/${encodeURIComponent(e.trace_id)}`);
      $("detail-title").textContent = `trace ${e.trace_id}`;
      $("detail-json").textContent = JSON.stringify(doc, null, 2);
    } catch (err) { $("detail-json").textContent = String(err.message); }
  };
}

async function stats() {
  const box = $("bars");
  box.replaceChildren();
  try {
    const res = await api("/v1/stats", { by: $("by").value, where: $("statswhere").value });
    const max = Math.max(1, ...res.groups.map((g) => g.count));
    for (const g of res.groups) {
      const row = document.createElement("div");
      const label = document.createElement("span"); label.textContent = g.key;
      const bar = document.createElement("span"); bar.className = "bar"; bar.style.width = `${(100 * g.count) / max}%`;
      const n = document.createElement("span"); n.textContent = g.count.toLocaleString();
      row.append(label, bar, n);
      box.appendChild(row);
    }
  } catch (err) { box.textContent = String(err.message); }
}

async function catalog() {
  const tbody = $("segments").tBodies[0];
  tbody.replaceChildren();
  try {
    const res = await api("/v1/catalog");
    for (const s of res.segments) {
      const tr = document.createElement("tr");
      cell(tr, s.id); cell(tr, s.sealed ? "yes" : "active"); cell(tr, s.records); cell(tr, s.first_seq ? `${s.first_seq}–${s.last_seq}` : "");
      cell(tr, s.min_time); cell(tr, s.max_time); cell(tr, s.bytes?.toLocaleString());
      tbody.appendChild(tr);
    }
  } catch (err) { status(String(err.message)); }
}

$("auth").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  state.token = $("token").value || state.token;
  sessionStorage.setItem("ll-token", state.token);
  try { await loadNamespaces(); sessionStorage.setItem("ll-ns", state.ns); await search(); } catch (err) { status(String(err.message)); }
});
$("ns").addEventListener("change", () => { state.ns = $("ns").value; sessionStorage.setItem("ll-ns", state.ns); search(); });
$("query").addEventListener("submit", (ev) => { ev.preventDefault(); search(); });
$("more").addEventListener("click", () => search(true));
$("statsform").addEventListener("submit", (ev) => { ev.preventDefault(); stats(); });
$("close").addEventListener("click", () => { $("detail").hidden = true; });
for (const b of document.querySelectorAll("nav button")) {
  b.addEventListener("click", () => {
    for (const x of document.querySelectorAll("nav button")) x.classList.toggle("active", x === b);
    for (const v of ["search", "stats", "catalog"]) $("view-" + v).hidden = v !== b.dataset.view;
    if (b.dataset.view === "stats") stats();
    if (b.dataset.view === "catalog") catalog();
  });
}
if (state.token) loadNamespaces().then(() => search()).catch((err) => status(String(err.message)));
