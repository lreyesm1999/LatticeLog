# Latency at a fixed rate — v1 (P02-T08, P14-T03)

Fecha: 18-nov-2026 · `dotnet run -c Release --project benchmarks/LatticeLog.Benchmarks -- latency` · Intel Core
i9-14900KF, Windows 11 10.0.26300, .NET 10.0.15, SSD local con antivirus del sistema activo. **Una máquina**: los
números no se generalizan.

## Método

- Productores (1 o 4 hilos) emiten el evento tipado de 4 campos del benchmark a una **tasa fija** total (10k, 100k o
  500k eventos/s), 1 s de calentamiento y 5 s medidos.
- Por cada llamada se guardan dos tiempos y se ordenan todas las muestras (percentiles exactos, sin histograma
  aproximado):
  - **servicio**: lo que dura la llamada de log;
  - **respuesta**: desde el instante en que la llamada *debía* ocurrir según el horario hasta que vuelve. Incluye el
    retraso acumulado si una llamada anterior se bloqueó, así que no oculta los bloqueos (omisión coordinada).
- Sinks: almacén real (`StoreWriter`, segmentos de 64 MiB) en `Balanced` y `Durable`, y sink nulo como referencia.
- Resolución del reloj (`Stopwatch`) en Windows: 100 ns; los valores de 100–300 ns están en el límite de medida.
- Política de saturación por defecto (`Block`, cola de 65 536 huecos).

## 1. Resultados (corrida completa, antes de la corrección de §4)

Valores en µs. Servicio = duración de la llamada; respuesta = desde el instante programado. `research/results/latency-20261118-104821.csv`.

| sink | modo | prod. | eventos/s | servicio p50 | p99 | p99.9 | máx | respuesta p99 | p99.9 | máx | GC 0/1/2 | descartes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|
| store | Balanced | 1 | 10 000 | 0.1 | 1.1 | 2.5 | 70 | 2.5 | 36 | 163 | 0/0/0 | 0 |
| store | Balanced | 1 | 100 000 | 0.1 | 0.3 | 4.0 | 67 | 2.5 | 40 | 159 | 1/1/1 | 0 |
| store | Balanced | 1 | 500 000 | 0.1 | 0.2 | 2.1 | 138 | 2.9 | 56 | 266 | 1/1/1 | 0 |
| store | Balanced | 4 | 10 000 | 0.3 | 1.4 | 2.9 | 35 | 2.3 | 36 | 232 | 0/0/0 | 0 |
| store | Balanced | 4 | 100 000 | 0.2 | 0.8 | 4.1 | 107 | 2.0 | 44 | 397 | 0/0/0 | 0 |
| store | Balanced | 4 | 500 000 | 0.2 | 0.5 | 2.8 | 224 | 3.6 | 79 | 1 313 | 1/1/0 | 0 |
| store | Durable | 1 | 10 000 | 0.1 | 0.9 | 1.7 | 82 | 6.5 | 110 | 342 | 0/0/0 | 0 |
| store | Durable | 1 | 100 000 | 0.1 | 0.4 | 1.3 | 2 114 | 4.2 | 102 | 2 114 | 1/0/0 | 0 |
| store | Durable | 1 | 500 000 | 0.1 | 0.2 | 0.8 | 4 181 | 7.2 | 215 | 4 181 | 1/1/0 | 0 |
| store | Durable | 4 | 10 000 | 0.3 | 1.0 | 1.6 | 34 | 1.5 | 59 | 192 | 0/0/0 | 0 |
| store | Durable | 4 | 100 000 | 0.2 | 0.9 | 2.2 | 1 968 | 4.6 | 107 | 1 968 | 3/3/1 | 0 |
| store | Durable | 4 | 500 000 | 0.3 | 0.6 | 1.9 | 171 | 5.9 | 87 | 252 | 0/0/0 | 0 |
| null | Balanced | 1 | 10 000 | 0.1 | 1.3 | 2.8 | 34 | 4.9 | 85 | 338 | 0/0/0 | 0 |
| null | Balanced | 1 | 100 000 | 0.1 | 0.5 | 5.1 | 251 | 8.8 | 118 | 756 | 0/0/0 | 0 |
| null | Balanced | 1 | 500 000 | 0.1 | 0.3 | 2.4 | 230 | 8.3 | 121 | 303 | 0/0/0 | 0 |
| null | Balanced | 4 | 10 000 | 0.3 | 1.5 | 5.0 | 165 | 5.7 | 97 | 210 | 0/0/0 | 0 |
| null | Balanced | 4 | 100 000 | 0.3 | 0.9 | 6.4 | 74 | 5.8 | 88 | 248 | 0/0/0 | 0 |
| null | Balanced | 4 | 500 000 | 0.3 | 0.6 | 4.4 | 171 | 6.5 | 106 | 2 521 | 0/0/0 | 0 |

