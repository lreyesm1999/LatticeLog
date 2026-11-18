# Baseline v1 — storage, codecs, selective queries and P09 pre-study

Fecha: 18-nov-2026 · Generado con `research/LatticeLog.Lab` (`baseline --events 100000 --reps 3`) · Datos crudos:
`research/results/baseline-20261118-072257.{csv,md}` y `queries-20261118-072257.csv`.

**Entorno**: Intel Core i9-14900KF (32 hilos lógicos), 64 GiB RAM, Windows 11 (10.0.26300), .NET 10.0.15, Release,
GC de estación de trabajo, SSD local, caché de página caliente, un hilo. **Una sola máquina y una sola corrida**:
los resultados no son generalizables (regla maestra del plan).

**Datos**: 8 cargas sintéticas deterministas (semilla 1), 100 000 registros cada una. Ningún dataset público ni real
(Loghub-2.0 y datos propios: pendientes, ver `research/dataset-manifest.yaml`).

**Contrato de comparación**: todos los formatos *lossless* se verificaron por SHA-256 del JSONL canónico
reconstruido (`roundtrip ok` en 100 % de las filas). Los tamaños `.llog` son el archivo completo: cabecera, catálogos,
índice del footer (incluye bloom) y trailer. `text-lines` es solo referencia de escala (pierde información).

## 1. Tamaño total almacenado (bytes, menor es mejor)

| carga | JSONL | JSONL+zstd-3 | JSONL+zstd-19 | JSONL+gzip | llog-raw | llog-S | llog-S+deflate | llog-C+deflate | S+deflate vs zstd-3 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| http-api | 36.8 M | 5.68 M | **4.27 M** | 5.50 M | 16.4 M | 5.92 M | 4.69 M | 4.70 M | **−17 %** |
| inventory | 37.4 M | 5.00 M | **3.91 M** | 4.96 M | 13.5 M | 7.57 M | 5.70 M | 5.60 M | **+14 % (peor)** |
| batch | 25.7 M | 2.01 M | **1.19 M** | 2.03 M | 10.8 M | 5.40 M | 1.79 M | 1.79 M | −11 % |
| iot | 20.9 M | 0.97 M | **0.64 M** | 1.47 M | 8.69 M | 3.28 M | 0.92 M | 0.91 M | −6 % |
| queue | 27.5 M | 2.71 M | **1.90 M** | 2.64 M | 10.2 M | 4.54 M | 2.07 M | 2.01 M | −23 % (C: −26 %) |
| microservices | 33.5 M | 3.87 M | **2.87 M** | 3.87 M | 12.1 M | 6.56 M | 3.50 M | 3.49 M | −10 % |
| high-cardinality | 36.2 M | 11.27 M | **10.25 M** | 11.48 M | 16.1 M | 11.72 M | 11.12 M | 11.11 M | −1 % |
| adversarial-random | 39.7 M | 21.68 M | **21.12 M** | 21.80 M | 23.55 M | 23.67 M | 23.67 M | 23.87 M | **+9 % (peor)** |

Lectura honesta:
- **Lattice-S+deflate es más pequeño que JSONL+zstd-3 en 6 de 8 cargas (−1 % a −23 %)** y peor en 2 (`inventory`
  +14 %: UUIDs aleatorios por registro que zstd modela mejor en texto; `adversarial` +9 %: bytes aleatorios).
- **JSONL+zstd-19 es el más pequeño en todas las cargas**, pero tarda 9–16 s por cada 100 k registros (vs ~0,1 s de
  llog): sirve como cota de compresión offline, no como formato de ingesta en vivo (P02-T13).
- El objetivo aspiracional **≥30 % menos bytes que el mejor baseline aplicable NO se cumple** (mejor caso −26 % frente
  a zstd-3; frente a zstd-19 llog siempre ocupa más). Con índices incluidos el formato además permite lectura selectiva,
  que JSONL+zstd no tiene (§3); se publican ambas cosas por separado como exige §6.1 del plan.
- **Lattice-S vs binario simple (llog-raw)**: −40 % a −64 % sin compresión general en cargas repetitivas; ≈0 en
  `high-cardinality` y +0,5 % en `adversarial` (el diccionario no ayuda). Gate G7: **cumplido** para cargas repetitivas,
  con fallback correcto (DEFLATE se omite automáticamente cuando no reduce: `adversarial` raw = raw+deflate).
- **Lattice-C vs Lattice-S**: sin compresión general gana −3 % a −21 % (`queue`, `inventory`, `microservices`), pero
  tras DEFLATE la ventaja cae a 0–3,5 %. Gate G8: **ahorro neto positivo pero pequeño**; se mantiene como opción del
  planificador, no como valor por defecto.

## 2. Tiempos de codificación/decodificación

