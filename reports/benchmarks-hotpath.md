# Producer hot path — BenchmarkDotNet (P03-T06, P03-T15)

Fecha: 18-nov-2026 · `benchmarks/LatticeLog.Benchmarks` · BenchmarkDotNet 0.15.8, toolchain in-process, 5 warmup +
15 iteraciones · Intel Core i9-14900KF, Windows 11 10.0.26300, .NET 10.0.15 x64 RyuJIT (x86-64-v3).
Sink nulo (sin E/S): mide productor + cola + hilo escritor (redacción incluida), no el disco.

| caso | corrida 1 (antes del cache de redacción) | corrida 2 (después) | asignado/op |
|---|---:|---:|---:|
| evento tipado, nivel deshabilitado | 1.79 ns | 1.29 ns | 0 B |
| MEL `LoggerMessage` deshabilitado (referencia) | 0.69 ns | 0.13 ns | 0 B |
| evento tipado habilitado (4 campos) | 315 ns | 185 ns | 414–422 B |
| texto libre + 2 atributos habilitado | 178 ns | 335 ns | 339–347 B |

Contadores del hilo escritor en la corrida (bucle cerrado, sin límite de tasa):

| caso | aceptados | escritos | descartados por saturación |
|---|---:|---:|---:|
| tipado, corrida 1 | 7.9 M | 3.9 M | 16.3 M |
| texto libre, corrida 1 | 12.1 M | 8.1 M | 12.0 M |
| tipado, corrida 2 | 18.7 M | 14.7 M | 29.5 M |
| texto libre, corrida 2 | 48.2 M | 46.4 M | **0** |

Interpretación:
- **Ruta deshabilitada**: 0 asignaciones, 1,3–1,8 ns. Cumple el objetivo (< 5 ns) pero es más lenta que MEL
  (dispatch por interfaz `ILatticeLogger.IsEnabled`). Mejora posible: exponer el nivel mínimo como campo para que el
  código generado compare sin llamada virtual.
- **Ruta habilitada**: 185–335 ns y ~340–420 B por evento (registro + array de atributos + nodos de la cola). La
  varianza entre corridas es grande porque el resultado mezcla el coste del productor con la dinámica de saturación:
  cuando la cola se llena, descartar es más barato que encolar; cuando el escritor mantiene el ritmo, los objetos
  sobreviven más y se promueven a Gen1/Gen2. **No cumple aún** un objetivo razonable de < 100 ns / < 200 B.
- **Hallazgo y corrección**: el hilo escritor comparaba cada clave contra ~15 patrones sensibles en cada registro. El
  cache por clave (`RedactionPolicy.IsSensitiveKey`) eliminó los descartes en el caso de texto libre (0 de 48 M).
- Toda pérdida quedó contada (`DroppedSaturation`); ningún descarte fue silencioso.

## Actualización P14 (18-nov-2026, misma máquina y configuración)

Tres cambios, medidos uno a uno con el mismo benchmark (`--filter '*Enabled*'`, sink nulo):

| paso | tipado (4 campos) | asignado | texto libre + 2 atributos | asignado |
|---|---:|---:|---:|---:|
| antes (cola `ConcurrentQueue` + `SortedSet` de tickets) | 189 ns | 417 B | 252 ns | 340 B |
| 1. anillo acotado preasignado (`BoundedRing`, posición = ticket) | 80 ns | 488 B | 68 ns | 280 B |
| 2. redacción sin enumerador (bucle por índice + caché de sensibilidad del descriptor) | 75 ns | 360 B | 67 ns | 280 B |
| 3. pool de registros (`LogRecordPool`) para eventos generados y API de texto libre | **56 ns** | **5 B** | **63 ns** | **105 B** |

Corrida final con los 4 casos: evento deshabilitado 1,12 ns / 0 B; MEL deshabilitado 0,12 ns; tipado habilitado
55,6 ns / 5 B (Gen0 0,0003 por 1000 operaciones); texto libre 63,1 ns / 105 B (el array `params` de quien llama).

Lectura honesta:
- El paso 1 subió las asignaciones medidas del tipado (417 → 488 B) aunque bajó el tiempo a menos de la mitad: antes
  dos tercios de los registros se **descartaban** por saturación y nunca llegaban al hilo escritor, así que lo que
  éste asignaba no se contaba entero. Con el anillo el escritor ya no se queda atrás (**0 descartes** en 193 M
  registros) y su coste aparece completo. Eso destapó el paso 2: `RedactionPolicy.Apply` recorría `IReadOnlyList`
  con `foreach`, un enumerador por atributo y registro.
- El pool solo recicla un registro cuando el sink declara `IRecordSink.RetainsRecords = false` (`StoreWriter` lo
  hace); cualquier otro sink o logger simplemente deja los registros al GC. Los arrays de atributos que pasa quien
  llama nunca se limpian ni se reutilizan (prueba `Pooled_records_are_recycled_without_corrupting_persisted_data`).
- Objetivo propuesto en la versión anterior (< 100 ns / < 200 B en la ruta habilitada): **cumplido** en ambos casos.
- Limitaciones: un hilo productor, sink nulo, una máquina Windows. El coste con disco real y bajo carga sostenida
  está en `reports/latency-v1.md`. Pendiente: Linux y ARM64.
