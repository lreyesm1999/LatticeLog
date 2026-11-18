# Compatibilidad y versiones (P14-T10, P27)

## Qué se garantiza

| superficie | regla | cómo se comprueba |
|---|---|---|
| Formato `.llog` | Un lector lee la versión menor actual y **todas las anteriores de la misma mayor** (hoy 0.1 y 0.2). Una menor nueva solo añade elementos opcionales: un lector antiguo los ignora o lee el segmento de forma secuencial, nunca devuelve datos erróneos. | archivos de prueba congelados `tests/GoldenFiles/v0.1` y `v0.2`, leídos por el lector .NET y por el lector independiente en Python, en cada ejecución de la suite y del CI |
| Intercambio `latticelog.jsonl` v1 | Salida estable; se pueden añadir campos opcionales, nunca cambiar el significado de uno existente | pruebas de ida y vuelta (`EvidenceAndInteropTests`, exportación del servidor) |
| Protocolo de ingestión v1 | `body_encoding` 1 (JSONL) siempre aceptado; codificaciones nuevas por negociación (415 → el cliente vuelve a una aceptada) | `IngestTests.Forwarder_falls_back_to_jsonl_when_the_server_rejects_the_binary_body`, reglas de deduplicación con ambas codificaciones |
| API HTTP v1 (`/v1/...`) | Campos añadidos son compatibles; quitar o cambiar uno exige `/v2`. Los códigos `error` son estables. | `ServerApiTests` |
| Cursores | Opacos. Los del servidor caducan (1 h) y están firmados; los de la CLI se ligan al almacén y a la consulta. Ningún cliente debe interpretarlos. | pruebas de manipulación, préstamo y caducidad |
| API .NET pública | 0.x: puede cambiar entre menores, siempre anotado en `CHANGELOG.md`. Desde 1.0, semver estricto. | — |

## Cambios de este ciclo que afectan a usuarios (no publicados todavía)

- `latticelog-collector` pasa a `latticelog-server`. El almacén de cada namespace vive en `data/ns/<namespace>`. Un
  almacén creado con el collector de P16 se migra moviéndolo a `data/ns/default`; nunca se publicó.
- `QueueCapacity` máximo 16 777 216 (antes 100 M) y se redondea a potencia de dos.
- El JSON "friendly" añade `observed_time` cuando existe.
- El `Forwarder` envía el cuerpo binario por defecto; los servidores que no lo conozcan provocan la vuelta automática
  a JSONL.
- Nuevas superficies de la API v1 (añadidas, compatibles): `/v1/admin/*`, `/metrics`, `/ui`. Los almacenes de un
  namespace con réplica guardan `upstream` en `INGEST-CHECKPOINT.json` (campo opcional; las versiones anteriores lo
  ignoran).

## Política de obsolescencia

1. Lo que se va a retirar se marca en el CHANGELOG y en la documentación al menos **una versión menor antes**.
2. Mientras tanto la versión anterior sigue funcionando: lectores, codificaciones y `/v1` conviven.
3. Los formatos de datos persistentes (`.llog`, MANIFEST, STORE.json) **nunca** dejan de leerse dentro de la misma
   versión mayor. Una mayor nueva incluye una herramienta de migración.
