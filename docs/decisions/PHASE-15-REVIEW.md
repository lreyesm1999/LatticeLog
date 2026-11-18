# PHASE-15-REVIEW — CLI y herramientas locales

- Estado: **CONDITIONAL**
- Fecha y versión/commit: 2026-11-18 · 0.1.0-alpha.1 (working tree, sin commits)
- PRs e issues cerrados: ninguno (no existe repositorio remoto ni tracker; IDs de tarea según docs/PLAN-MAESTRO.md)
- Entregables verificados: latticelog: init, inspect, query, trace, context, event, related, errors, overview, schema, stats, verify, repair, compact, export, import, tail, mcp
- Evidencia de tests: uso end-to-end sobre almacén de 80 k eventos
- Evidencia de benchmarks: verify 80 k registros en 165 ms
- Seguridad/compatibilidad: export exige --output/--stdout y no sobrescribe sin --force
- Hipótesis aceptadas/refutadas: un desarrollador puede crear, consultar, verificar y exportar sin servidor
- Limitaciones abiertas: T08 binarios self-contained/AOT; T09 instaladores firmados; T10 completado de shell; T11 CI de instaladores
- ADRs nuevas: ver docs/decisions/ADR-*.md
- Riesgos residuales: —
- Decisión: avanzar
