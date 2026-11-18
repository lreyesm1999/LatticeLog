# Plan de lanzamiento (P27)

Estado a 18-nov-2026. El orden es fijo: no se publica nada antes de que estén marcadas las casillas bloqueantes de
`docs/release/rc-checklist.md` (repositorio con CI, hardware estable para las cifras).

## Fase 0 — antes del RC (pendiente de decisiones)
- ~~Licencia aplicada~~ hecho: MIT. Nombre: no hay otro proyecto de logs llamado LatticeLog en GitHub (comprobado el 18-nov-2026); sin registro de marca, que no hace falta para un proyecto gratuito.
- Repositorio público, CI verde en Linux/macOS/Windows + AOT; SBOM. `SECURITY.md` existe: falta poner el canal
  privado real (P27-T09).
- `CONTRIBUTING.md` existe: falta el código de conducta y activar la revisión obligatoria en el repositorio (P27-T10).

## Fase 1 — RC público (P27-T13)
- Paquetes: NuGet (`LatticeLog`, `LatticeLog.Ingest`, …), PyPI (`latticelog`), Packagist (`latticelog/latticelog`),
  imagen del servidor. Todos `0.x.0-rc.N`.
- Documentación mínima publicada (P27-T02): README, formato `.llog`, protocolo de ingestión, API del servidor,
  lenguaje de consulta, CLI, MCP, despliegue, operación, compatibilidad.
- Guías de migración (P27-T03): desde `ILogger`/Serilog (el bridge `LatticeLog.Extensions.Logging` existe), desde
  OTLP (importador existe), Python `logging` (handler existe), PHP (PSR-3 pendiente).
- Demo de diagnóstico (P27-T05): búsqueda frente a Context Pack sobre Loghub-2.0, con sus límites.
- Benchmarks publicados (P27-T06) **solo** con hardware, versiones, datasets y scripts; repetidos en máquina estable.

## Fase 2 — estable (P27-T14)
- Al menos 2 semanas de RC sin bloqueantes abiertos (corrupción, fuga entre tenants, ACK falso, caída no
  recuperable: P26-T16).
- Notas de versión y guía de actualización; plan de vuelta atrás del día de lanzamiento (`docs/operations/runbook.md`).

## Métricas tras el lanzamiento (P27-T15)
Activación (primer evento recibido), registros ingeridos, consultas, retención de desarrolladores a 30 días y fallos de
onboarding. Requieren telemetría **opcional y declarada**; el producto no envía nada por defecto.

## Landing (P27-T12)
Solo afirmaciones respaldadas por `reports/`: ratios de compresión medidos, ACK confirmado por fsync, lectura sin
pérdida y evidencias verificables. Nada de "N veces más rápido" sin la máquina y el dataset al lado.
