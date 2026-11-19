# Threat model (P18-T01, partial scope)

Scope: SDK, format/reader, CLI and local MCP (table 1) and, since 2026-11-18, the single-node server (table 2).
Cluster and cloud are **not** covered (they do not exist).

## 1. Local engine

| asset / surface | threat | implemented control | open |
|---|---|---|---|
| Producer data | persisted secrets | redaction on the writer thread before persistence (`Secret` fields, sensitive keys), `redacted` flag | redaction on the producer thread; `hash`/`allow-list` policies |
| `.llog` files | malicious or corrupt file (parser) | CRC per header/payload, limits on size/expansion/depth/counts, explicit errors, 300 bit-flip fuzzing cases | continuous coverage-guided fuzzing (SharpFuzz/libFuzzer) |
| `.llog` files | intentional tampering | none (a CRC does not authenticate) | segment signature/MAC for the server (P18-T04) |
| Store directory | two writers | exclusive OS lock | — |
| Store directory | permissions | inherited from the OS | document recommended permissions (0700) |
| CLI | accidental mass export | `export` requires `--output` or `--stdout` and does not overwrite without `--force` | — |
| Queries | resource exhaustion | limits on length, depth, IN, results, scan and time | per-user quotas (server) |
| MCP / evidence | instruction injection through log text | text marked as untrusted, safe Markdown fences, no write tools | evaluation with real agents (P22-T13) |
| MCP | path traversal | store fixed at startup, no path parameters | — |
| Importers | huge inputs / deep nesting | limits on bytes, records, attributes and depth | importer fuzzing |
| Dependencies | supply chain | minimal runtime dependencies (only `System.IO.Hashing`); Zstd/LZ4 only in the lab | SBOM and vulnerable-package check run in CI; SCA and secret scanning not yet |

Cryptography: no home-made encryption. Encryption at rest and TLS are delegated to standard libraries and mechanisms
(P18-T04).

## 2. Server (`latticelog-server`, P16–P18)

| asset / surface | threat | implemented control | open |
|---|---|---|---|
| Credentials | token theft in transit | TLS required off loopback (the server does not start on plain HTTP without an explicit option) | optional mTLS; end-to-end TLS test |
| Credentials | theft of the tokens file | only SHA-256 hashes are stored; constant-time comparison | expiring tokens; secret-manager integration |
| Credentials | brute force | limit of 20 failures per IP per minute (429), audited | distributed lockout (not applicable on one node) |
| Authorization | cross-tenant access | isolated namespaces (one store per namespace), `ingest`/`read`/`admin` scopes, names validated against path traversal | penetration testing |
| Cursors | tampering or reuse | HMAC-SHA256 with expiry, bound to namespace and token; `server.key` (0600 on Unix) | key rotation |
| Ingestion | duplicates or loss through retries | deduplication by (producer, sequence) rebuilt from the data; gaps are accepted only when declared (and the client confirms them with a full scan) | — |
| Ingestion | denial of service | limits on request, decompression, ratio, records and line length; per-token quota; bounded queue (503); minimum body rate (slowloris) | per-IP limits for anonymous requests |
| Reads | abusive queries | global and per-token concurrency, timeout, cap on scanned records (explicit partial result) | per-tenant byte quotas |
| Personal data | right to erasure | `POST /v1/admin/erase` rewrites segments without the affected records, with verification and audit; resends do not reintroduce them | erasure in external copies (producer stores, exports, backups, replicas) |
| Personal data | excessive retention | retention by age or size (CLI, automatic on rotation, admin endpoint) | per-namespace policy in configuration |
| Audit | actions without a trace | append-only log with a CRC per line: administration, reads (filters, never data) and authentication failures | chained signatures (strong tamper evidence); shipping to a SIEM |
| Web console (`/ui`) | XSS through log content, clickjacking, token theft | everything rendered with `textContent`; CSP `default-src 'self'` with no inline scripts; `X-Frame-Options: DENY` and `frame-ancestors 'none'`; token only in the tab's `sessionStorage` | SSO/OIDC instead of pasted tokens; accessibility audit and browser E2E |
| Metrics (`/metrics`) | reconnaissance (namespaces and volumes) | requires an admin token unless `--metrics-anonymous` is set explicitly | — |
| Backups | writes to arbitrary paths; inconsistent copy | the destination is set by the operator (`--backup-dir`), never the client; the copy is verified by reading it | backup encryption |
| Replica | exposed replica token; unencrypted data in transit | token only through the environment; HTTPS off loopback, as for ingestion | fencing between primaries |
| Error responses | leaking internal information | the client only gets a type and a message, never a stack trace; details go to `Diagnostics` | — |
| Supply chain | server dependencies | only the ASP.NET Core framework; *chiseled* image with no shell and a non-root user | image scanning |

External review: the proposed pentest scope is in `docs/security/pentest-scope.md`. **No pentest has been done.**
