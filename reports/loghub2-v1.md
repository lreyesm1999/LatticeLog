# Loghub-2.0 — primera medición con datos públicos (P02-T01)

Fecha: 18-nov-2026 · `dotnet run -c Release --project research/LatticeLog.Lab -- loghub --lines 200000` · Datos
crudos: `research/results/loghub2-20261118-104533.csv` · Misma máquina que `reports/baseline-v1.md`.

**Datos**: Loghub-2.0 (Zenodo 8275861, CC-BY-4.0; Jiang et al., ISSTA 2024), 14 conjuntos, MD5 verificados. Se usan
las **primeras 200 000 líneas** de cada conjunto (o todas si tiene menos), leídas directamente de los zip, sin
extraer. No es una muestra aleatoria: el principio de cada fichero puede no representar el resto.

## Qué se compara

1. **Texto byte a byte**: cada línea cruda pasa por `RawTextImporter` y se guarda como `.llog` (Lattice-S + DEFLATE).
   Se comprueba que al exportarlo vuelven exactamente los mismos bytes (**14/14 idénticos**). Se compara con el
   texto crudo comprimido con zstd-3 y zstd-19.
2. **Tipado**: cada línea del CSV estructurado de Loghub (`*_full.log_structured.csv`) se convierte en un evento
   tipado. La plantilla (`EventId`) es el evento; las columnas de cabecera y los valores de los comodines `<*>` son los
   atributos; los enteros sin ceros a la izquierda pasan a Int64. Se comprueba que cada `Content` se reconstruye
   exactamente desde su plantilla y sus parámetros (**0 discrepancias y 0 plantillas que no encajan**) y que el
   `.llog` se relee igual. Se compara con el mismo CSV comprimido con zstd, porque contiene la misma información. No
   se compara con el texto crudo como si fuera equivalente: el formato exacto de la línea original (espacios,
   separadores de cabecera) no forma parte de esta variante.

Las plantillas vienen anotadas en el propio Loghub (verdad de referencia). Así se aproxima a una aplicación que
emite eventos tipados desde el origen; no se mide un parser de logs.

## Resultados (MB = 10⁶ bytes)

| conjunto | líneas | texto | zstd-3 | zstd-19 | llog texto | vs zstd-3 | CSV+zstd-3 | CSV+zstd-19 | llog tipado | vs CSV+zstd-3 | vs CSV+zstd-19 | plantillas |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Apache | 51 978 | 4.98 | 0.26 | 0.17 | 0.48 | +82 % | 0.30 | 0.20 | 0.17 | **−44 %** | −16 % | 29 |
| BGL | 200 000 | 26.62 | 3.03 | 1.43 | 4.72 | +56 % | 0.89 | 0.53 | 0.54 | **−40 %** | +1 % | 118 |
| HDFS | 200 000 | 28.01 | 2.56 | 1.67 | 4.69 | +83 % | 2.65 | 1.76 | 2.52 | −5 % | +43 % | 22 |
| HPC | 200 000 | 14.06 | 1.49 | 0.88 | 2.52 | +69 % | 0.53 | 0.38 | 0.27 | **−49 %** | −29 % | 47 |
| Hadoop | 179 993 | 31.89 | 1.81 | 1.08 | 3.82 | +111 % | 1.34 | 0.80 | 1.02 | −24 % | +28 % | 236 |
| HealthApp | 200 000 | 19.30 | 1.94 | 1.18 | 3.32 | +71 % | 1.69 | 0.92 | 1.21 | −28 % | +31 % | 156 |
| Linux | 23 921 | 2.14 | 0.18 | 0.11 | 0.33 | +85 % | 0.15 | 0.10 | 0.11 | −23 % | +19 % | 338 |
| Mac | 100 314 | 15.44 | 0.87 | 0.57 | 2.38 | +173 % | 0.96 | 0.63 | 0.79 | −18 % | +25 % | 626 |
| OpenSSH | 200 000 | 21.66 | 1.50 | 0.82 | 2.28 | +53 % | 1.00 | 0.62 | 0.42 | **−58 %** | −32 % | 34 |
| OpenStack | 200 000 | 59.20 | 4.98 | 3.74 | 8.97 | +80 % | 2.03 | 1.34 | 1.53 | −25 % | +14 % | 48 |
| Proxifier | 21 320 | 2.52 | 0.18 | 0.12 | 0.32 | +74 % | 0.21 | 0.14 | 0.15 | −29 % | +6 % | 11 |
| Spark | 200 000 | 24.15 | 1.32 | 0.77 | 2.41 | +83 % | 1.73 | 1.05 | 1.34 | −22 % | +28 % | 162 |
| Thunderbird | 200 000 | 29.80 | 2.03 | 1.38 | 4.31 | +113 % | 1.12 | 0.80 | 0.85 | −24 % | +6 % | 588 |
| Zookeeper | 74 273 | 10.33 | 0.45 | 0.27 | 0.82 | +84 % | 0.31 | 0.22 | 0.19 | **−39 %** | −13 % | 89 |
| **total** | | 290.09 | 22.60 | 14.19 | 41.38 | **+83 %** | 14.92 | 9.49 | 11.12 | **−25 %** | **+17 %** | |

## Lectura honesta

- **Resultado negativo: guardar texto crudo en `.llog` no comprime bien.** La importación byte a byte ocupa
  **1,5–2,7× lo que ocupa el texto con zstd-3** (+83 % en total). Los cuerpos son bytes opacos para el códec (sin
  diccionario de bloque) y DEFLATE, con su ventana de 32 KiB, queda muy por detrás de zstd. Esta importación sirve para
  conservar el original exacto y poder consultarlo, no para ahorrar espacio. Mejoras posibles, sin medir todavía:
  zstd como compresión de bloque (cambio de formato, ADR) y separar las cabeceras parseadas del resto de la línea.
- **Resultado positivo: el modelo tipado es más pequeño que el mismo contenido en CSV+zstd-3 en los 14 conjuntos**
  (−5 % a −58 %, −25 % en total). En 5 de 14 (Apache, BGL, HPC, OpenSSH, Zookeeper) supera el objetivo aspiracional
  del −30 %. Frente a CSV+zstd-19, el compresor lento y offline, solo gana en 4 de 14 (+17 % en total).
- El caso tipado es el escenario de diseño del producto (eventos con contrato), pero aquí parte de plantillas
  perfectas anotadas por Loghub. Con plantillas extraídas automáticamente o eventos mal diseñados, el resultado
  será peor.
- Los conjuntos con muchos parámetros de alta cardinalidad (HDFS: IDs de bloque y direcciones IP) son los que menos
  ganan (−5 %), igual que la carga sintética `high-cardinality` (§1 de `baseline-v1.md`).
- Tiempos no medidos en este experimento (solo tamaño). Una máquina y una sola corrida (la salida es determinista
  para los mismos zips).

Reproducir: descargar Loghub-2.0 (ver `research/dataset-manifest.yaml`) y ejecutar el comando de arriba; el CSV de
salida debe coincidir byte a byte con el publicado si las versiones de zstd/DEFLATE son las mismas.