## 2. Lectura

- En esta corrida (máquina en reposo) **no hubo bloqueos largos**: el peor tiempo de servicio fue 4,2 ms (`Durable`,
  1 productor, 500k/s) y en `Balanced` el peor retraso respecto al horario fue 1,3 ms. Las corridas anteriores de la
  misma configuración sí los tuvieron (§3): el fenómeno es **intermitente**, y una sola corrida limpia no lo descarta.
- La llamada típica cuesta 0,1–0,3 µs (p50, en el límite de resolución del reloj) y p99 ≤ 1,5 µs en todos los casos,
  también con disco real: el productor no hace E/S.
- La cola de la **respuesta** (p99.9 de 35–215 µs) es mayor que la del **servicio**: refleja interrupciones del
  propio hilo productor por el planificador del SO (también aparecen con el sink nulo), no el logger.
- 0 descartes en todas las configuraciones y muy pocas recolecciones del GC (los arrays de muestras del propio
  benchmark ya suman 40 MB por punto).
- No es una medición de throughput máximo: el escritor de almacén sostiene ~3 M registros/s en esta máquina (§3).

## 3. Diagnóstico de los bloqueos del hilo escritor

Configuración repetida: almacén, `Balanced`, 1 productor, 500k eventos/s, 5 s.

| variante | corridas | corridas con bloqueo > 10 ms | peor servicio máx. | peor retraso respecto al horario |
|---|---:|---:|---:|---:|
| fsync cada 1 s **y** cada 8 MiB, segmentos de 64 MiB | 6 | 5 | 1,51 s | 6,2 s |
| fsync cada 1 s (valor por defecto actual), segmentos de 64 MiB | 4 | 1 | 252 ms | 252 ms |
| ídem, corrida completa de §1 | 1 | 0 | 0,14 ms | 0,27 ms |
| fsync cada 8 MiB + segmentos de 4 GiB (sin rotación) | 4 | 3 | 2,08 s | 8,2 s |

(La primera fila se midió con el fsync por volumen de 8 MiB, que fue el valor por defecto provisional durante la
investigación; hoy está desactivado por defecto. Las muestras son pequeñas: 4–6 corridas por variante.)

Medición directa del escritor de almacén sin logger (8 s a máxima velocidad, 3,2 M registros/s, fsync cada 1 s):
lotes de 1 024 registros con p50 0,20 ms y p99 0,99 ms, pero **lotes de 18–137 ms al sellar y rotar** y de hasta
**330 ms en los fsync periódicos**.

Conclusiones:
- El hilo escritor se bloquea en E/S (fsync, sellado y probablemente la propia escritura cuando el sistema limita las
  páginas sucias). Con la política `Block`, un bloqueo más largo que `QueueCapacity / tasa` (131 ms a 500k/s) se
  traslada a los productores. Esto es el contrato (memoria acotada), pero en esta máquina ocurre con frecuencia.
- **La causa no está identificada**: hay bloqueos con y sin rotación, y con y sin fsync por volumen. Candidatos:
  limitación de escritura del gestor de caché de Windows, escaneo del antivirus al crear o cerrar segmentos, firmware
  del SSD. No se descarta nada sin una traza ETW/WPR.
- El fsync por volumen (`BalancedFsyncBytes`) **no** mostró una mejora consistente; queda como opción desactivada
  (sirve para acotar datos en riesgo, no para la latencia).
