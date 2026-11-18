# Modelo de amenazas — motor local (P18-T01, alcance parcial)

Alcance: SDK, formato/lector, CLI y MCP local. Servidor, clúster, UI y cloud **no** están cubiertos (no existen).

| activo / superficie | amenaza | control implementado | pendiente |
|---|---|---|---|
| Datos del productor | secretos persistidos | redacción en el hilo escritor antes de persistir (campos `Secret`, claves sensibles), flag `redacted` | redacción en el hilo productor; política `hash`/`allow-list` |
| Archivos `.llog` | archivo malicioso o corrupto (parser) | CRC por cabecera/payload, límites de tamaño/expansión/profundidad/conteos, errores explícitos, 300 casos de fuzzing por bit-flip | fuzzing continuo con cobertura (SharpFuzz/libFuzzer) |
| Archivos `.llog` | manipulación intencional | ninguno (CRC no autentica) | firma/MAC de segmentos para servidor (P18-T04) |
| Directorio del almacén | dos escritores | lock exclusivo del SO | — |
| Directorio del almacén | permisos | heredados del SO | documentar permisos recomendados (0700) |
| CLI | exportación accidental masiva | `export` exige `--output` o `--stdout`, no sobrescribe sin `--force` | — |
| Consultas | agotamiento de recursos | límites de longitud, profundidad, IN, resultados, escaneo y tiempo | cuotas por usuario (servidor) |
| MCP / evidencias | inyección de instrucciones vía texto de logs | texto marcado como no confiable, fences Markdown seguros, sin herramientas de escritura | evaluación con agentes reales (P22-T13) |
| MCP | path traversal | almacén fijo al arranque, sin parámetros de ruta | — |
| Importadores | entradas gigantes / anidamiento | límites de bytes, registros, atributos y profundidad | fuzzing de importadores |
| Dependencias | cadena de suministro | dependencias mínimas en runtime (solo `System.IO.Hashing`); Zstd/LZ4 solo en el laboratorio | SBOM, SCA y secret scanning en CI (workflow definido, no ejecutado) |

Criptografía: no se inventa cifrado. El cifrado en reposo y TLS se delegarán a bibliotecas y mecanismos estándar (P18-T04).
