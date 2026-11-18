# PHASE-05-REVIEW — Pipeline de ingestión, WAL y escritura duradera

- Estado: **CONDITIONAL**
- Fecha y versión/commit: 2026-11-18 · 0.1.0-alpha.1 (working tree, sin commits)
- PRs e issues cerrados: ninguno (no existe repositorio remoto ni tracker; IDs de tarea según docs/PLAN-MAESTRO.md)
- Entregables verificados: LatticeLogger (cola MPSC acotada + group commit), StoreWriter (rotación, cuota, lock, journal), specs/durability.md
- Evidencia de tests: DurabilityTests: truncamiento en ~1 400 offsets, recuperación de cola cortada, segundo escritor rechazado, cuota; LoggerTests de saturación/errores
- Evidencia de benchmarks: ejemplo: 80 362 eventos en 626 ms (Balanced)
- Seguridad/compatibilidad: sin borrado automático; lock exclusivo
- Hipótesis aceptadas/refutadas: recuperación demostrada ante cortes simulados; garantías por modo documentadas sin prometer cero pérdida en RAM
- Limitaciones abiertas: T01/T02 comparación de colas (SPSC/ring) no medida; T10 no hay pruebas de kill -9 de proceso real ni corte de energía; T11 disco lleno real/permisos/readonly no probados (solo cuota); T13 SSD/HDD/NVMe no medidos; fsync de directorio en Linux pendiente
- ADRs nuevas: ver docs/decisions/ADR-*.md
- Riesgos residuales: R04, R05
- Decisión: avanzar con pruebas de crash de proceso real en P14/P26
