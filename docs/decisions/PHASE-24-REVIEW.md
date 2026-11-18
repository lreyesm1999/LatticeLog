# PHASE-24-REVIEW — Operación, SRE, backup y DR

- Estado: **CONDITIONAL** (métricas, backup verificado, SLI iniciales y runbooks escritos; sin ensayos en
  infraestructura real)
- Fecha: 2026-11-18
- Entregables: `GET /metrics` y `POST /v1/admin/backup` (`LatticeServer.Ops.cs`), `src/LatticeLog.Storage/Backup.cs`,
  `latticelog backup`, opciones `--backup-dir`, `--metrics-anonymous` y `--replicate-to` en `latticelog-server`,
  `docs/operations/runbook.md`, `tests/LatticeLog.Tests/BackupTests.cs`

| tarea | estado | evidencia |
|---|---|---|
| T01 SLI/SLO | hecho (iniciales) | runbook §SLI; sin medir en producción |
| T02 métricas sin bucles | hecho | Prometheus por namespace; el servidor no escribe en sus propios almacenes |
| T03 dashboards | no hecho | — |
| T04 alertas | hecho (definidas) | runbook §Métricas y alertas |
| T05 backup consistente | hecho | en caliente, prefijo válido del segmento activo, verificado leyendo la copia |
| T06 restore en infraestructura nueva | parcial | restauración y escritura posterior probadas en la misma máquina |
| T07 RPO/RTO | parcial | RPO descrito; RTO sin medir |
| T08 DR regional | no hecho | — |
| T09 runbooks | hecho | disco lleno, corrupción, caída, token comprometido, privacidad, actualización |
| T10–T12 canary, rollback, simulacros | parcial | rollback descrito según la compatibilidad de formato; sin ensayos |
| T13 costes | no hecho | — |
| T14 guardias | no aplica | sin equipo |
| T15 on-prem/Docker/K8s | parcial | binario y Dockerfile (no construido); sin Kubernetes |
| T16 capacidad | parcial | cifras con 4 CPUs, no generalizables |

- Además: un middleware convierte cualquier excepción no capturada en un 500 JSON y la reporta por diagnósticos.
- Gate G24 ("restauración probada, SLOs observables, runbooks ejecutados"): falta ejecutar los runbooks y restaurar
  en otra máquina.
