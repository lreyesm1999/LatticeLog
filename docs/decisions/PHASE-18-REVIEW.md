# PHASE-18-REVIEW — Security, privacy and auditability

- Status: **NOT PASSED** (the main server controls are built and tested; gate G18 requires encryption at rest and a
  pentest, which do not exist yet)
- Date: 2026-11-18
- Deliverables: `src/LatticeLog.Server/AuditLog.cs`, `LatticeServer.Admin.cs` (erase), the authentication brake in
  `LatticeServer.cs`, security headers in `LatticeServer.Ops.cs`, `docs/security/threat-model.md` §2,
  `docs/security/pentest-scope.md`, `docs/operations/runbook.md` (compromised token, privacy)

| task | status | evidence |
|---|---|---|
| T01 threat modeling | partial | local engine + server table (ingestion, reads, admin, console, replica); cloud not covered |
| T02 data classification and capture policies | partial | `RedactionPolicy` (sensitive keys redacted in outputs and in the .NET SDK); no per-tenant configurable per-field `never-log`/`hash` |
| T03 producer-side redaction | partial | .NET SDK (`LatticeLoggerOptions`); the Python/PHP SDKs do not redact |
| T04 TLS and encryption at rest | partial | TLS required off loopback and tested end to end with a real certificate (`TlsTests`, container smoke test; not between two machines); **no encryption at rest** (delegated to the OS's encrypted disk) |
| T05 keys | partial | secrets only through the environment; `server.key` with mode 0600 on Unix; no KMS |
| T06 RBAC | partial | `ingest`/`read`/`admin` scopes × namespaces; no ABAC or per-field permissions |
| T07 read-only access for tools | done | `read` scope, read-only MCP, expiring cursors; no time-limited tokens |
| T08 protected audit log | partial | `data/audit/*.jsonl` with a CRC per line: detects accidental corruption, **not** deliberate tampering |
| T09 costly queries, traversal, parser | done | query limits (P17), validated namespace names, decompression/line limits, binary-body fuzzing |
| T10 untrusted log text | done | console with `textContent` and a strict CSP; JSON in the API; MCP with provenance marking |
| T11 tenant isolation | done | negative tests in `ServerApiTests` (403/404 per namespace, borrowed cursors) |
| T12 retention and privacy erasure | done with limits | online retention and `erase`, tested (no trace left in the files; resends do not bring data back). Does not reach backups or the replica: documented |
| T13 legal hold | not done | — |
| T14 SAST/SCA/secret scanning/SBOM | done | CodeQL (C#, Python, JavaScript, workflows; `security-and-quality` queries) on every PR, on `main` and weekly; secret scanning with push protection enabled on the repository; SBOM (CycloneDX), vulnerable-package check, Trivy image scan and Dependabot |
| T15 incident response | partial | compromised-token runbook; private vulnerability reporting enabled (`SECURITY.md`) |
| T16 pentest | blocked | needs a third party; scope in `docs/security/pentest-scope.md` |
| T17 legislation | blocked | needs specialized legal advice |
| T18 Context Packs and sensitive data | partial | redaction applied when building server evidence; no specific test for partial labels |

- Brute-force brake: 20 failures/min per address → 429 (tested).
- Decision: do not handle third-party data in production until encryption at rest and a pentest exist.
