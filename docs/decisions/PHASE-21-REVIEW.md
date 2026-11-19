# PHASE-21-REVIEW — SDKs in other languages (agreed scope: Python and PHP)

- Status: **CONDITIONAL** (Python and PHP built and tested end to end; Java, Go and Node outside the agreed scope;
  no packages published)
- Date: 2026-11-18
- Deliverables: `sdks/python/` (`latticelog`), `sdks/php/` (`latticelog/latticelog`), `tests/sdk/python_smoke.py`,
  `tests/sdk/php_smoke.php`, `tests/LatticeLog.Tests/SdkTests.cs`

| task | status | evidence |
|---|---|---|
| T01 architecture | done | transport clients: durable JSONL spool + protocol v1 JSONL; no native `.llog` writer (not justified) |
| T02 protocol and contract tests | partial | `specs/ingest-protocol.md`; the SDKs are tested against the real server (deduplication, resend, types) |
| T03 test vectors | partial | lossless values (`$u64`, `$f64bits`, `$bytes`, `$ts`, `$uuid`, `$dec`, maps, lone surrogates) checked by round trip in `SdkTests` |
| T04–T07 Java, Go | out of scope | — |
| T08 Python with `logging` | done | `LatticeLogHandler`; background shipping with backoff and jitter |
| T09 Python multiprocessing and shutdown | documented | one process per spool; pending shipping on exit |
| T10–T11 Node | out of scope | — |
| PHP (added) | done | spool shared across processes with `flock` (6 processes tested), shipping at the end of the request after `fastcgi_finish_request`, `bin/latticelog-ship.php` for cron |
| T12 same model | done | severities, scopes/operations, trace context and types equivalent to the .NET SDK |
| T13 TLS, auth, retries | done | HTTPS with verification by default, bearer token, idempotent retries by (producer, sequence) |
| T14 published packages | not done | MIT license already declared; PyPI/Packagist accounts missing |
| T15 N-1/N-2 | not applicable yet | first version |
| T16 mixed microservice examples | not done | — |
| T17 overhead vs the ecosystem's base logging | not measured | — |
| T18 maintenance per runtime | done | `docs/release/maintenance-policy.md` (Python 3.10+, PHP 8.1+) |

- The SDK tests are skipped when `python`/`php` are not installed; they run locally and in CI.
- Gate G21: contract tests and retry/authentication semantics met for Python and PHP; reading the format stays
  independent of the producer.
