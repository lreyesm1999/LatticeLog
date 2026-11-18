# PHASE-16-REVIEW — Protocolo de ingestión remota y collector

- Estado: **CONDITIONAL** (protocolo, cliente y collector funcionan y están probados en loopback con fallos
  inyectados; faltan red real, TLS probado y carga multi-productor)
- Fecha: 2026-11-18
- Decisiones: `docs/decisions/ADR-0009.md` · Especificación: `specs/ingest-protocol.md`
- Entregables: `src/LatticeLog.Ingest` (envelope, cliente, forwarder), `src/LatticeLog.Server` (collector),
  `src/LatticeLog.Collector` (ejecutable `latticelog-collector`), `tests/LatticeLog.Tests/IngestTests.cs`,
  `reports/ingest-v1.md`

| tarea | estado | evidencia |
|---|---|---|
| T01 transporte | hecho | HTTP/1.1+2, un POST por lote; gRPC descartado con motivos (ADR-0009) |
| T02 envelope | hecho | cabecera de 48 B: productor, rango de secuencias, número de registros, codificación, compresión, CRC-32 |
| T03 semántica | hecho | at-least-once en el cable, exactly-once efectivo por productor; límites documentados |
| T04 idempotencia | hecho y probado | deduplicación por (productor, secuencia local); recorte de solapes; huecos explícitos |
| T05 ACK | hecho | nivel alcanzado de verdad (fsync/os) con group commit; los duplicados también fuerzan el flush pedido; réplica: N/A hasta P19 |
| T06 reintentos | hecho | backoff exponencial con jitter completo y `Retry-After`; errores no reintentables reportados |
| T07 spool | hecho | el almacén local es el spool (durable, con cuota) |
| T08 spool lleno | documentado | cuota local: escritura rechazada y contada (`DroppedWriteError`), sin borrado; sin prueba de extremo a extremo |
| T09 negociación | parcial | campo `body_encoding` + 415 con lista aceptada; solo existe la codificación JSONL, no hay alternativa a la que caer |
| T10 TLS/auth/rotación | parcial | tokens con hash y rotación en caliente probados; TLS con Kestrel implementado pero **no probado**; HTTP en dirección pública rechazado |
| T11 cuotas/backpressure | hecho | 429 por token probado; 503 por cola llena implementado pero no probado; límite de tasa mínima (slowloris) |
| T12 eventos gigantes/bombas | hecho y probado | límites de petición, descomprimido, ratio, registros y bytes por línea |
| T13 pruebas de red | parcial | pérdida de petición y de respuesta, retrasos, caída del collector; sin pérdida a nivel TCP ni reordenación (un lote en vuelo) |
| T14 medición | hecho (loopback) | ~180–217k registros/s, ACK p50 12–15 ms por lote de 5 000 |
| T15 límites de garantías | hecho | `specs/ingest-protocol.md` §4 |

- Gate G16 ("desconectarse, reintentar y recuperarse sin duplicados silenciosos ni confirmaciones falsas"):
  **cumplido en loopback con fallos inyectados**. Falta repetirlo por una red real y con TLS.
- Riesgos residuales:
  - el forwarder relee el segmento activo en cada sondeo (coste de CPU con segmentos grandes);
  - un collector que falla al escribir deja de aceptar hasta reiniciarse (decisión deliberada de seguridad);
  - sin réplica (P19).
- Decisión: avanzar a P17 manteniendo los pendientes.
