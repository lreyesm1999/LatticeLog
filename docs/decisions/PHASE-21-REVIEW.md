# PHASE-21-REVIEW — SDKs en otros lenguajes (alcance acordado: Python y PHP)

- Estado: **CONDITIONAL** (Python y PHP hechos y probados de extremo a extremo; Java, Go y Node fuera del alcance
  acordado; sin paquetes publicados)
- Fecha: 2026-11-18
- Entregables: `sdks/python/` (`latticelog`), `sdks/php/` (`latticelog/latticelog`), `tests/sdk/python_smoke.py`,
  `tests/sdk/php_smoke.php`, `tests/LatticeLog.Tests/SdkTests.cs`

| tarea | estado | evidencia |
|---|---|---|
| T01 arquitectura | hecho | clientes de transporte: spool JSONL duradero + protocolo v1 JSONL; sin escritor `.llog` nativo (no justificado) |
| T02 protocolo y contract tests | parcial | `specs/ingest-protocol.md`; los SDKs se prueban contra el servidor real (deduplicación, reenvío, tipos) |
| T03 test vectors | parcial | valores sin pérdida (`$u64`, `$f64bits`, `$bytes`, `$ts`, `$uuid`, `$dec`, mapas, surrogates sueltos) comprobados por ida y vuelta en `SdkTests` |
| T04–T07 Java, Go | fuera de alcance | — |
| T08 Python con `logging` | hecho | `LatticeLogHandler`; envío en hilo de fondo con backoff y jitter |
| T09 multiproceso y shutdown en Python | documentado | un proceso por spool; envío pendiente al salir |
| T10–T11 Node | fuera de alcance | — |
| PHP (añadido) | hecho | spool compartido entre procesos con `flock` (6 procesos probados), envío al terminar la petición tras `fastcgi_finish_request`, `bin/latticelog-ship.php` para cron |
| T12 mismo modelo | hecho | severidades, scopes/operación, trace context y tipos equivalentes al SDK .NET |
| T13 TLS, auth, reintentos | hecho | HTTPS con verificación por defecto, bearer token, reintentos idempotentes por (productor, secuencia) |
| T14 paquetes publicados | no hecho | licencia MIT ya declarada; faltan cuentas en PyPI/Packagist |
| T15 N-1/N-2 | no aplica aún | primera versión |
| T16 ejemplos de microservicios mixtos | no hecho | — |
| T17 sobrecarga frente a logging base | no medido | — |
| T18 mantenimiento por runtime | hecho | `docs/release/maintenance-policy.md` (Python 3.10+, PHP 8.1+) |

- Las pruebas de SDK se omiten si `python`/`php` no están instalados; en esta máquina se ejecutaron.
- Gate G21: contract tests y semántica de reintento/autenticación cumplidos para Python y PHP; la lectura del formato
  sigue siendo independiente del productor.
