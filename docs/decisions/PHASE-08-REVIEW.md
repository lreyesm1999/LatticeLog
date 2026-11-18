# PHASE-08-REVIEW — Lattice-C: herencia de contexto (I+D)

- Estado: **CONDITIONAL**
- Fecha y versión/commit: 2026-11-18 · 0.1.0-alpha.1 (working tree, sin commits)
- PRs e issues cerrados: ninguno (no existe repositorio remoto ni tracker; IDs de tarea según docs/PLAN-MAESTRO.md)
- Entregables verificados: CodecId.Contextual (tabla de trazas por bloque, bit de herencia de contexto) + snapshots de contexto internados por segmento
- Evidencia de tests: propiedades + golden
- Evidencia de benchmarks: C vs S sin deflate −3 % a −21 %; con deflate 0–3,5 %
- Seguridad/compatibilidad: no se infieren contextos no referenciados
- Hipótesis aceptadas/refutadas: ahorro neto positivo pero pequeño tras compresión general
- Limitaciones abiertas: T05 tasa de reutilización en apps reales no medida; T06 compactación del grafo de contextos no aplica aún
- ADRs nuevas: ver docs/decisions/ADR-*.md
- Riesgos residuales: R02
- Decisión: mantener como opción del planificador, no por defecto
