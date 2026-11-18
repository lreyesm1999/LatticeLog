# Operación de `latticelog-server` (P24)

Estado a 18-nov-2026, un nodo (más réplica opcional). Complementa `docs/deploy/single-node.md`.

## SLI y objetivos iniciales (P24-T01)

Objetivos de partida para un nodo; hay que revisarlos con datos reales y en hardware estable.

| SLI | medida | objetivo inicial |
|---|---|---|
| Disponibilidad de ingestión | % de `POST /v1/ingest` respondidos con 2xx o 409, sin contar los 429 por cuota | 99,9 % mensual |
| Latencia de ACK | p99 del ACK `fsync` por lote de 5 000 | < 250 ms |
| Durabilidad | lotes confirmados y no legibles tras una caída | 0 |
| Búsqueda | p95 de `/v1/search` sobre las últimas 24 h, ≤ 100 resultados | < 2 s |
| Lag de réplica | `latticelog_replication_lag_records` | 0 en régimen; < 60 s de datos tras un pico |
| Restauración | tiempo de restaurar un backup verificado | < 30 min por cada 100 GB (sin medir aún) |

## Métricas y alertas (P24-T02..T04)

Hay que rascar `/metrics` con un token admin (`Authorization: Bearer …`) cada 15–30 s. El servidor no escribe sus
propios logs en sus almacenes, así que no hay bucles.

| alerta | condición | acción |
|---|---|---|
| Namespace en fallo | `latticelog_ingest_failed == 1` | El namespace rechaza escrituras tras un error de disco. Ver *Disco lleno o error de E/S*. |
| Cola saturada | `latticelog_ingest_queue_depth` > 80 % de la capacidad durante 5 min | Mirar CPU y disco; bajar `MaxConcurrentQueries` si las consultas compiten; repartir namespaces. |
| Ingestión parada | `rate(latticelog_ingest_records_stored_total[10m]) == 0` con productores conocidos | Comprobar red y tokens (los 401 aparecen en el audit log como `auth.*`). |
| Réplica atrasada | lag > 0 durante 5 min y creciendo | Ver estado en `/v1/health`; comprobar la réplica y el token de réplica. |
| Auditoría perdiendo eventos | `increase(latticelog_audit_events_dropped_total[5m]) > 0` | Disco lento o lleno en `data/audit`. |
| Disco | espacio libre < 15 % (exportador del sistema) | Retención (`/v1/admin/retention`) o ampliar disco. El escritor se niega a escribir por debajo de `MinFreeDiskBytes`. |
| Fuerza bruta | muchos `auth.*` con `unauthorized` en el audit log | El freno (429 por IP) ya actúa; investigar el origen. |

## Backup y restauración (P24-T05, T06)

- **En caliente**: `POST /v1/admin/backup?ns=<namespace>` (requiere `--backup-dir`) o, en la máquina,
  `latticelog backup <data>/ns/<dir> <destino>`. Los segmentos sellados se copian tal cual; el activo, hasta su último
  bloque completo. La copia se verifica leyéndola (`verified: true`). Lo escrito después del backup no está en él.
- Incluye también `server.key` (firma de cursores; sin ella los cursores emitidos dejan de valer, nada más) y el
  fichero de tokens.
- **Restaurar**: servidor parado → copiar el directorio del backup a `data/ns/<dir>` → arrancar. La recuperación sella
  el segmento activo copiado y reconstruye la deduplicación desde los datos, así que los productores reenvían lo
  posterior al backup sin duplicados (si aún lo tienen en su spool).
- **Ensayo**: restaurar periódicamente en otra máquina y ejecutar `latticelog verify`. Probado en
  `BackupTests` (copia mientras el escritor sigue escribiendo, restauración y escritura posterior). **No ensayado en
  infraestructura nueva** (P24-T06 pendiente).
- **RPO/RTO**: sin réplica, el RPO es el intervalo entre backups, salvo lo que siga en los spools de los productores.
  Con réplica, el RPO es el lag de réplica en el momento del fallo. El RTO depende del tamaño y no está medido.

## Runbooks (P24-T09)

### Disco lleno o error de E/S
1. `latticelog_ingest_failed == 1` o 503 en ingestión. Los productores retienen en su spool.
2. Liberar espacio: retención en seco (`dry_run=true`) para ver qué se borraría, luego real. No borrar ficheros a mano.
3. Reiniciar el servidor: la recuperación revisa cada namespace y el namespace vuelve a aceptar.

### Corrupción
1. `POST /v1/admin/verify?ns=…` o `latticelog verify`. Las consultas ya informan `storage_issues` en la cobertura.
2. Un segmento con bloques corruptos se sigue leyendo hasta el daño; no se repara en sitio. Restaurar ese segmento
   desde un backup o la réplica con el servidor parado.

### Servidor caído
1. Arrancar de nuevo; la recuperación es automática (sella el segmento activo y reconstruye la deduplicación).
2. Si no arranca: revisar el log de diagnóstico en stderr. Con réplica, seguir el failover de
   `docs/operations/replication.md`.

### Token comprometido
1. Quitarlo del fichero de tokens (se recarga sin reiniciar). Emitir uno nuevo.
2. Buscar en el audit log las acciones de ese `token` desde la fecha sospechosa.
3. Si era admin o tenía lectura amplia: rotar `server.key` (invalida los cursores) y valorar si hay obligación de
   notificar.

### Privacidad: solicitud de borrado
1. `POST /v1/admin/erase?ns=…&where=…&dry_run=true` para contar; después sin `dry_run`.
2. Repetir en la réplica. Los backups anteriores **siguen conteniendo** los datos: o se borran según su propia
   caducidad, o se restauran, se borra y se vuelve a hacer el backup. Los spools de los productores caducan con su
   retención local.
3. El audit log guarda el filtro usado: puede contener el dato personal. Tratarlo en consecuencia.

### Actualización y vuelta atrás (P24-T10, T11)
1. Backup de cada namespace. Parar, sustituir el binario o la imagen y arrancar.
2. Vuelta atrás: el formato `.llog` de la misma mayor se lee en ambos sentidos (`docs/compatibility.md`). Un segmento
   escrito por la versión nueva con una función opcional nueva lo lee la anterior de forma secuencial. Si la nota de la
   versión dice lo contrario, restaurar el backup.

## Capacidad (P24-T16)

Medido solo en la máquina de referencia y con 4 CPUs (`reports/ingest-v1.md`): con cuerpo binario, de 470k a 561k
registros/s con 1 productor, 1,15M con 4 y 1,45M con 16. Con lectores a la vez, la ingestión baja aproximadamente a
la mitad. Estas cifras **no son una guía de dimensionamiento** hasta repetirlas en hardware estable.

## Pendiente

Dashboards listos para importar (T03), canary/rolling (T10), simulacros (T12), costes (T13), guardias y comunicación
(T14), Kubernetes (T15), DR regional (T08).
