# LatticeLog — Visión (P00)

## Problema (P00-T01)
Los logs se generan con APIs de texto libre, se almacenan como texto o JSON redundante y se leen volcando miles de
líneas. Tres costes se repiten:
1. **Captura**: formatear strings y serializar JSON en el hilo de la aplicación añade latencia y asignaciones.
2. **Almacenamiento**: el 80–95 % de cada línea se repite (nombres de campo, servicio, plantillas, ids de traza).
3. **Lectura**: humanos e IA externa reciben volcados sin estructura, sin referencias estables y sin saber qué se omitió.

## Usuarios y escenarios
| usuario | escenario concreto |
|---|---|
| Desarrollador local | Instrumenta una API .NET con eventos tipados y consulta `latticelog query ./logs --where "level >= ERROR"` sin servidor. |
| Equipo de microservicios | Correlaciona una petición entre servicios por `trace_id`/`operation_id` y exporta un Context Pack de un incidente. |
| Operaciones / soporte | Verifica integridad tras un corte de luz (`verify`, `repair`) y obtiene grupos de error con conteos exactos. |
| Plataforma SaaS (futuro) | Ingesta multi-tenant, retención por niveles y acceso de solo lectura para agentes externos vía MCP. |

## Tres diferenciadores (P00-T02)
1. **Captura eficiente**: eventos tipados generados en compilación (sin `string.Format`, JSON ni reflexión en la ruta caliente).
2. **Almacenamiento especializado**: formato `.llog` abierto con catálogos de esquema/contexto, códecs estructurales e
   índices por segmento; reconstrucción exacta verificada.
3. **Lectura ventajosa para IA externa sin IA interna**: evidencias deterministas con cobertura, referencias estables
   y marcado de texto no confiable.

## Exclusiones explícitas (P00-T03)
- LatticeLog **no ejecuta IA**: sin LLM, embeddings ni clasificación neuronal.
- Sin pérdida silenciosa: toda pérdida (filtro, muestreo, saturación, error de escritura, retención) se cuenta.
- No se promete compresión universal ni superioridad sin datasets y hardware publicados.
- Sin dependencia obligatoria de cloud: todo funciona en local.
- Fuera del núcleo: APM de métricas/trazas completo, base de datos relacional generalista, deducir causalidad por timestamps.

## Superficies del producto (P00-T04)
SDK (.NET hoy; Java/Go/Python/Node planificados) · formato `.llog` · CLI `latticelog` · lenguaje de consulta ·
API de evidencias · adaptador MCP de solo lectura · servidor (P16–P17) · consola web (P23) · cloud (P25).

## Estado a 18-nov-2026
Motor local (SDK, formato, escritor, lector, consultas, evidencias, CLI, MCP) implementado como **0.1.0-alpha.1**,
con pruebas. Servidor, clúster, multi-SDK, UI y SaaS no iniciados. Ver `ROADMAP.md` y `docs/decisions/PHASE-*-REVIEW.md`.