Los tiempos de `.llog` incluyen escribir y releer un archivo temporal y mostraron **varianza alta** (p. ej.
`high-cardinality/llog-structural+deflate` min 153 ms, mediana 645 ms, máx 714 ms; outliers probablemente de E/S o
antivirus). Con 3 repeticiones **no se consideran fiables**; se repetirán en Linux sin antivirus y con más repeticiones.
Además la columna de decodificación **no es comparable**: para `.llog` incluye materializar objetos y re-serializar a
JSONL (≈150–300 ms / 100 k), mientras que para JSONL+zstd solo mide descomprimir sin parsear. La comparación justa de
lectura es la de §3 (decodificar + filtrar).

## 3. Consultas selectivas (ms, mediana de 3; "segmentos" = leídos/total)

| carga | consulta | aciertos | llog indexado | segmentos | llog full scan | JSONL+zstd decode+parse+filter |
|---|---|---:|---:|---:|---:|---:|
| http-api | trace_id | 2 | 24.3 | 1/3 | 51.1 | 265 |
| http-api | level ≥ ERROR | 985 | 47.4* | 3/3 | 36.2 | 252 |
| http-api | time 1 % | 1 006 | 1.5 | 1/3 | 36.4 | 255 |
| inventory | trace_id | 5 | 25.5 | 1/3 | 66.2 | 294 |
| inventory | time 1 % | 994 | 1.9 | 1/3 | 56.9 | 370 |
| microservices | trace_id | 3 | 26.9 | 1/2 | 35.9 | 220 |
| high-cardinality | trace_id | 1 | 17.3 | 1/5 | 60.5 | 238 |
| high-cardinality | time 1 % | 988 | 1.9 | 1/5 | 70.1 | 224 |
| batch/iot/queue | level ≥ ERROR (0 aciertos) | 0 | ≈0.0 | 0/1 | 19–23 | 180–192 |

Todas las consultas devolvieron los mismos resultados en los tres métodos (`consistent`). Hallazgos:
- Un escaneo completo de `.llog` es **4–9× más rápido** que descomprimir y parsear JSONL+zstd con el mismo filtro.
- Los índices de tiempo y severidad dan **20–40×** sobre el full scan cuando podan; el bloom de `trace_id` poda
  segmentos (1/3, 1/5) pero el bloque se sigue decodificando (sin índice por bloque de trace).
- *Regresión detectada*: con poda solo a nivel de segmento (`errors`), la lectura aleatoria por bloque era más lenta
  que la secuencial (47 vs 36 ms). **Corregido** tras la medición: si ningún bloque es podable se lee secuencialmente
  (cambio en `QueryEngine.Blocks`, sin repetir aún la medición).

## 4. Estudio previo de Lattice-P (gate G9)

Longitudes de código adaptativas (aprenden el modelo en línea, incluyen el coste de aprenderlo): orden-0 vs orden-1
(evento anterior como contexto) para la identidad del evento, y Δt vs ΔΔt (µs) para timestamps.

| carga | evento bits/reg o0 | o1 | Δt bits/reg | ΔΔt bits/reg | ahorro optimista | % de S+deflate |
|---|---:|---:|---:|---:|---:|---:|
| http-api | 0.081 | 0.081 | 11.47 | 12.69 | 0 B | 0.00 % |
| inventory | 0.178 | 0.178 | 12.18 | 13.71 | 3 B | 0.00 % |
| batch | 0.069 | 0.035 | 7.65 | 8.44 | 417 B | 0.02 % |
| iot | 0.001 | 0.001 | 0.001 | 0.001 | 0 B | 0.00 % |
| queue | 1.187 | 0.187 | 6.37 | 11.23 | 12 495 B | 0.60 % |
| microservices | 0.001 | 0.001 | 12.23 | 13.52 | 0 B | 0.00 % |
| high-cardinality / adversarial | — | — | — | — | ≤ 4 B | 0.00 % |

**Decisión G9: no construir Lattice-P** (resultado negativo válido según el plan). La cota optimista no supera 0,6 %
del tamaño S+deflate en ninguna carga; el modelado de transiciones ya lo capturan el diccionario + DEFLATE.
Limitación: no se evaluaron residuos de valores numéricos (P09-T06/T07, p. ej. temperaturas en `iot`), ni datos reales;
reabrir si datasets reales muestran secuencias largas y deterministas.

## 5. Reproducción

```
dotnet run -c Release --project research/LatticeLog.Lab -- baseline --events 100000 --reps 3 --out research/results
```
o `scripts/reproduce.ps1` / `scripts/reproduce.sh`. Las cargas son deterministas; los tamaños deben coincidir
byte a byte en otra máquina con el mismo runtime (DEFLATE/zstd pueden variar entre versiones de biblioteca).
