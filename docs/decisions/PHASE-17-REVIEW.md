# PHASE-17-REVIEW — Servidor de consultas, catálogo y multiusuario (Server v1.0)

- Estado: **CONDITIONAL** (servidor de un nodo con namespaces, roles, API de lectura completa y probada; faltan
  administración en línea, retención, imagen Docker construida y validación en otro hardware)
- Fecha: 2026-11-18
- Entregables: `src/LatticeLog.Server` (`LatticeServer`), `src/LatticeLog.ServerHost` (`latticelog-server`),
  `specs/server-api.md`, `deploy/Dockerfile`, `docs/deploy/single-node.md`, `tests/LatticeLog.Tests/ServerApiTests.cs`,
  `ConcurrentReadTests.cs`, `reports/ingest-v1.md` §3–§4

| tarea | estado | evidencia |
|---|---|---|
| T01 tenant/project/environment | hecho | namespaces `tenant[/project[/env]]`, un almacén por namespace; aislamiento probado |
| T02 metadatos separados y transaccionales | parcial | por namespace: STORE.json + MANIFEST.jsonl (journal con CRC y fsync) + footers; no hay plano de control global |
| T03 catálogo | parcial | `GET /v1/catalog` (segmentos, rangos, servicios, problemas del manifest) y `/v1/schema`; sin estado de compactación en línea |
| T04 endpoints | hecho | ingest, search, event, related, trace, stats, overview, error groups, context pack, schema, export, catalog |
| T05 authn/authz | hecho | tokens con scopes `ingest`/`read`/`admin` y namespaces; 401/403 probados |
| T06 cuotas | parcial | ingesta: registros/s por token; lectura: concurrencia por token y global, timeout, registros escaneados; sin cuotas de bytes por tenant |
| T07 cursores firmados | hecho | HMAC-SHA256 + caducidad, ligados a namespace y token; manipulación, préstamo y caducidad probados |
| T08 lecturas separadas de ingestión | hecho | semáforo de consultas en el thread pool frente a hilos escritores dedicados; competencia de CPU medida (§4) |
| T09 snapshots y compactación | documentado | snapshot por petición; cursores por secuencia estable; la compactación retira segmentos vía manifest |
| T10 datos recientes y compactados | hecho | un único planificador sobre segmentos sellados y el activo |
| T11 llegada frente a evento | hecho | `observed_time` = llegada al servidor salvo que el productor la fije; documentado y probado |
| T12 administración (retención, compactación, reindexado) | no hecho | compactación y verificación solo fuera de línea con la CLI; **sin retención** |
| T13 migraciones de metadatos | no aplica aún | formato de metadatos v1 único; la versión de STORE.json se comprueba al abrir |
| T14 Docker y health | parcial | `deploy/Dockerfile` (chiseled, no root, configuración por env y secretos) **no construido**; `/v1/health` con detalle para admin |
| T15 guía de una máquina | hecho | `docs/deploy/single-node.md` |
| T16 carga productores + lectores | hecho en esta máquina | `reports/ingest-v1.md` §4 (4 CPUs por la inestabilidad del equipo) |

- Gate Server v1.0 ("múltiples fuentes ingestan y consultan de forma segura, con persistencia recuperable y sin
  exigir clúster"): **cumplido en loopback**. Falta TLS probado, red real, la imagen construida y repetir la carga en
  hardware estable.
- **Hallazgo de plataforma**: el equipo de referencia (i9-14900KF, microcódigo 0x11D, BIOS de 2023) corrompe memoria
  bajo carga en muchos núcleos, también en un programa de control sin LatticeLog (`tools/platform-stress`). Hasta que
  se actualice la BIOS, las mediciones con concurrencia se hacen con CPUs limitadas y no son generalizables.
- Decisión: avanzar con los pendientes listados. Antes de afirmar rendimiento hay que repetir en otra máquina o en CI.
