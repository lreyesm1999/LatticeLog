# Changelog

## Sin publicar — endurecimiento del SDK (P14)

- **Pruebas de caída real**: un proceso hijo (`tests/LatticeLog.CrashHarness`) se mata a mitad de escritura 3 veces
  por modo (Fast/Balanced/Durable) con rotación de segmentos; ningún registro confirmado se pierde, el almacén queda
  como prefijo sin huecos ni duplicados y la recuperación es limpia.
- **Ruta caliente**: la cola pasa a un anillo acotado preasignado (`BoundedRing`) donde la posición es el ticket de
  confirmación (sin `SortedSet` en el hilo escritor); pool de registros (`LogRecordPool`) para eventos generados,
  `WriteEvent` y la API de texto libre; redacción sin asignaciones. Evento tipado: 189 → 56 ns y 417 → 5 B por
  evento; texto libre: 252 → 63 ns y 340 → 105 B (`reports/benchmarks-hotpath.md`).
- `IRecordSink.RetainsRecords` (por defecto `true`): un sink que no retiene registros permite reciclarlos.
  `StoreWriter` devuelve `false`.
- `QueueCapacity` se redondea a potencia de dos, el anillo se reserva al crear el logger (16 B por hueco) y el máximo
  baja de 100 M a 16 777 216.
- `BalancedFsyncBytes` (opcional, 0 = desactivado): fsync adicional por volumen en modo Balanced.
- **Latencia a tasa fija**: nuevo modo `latency` del proyecto de benchmarks (p50/p99/p99.9/máx exactos, tiempo de
  servicio y de respuesta respecto al horario). Hallazgo abierto: bloqueos de E/S del hilo escritor de 0,1–2 s en
  Balanced a 500k eventos/s en la máquina de referencia (`reports/latency-v1.md`).
- **Trimming/AOT**: el análisis `IsAotCompatible` nunca estaba activo (se evaluaba antes de conocer `TargetFramework`);
  movido a `Directory.Build.targets`, con avisos IL de trimming/AOT como errores en `src/`. Corregidos los usos de
  serialización JSON por reflexión en CLI, cursores de consulta (source generator, mismo JSON) y MCP. La CLI publicada
  con trimming funciona (verify, inspect, query con cursor, stats, context, MCP). Native AOT real queda en CI (Linux).
- **Paquetes NuGet** locales (9 paquetes + símbolos); `LatticeLog.Core` incluye el generador; la CLI como
  `dotnet tool` (`LatticeLog.Cli`). Sin licencia: no publicables todavía.
- **Loghub-2.0** (CC-BY-4.0) descargado con aprobación y verificado por MD5; nuevo experimento `loghub` del
  laboratorio (`reports/loghub2-v1.md`).
- Segmentos abiertos: analizado el riesgo de catálogo (un corte por caída nunca deja registros huérfanos, probado);
  sin cambio de formato (ADR-0006).

## Sin publicar — formato `.llog` 0.2

- **P04-R1 mitigado**: el footer de cada segmento sellado guarda una copia comprimida de los catálogos de esquemas y
  contextos; un bloque de catálogo corrupto ya no hace perder registros (sigue reportándose). Los lectores contrastan
  los bloques de catálogo con la copia.
- Los bloques de catálogo se comprimen con DEFLATE cuando el segmento usa compresión (en la carga `inventory`, con un
  contexto por operación, el tamaño total baja un 30 %).
- El acceso aleatorio a un bloque ya no necesita leer los bloques de catálogo anteriores.
- Compatibilidad: los archivos 0.1 se siguen leyendo (golden files `v0.1` congelados); un lector 0.1 trata un
  segmento 0.2 como no sellado y lo recorre secuencialmente.
- Lector Python independiente actualizado; `verify-golden` acepta varias carpetas.

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
