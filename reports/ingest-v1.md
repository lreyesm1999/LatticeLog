# Remote ingestion v1 — functional evidence and end-to-end measurement (P16-T13, P16-T14)

Fecha: 18-nov-2026 · Misma máquina que el resto de informes (i9-14900KF, Windows 11, .NET 10.0.15, SSD al 98 % de
ocupación). Collector y cliente en **loopback**: no hay red real de por medio.

## 1. Pruebas funcionales (`tests/LatticeLog.Tests/IngestTests.cs`, todas verdes 3 veces seguidas)

| prueba | qué se inyecta | qué se comprueba |
|---|---|---|
| red con pérdidas | 20 % peticiones perdidas, 20 % respuestas perdidas **después** de que el servidor guardó, 10 % con retraso; 12 000 registros en lotes de 700 | el servidor tiene cada registro **exactamente una vez**, en orden local, con su contexto original debajo del de `ingest` |
| caída del collector | se detiene sin checkpoint a mitad; el cliente pierde además su cursor y reenvía todo desde 1 | la deduplicación se reconstruye a partir de los datos y el resultado final tiene 6 000 registros, ninguno dos veces |
| reglas de deduplicación | lote repetido, lote solapado, hueco, hueco declarado | `duplicate` / `partial_duplicate` / 409 `sequence_gap` / `gap_before` |
| seguridad y límites | sin token, token erróneo, productor no permitido, CRC alterado, codificación desconocida, bomba gzip (4 MB de espacios), petición demasiado grande | 401 / 401 / 403 / 400 / 415 / 413 / 413, y el servidor sigue sano después |
| rotación y cuotas | se cambia el fichero de tokens en caliente; token con 100 registros/s | el token viejo da 401 y el nuevo 200, sin reiniciar; 429 con `Retry-After` |
| HTTP en una dirección pública | `http://0.0.0.0` sin `AllowInsecureHttp` | se niega a arrancar |

## 2. Rendimiento de extremo a extremo (`benchmarks … -- ingest`)

300 000 eventos tipados (4 campos), lotes de 5 000, **un lote en vuelo** (como el forwarder), con calentamiento
descartado. Dos corridas:

| cuerpo | ACK | registros/s | MB/s en el cable | bytes/registro | ACK p50 ms | ACK p99 ms |
|---|---|---:|---:|---:|---:|---:|
| jsonl+gzip | FsyncConfirmed | 195 277 / 191 984 | 5,7 | 29,2 | 13,9 / 14,8 | 48,9 / 43,7 |
| jsonl | FsyncConfirmed | 179 028 / 181 828 | 42 | 233,4 | 14,9 / 14,0 | 66,7 / 45,3 |
| jsonl+gzip | WrittenToOs | 216 728 / 215 191 | 6,3 | 29,2 | 12,3 / 12,6 | 42,8 / 39,8 |
| jsonl | WrittenToOs | 208 701 / 216 242 | 49 | 233,4 | 13,1 / 12,2 | 49,1 / 40,8 |

Lectura:
- **gzip conviene**: el tráfico baja 8× (29 frente a 233 bytes por registro) sin coste medible de throughput.
- Pedir fsync al servidor cuesta ~10 % de throughput con lotes de 5 000. El *group commit* lo amortiza cuando hay
  varios productores, pero eso **no se midió**.
- El techo de ~200k registros/s con un lote en vuelo lo marca la codificación y decodificación JSONL más el viaje de
  ida y vuelta, no el disco. Las mejoras pendientes serían un cuerpo binario `.llog` (el campo `body_encoding` ya lo
  admite) y varios lotes en vuelo por productor (exigiría reordenar en el servidor).
- **No medido**: red real con latencia y pérdidas a nivel TCP, TLS, memoria del servidor aislada bajo carga
  (los productores concurrentes están en §3), y la lectura incremental del segmento activo en el forwarder. Hoy el forwarder relee los
  bloques del segmento abierto en cada sondeo; con segmentos activos grandes y tasas altas eso costará CPU.

## 3. Muchos productores a la vez (18-nov-2026, segunda medición)

`benchmarks … -- ingest --affinity FF --records 320000 --producers 1,4,16,64 --batch 1000`: P productores con ids
distintos y un lote en vuelo cada uno, el mismo volumen total, gzip y ACK fsync. Los envelopes se codifican antes de
medir, así que se mide el collector (transporte, decodificación, deduplicación y group commit), no los clientes.
Cada corrida comprueba que el servidor guardó exactamente todos los registros. Proceso fijado a **8 CPUs lógicas**
(ver la nota sobre el equipo). Dos corridas:

