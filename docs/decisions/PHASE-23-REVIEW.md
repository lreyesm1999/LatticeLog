# PHASE-23-REVIEW — Web console

- Status: **CONDITIONAL** (a minimal console served by the server itself; no user testing or browser E2E)
- Date: 2026-11-18
- Deliverables: `src/LatticeLog.Server/ui/` (`index.html`, `app.js`, `app.css`, embedded resources), `/ui` routes in
  `LatticeServer.Ops.cs`, test `ServerApiTests.Console_metrics_and_backup`

| task | status | evidence |
|---|---|---|
| T01 interviews | blocked | needs users |
| T02 navigation | partial | Search, Stats and Catalog; no Overview/Sources/Settings |
| T03 search | done | filter, since, order, signed-cursor pagination, visible coverage |
| T04 inspector | partial | the event's full JSON with context; no schema view |
| T05 trace timeline | partial | the `/v1/traces/{id}` document in the inspector; no chart |
| T06 errors and volume | partial | bars by event/level/service with exact counts |
| T07 storage costs | partial | segment catalog with bytes and records |
| T08 Context Pack from the UI | not done | — |
| T09 permissions and keys | not done | the user types the token, which is kept only in `sessionStorage` |
| T10 light/dark, responsive | partial | `prefers-color-scheme` and a flexible layout; no WCAG audit |
| T11 virtualization | not done | pages of 100 with "More" |
| T12 XSS | done | everything through `textContent`; CSP `default-src 'self'` with no inline code; `X-Frame-Options: DENY`; the test checks that the JS never uses `innerHTML` |
| T13 E2E | partial | HTTP test of serving and headers; no automated browser |
| T14 usability | blocked | needs users |
| T15 partial data | done | the coverage line shows `PARTIAL: reason` and storage problems |
