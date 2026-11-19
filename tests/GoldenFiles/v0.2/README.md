# Golden files `.llog` v0.2 (congelados)

Generados por `tests/LatticeLog.Tests/GoldenFileTests.cs` con `LATTICELOG_REGENERATE_GOLDEN=1` (solo se regenera la
versión actual; esta carpeta y `v0.1/` están congeladas y deben seguir decodificándose).

Mismos 27 registros y mismas variantes que `v0.1/` (ver su README), con el formato 0.2:
- el footer contiene la copia comprimida de los catálogos de esquemas y contextos (bit 0 de `optional_features`);
- en las variantes `-deflate` los bloques de catálogo también están comprimidos;
- `corrupt-bitflip.llog` invierte un bit en el payload del primer bloque de esquemas: el lector debe reportar el CRC
  **y devolver los 27 registros** usando la copia del footer (en v0.1 el mismo daño hacía perder todos los registros);
- `corrupt-truncated.llog` corta el último bloque de registros (sin footer): debe reportarse la cola cortada.

Verificación: `dotnet test` y `python -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2`.