- `Durable` se comportó mejor que `Balanced` a 500k/s con 4 productores (fsync pequeños y frecuentes), aunque con 1
  productor también tuvo un pico de 9 ms.

Próximos pasos: traza ETW/WPR de una corrida con bloqueo; la misma medición en Linux (CI) y con el antivirus
excluyendo el directorio; diseño de fsync y sellado fuera del hilo escritor (los datos de un segmento ya escritos no
dependen del sellado para ser legibles), y evaluar si `Block` debe seguir siendo la política por defecto.

## 4. Causa encontrada y corrección (18-nov-2026, segunda investigación)

**Instrumentación.** Cada operación lenta del hilo escritor y del almacén (escritura, fsync, sellado, creación de
segmento, manifiesto) se informa con su duración (`SlowWriterOperationThreshold`, `StoreWriterOptions.Diagnostics`).
Cada pausa del GC se lee del propio runtime (`EventListener`, de SuspendEE a RestartEE). Todo se cruza en una línea de
tiempo con los bloqueos de los productores (`latency --diag`).

**Lo que mostraron ~60 corridas instrumentadas** (store, 500k/s, 1 y 4 productores):

| observación | valores medidos |
|---|---|
| pausas del GC | 2–6,6 ms en todas las corridas: **descartado** como causa |
| fsync del segmento activo | 50–330 ms habitual; picos de **1,8 s** y **5,3 s** |
| sellado de segmento (pie + fsync + cierre) | 171 ms, 462 ms, **3,8 s** |
| escritura de 32 KB en el segmento activo | tarda **exactamente lo mismo** que el fsync en curso del mismo archivo (p. ej. 298,6 ms frente a 299,9 ms; 1 834,6 ms frente a 1 836,3 ms) |
| escritura sin fsync en curso | un caso de 755 ms |

Conclusión: la causa son **picos de latencia del dispositivo de almacenamiento**. Además, en Windows/NTFS una
escritura en un archivo espera mientras se vacía ese mismo archivo. El SSD de la máquina (NVMe, unidad C:) estaba al
**98 % de ocupación** (≈ 11 GB libres de 476 GB). Es la hipótesis más probable para latencias de segundos, porque un SSD
casi lleno tiene mucha más amplificación de escritura, pero **no está verificada**: no hay otro SSD en la máquina con el
que comparar (D: es un HDD).

**Corrección aplicada: durabilidad en segundo plano.** El sellado de segmentos rotados y el fsync periódico de
`Balanced` corren ahora en un hilo propio del almacén (`DurabilityWorker`, FIFO, peticiones de fsync fusionadas,
fallo pegajoso). Los segmentos se escriben con `RandomAccess` sobre un handle del SO, para que el fsync pueda correr
en paralelo. Los `FlushAsync(FsyncConfirmed)` explícitos y el modo `Durable` siguen esperando el fsync, como dice su
contrato. Pruebas: `BackgroundDurabilityTests` (disco lento o con fallos simulado).

| configuración (Balanced, 500k/s) | antes: corridas con bloqueo > 10 ms | después |
|---|---:|---:|
| 1 productor | 4 de 12 (peor 123 ms) | **0 de 12** (peor 2,5 ms) |
| 4 productores | 2 de 6 | 1 de 6 en una tanda; 6 de 8 en otra, con vaciados del disco de hasta 5,3 s |

Lectura honesta:
- Los sellados ya no bloquean al escritor. El fsync periódico tampoco, **salvo** por la regla de NTFS: mientras se
  vacía el archivo activo, escribir en él espera. Con un disco que tarda segundos en vaciar, el escritor sigue
  parándose ese tiempo y, con la cola llena, también los productores (hasta `BlockTimeout`; en la corrida oficial
  de abajo, 5 s y 4 registros descartados y **contados**).
- **Probado y descartado**: fsync por volumen (cada 4 u 8 MiB) en segundo plano. Empeora, porque cada vaciado cuesta
  50–230 ms con independencia del tamaño y cada uno abre otra ventana de bloqueo. Una cola 4× mayor (262 144 huecos)
  no dio una mejora demostrable con 5–8 corridas.
