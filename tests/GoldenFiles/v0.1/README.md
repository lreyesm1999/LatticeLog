# Golden files `.llog` v0.1 (congelados)

Generados por `tests/LatticeLog.Tests/GoldenFileTests.cs` (`LATTICELOG_REGENERATE_GOLDEN=1`). **No modificar**:
una versión futura del formato añade una carpeta nueva; estos archivos deben seguir decodificándose (compatibilidad N-1/N-2).

| archivo | contenido |
|---|---|
| `raw.llog`, `raw-deflate.llog` | códec 0 sin/con DEFLATE |
| `structural.llog`, `structural-deflate.llog` | Lattice-S |
| `contextual.llog`, `contextual-deflate.llog` | Lattice-C (tabla de trazas, herencia de contexto) |
| `expected.llog.jsonl` | los 27 registros esperados en el formato de intercambio sin pérdida |
| `corrupt-truncated.llog` | segmento cortado dentro del último bloque de registros (sin footer): lector debe reportar cola cortada |
| `corrupt-bitflip.llog` | un bit invertido en el payload del bloque de esquemas: lector debe reportar el CRC y los bloques dependientes |

Cobertura de los registros: todos los tipos de valor (incluidos `NaN` con payload, `-0.0`, `ulong.MaxValue`,
`decimal.MaxValue`, strings con surrogates no emparejados, bytes, arrays y maps anidados con claves duplicadas),
eventos con esquema (atributos con forma exacta y con tipos discrepantes), cadenas de contexto de 3 niveles,
trace/span/operation/correlation ids, excepciones anidadas con `AggregateException`, timestamps desordenados y negativos,
flags de redacción e importación raw.

Verificación: `dotnet test` (lector .NET, más re-codificación byte a byte de las variantes sin compresión) y
`python -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1` (lector independiente).
