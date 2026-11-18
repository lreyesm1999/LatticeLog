# LatticeLog — PRD v0.1 (P00)

Versión 0.1 · 18-nov-2026 · Estado: borrador para revisión. Los umbrales son **objetivos/hipótesis**, no resultados.

## 1. Requisitos funcionales (motor local, M1–M3)
| id | requisito | estado |
|---|---|---|
| F1 | Eventos tipados declarados con atributos; validación en compilación de ids, nombres, placeholders, tipos y cambios de esquema. | implementado (`LatticeLog.Generator`) |
| F2 | API de texto libre y excepciones con coste documentado. | implementado (`LoggerExtensions`) |
| F3 | Contexto inmutable (scopes, operaciones) y captura de `Activity`. | implementado |
| F4 | Persistencia local `.llog` con modos Fast/Balanced/Durable y ACK verificable. | implementado |
| F5 | Recuperación tras crash sin interpretar bytes incompletos; `verify`/`repair`. | implementado |
| F6 | Consultas tipadas con índices, cursores, EXPLAIN y agregaciones. | implementado |
| F7 | Evidencias: overview, trace, related, error groups, Context Pack con presupuesto y cobertura. | implementado |
| F8 | Exportación sin pérdida (JSONL de intercambio) y OTLP/JSON con informe de pérdidas; importación JSONL/texto/OTLP. | implementado |
| F9 | CLI completa y MCP de solo lectura. | implementado (alpha) |
| F10 | Bridge `Microsoft.Extensions.Logging`. | **pendiente** (P03-T11) |
| F11 | Servidor, multi-tenant, RBAC, clúster, tiers, UI, SaaS. | no iniciado (P16+) |

## 2. Modos de durabilidad (P00-T07)
Definidos normativamente en `specs/durability.md`. Resumen: `Fast` = aceptado en memoria; `Balanced` (por defecto) =
escrito al SO por lote, fsync periódico (1 s); `Durable` = fsync por lote antes de confirmar. Saturación configurable:
bloquear (con timeout), descartar con contador o lanzar excepción.

## 3. "Sin pérdida" (P00-T06)
- **Structured-exact**: tipos, valores, nulos, orden de atributos, duplicados, NaN, −0.0, escalas decimales y strings
  con surrogates no emparejados se reconstruyen exactamente (pruebas de propiedades y golden files).
- **Raw byte-exact**: la importación de texto conserva los bytes de cada línea, el `\r` y la ausencia de salto final.
- Excepción documentada: claves/nombres con surrogates no emparejados se sanean a U+FFFD y el registro se marca `lossy_import`.

## 4. Configuración (P00-T08)
Precedencia propuesta: valores por defecto < archivo de configuración < variables de entorno < código explícito.
La configuración remota (servidor) **no** podrá cambiar durabilidad ni redacción de forma silenciosa: todo cambio se
registra. Hoy solo existe configuración por código (`LatticeLoggerOptions`, `StoreWriterOptions`) con validación y
mensajes accionables.

## 5. Plataformas (P00-T09)
.NET 10 LTS (soporte de Microsoft hasta nov-2028). Objetivo: Windows/Linux/macOS en x64 y ARM64. **Probado hasta
ahora: solo Windows 11 x64** (esta máquina). El formato es independiente del lenguaje (lector Python incluido).

## 6. Versiones y compatibilidad (P00-T10)
SemVer por paquete. Formato `.llog`: lectores 1.x leerán todos los archivos 0.1+ mediante golden files congelados;
ventana de compatibilidad objetivo N-2 versiones menores; deprecaciones con aviso de una versión menor y exportador
funcional (`latticelog export --format llog-jsonl`).

## 7. Criterios go/no-go (P00-T12)
| decisión | go si… | si no |
|---|---|---|
| Lattice-S en producción | ahorro neto vs binario simple en ≥3 cargas repetitivas sin regresión de lectura >20 % | simplificar códec |
| Lattice-C | ahorro neto positivo en cargas con contexto rico | usar contexto solo como índice |
| Lattice-P | cota optimista de ahorro ≥5 % sobre S+deflate en cargas relevantes | archivar P (no construir) |
| Clúster (P19) | límite medido del servidor single-node insuficiente para clientes reales | mantener single-node |
| SaaS (P25) | márgenes y seguridad aceptables en piloto | no lanzar SaaS público |

## 8. Presupuesto y recorte de alcance (P00-T15)
Sin datos de presupuesto ni equipo en este repositorio: **pendiente de decisión del responsable**. Regla de recorte:
priorizar según §8.6 del plan maestro (contrato, fidelidad, lector, durabilidad, seguridad básica antes que escala/comercial).