- Pendiente:
  - repetir en otra máquina o disco con espacio libre, y en Linux, donde el page cache no bloquea así las escrituras
    durante un `fsync`;
  - valorar separar codificación y E/S, para que el escritor acumule los lotes ya codificados (~32 B por registro en
    lugar de ~300 B por objeto) mientras el disco está bloqueado;
  - documentar el dimensionado de la cola frente a `BlockTimeout` y `DropNewest`.

**Corrida oficial completa después de la corrección** (`research/results/latency-20261118-112854.csv`, µs):

| sink | modo | prod. | eventos/s | servicio p50 | p99 | p99.9 | máx | respuesta p99 | p99.9 | máx | GC 0/1/2 | descartes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|
| store | Balanced | 1 | 10 000 | 0.1 | 0.9 | 1.8 | 33 | 8.5 | 118 | 280 | 0/0/0 | 0 |
| store | Balanced | 1 | 100 000 | 0.1 | 0.3 | 3.2 | 52 | 3.5 | 44 | 217 | 1/1/1 | 0 |
| store | Balanced | 1 | 500 000 | 0.1 | 0.2 | 2.0 | 448 | 3.0 | 52 | 448 | 1/1/1 | 0 |
| store | Balanced | 4 | 10 000 | 0.3 | 0.9 | 1.6 | 25 | 1.5 | 27 | 169 | 0/0/0 | 0 |
| store | Balanced | 4 | 100 000 | 0.2 | 0.5 | 3.0 | 106 | 1.0 | 30 | 509 | 0/0/0 | 0 |
| store | Balanced | 4 | 500 000 | 0.3 | 0.9 | 161 | 5 000 003 | 5 392 415 | 5 431 773 | 5 435 489 | 4/3/1 | 4 |
| store | Durable | 1 | 10 000 | 0.1 | 0.9 | 1.4 | 40 | 3.8 | 92 | 395 | 0/0/0 | 0 |
| store | Durable | 1 | 100 000 | 0.1 | 0.6 | 1.3 | 1 513 | 7.5 | 136 | 1 513 | 4/4/0 | 0 |
| store | Durable | 1 | 500 000 | 0.1 | 0.1 | 16 | 120 022 | 2 616 377 | 2 624 910 | 2 627 545 | 1/1/0 | 0 |
| store | Durable | 4 | 10 000 | 0.3 | 1.4 | 5.1 | 20 | 5.1 | 68 | 1 726 | 1/1/0 | 0 |
| store | Durable | 4 | 100 000 | 0.2 | 0.5 | 1.1 | 75 | 1.3 | 28 | 148 | 0/0/0 | 0 |
| store | Durable | 4 | 500 000 | 0.2 | 0.4 | 1.0 | 1 342 | 2.3 | 57 | 1 342 | 1/0/0 | 0 |
| null | Balanced | 1 | 10 000 | 0.1 | 1.1 | 2.5 | 64 | 2.4 | 52 | 1 116 | 0/0/0 | 0 |
| null | Balanced | 1 | 100 000 | 0.1 | 0.3 | 3.2 | 85 | 2.3 | 39 | 212 | 0/0/0 | 0 |
| null | Balanced | 1 | 500 000 | 0.1 | 0.2 | 1.9 | 94 | 3.3 | 36 | 169 | 0/0/0 | 0 |
| null | Balanced | 4 | 10 000 | 0.3 | 0.9 | 1.9 | 52 | 1.8 | 39 | 138 | 0/0/0 | 0 |
| null | Balanced | 4 | 100 000 | 0.2 | 0.6 | 3.3 | 146 | 1.5 | 38 | 218 | 0/0/0 | 0 |
| null | Balanced | 4 | 500 000 | 0.2 | 0.4 | 2.6 | 417 | 2.8 | 84 | 2 164 | 0/0/0 | 0 |

Con el sink nulo, sin disco, no hay bloqueos en ninguna configuración: el logger por sí mismo no los produce.
