# Golden files `.llog` v0.3 (actuales)

Generados por `tests/LatticeLog.Tests/GoldenFileTests.cs` con `LATTICELOG_REGENERATE_GOLDEN=1` (solo se regenera la
versión actual; `v0.1/` y `v0.2/` están congeladas y deben seguir decodificándose).

Mismos 27 registros y mismas variantes que `v0.2/` (ver su README), con el formato 0.3:
- el footer termina con la zona de extensiones (spec §7.4) y lleva la extensión 1, el resumen de cada bloque Records
  (severidades presentes, eventos y servicios como índices en las listas del footer); bit 1 de `optional_features`;
- con 8 registros por bloque hay 4 bloques Records: el primero tiene los 5 eventos (se codifica como mapa de bits) y
  los otros tres solo uno (se codifica como lista), así que las dos codificaciones de conjunto quedan cubiertas;
- `corrupt-bitflip.llog` y `corrupt-truncated.llog` se comportan como en `v0.2/`.

Verificación: `dotnet test` y
`python -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2 tests/GoldenFiles/v0.3`
(el lector Python recalcula los resúmenes a partir de los registros decodificados y los compara con los del footer).