| productores | registros/s | ACK p50 ms | ACK p99 ms | ACK máx ms | lotes por fsync | memoria pico del proceso MiB |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 215 227 / 233 859 | 4,0 / 3,8 | 30,4 / 16,2 | 46 / 22 | 1,0 | 359 / 333 |
| 4 | **575 861 / 636 266** | 6,1 / 5,7 | 28,9 / 24,6 | 47 / 37 | 1,3 / 1,2 | 360 / 336 |
| 16 | 443 889 / 437 860 | 33,4 / 34,0 | 75,5 / 77,6 | 96 / 94 | 5,8 / 5,6 | 403 / 387 |
| 64 | 347 390 / 373 267 | 165,6 / 174,3 | 287,5 / 284,1 | 288 / 284 | 22,9 / 26,7 | 507 / 482 |

Lectura:
- **El group commit funciona**: con 64 productores, cada fsync confirma 23–27 lotes.
- **Pico con 4 productores** (~600k registros/s). Con más, el total baja y la latencia del ACK crece casi en
  proporción (cola en el único hilo escritor). Clientes y servidor comparten las 8 CPUs: no se separó cuánto es CPU de
  decodificación y cuánto es el escritor. Siguiente paso: perfilar el collector solo y paralelizar la decodificación
  y el envolvente de procedencia fuera del hilo escritor.
- La memoria pico incluye los envelopes precodificados y los registros de origen del propio benchmark, no solo el
  servidor.

### Nota sobre el equipo de referencia (afecta a todas las mediciones con muchos hilos)

Con 16 o más hilos ocupados, el proceso del benchmark se cayó de forma aleatoria: violación de segmento, *stack
overflow* dentro de `DateTimeOffset.TryParse` y excepciones imposibles dentro del parser JSON de .NET. Un **programa de
control sin código de LatticeLog** (gzip + JSON + fechas, `tools/platform-stress`: `dotnet run -c Release --project tools/platform-stress -- 16 30`) reproduce lo mismo con 16 hilos, y
funciona sin errores con 1, 4 y 8 hilos (decenas de millones de líneas comprobadas).

El equipo tiene un Intel Core i9-14900KF con **microcódigo 0x11D** y BIOS F4 del 11-09-2023 (Gigabyte Z790 AORUS
ELITE X WIFI7). Es anterior a las actualizaciones con las que Intel corrigió la inestabilidad de los Core de 13.ª/14.ª
generación (0x125 en adelante). Es la explicación más probable, **pero no está demostrada**.

Recomendación al propietario:
1. Actualizar la BIOS a la última versión del fabricante, que trae el microcódigo corregido.
2. Repetir el control.
3. Si sigue fallando, considerar la garantía extendida de Intel para ese problema.

Hasta entonces, las mediciones de esta máquina con muchos hilos se hacen fijando 8 CPUs, y los resultados con más
concurrencia deben repetirse en otro equipo o en CI.

## 4. Productores y lectores a la vez (P17-T16)

`benchmarks … -- ingest --records 320000 --producers 4,16 --batch 1000 --readers 0|4`, con el proceso fijado a **4
CPUs lógicas** (con 8 seguía apareciendo la inestabilidad del equipo de §3: una `AccessViolationException` y
`NullReferenceException` dentro de bucles de .NET; con 4 CPUs, 0 fallos en todas las corridas). Cada lector alterna
una búsqueda por atributo (`attributes.quantity = 3`, sin índice que la pode) y una agregación por evento sobre el
namespace que se está escribiendo. Dos corridas por configuración:

| productores | lectores | registros/s ingeridos | ACK p50 ms | ACK p99 ms | consultas | consulta p50 ms | consulta p99 ms |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 4 | 0 | 290 895 / 287 102 | 12,1 / 12,7 | 43,8 / 38,0 | — | — | — |
| 16 | 0 | 383 780 / 372 944 | 41,2 / 37,9 | 78,4 / 102,5 | — | — | — |
| 4 | 4 | 135 393 / 127 753 | 23,4 / 25,0 | 78,8 / 95,0 | 42 / 44 | 174 / 156 | 865 / 1 066 |
| 16 | 4 | 168 041 / 202 484 | 71,7 / 71,3 | 278,6 / 167,4 | 23 / 29 | 270 / 167 | 845 / 741 |

Lectura:
- Las lecturas no bloquean la ingestión (todas las consultas y todos los ACK terminaron), pero **compiten por CPU**:
  con 4 CPUs y 2 consultas a la vez (el valor por defecto, la mitad de las CPUs) el throughput de ingestión baja a la
  mitad. Si la ingestión es prioritaria, `QueryLimits.MaxConcurrentQueries = 1`. No hay todavía prioridades de CPU
  entre ambos caminos.
- Las consultas usadas recorren todo el namespace (sin poda por índice) mientras crece hasta 320 000 registros; las
  consultas por tiempo o severidad se podan por índice y son mucho más baratas (`reports/baseline-v1.md` §3).
- Esta prueba encontró un fallo real: errores inesperados de lectura salían como 500 sin rastro. Desde ahora se
  reportan por `ServerOptions.Diagnostics` (tipo, mensaje y pila en el servidor; al cliente solo tipo y mensaje).
