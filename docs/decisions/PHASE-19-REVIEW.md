# PHASE-19-REVIEW — Escalabilidad horizontal y replicación

- Estado: **NOT PASSED** (réplica asíncrona y failover manual hechos; no hay clúster)
- Fecha: 2026-11-18
- Entregables: `src/LatticeLog.Server/Replication.cs`, seguimiento de procedencia upstream en `IngestService`,
  `--replicate-to` en `latticelog-server`, `tests/LatticeLog.Tests/ReplicationTests.cs`,
  `docs/operations/replication.md`

| tarea | estado | evidencia |
|---|---|---|
| T01 límites de un nodo | parcial | `reports/ingest-v1.md` con 4 CPUs; el equipo no es estable para medir el límite real |
| T02–T04 shards, router, control plane | no hecho | — |
| T05 líder/época/fencing | no hecho | documentado como riesgo: nada impide dos primarios a la vez |
| T06 consistencia | documentado | asíncrona: el ACK no espera a la réplica |
| T07 replicación | hecho (asíncrona) | un `Forwarder` por namespace; cursor persistente; lag en `/metrics` y `/v1/health` |
| T08 duplicados tras failover | hecho | la réplica aprende las secuencias del productor original desde la procedencia y las conserva tras reiniciar; probado: nada duplicado ni perdido al reenviar a la réplica |
| T09 snapshots y reconstrucción | parcial | backup en caliente verificado (P24); índices y catálogo se reconstruyen desde segmentos (P11) |
| T10–T12 movimiento de shards, cuotas por nodo | no hecho | — |
| T13 resultados parciales | hecho en un nodo | `partial_results` y `storage_issues` en la cobertura |
| T14 particiones, split-brain | no hecho | solo se probó la caída del primario |
| T15 escalado 1/3/5/N | no hecho | — |
| T16 topologías y runbook | hecho para primario+réplica | `docs/operations/replication.md` |

- Gate G19 ("ningún fallo de nodo conduce a confirmaciones falsas…; escalado horizontal medido"): la parte de
  confirmaciones falsas se cumple para primario+réplica (el ACK nunca afirma replicación); el escalado no existe.
- Decisión: no construir clúster antes de medir el límite de un nodo en hardware estable (T01).
