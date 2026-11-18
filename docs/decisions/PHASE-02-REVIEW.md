# PHASE-02-REVIEW — Laboratorio, datasets y baselines (I+D)

- Estado: **CONDITIONAL**
- Fecha y versión/commit: 2026-11-18 · 0.1.0-alpha.1 (working tree, sin commits)
- PRs e issues cerrados: ninguno (no existe repositorio remoto ni tracker; IDs de tarea según docs/PLAN-MAESTRO.md)
- Entregables verificados: research/LatticeLog.Lab (8 cargas deterministas, baselines JSONL/gzip/zstd/LZ4), scripts/reproduce.*, reports/baseline-v1.md
- Evidencia de tests: round trip SHA-256 en todas las filas; consultas consistentes en 3 métodos
- Evidencia de benchmarks: 100k eventos × 8 cargas × 3 reps, una máquina
- Seguridad/compatibilidad: sin datos sensibles en el repo
- Hipótesis aceptadas/refutadas: baselines medidos y reproducibles en la misma máquina
- Limitaciones abiertas: T01 Loghub-2.0 y T02 datos reales no descargados (requiere aprobación/licencia); T07 escala 10M/100M; T08 reproductor a tasa variable; T10 CLP; T15 intervalos de confianza/control térmico; T17 evaluación AI externa; G2 exige ejecución en otra máquina
- ADRs nuevas: ver docs/decisions/ADR-*.md
- Riesgos residuales: R14, R20
- Decisión: avanzar; repetir en Linux/segunda máquina antes de afirmaciones públicas
