# Maintenance policy (P28, draft)

Draft as of 2026-11-18. It is published together with the first stable version.

## Supported versions (P28-T01)
- **Engine and .NET SDK**: the latest minor of the current major gets fixes; the previous one gets security fixes only,
  for 6 months. Runtime: the current .NET LTS and the latest .NET version.
- **Python SDK**: CPython versions with upstream support (today 3.10+). **PHP SDK**: 8.1+ while PHP maintains it.
- **Operating systems**: Linux x64, Linux ARM64, Windows x64 and macOS ARM64, all covered by CI.

## Data compatibility (P28-T06, T07)
- `.llog` files of a major version are readable throughout that major and for at least **2 years** after the next
  major is released, with a migration tool published before reading support is removed.
- Removals are announced one minor version ahead (`docs/compatibility.md`).

## Continuous quality (P28-T02, T03, T05)
- A regression benchmark on every release, on the same stable reference machine, with the frozen corpus
  (`tests/GoldenFiles`, Loghub-2.0). A confirmed regression > 5 % blocks the release.
- A new codec only lands with an ablation experiment in `reports/` and a format version that declares it.

## Security (P28-T08)
- Private reporting channel (`SECURITY.md`); acknowledgement within 3 business days; a fix or mitigation for high
  severity within 30 days; a public advisory with a CVE when applicable.

## Evolution (P28-T10..T13, T15)
- Tools for external AI (MCP, Context Packs) improve without putting models in the core.
- New SDK languages only when there is verified, real demand.
- `ROADMAP.md` is public and reflects the true status of every phase, including those that did not pass.
