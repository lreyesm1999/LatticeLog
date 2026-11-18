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
- **No medido**: red real con latencia y pérdidas a nivel TCP, TLS, muchos productores concurrentes, memoria del
  servidor bajo carga, y la lectura incremental del segmento activo en el forwarder. Hoy el forwarder relee los
  bloques del segmento abierto en cada sondeo; con segmentos activos grandes y tasas altas eso costará CPU.
