# Changelog

## Sin publicar — proyecto público con licencia MIT

- Licencia **MIT** (`LICENSE`): el proyecto es gratuito y abierto, sin ediciones comerciales. Metadatos de paquete
  (`PackageLicenseExpression`, repositorio) en `Directory.Build.props`, `pyproject.toml` y `composer.json`.
- Limpieza para publicar: cero avisos de compilación (orden de `CancellationToken`, `Dispose` del stream de envoltura,
  mensaje del diagnóstico LL0007) y `.gitignore` que ya no oculta `sdks/php/bin/`.

## Sin publicar — operación, seguridad, réplica y SDKs (P17–P24)

- **Retención** por antigüedad o tamaño: segmentos sellados completos, el más antiguo primero; se anota en el journal
  antes de borrar y nunca toca el segmento activo. CLI `latticelog retention` y `POST /v1/admin/retention`.
- **Administración en línea** sin parar la ingestión: compactación, verificación, **borrado de privacidad**
  (`/v1/admin/erase`, reescribe sin dejar rastro y los reenvíos no resucitan datos) y **backup en caliente** verificado
  (`/v1/admin/backup`, `latticelog backup`).
- **Ingestión más rápida**: cuerpo binario (`body_encoding = 2`, mini segmento Lattice-S), 2,0–3,4× frente a JSONL
  con 4 CPUs; decodificación fuera del hilo escritor. Vuelta automática a JSONL si el servidor responde 415.
- **Forwarder** incremental (`StoreTail`): no relee el segmento activo en cada sondeo.
- **Seguridad**: auditoría (`data/audit/*.jsonl`, CRC por línea) de admin, lecturas y fallos de autenticación; freno
  de 20 fallos/min por dirección; cabeceras `nosniff`/`no-referrer`; 500 JSON sin detalles internos; `server.key` 0600.
- **Réplica asíncrona** (`--replicate-to`): un forwarder por namespace; la réplica deduplica tras un failover de los
  productores usando la procedencia.
- **Operación**: `GET /metrics` (Prometheus), consola web en `/ui` (CSP estricta, sin HTML de los logs), runbooks.
- **SDKs**: Python (`logging`, spool duradero, envío en segundo plano) y PHP (spool compartido entre procesos con
  `flock`, envío al final de la petición).
- **Consultas**: `attributes.<nombre>` es alias de `attr.<nombre>`. Un segmento borrado durante una consulta aparece
  como problema en la cobertura en lugar de ignorarse.
- Corregido antes de publicar: un reintento del forwarder incremental podía declarar un hueco falso y perder datos.

## Sin publicar — servidor de consultas v1 (P17) y carga multi-productor

- `latticelog-server` (sustituye a `latticelog-collector`):
  - **namespaces** `tenant/proyecto/entorno`, con un almacén aislado por namespace;
  - **tokens** con scopes `ingest`/`read`/`admin` y namespaces permitidos.
- API de lectura (`specs/server-api.md`):
  - búsqueda con cobertura y cursores **firmados con HMAC y con caducidad**;
  - evento, relacionados, traza, estadísticas, overview, grupos de error, context pack y esquema;
  - exportación sin pérdida paginada;
  - catálogo de segmentos y salud con detalle para admin.
- Separación lectura/ingestión: semáforo de consultas, concurrencia por token, timeout y registros escaneados con
  resultado parcial explícito.
- Hora de llegada (`observed_time`) en el servidor y en el JSON de salida.
- `deploy/Dockerfile` (chiseled, no root; no construido) y `docs/deploy/single-node.md`.
- Medición con 1–64 productores: group commit de hasta 27 lotes por fsync y pico de ~600k registros/s con 4
  productores. Con productores y lectores a la vez, compiten por CPU (`reports/ingest-v1.md` §3–§4).
- **Hallazgo de plataforma**: el equipo de referencia (i9-14900KF, microcódigo 0x11D) corrompe memoria bajo carga en
  muchos núcleos, también sin LatticeLog (`tools/platform-stress`). Las mediciones con concurrencia se hacen con CPUs
  limitadas.

## Sin publicar — ingestión remota v1 (P16)

- Protocolo v1 (`specs/ingest-protocol.md`, ADR-0009): HTTP `POST /v1/ingest`, envelope binario de 48 bytes
  (productor, rango de secuencias locales, número de registros, codificación, compresión, CRC-32) y cuerpo
  `latticelog.jsonl` opcionalmente con gzip.
- `LatticeLog.Ingest`: `IngestClient` y `Forwarder`. El almacén local es el spool y hay un cursor por endpoint.
  Reintentos con backoff exponencial, jitter y `Retry-After`; los lotes demasiado grandes se parten y los huecos se
  resincronizan.
- `LatticeLog.Server` + `latticelog-collector`:
  - **deduplicación** por (productor, secuencia local), reconstruida desde los datos guardados tras una caída;
  - **group commit** con el nivel de ACK realmente alcanzado y contexto de procedencia `ingest`;
  - **acceso**: tokens con hash SHA-256 y rotación en caliente, cuotas por token (429), cola acotada (503);
  - **límites** de tamaño, descompresión, ratio y línea; TLS obligatorio fuera de loopback.
- Pruebas: red con pérdidas (petición y respuesta), caída del collector sin checkpoint, reglas de duplicado, solape y
  hueco, seguridad y límites, rotación y cuotas. Medición: ~180–217k registros/s y ACK p50 12–15 ms por lote de 5 000
  en loopback (`reports/ingest-v1.md`).

## Sin publicar — durabilidad en segundo plano

- **Causa de los bloqueos del hilo escritor encontrada**: picos de latencia del almacenamiento (fsync de hasta 5,3 s,
  sellados de hasta 3,8 s en un SSD al 98 %) y la regla de NTFS de que una escritura espera mientras se vacía el mismo
  archivo. Las pausas del GC quedan descartadas (≤ 6,6 ms). Instrumentación nueva:
  `LatticeLoggerOptions.SlowWriterOperationThreshold`, `StoreWriterOptions.Diagnostics` y `latency --diag`.
- **Durabilidad en segundo plano** (`StoreWriterOptions.BackgroundDurability`, activa por defecto): el sellado de
  segmentos rotados y el fsync periódico de Balanced pasan a un hilo propio del almacén (FIFO, peticiones fusionadas,
  fallo pegajoso). `StoreWriter.DurableSequence`, `RequestBackgroundFsync` y `PendingDurabilityWork` son nuevos.
  `IRecordSink.RequestBackgroundFsync` y `IRecordSink.DurableSequence` tienen implementación por defecto.
  `SegmentWriter` escribe con `RandomAccess`.
- Resultado: 1 productor a 500k/s pasa de 4/12 corridas con bloqueo a 0/12. Con 4 productores siguen los bloqueos
  cuando el disco tarda segundos en vaciar (`reports/latency-v1.md` §4).

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
  `dotnet tool` (`LatticeLog.Cli`). Todavía no publicados en NuGet.
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
