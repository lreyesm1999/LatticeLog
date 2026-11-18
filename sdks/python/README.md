# LatticeLog SDK para Python (P21, alpha)

Registros estructurados y tipados en un **spool local duradero**, enviados a `latticelog-server` en segundo plano
con el protocolo de ingestión v1. Solo biblioteca estándar (Python ≥ 3.10).

```python
import os, latticelog

log = latticelog.Logger("./spool", endpoint="https://logs:7400", token=os.environ["LATTICELOG_TOKEN"],
                        service="orders-api", namespace="acme/prod", durability="balanced")
with log.operation("checkout", operation_id="op-991"):
    log.error("payment failed for {order}", order=991, amount=19.99)
log.flush()   # espera a que el servidor confirme (fsync) todo lo escrito
log.close()
```

Con el módulo `logging`: `logging.getLogger().addHandler(latticelog.LatticeLogHandler(log))`.

- **Garantías**: cada registro recibe una secuencia local densa; el emisor reintenta con backoff y jitter, y el
  servidor deduplica por (productor, secuencia). Sin red, todo espera en el spool, también entre reinicios del
  proceso. Solo se borra lo que el servidor ha confirmado.
- **Durabilidad**: `fast` (búfer), `balanced` (flush por registro) o `durable` (fsync por registro).
- **Spool lleno** (`max_spool_bytes`): los registros nuevos se rechazan y se cuentan en `log.dropped`. Nunca se borra
  nada sin confirmar.
- **Tipos**: `int` → Int64 (o UInt64/decimal si no cabe), `float` → Double (NaN e infinitos incluidos), `bytes`,
  `datetime`, `timedelta`, `UUID`, `Decimal` (con su escala), `dict` (mapa ordenado), listas. Las cadenas con
  sustitutos sueltos se conservan exactas.
- **Limitaciones**: un solo proceso escritor por spool (para varios procesos, un spool por proceso o el SDK de PHP).
  No hay eventos con esquema declarado todavía: el nombre del evento va en `event=`.

Prueba de integración con un servidor real: `tests/LatticeLog.Tests/SdkTests.cs` (`tests/sdk/python_smoke.py`).
