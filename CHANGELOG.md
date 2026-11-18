# Changelog

## 0.1.0-alpha.1 — 2026-11-18 (sin publicar)

Primera versión del motor local.

- Modelo de eventos (valores tipados con fidelidad exacta, contextos inmutables, trace context, excepciones).
- Logger: cola MPSC acotada, hilo escritor con group commit, modos Fast/Balanced/Durable, ACK verificable,
  políticas de saturación, contadores de toda pérdida, redacción antes de persistir, scopes/operaciones, `Activity`.
- Generador Roslyn de eventos tipados con diagnósticos LL0001–LL0007 y lock file de esquemas.
- Formato `.llog` v0.1 (especificado en `specs/llog-format-v0.md`), códecs Raw, Lattice-S y Lattice-C, DEFLATE opcional,
  índice en footer con bloom filter, golden files congelados y lector independiente en Python.
- Almacén local: rotación, cuota sin borrado automático, lock de un solo escritor, journal, recuperación de colas
  cortadas, cuarentena, compactación atómica y reanudable con verificación registro a registro.
- Consultas (DSL tipado, poda por índices, cursores estables, EXPLAIN, agregaciones desde índices).
- Evidencias `latticelog.evidence.v1`: overview, trace, related, error groups, Context Pack con presupuesto y cobertura, Markdown seguro.
- Interoperabilidad: JSONL de intercambio sin pérdida, OTLP/JSON (export/import con informe de pérdidas), JSONL
  estructurado (CLEF/pino), texto byte-exacto; bridge `Microsoft.Extensions.Logging`.
- CLI `latticelog` (18 comandos) y servidor MCP de solo lectura por stdio.
