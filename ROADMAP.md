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
| P16 | Protocolo de ingestión remota y collector | CONDITIONAL (cuerpo binario, multi-productor; falta red real y TLS probado) |
| P17 | Servidor de consultas y multiusuario | CONDITIONAL (admin en línea y retención hechas; falta imagen construida y otro hardware) |
| P18 | Seguridad, privacidad y auditabilidad | NOT PASSED (auditoría, borrado, freno, cabeceras; falta cifrado en reposo y pentest) |
| P19 | Escalabilidad horizontal y replicación | NOT PASSED (réplica asíncrona + failover manual; sin clúster) |
| P20 | Almacenamiento por niveles y object storage | NOT STARTED |
| P21 | SDKs en otros lenguajes | CONDITIONAL (Python y PHP; Java/Go/Node fuera de alcance por ahora) |
| P22 | Adaptador MCP de solo lectura | CONDITIONAL |
| P23 | Consola web | CONDITIONAL (consola mínima segura; sin usuarios ni E2E en navegador) |
| P24 | Operación, SRE, backup y DR | CONDITIONAL (métricas, backup verificado, runbooks; sin ensayos reales) |
| P25 | Producto comercial, licencias y SaaS | NO APLICA (decisión: proyecto gratuito con licencia MIT; `docs/product/commercial-decisions.md`) |
| P26 | Calidad, caos y certificación interna | NOT PASSED (prácticas activas; sin CI ni hardware estable) |
| P27 | Lanzamiento, documentación y soporte | CONDITIONAL (repositorio público con licencia MIT; faltan CI ejecutado y paquetes) |
| P28 | Mantenimiento y evolución | NOT STARTED (política en borrador) |

## Current
- Cerrar pendientes de M1/M3 para Local v1.0 (P14). Hecho el 18-nov-2026: pruebas de caída con proceso real, anillo
  y pool en la ruta habilitada (56 ns / 5 B), análisis de trimming/AOT activo y limpio, paquetes NuGet locales,
  benchmark de latencia a tasa fija. Falta: CI Linux/macOS/ARM64 + Native AOT (requiere remoto), causa de los
  repetir la medición de latencia en otro disco y en Linux (causa de los bloqueos ya identificada y mitigada),
  firma, N-1 y RC (`docs/decisions/PHASE-14-REVIEW.md`).
- ~~Mitigar P04-R1~~ hecho en el formato 0.2 (copia comprimida de catálogos en el footer).

## Next
- Pendientes de P16–P17: red real y TLS probados entre máquinas, imagen Docker construida.
- P18: cifrado en reposo, canal de vulnerabilidades, pentest externo (`docs/security/pentest-scope.md`).
- P24: restaurar un backup en otra máquina y ejecutar los runbooks.
- **Plataforma**: el equipo de referencia corrompe memoria bajo carga multinúcleo (BIOS/microcódigo antiguos); repetir las mediciones en hardware estable.
- Evaluación externa AI-friendly (P12-T17..T19) con modelo externo y revisión humana.
- Datasets reales propios en el laboratorio (Loghub-2.0 ya medido: `reports/loghub2-v1.md`).

## Later
- P19 clúster (tras medir el límite de un nodo en hardware estable), P20 tiers/object storage, SDKs Java/Go/Node,
  P23 consola completa con usuarios, P25–P27 comercial y lanzamiento (tras las decisiones bloqueadas).

## Blocked (requieren decisión o recursos humanos)
- Marca "LatticeLog", dominios (P00-T14, P27-T11): verificación jurídica.
- Presupuesto/equipo (P00-T15), entrevistas con usuarios (P23-T01, P25-T02), pentest (P18-T16).
