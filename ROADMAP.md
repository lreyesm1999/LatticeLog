# ROADMAP

Actualizado 2026-11-18. Fuente de verdad del estado por fase; detalle en `docs/decisions/PHASE-XX-REVIEW.md`.
Estados: PASS · CONDITIONAL (gate cumplido con pendientes explícitos) · NOT PASSED · NOT STARTED.

| fase | título | estado |
|---|---|---|
| P00 | Visión, alcance, requisitos y gobierno | CONDITIONAL |
| P01 | Modelo semántico de eventos y contratos | PASS |
| P02 | Laboratorio, datasets y baselines (I+D) | CONDITIONAL |
| P03 | SDK .NET y generador de eventos | CONDITIONAL |
| P04 | Especificación binaria .llog v0.x | PASS |
| P05 | Pipeline de ingestión, WAL y escritura duradera | CONDITIONAL |
| P06 | Lector, reconstrucción exacta y compatibilidad | PASS |
| P07 | Lattice-S: codificación estructural (I+D) | PASS |
| P08 | Lattice-C: herencia de contexto (I+D) | CONDITIONAL |
| P09 | Lattice-P: predicción (I+D) | PASS (resultado negativo) |
| P10 | Planificador adaptativo y compactación | CONDITIONAL |
| P11 | Índices, consultas y recuperación selectiva | PASS |
| P12 | Lectura AI-friendly sin IA: evidencias | CONDITIONAL |
| P13 | Interoperabilidad OpenTelemetry e importadores | CONDITIONAL |
| P14 | SDK .NET listo para producción (Local v1.0) | NOT PASSED |
| P15 | CLI y herramientas locales | CONDITIONAL |
| P16 | Protocolo de ingestión remota y collector | NOT STARTED |
| P17 | Servidor de consultas y multiusuario | NOT STARTED |
| P18 | Seguridad, privacidad y auditabilidad | NOT STARTED (controles locales parciales) |
| P19 | Escalabilidad horizontal y replicación | NOT STARTED |
| P20 | Almacenamiento por niveles y object storage | NOT STARTED |
| P21 | SDKs Java, Go, Python y Node.js | NOT STARTED |
| P22 | Adaptador MCP de solo lectura | CONDITIONAL |
| P23 | Consola web | NOT STARTED |
| P24 | Operación, SRE, backup y DR | NOT STARTED |
| P25 | Producto comercial, licencias y SaaS | NOT STARTED |
| P26 | Calidad, caos y certificación interna | NOT STARTED (prácticas transversales iniciadas) |
| P27 | Lanzamiento, documentación y soporte | NOT STARTED (documentación inicial) |
| P28 | Mantenimiento y evolución | NOT STARTED |

## Current
- Cerrar pendientes de M1/M3 para Local v1.0 (P14). Hecho el 18-nov-2026: pruebas de caída con proceso real, anillo
  y pool en la ruta habilitada (56 ns / 5 B), análisis de trimming/AOT activo y limpio, paquetes NuGet locales,
  benchmark de latencia a tasa fija. Falta: CI Linux/macOS/ARM64 + Native AOT (requiere remoto), causa de los
  bloqueos del hilo escritor en Balanced a 500k/s, licencia, firma, N-1 y RC (`docs/decisions/PHASE-14-REVIEW.md`).
- ~~Mitigar P04-R1~~ hecho en el formato 0.2 (copia comprimida de catálogos en el footer).

## Next
- P16 protocolo de ingestión remota (ADR-0009) y P17 servidor single-node.
- Evaluación externa AI-friendly (P12-T17..T19) con modelo externo y revisión humana.
- Datasets reales propios en el laboratorio (Loghub-2.0 ya medido: `reports/loghub2-v1.md`).

## Later
- P18 seguridad de servidor, P19–P20 escala y tiers, P21 SDKs, P23 consola, P24 SRE, P25–P27 comercial y lanzamiento.

## Blocked (requieren decisión o recursos humanos)
- Licencia del proyecto y ediciones (P25-T03): decisión del responsable + asesoría legal.
- Marca "LatticeLog", dominios (P00-T14, P27-T11): verificación jurídica.
- Presupuesto/equipo (P00-T15), entrevistas con usuarios (P23-T01, P25-T02), pentest (P18-T16).
