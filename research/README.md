# Laboratorio LatticeLog (P02)

Objetivo: poder refutar o verificar cualquier mejora con experimentos reproducibles.

| pieza | ubicación |
|---|---|
| Generador determinista de 8 cargas (HTTP/API, inventario, batch, IoT, colas, microservicios, alta cardinalidad, adversarial) con *ground truth* de incidentes | `LatticeLog.Lab/Workloads.cs` |
| Baselines: texto, JSONL, JSONL+gzip, JSONL+zstd-3/19, JSONL+LZ4; `.llog` raw/S/C ± DEFLATE | `LatticeLog.Lab/Program.cs` |
| Verificación round-trip por SHA-256 del JSONL canónico | idem (`roundtrip ok`) |
| Consultas fijas (trace, errores, evento, ventana 1 %) indexado vs full scan vs JSONL+zstd | idem |
| Estudio previo de Lattice-P (longitudes de código adaptativas) | idem (`Entropy`) |
| Resultados | `results/` (CSV + Markdown por corrida) y análisis en `../reports/baseline-v1.md` |

Uso:
```
dotnet run -c Release --project research/LatticeLog.Lab -- baseline --events 100000 --reps 3
dotnet run -c Release --project research/LatticeLog.Lab -- generate --workload queue --events 1000000 --out queue.llog.jsonl
```

Pendiente: datasets públicos (Loghub-2.0, licencia por revisar) y reales anonimizados; tamaños 10M/100M; reproductor a
tasa variable con latencia del productor; control térmico, intervalos de confianza y caché fría; evaluación AI-friendly
externa (P02-T17, P12-T17) — requiere un modelo externo y revisión humana, fuera de este repositorio.
