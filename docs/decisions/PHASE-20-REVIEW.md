# PHASE-20-REVIEW — Almacenamiento por niveles y object storage

- Estado: **NOT STARTED**
- Fecha: 2026-11-18
- Base disponible: segmentos inmutables una vez sellados, manifest con journal, retención por antigüedad y tamaño
  (`Retention.cs`), backup verificado. Es la base para `hot`/`warm`; no hay `cold` ni object storage.
- Decisión: depende de P19-T01 y de demanda real de retención larga.
