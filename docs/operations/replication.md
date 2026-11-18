# Replicación entre servidores (P19, alcance actual)

Estado a 18-nov-2026. Lo que hay es **replicación asíncrona primario → réplica con failover manual de los
productores**. No es un clúster: no hay sharding, ni elección de líder, ni consenso, ni failover automático.

## Cómo funciona

- El primario arranca con `--replicate-to https://replica:7400` y `LATTICELOG_REPLICA_TOKEN` (un token `ingest` válido
  en la réplica para los namespaces replicados).
- Por cada namespace del primario corre un `Forwarder` (el mismo componente que usan las aplicaciones) que lee el
  almacén del namespace y lo envía al mismo namespace de la réplica con el protocolo de ingestión v1. Los namespaces
  nuevos se descubren cada 2 s.
- El cursor de cada namespace se guarda junto al almacén del primario. Tras un reinicio del primario se reanuda
  donde quedó; la réplica deduplica lo reenviado.
- **Procedencia**: la réplica guarda cada registro con dos capas de contexto `ingest` (la del primario y la del
  productor original). Con eso la réplica aprende hasta qué secuencia ha recibido de cada productor original, y lo
  conserva entre reinicios (`INGEST-CHECKPOINT.json`, campo `upstream`).

## Garantías

| situación | resultado |
|---|---|
| El primario confirma un lote a un productor | **No** espera a la réplica (asíncrona). Lo confirmado y aún no replicado está solo en el primario y en el spool del productor. |
| El primario cae y los productores pasan a la réplica | El productor reenvía desde su cursor (o desde el principio si lo perdió); la réplica descarta lo que ya tenía del primario y guarda el resto. **Nada duplicado, nada perdido** mientras el spool del productor conserve los datos. Probado en `ReplicationTests`. |
| La réplica cae | El primario sigue aceptando; el lag crece y se recupera al volver. |
| Borrado de privacidad (`/v1/admin/erase`) | **No se replica**. Hay que lanzarlo también en la réplica. |
| Retención / compactación | Cada servidor aplica la suya. |

## Supervisión

- `GET /v1/health` (token admin) incluye el estado por namespace: secuencia replicada, estado del forwarder y último
  error.
- `latticelog_replication_lag_records{namespace=…}` en `/metrics`. Alerta sugerida: lag > 0 sostenido 5 min, o
  creciendo durante 15 min.

## Failover manual (runbook)

1. Confirmar que el primario no vuelve pronto (si vuelve, no hay nada que hacer: se reanuda solo).
2. Cambiar el endpoint de los productores a la réplica (DNS o configuración). Sus `Forwarder` reenvían lo que les
   falte confirmar; la réplica deduplica.
3. Lo que el primario confirmó pero no llegó a replicar **solo se recupera** si sigue en los spools de los productores.
   Por eso la retención local de los productores debe cubrir al menos el tiempo de detección + cambio.
4. Cuando el antiguo primario vuelva, **no** arrancarlo como primario a la vez: hoy no hay fencing. Arrancarlo como
   réplica vacía (o tras copiar el almacén de la nueva primaria) y apuntar la replicación en el otro sentido.

## Lo que falta para hablar de clúster (P19/P20)

Fencing/épocas (T05), modelo de consistencia con ACK de réplica (T06–T07), shards y router (T02–T03), movimiento de
shards (T10), pruebas de partición y split-brain (T14), escalado medido a N nodos (T15), object storage por niveles
(P20). Ninguno está hecho; el plan pide antes medir los límites de un nodo (T01), y eso necesita hardware estable.
