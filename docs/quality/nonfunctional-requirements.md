# Requisitos no funcionales y SLOs objetivo (P00-T05)

**Todos los valores son objetivos iniciales, no resultados.** Las mediciones reales están en `reports/` con hardware.

| área | objetivo inicial | cómo se mide | estado |
|---|---|---|---|
| Logging deshabilitado (nivel filtrado) | 0 asignaciones, < 5 ns por llamada | BenchmarkDotNet `HotPathBenchmarks` | medido, ver `reports/benchmarks-hotpath.md` |
| Evento tipado habilitado (ruta del productor) | p99 < 2 µs sin saturación; asignaciones acotadas y documentadas | BenchmarkDotNet + prueba de latencia | medido parcialmente |
| Throughput sostenido local | ≥ 100 k eventos/s por proceso en modo Balanced en hardware de escritorio | app de ejemplo / laboratorio | 128 k ev/s medido en una corrida (Windows x64) |
| Durable | p99 de ACK < 10 ms en NVMe | pendiente | no medido |
| Recuperación tras crash | sin corrupción ni registros inventados en el 100 % de los casos de prueba | inyección de fallos | cumplido en pruebas (muestreo de offsets) |
| Lectura secuencial | ≥ 1 M registros/s decodificados | laboratorio | ver `reports/baseline-v1.md` |
| Consulta selectiva | trace lookup sin decodificar segmentos que el bloom excluye | `QueryTests`, laboratorio | cumplido |
| Disponibilidad del servidor | 99,9 % mensual | P24 | no aplicable (sin servidor) |
| Durabilidad del servidor | ACK solo tras persistencia según política | P16 | no aplicable |
| Seguridad | ningún secreto conocido persistido; texto de logs marcado como no confiable en salidas | pruebas | cumplido para el motor local |
| Portabilidad | archivo legible sin .NET | lector Python independiente | cumplido (golden files) |
