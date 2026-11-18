# PHASE-14-REVIEW — SDK .NET listo para producción (Local v1.0)

- Estado: **NOT PASSED** (avance sustancial; quedan criterios que exigen CI multiplataforma, licencia y RC)
- Fecha y versión/commit: 2026-11-18 · sobre `a10d1f9` (formato 0.2) + cambios de esta revisión
- PRs e issues cerrados: ninguno (no existe repositorio remoto ni tracker; IDs de tarea según docs/PLAN-MAESTRO.md)

## Hecho y verificado en esta revisión

| criterio | evidencia | resultado |
|---|---|---|
| Durabilidad demostrada con caída real | `tests/LatticeLog.Tests/CrashTests.cs` + `tests/LatticeLog.CrashHarness` | 3 modos × 3 kills con rotación: 0 registros confirmados perdidos, prefijo sin huecos ni duplicados, recuperación limpia |
| Catálogos de segmentos abiertos | `DurabilityTests.Torn_tail_never_orphans_records_from_their_catalog`, ADR-0006 | un corte nunca deja registros sin catálogo; riesgo residual acotado y documentado |
| Asignaciones de la ruta habilitada | `reports/benchmarks-hotpath.md` | tipado 56 ns / 5 B (antes 189 ns / 417 B); texto libre 63 ns / 105 B |
| Concurrencia del anillo | `LoggerTests.Ring_with_many_producers_...`, `Pooled_records_...` | 8 productores en un anillo de 16 huecos: cada registro una vez y en orden por productor; el reciclaje no corrompe datos ni secretos redactados |
| Latencia a tasa fija | `benchmarks/LatticeLog.Benchmarks/FixedRate.cs`, `reports/latency-v1.md` | medida; hallazgo abierto de bloqueos del hilo escritor (ver abajo) |
| Trimming/AOT | `Directory.Build.targets`, publish recortado de la CLI | análisis activo y sin avisos; CLI recortada probada de punta a punta. Native AOT: solo en CI Linux (sin toolchain nativo local) |
| Paquetes | `dotnet pack` → 9 paquetes + snupkg; consumo desde proyecto limpio | generador incluido en `LatticeLog.Core`; eventos tipados funcionan desde el paquete |
| Datos públicos | Loghub-2.0, `reports/loghub2-v1.md` | importación de texto byte a byte verificada en 14 conjuntos |

Pruebas: 103 en Windows x64 (todas verdes, Debug y Release) + lector Python independiente sobre los golden files v0.1 y v0.2.

## Pendiente para PASS

- CI en Linux/macOS/ARM64 y Native AOT real (job `aot-and-pack` preparado, nunca ejecutado: no hay remoto).
- Bloqueos del hilo escritor: **causa identificada** (latencia del almacenamiento, fsync de hasta 5,3 s en un SSD al
  98 %, más la espera de NTFS al escribir en el archivo que se está vaciando) y **mitigada** con durabilidad en segundo
  plano (1 productor a 500k/s: de 4/12 a 0/12 corridas con bloqueo). Con 4 productores persisten cuando el disco tarda
  segundos. Falta repetirlo en un disco con espacio libre y en Linux, y decidir si separar codificación y E/S.
- Licencia (bloquea publicar paquetes), firma de paquetes, SourceLink con el repositorio remoto, SBOM en CI.
- Compatibilidad N-1 y release candidate.
- Pruebas de pérdida de energía / caída del SO (requieren VM o hardware de inyección de fallos).

- Riesgos residuales: R04, R05; nuevo: bloqueos de E/S del escritor trasladados a productores con política `Block`.
- Decisión: no liberar v1.0.
