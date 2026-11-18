# LatticeLog — Plan maestro de desarrollo integral

> **Documento vivo de ejecución | Versión 1.0 | 18 de noviembre de 2026**  
> **Estado:** planificación, sin resultados experimentales todavía  
> **Alcance:** investigación → librería SDK → motor local → consultas y lectores AI-friendly → servidor distribuido → plataforma comercial → operación sostenida  
> **Stack inicial:** .NET 10 LTS / C# para el motor y SDK iniciales, con independencia del formato de persistencia frente al lenguaje.  
> **Marca «LatticeLog»:** nombre provisional; verificar disponibilidad jurídica, dominios y proyectos homónimos antes del lanzamiento.

---

## 0. Cómo usar este documento

Este archivo es un **plan de trabajo ejecutable y trazable**, no solo una descripción conceptual. Se ha pensado para avanzar fase a fase durante todo el ciclo de vida del producto. Cada fase contiene objetivos, dependencias, tareas marcables, entregables y una puerta de salida (*gate*) que impide avanzar cuando faltan fundamentos.

**Convenciones de seguimiento**:

- `[ ]` pendiente; `[x]` terminado y validado con evidencia.
- Cada tarea tiene código estable (`PXX-TYY`); utilizarlo como ID de issue en GitHub/Linear/Jira.
- No marcar completada una tarea solo porque «el código existe»: hacen falta pruebas, documentación y evidencia reproducible.
- Al cerrar una fase, crear `docs/decisions/PHASE-XX-REVIEW.md` con enlaces a commits, pruebas, benchmarks, riesgos y decisiones.
- Los umbrales cuantitativos propuestos son **objetivos/hipótesis**, no resultados conseguidos ni promesas de rendimiento.
- Las fases con etiqueta **I+D** contienen decisiones que podrían resultar negativas. Un resultado negativo científicamente sólido puede cerrar satisfactoriamente una fase y permitir continuar con el diseño más simple.

### Identidad y límites innegociables

1. **LatticeLog NO ejecuta IA.** Sin LLM interno, embeddings, clasificación neuronal ni dependencia de API de modelos.
2. **AI-friendly significa facilidad de lectura por IA externa:** interfaces deterministas, contexto útil, evidencias recuperables, referencias estables y menor información redundante.
3. **Sin pérdida por defecto:** los datos persistidos deben reconstruirse fielmente según un contrato explícito; no colapsar ni descartar registros por deduplicación silenciosa.
4. **La ruta crítica de la aplicación es prioritaria:** capturar un evento debe introducir mínima latencia, asignaciones y contención, con políticas explícitas de durabilidad y sobrecarga.
5. **No hay superioridad universal:** cualquier afirmación «mejor que X» debe acotarse a datasets, configuración, hardware y cargas publicadas.
6. **Compatibilidad e independencia:** archivo binario versionado, lector documentado, exportaciones estándar y posibilidad de abandonar LatticeLog sin bloqueo de proveedor.
7. **Seguridad desde el inicio:** no registrar secretos por defecto; permisos, aislamiento y redacción para las herramientas que leen datos.
8. **Local primero, red opcional:** poder usarlo sin infraestructura, y escalar después a servidor distribuido y cloud.

### Definición de producto final

Una plataforma de observabilidad **especializada en logs y eventos** que incluya: SDKs multilenguaje, formato `.llog` público, ingestión local y remota, codificadores estructurales/contextuales/predictivos, consultas indexadas, reconstrucción exacta, búsqueda, correlación de trazas, CLI, API HTTP, exportación eficiente de evidencias, adaptador MCP de solo lectura, servidor multiusuario, clúster distribuido, almacenamiento por niveles, panel de administración, seguridad, auditoría, SaaS comercial, documentación y mantenimiento permanente.

**Fuera de alcance del núcleo**: ejecutar modelos de IA, determinar causas raíz mediante inferencia, sustituir APM completo de métricas/trazas, crear una base de datos relacional generalista, garantizar cero pérdida sin persistencia sincronizada, deducir causalidad a partir de timestamps únicamente.

---

## 1. Mapa general de fases y dependencias

| Etapa | Fase | Resultado verificable | Dependencia clave | Hito |
|---|---|---|---|---|
| Fundamentos | P00 | Charter, alcance y criterios de éxito | — | G0 |
| Fundamentos | P01 | Especificación del modelo semántico y garantías | P00 | G1 |
| I+D | P02 | Laboratorio, datasets y baselines | P00–P01 | G2 |
| Captura | P03 | SDK .NET y generador de eventos tipados | P01 | G3 |
| Persistencia | P04 | Especificación binaria `.llog` v0.x | P01–P03 | G4 |
| Persistencia | P05 | Cola, escritor, WAL, política de durabilidad | P04 | G5 |
| Persistencia | P06 | Lector exacto, recuperación y golden files | P04–P05 | G6 |
| Codificación | P07 | Códec estructural Lattice-S | P02, P06 | G7 |
| Codificación | P08 | Códec contextual Lattice-C | P07 | G8 |
| Codificación | P09 | Códec predictivo Lattice-P (experimental) | P08 | G9 |
| Codificación | P10 | Selección adaptativa y compactación en segundo plano | P07–P09 | G10 |
| Recuperación | P11 | Índices y lenguaje de consulta | P06, P10 | G11 |
| Lectura | P12 | Salidas AI-friendly y Context Packs deterministas | P11 | G12 |
| Interoperabilidad | P13 | OpenTelemetry, importación y exportación | P01, P11 | G13 |
| SDK estable | P14 | Calidad industrial de librería .NET | P03–P13 | **Local v1.0** |
| Herramientas | P15 | CLI, lector embebido y empaquetado multiplataforma | P14 | G15 |
| Servidor | P16 | Ingestión remota y protocolo de transporte | P14–P15 | G16 |
| Servidor | P17 | Servicio de consultas, metadatos y multiusuario | P11–P16 | **Server v1.0** |
| Seguridad | P18 | Privacidad, autenticación, RBAC, auditabilidad | P16–P17 y controles previos | G18 |
| Escala | P19 | Sharding, replicación, tolerancia a fallos | P17–P18 | G19 |
| Almacenamiento | P20 | Tiers hot/warm/cold y object storage | P19 | G20 |
| Ecosistema | P21 | SDKs Java, Go, Python y Node.js | P14–P16 | G21 |
| Integración externa | P22 | MCP read-only y herramientas para IDE/agentes | P12, P18 | G22 |
| Experiencia de usuario | P23 | UI operativa, explorador de logs y panel de control | P17–P18 | G23 |
| Operación | P24 | Observabilidad, SRE, backups y recuperación | P19–P23 | G24 |
| Negocio | P25 | Licencia, planes, medición de uso, facturación y SaaS | P18–P24 | **Cloud GA** |
| Calidad final | P26 | Performance, caos, seguridad y compatibilidad certificables | Todas las anteriores | G26 |
| Lanzamiento | P27 | Documentación, comunidad, soporte y distribución | P25–P26 | **Product GA** |
| Continuidad | P28 | Roadmap posterior y proceso sostenible de versiones | P27 | Evolución |

> **Dependencias no son una obligación de implementación monolítica:** pruebas, seguridad, documentación y benchmarks son transversales desde el día uno. No aplazar seguridad hasta P18 ni pruebas de fallos hasta P26.

### Hitos de entrega (sin fechas prometidas)

- **M0 — Hipótesis medible:** P00–P02.
- **M1 — Prototipo verificable:** P03–P06.
- **M2 — Conclusión de investigación de codificación:** P07–P10.
- **M3 — Motor local útil:** P11–P15.
- **M4 — Servidor comercializable:** P16–P18.
- **M5 — Producto escalable:** P19–P24.
- **M6 — Plataforma comercial completa:** P25–P27.
- **M7 — Evolución indefinida:** P28.

---

## 2. Fases de desarrollo detalladas

### P00 — Visión, alcance, requisitos y gobierno técnico

**Objetivo:** fijar qué se va a construir, cómo mediremos el éxito y cómo se tomarán decisiones difíciles.  
**Dependencias:** ninguna.  
**Entregables:** `docs/product/vision.md`, `docs/product/prd.md`, `docs/quality/nonfunctional-requirements.md`, `docs/decisions/ADR-0001.md`.

**Tareas**

- [ ] **P00-T01** Redactar problema, usuarios objetivo (desarrollador local, equipo de microservicios, operaciones, plataforma SaaS) y escenarios concretos.
- [ ] **P00-T02** Definir los tres diferenciadores: captura eficiente, almacenamiento especializado y lectura ventajosa para IA externa.
- [ ] **P00-T03** Escribir exclusiones explícitas: IA interna, pérdida silenciosa, compresión universal, dependencia obligatoria de cloud.
- [ ] **P00-T04** Establecer superficies del producto: SDK, `.llog`, CLI, query, API, MCP, servidor, consola web y cloud.
- [ ] **P00-T05** Definir SLOs objetivo iniciales para latencia de emisión, disponibilidad del servidor y durabilidad, sin fingir resultados.
- [ ] **P00-T06** Precisar qué es «sin pérdida»: fidelidad semántica para eventos tipados y fidelidad byte a byte para importaciones raw en modo de preservación exacta.
- [ ] **P00-T07** Definir modos `Fast`, `Balanced` y `Durable`, su semántica de ACK, saturación y posibles pérdidas.
- [ ] **P00-T08** Diseñar modelo de configuración y reglas de precedencia (código, config, entorno, remoto; sin cambios secretos).
- [ ] **P00-T09** Identificar requisitos de Linux/Windows/macOS, x64/ARM64, frameworks compatibles y horizonte de soporte.
- [ ] **P00-T10** Aprobar políticas de soporte de versiones, deprecaciones y compatibilidad de archivo.
- [ ] **P00-T11** Elaborar matriz de riesgos técnicos/científicos/legales y asignar responsables.
- [ ] **P00-T12** Definir criterios de decisión *go/no-go* para predicción, compresión, clúster y SaaS.
- [ ] **P00-T13** Elegir arquitectura inicial local en .NET 10 LTS y justificarla en ADR; revisar compatibilidad al lanzar versiones nuevas.
- [ ] **P00-T14** Hacer comprobación preliminar de nombre, dominios y licencias de dependencias antes de divulgar la marca.
- [ ] **P00-T15** Establecer presupuesto de I+D, hardware disponible, limitaciones de tiempo y criterios de recorte de alcance.

**Gate G0:** PRD versionado, casos de uso, no-objetivos, métricas y matriz de riesgos revisados. Nadie debe comenzar «optimización» sin un baseline acordado.

### P01 — Modelo semántico de eventos y contratos estables

**Objetivo:** diseñar un registro lógico independiente de su representación binaria y del lenguaje.  
**Dependencias:** P00.  
**Entregables:** `specs/event-model.md`, `specs/schema-compat.md`, `specs/context-model.md`, `specs/ordering.md`.

**Tareas**

- [ ] **P01-T01** Definir `LogRecord`: `timestamp`, `observed_timestamp`, `severity`, `event_id`, `event_name`, `body`, `attributes`, `resource`, `scope`.
- [ ] **P01-T02** Incorporar `trace_id`, `span_id`, `operation_id`, `parent_id` y `correlation_id` opcionales, con semántica inequívoca.
- [ ] **P01-T03** Especificar tipos base: signed/unsigned, float, decimal, bool, string, bytes, array, map, null, timestamp, duration, UUID.
- [ ] **P01-T04** Diferenciar identidad estable de evento, versión del esquema, instancia de registro e identidad lógica de operación.
- [ ] **P01-T05** Establecer convenios de nombres, campos reservados y límites por campo, evento y contexto.
- [ ] **P01-T06** Crear sistema de contratos de evento: versión, campos requeridos/opcionales, valores por defecto y cambios compatibles/incompatibles.
- [ ] **P01-T07** Formalizar contextos inmutables o snapshots/versiones para evitar herencias ambiguas y carreras.
- [ ] **P01-T08** Definir cuándo se propagan propiedades (servicio, usuario, despliegue) y cuándo no (datos secretos, contexto efímero).
- [ ] **P01-T09** Documentar orden parcial: secuencia por productor + monotonic time local + wall clock; no inferir causalidad global a partir del reloj.
- [ ] **P01-T10** Definir relación explícita entre spans, operaciones y eventos, incluso con relojes desincronizados.
- [ ] **P01-T11** Especificar fingerprints deterministas de errores y sus límites, sin considerarlos equivalentes a causa raíz.
- [ ] **P01-T12** Diseñar `SourceLocation` opcional: repositorio, commit, archivo, función y línea, evitando rutas privadas innecesarias.
- [ ] **P01-T13** Especificar cómo registrar excepciones: tipo, mensaje, frames, stack original y `inner exceptions`.
- [ ] **P01-T14** Incluir metadata de confidencialidad, categorías de retención y marca de campos potencialmente sensibles.
- [ ] **P01-T15** Definir referencias estables `event_ref` y su resolución incluso tras compactación y migración entre nodos.
- [ ] **P01-T16** Construir ejemplos de contrato en JSON Schema o esquema equivalente y casos de evolución.
- [ ] **P01-T17** Mapear explícitamente a OpenTelemetry Logs Data Model y W3C Trace Context.
- [ ] **P01-T18** Publicar ejemplos de operación correcta, error, retry, timeout, fan-out y operación incompleta.

**Gate G1:** toda propiedad puede serializarse/deserializarse sin ambigüedad; las reglas de versión, identidad, orden y contexto están documentadas.

### P02 — Laboratorio científico, datasets y baseline justo (I+D)

**Objetivo:** poder refutar o verificar cualquier mejora con experimentos reproducibles.  
**Dependencias:** P00–P01.  
**Entregables:** `research/README.md`, `research/dataset-manifest.yaml`, `benchmarks/baselines/`, `scripts/reproduce.sh`, `reports/baseline-v1.md`.

**Tareas**

- [ ] **P02-T01** Seleccionar datasets públicos estructurados/no estructurados (p. ej. Loghub-2.0, con licencias y condiciones verificadas).
- [ ] **P02-T02** Preparar datasets propios de aplicaciones reales con anonimización y autorización.
- [ ] **P02-T03** Generar datasets sintéticos deterministas con semillas versionadas y *ground truth* de relaciones.
- [ ] **P02-T04** Incluir workloads de HTTP/API, inventario, procesamiento por lotes, IoT, colas, operaciones concurrentes y fallos raros.
- [ ] **P02-T05** Incluir anti-casos: strings aleatorios, campos de alta cardinalidad, trazas truncadas, falta de contexto, mensajes desordenados.
- [ ] **P02-T06** Versionar corpus con hash, tamaño, origen, licencia y reglas de limpieza; no subir datos sensibles.
- [ ] **P02-T07** Implementar un generador de 100k/1M/10M/100M eventos con cardinalidad y repetición configurables.
- [ ] **P02-T08** Implementar reproductor a tasas variables y concurrencia creciente, con latencias del productor medibles.
- [ ] **P02-T09** Preparar baseline `raw text`, `JSONL`, `JSONL+Zstd`, `JSONL+LZ4` y formatos binarios simples.
- [ ] **P02-T10** Evaluar CLP y alternativas de investigación si existen implementaciones reproducibles y licencias compatibles.
- [ ] **P02-T11** Medir bytes totales incluyendo metadatos, índices, diccionarios, manifests y snapshots.
- [ ] **P02-T12** Medir throughput, CPU, memoria, GC, p50/p95/p99, latencia de ACK y lectura selectiva.
- [ ] **P02-T13** Distinguir compresión offline de ingestión en vivo y separar coste de compactación.
- [ ] **P02-T14** Crear un conjunto de consultas fijo: tiempo, servicio, evento, trace, campo, texto, top errores, correlación.
- [ ] **P02-T15** Automatizar warmups, repeticiones, intervalos de confianza, control térmico, caché fría/caliente y hardware registrado.
- [ ] **P02-T16** Añadir validación de round-trip y hashes canónicos de datos originales.
- [ ] **P02-T17** Diseñar evaluación externa AI-friendly con incidentes de respuesta conocida, control de filtrado y presupuesto de tokens.
- [ ] **P02-T18** Publicar scripts y resultados con cualquier configuración necesaria para replicarlos.

**Gate G2:** otra máquina puede ejecutar el benchmark y obtener resultados comparables; se sabe cuánto ocupan y cuánto tardan los baselines.

### P03 — SDK .NET de captura tipada y compilador de eventos

**Objetivo:** capturar eventos estructurados con mínima sobrecarga antes de aplicar codificación avanzada.  
**Dependencias:** P01; usar P02 para medición.  
**Entregables:** `LatticeLog.Abstractions`, `LatticeLog.Core`, `LatticeLog.Generator`, tests, sample API.

**Tareas**

- [ ] **P03-T01** Diseñar `ILatticeLogger`, `EventDescriptor`, `LoggerOptions`, `EventContext` y ciclo de vida `Flush/Dispose`.
- [ ] **P03-T02** Crear API para eventos de plantilla, tipados, texto libre y excepciones sin ocultar sus costes.
- [ ] **P03-T03** Implementar generador incremental Roslyn para producir serializers a partir de atributos o definiciones explícitas.
- [ ] **P03-T04** Validar en compilación IDs duplicados, tipos incompatibles, campos faltantes, placeholders no definidos y cambios de esquema.
- [ ] **P03-T05** Evitar `string.Format`, serialización JSON, reflexión y `boxing` en la ruta rápida tipada cuando sea posible.
- [ ] **P03-T06** Instrumentar allocations con BenchmarkDotNet; definir requisitos por clase de evento.
- [ ] **P03-T07** Usar `Span<T>`, `IBufferWriter<byte>`, pools y buffers reutilizables donde el perfilado justifique.
- [ ] **P03-T08** Implementar niveles, filtros y sampling opcional con reglas explícitas, señalizando pérdida por diseño.
- [ ] **P03-T09** Definir scopes y propagación de contexto con `AsyncLocal` donde sea adecuado y sin dependencia obligatoria.
- [ ] **P03-T10** Diseñar control de valores PII/secretos y defaults seguros para el SDK.
- [ ] **P03-T11** Crear bridge a `Microsoft.Extensions.Logging` sin imponer LatticeLog a toda la aplicación.
- [ ] **P03-T12** Permitir capturar eventos desde `Activity`/contexto de traza y conservar metadatos.
- [ ] **P03-T13** Crear integraciones de ejemplo ASP.NET Core, Worker Service y Console.
- [ ] **P03-T14** Validar comportamiento en aplicación con excepción no controlada, cancelación y apagado abrupto.
- [ ] **P03-T15** Medir coste con logging apagado, en buffer, volcado y bajo saturación.
- [ ] **P03-T16** Documentar contratos y advertencias de uso en `README` y XML docs.

**Gate G3:** SDK consume y produce eventos correctos, sin sorpresas de GC, y con semántica clara de filtrado y estado de persistencia.

### P04 — Especificación binaria abierta `.llog` y versionado

**Objetivo:** establecer un contrato de almacenamiento verificable, autocontenido y evolutivo.  
**Dependencias:** P01, P03.  
**Entregables:** `specs/llog-format-v0.md`, casos binarios dorados y parser de referencia mínimo.

**Tareas**

- [ ] **P04-T01** Elegir orden de bytes, magic bytes, `major/minor`, features y límites de tamaño de bloque.
- [ ] **P04-T02** Definir file header, segment header, schema catalog, context table, streams, indexes, checkpoints y footer.
- [ ] **P04-T03** Formalizar codificaciones base: varint, ZigZag, fixed width, bit packing, bytes y cadenas UTF-8.
- [ ] **P04-T04** Establecer framing de registros y protección frente a entradas malformadas/overflow.
- [ ] **P04-T05** Incluir versionado de codecs y descriptores necesarios para decodificar sin SDK original.
- [ ] **P04-T06** Definir `record_id`, `segment_id`, secuencia por escritor y `logical_ref` estable.
- [ ] **P04-T07** Definir checksum por bloques y manifiesto; CRC de integridad accidental no sustituye autenticación criptográfica.
- [ ] **P04-T08** Especificar commit atómico del segmento, manifests, journal y forma de recuperación.
- [ ] **P04-T09** Definir checkpoints y bounding del estado para consultas aleatorias.
- [ ] **P04-T10** Diferenciar formato de captura hot y formato consolidado cold; definir ruta de conversión exacta.
- [ ] **P04-T11** Definir extensiones opcionales y comportamiento ante versiones/campos desconocidos.
- [ ] **P04-T12** Documentar límites de decompression/expansion y prevención de zip-bombs propios.
- [ ] **P04-T13** Diseñar especificación independiente de .NET, sin serialización de objetos CLR.
- [ ] **P04-T14** Construir corpus de archivos minúsculos, gigantes, vacíos, truncados y corruptos.
- [ ] **P04-T15** Diseñar procedimiento seguro de migración de formato y rollback de versión.
- [ ] **P04-T16** Publicar ejemplos hexadecimales y vectores de test públicos.

**Gate G4:** al menos un segundo parser independiente puede interpretar los golden files únicamente con la especificación.

### P05 — Pipeline de ingestión, WAL y escritura duradera

**Objetivo:** canal productor→buffer→disco con durabilidad medible y sobrecarga controlada.  
**Dependencias:** P04.  
**Entregables:** writer, WAL, políticas de backpressure, pruebas de estrés y fault injection.

**Tareas**

- [ ] **P05-T01** Comparar SPSC/MPSC por núcleo, cola bloqueante y ring buffer acotado con workloads reales.
- [ ] **P05-T02** Seleccionar implementación tras bench de p99, contención y consumo de memoria.
- [ ] **P05-T03** Implementar framing hot append-only y batching de registros.
- [ ] **P05-T04** Diseñar límites de memoria, tamaño de lote, flushing por volumen/tiempo y no solo thresholds fijos.
- [ ] **P05-T05** Definir modos de durabilidad: accepted-in-memory, written-to-OS, fsync-confirmed; nombrar ACK de modo verificable.
- [ ] **P05-T06** Implementar política de saturación configurable: bloquear, fallar explícitamente o descartar con contador y alerta.
- [ ] **P05-T07** Incorporar cierre limpio, `FlushAsync`, cancelación y tiempos máximos configurables.
- [ ] **P05-T08** Diseñar WAL o journal transaccional para metadatos y transiciones de segmento.
- [ ] **P05-T09** Implementar rotación por tamaño/tiempo, cuota de disco y políticas de protección frente a disco lleno.
- [ ] **P05-T10** Realizar pruebas con interrupción forzada durante escritura, fsync, rotación, commit y actualización del índice.
- [ ] **P05-T11** Probar errores reales de archivo: permisos, disco lleno, readonly, path inválido, bloqueo y latencia extrema.
- [ ] **P05-T12** Definir comportamiento con múltiples procesos en un mismo directorio: prohibición explícita o coordinación probada.
- [ ] **P05-T13** Medir amplificación de escritura y durabilidad en SSD/HDD/NVMe bajo diferentes políticas.
- [ ] **P05-T14** Instrumentar métricas de cola, dropped events, bytes pending y tiempo de confirmación.
- [ ] **P05-T15** Implementar recuperación que no interprete bytes incompletos como eventos válidos.

**Gate G5:** recuperación demostrada ante crashes repetidos y garantías documentadas para cada modo, sin afirmar cero pérdida cuando solo hay buffer en RAM.

### P06 — Lector de referencia, reconstrucción exacta y compatibilidad

**Objetivo:** demostrar que todo archivo aceptado puede leerse de forma segura y reconstruirse sin pérdida.  
**Dependencias:** P04–P05.  
**Entregables:** reader library, CLI `verify`, test vectors y validación round-trip.

**Tareas**

- [ ] **P06-T01** Implementar parser streaming sin cargar todo el archivo.
- [ ] **P06-T02** Implementar decodificador de registros, schemas, scopes, contexts y versiones.
- [ ] **P06-T03** Asegurar reconstrucción exacta de tipos, valores, nulos, orden definido y excepciones.
- [ ] **P06-T04** Para importación raw, conservar bytes, encoding, delimitadores y orden cuando se solicite modo byte-exact.
- [ ] **P06-T05** Implementar `verify` para checksums, manifests, pointers y referencias.
- [ ] **P06-T06** Permitir lectura parcial de segmentos sanos cuando otros están corruptos, informando los huecos.
- [ ] **P06-T07** Implementar manejo de formatos no soportados con errores claros, sin reinterpretación peligrosa.
- [ ] **P06-T08** Fuzzear parser con archivos malformados y corrupción intencional de campos.
- [ ] **P06-T09** Crear pruebas de propiedades `decode(encode(x)) = x` para todos los tipos.
- [ ] **P06-T10** Generar golden files congelados para pruebas de compatibilidad en releases futuras.
- [ ] **P06-T11** Medir throughput secuencial, allocations, CPU y memoria del lector.
- [ ] **P06-T12** Implementar manifest de evidencia que reporte archivos omitidos, segmentos corruptos y saltos.
- [ ] **P06-T13** Probar múltiples tamaños de archivo, timestamps negativos, Unicode, floats especiales y valores extremos.
- [ ] **P06-T14** Producir decodificador simple de referencia separable del motor optimizado.

**Gate G6:** 100 % de pruebas round-trip y golden files; no se aceptan fallos silenciosos ante entradas dañadas.

### P07 — Lattice-S: codificación estructural (I+D)

**Objetivo:** eliminar redundancia estructural sin predicción temporal.  
**Dependencias:** P02, P06.  
**Entregables:** codec `Structural`, microbenchmarks, reporte de ahorro y costes.

**Tareas**

- [ ] **P07-T01** Separar descriptor del evento de valores dinámicos.
- [ ] **P07-T02** Implementar diccionarios de nombres, tipos, niveles, eventos y strings repetidos.
- [ ] **P07-T03** Evaluar diccionario por bloque, segmento, fuente y global, incluyendo coste de almacenamiento y reconstrucción.
- [ ] **P07-T04** Implementar delta/varint para timestamps y campos numéricos adecuados.
- [ ] **P07-T05** Implementar bit packing/RLE para pequeños dominios y valores repetidos.
- [ ] **P07-T06** Probar layouts row-wise, column-wise e híbrido con múltiples filtros de consulta.
- [ ] **P07-T07** Diseñar fallback raw de bajo coste para campos de alta entropía.
- [ ] **P07-T08** Medir impacto de cardinalidad y tamaño de bloques.
- [ ] **P07-T09** Establecer límite de crecimiento de diccionarios y política de reinicio.
- [ ] **P07-T10** Verificar que el orden y los metadatos originales se conservan según contrato.
- [ ] **P07-T11** Medir sobrecoste en CPU y throughput además de ratio de tamaño.
- [ ] **P07-T12** Comparar contra JSON+Zstd, binario simple y baselines aplicables.
- [ ] **P07-T13** Redactar decisión de formato con resultados positivos y negativos.

**Gate G7:** codec reversible, resultados reproducibles, y claridad sobre cuándo supera o no al binario simple.

### P08 — Lattice-C: herencia de contexto (I+D)

**Objetivo:** codificar propiedades comunes de una ejecución sin repetirlas y sin inferir relaciones que no existen.  
**Dependencias:** P07.  
**Entregables:** modelo de contextos, referencias compartidas, benchmark diferencial.

**Tareas**

- [ ] **P08-T01** Implementar snapshots de contexto inmutables y referencias versionadas.
- [ ] **P08-T02** Decidir nivel de contexto (servicio, proceso, request, span, operación) y reglas de alcance.
- [ ] **P08-T03** Implementar herencia explícita y lista de sobrescrituras, eliminaciones y valores locales.
- [ ] **P08-T04** Evitar que contextos sensibles se propaguen de manera implícita.
- [ ] **P08-T05** Medir tasa de reutilización de contexto en aplicaciones reales.
- [ ] **P08-T06** Implementar compactación de grafos de contexto, controlando ciclos y referencias huérfanas.
- [ ] **P08-T07** Garantizar recuperabilidad de eventos aunque su contexto esté en otro bloque.
- [ ] **P08-T08** Diseñar checkpoints para acceso aleatorio sin recorrer toda la operación.
- [ ] **P08-T09** Modelar concurrencia, span fan-out, retries y rutas parcialmente instrumentadas.
- [ ] **P08-T10** Diseñar fallback directo cuando el coste del contexto supera el ahorro.
- [ ] **P08-T11** Comparar Lattice-C vs Lattice-S con datos que sí y no tengan contexto explícito.
- [ ] **P08-T12** Publicar reporte de bytes netos, lookups, RAM y tiempos de búsqueda.

**Gate G8:** ahorro neto medible en cargas con contextos ricos sin degradación prohibitiva de lectura; el lector nunca asume contextos no demostrables.

### P09 — Lattice-P: codificación de transiciones y residuales (I+D)

**Objetivo:** comprobar si los patrones temporales y dependencias explícitas permiten codificar más eficientemente que Lattice-C.  
**Dependencias:** P08.  
**Entregables:** codec `Predictive`, benchmarks de ablación, documentación formal del estado del predictor.

**Tareas**

- [ ] **P09-T01** Definir contrato de predictor determinista y versionado con `predict(state, event_schema)` y `update(state, actual)`.
- [ ] **P09-T02** Formalizar secuencias de evento sin inventar registros ausentes y sin reordenar eventos de productores independientes.
- [ ] **P09-T03** Investigar codificación por diccionarios de secuencias frecuentes dentro de un contexto.
- [ ] **P09-T04** Implementar detector de transiciones frecuentes con memoria y CPU acotadas.
- [ ] **P09-T05** Implementar `PatternID + deviations` para secuencias exactas y desviaciones explícitas.
- [ ] **P09-T06** Implementar predicción reversible de timestamps/contadores/números y guardar residuales exactos.
- [ ] **P09-T07** Considerar XOR para floats y datos binarios cuando sea mejor que diferencias aritméticas.
- [ ] **P09-T08** Gestionar cambios de esquema, cortes de sesión, campos nulos, missing events y operaciones incompletas.
- [ ] **P09-T09** Versionar estados y snapshots necesarios para que el futuro lector reproduzca predicciones.
- [ ] **P09-T10** Incorporar checkpoints de estado para que una lectura no dependa de horas de histórico.
- [ ] **P09-T11** Medir entropía de residuales y tamaño total del diccionario de patrones.
- [ ] **P09-T12** Implementar algoritmo de fallback directo cuando no exista ganancia amortizada.
- [ ] **P09-T13** Realizar análisis de ablación: S, C, C+transiciones, C+residuales, P completo.
- [ ] **P09-T14** Probar datasets adversariales con secuencias raras y datos aleatorios.
- [ ] **P09-T15** Medir impacto de p99 y coste de compactación con carga real.
- [ ] **P09-T16** Documentar si se mantiene, limita a ciertos patrones o se descarta la predicción.

**Gate G9:** mejora neta frente a Lattice-C en workloads relevantes, incluyendo modelos, diccionarios, checkpoints, CPU y consultas. Si falla, mantener Lattice-C como motor principal y archivar Lattice-P como experimento, no forzarlo a producción.

### P10 — Planificador adaptativo, compactación, políticas de coste

**Objetivo:** elegir por segmento/campo el códec que más valor aporte sin afectar la captura.  
**Dependencias:** P07–P09.  
**Entregables:** `CodecPlanner`, `Compactor`, benchmark multivariado, mecanismos de rollback.

**Tareas**

- [ ] **P10-T01** Definir interfaz de codec (`estimate`, `encode`, `decode`, `index_capabilities`, `metadata_cost`).
- [ ] **P10-T02** Implementar muestreo estadístico de un segmento para estimar cardinalidad y repetición.
- [ ] **P10-T03** Diseñar función de coste: bytes + CPU de escritura/compactación + latencia de lectura + coste de índices.
- [ ] **P10-T04** Implementar políticas `Fast`, `Balanced`, `Compact`, con pesos configurables y valores por defecto seguros.
- [ ] **P10-T05** Permitir comparar alternativas candidatas sin desestabilizar la ruta caliente de ingestión.
- [ ] **P10-T06** Seleccionar codec por columnas/campos cuando sea rentable; documentar límites del formato.
- [ ] **P10-T07** Incorporar fallback raw y codec general opcional (Zstd/LZ4) para bloques no estructurables.
- [ ] **P10-T08** Implementar compactación asíncrona de hot segments a warm/cold segments.
- [ ] **P10-T09** Crear conversión atómica: escribir nuevo segmento, validar, activar manifest y retirar original solo después.
- [ ] **P10-T10** Diseñar idempotencia de compactación y reintentos ante caída.
- [ ] **P10-T11** Mantener referencias lógicas estables mediante tabla de resolución/mapeo de ubicaciones.
- [ ] **P10-T12** Limitar CPU, IO, RAM y ancho de banda del compactor para evitar competir con la aplicación.
- [ ] **P10-T13** Exponer métricas de ratio, deuda de compactación, write amplification y coste de búsqueda.
- [ ] **P10-T14** Implementar reorganización/reindexación sin perder metadatos de auditoría.
- [ ] **P10-T15** Medir tamaño efectivo del corpus tras compactación y almacenamiento duplicado temporal.
- [ ] **P10-T16** Publicar decisión por carga: qué codecs utilizar y bajo qué umbrales.

**Gate G10:** archivos reproducibles y recuperables, compactación reanudable y ventaja verificable por política; se soporta `no-compaction` para operación simple.

### P11 — Índices, consultas y recuperación selectiva

**Objetivo:** hacer posible localizar eventos útiles sin recorrer, decodificar ni exportar el histórico entero.  
**Dependencias:** P06, P10.  
**Entregables:** `LatticeLog.Query`, lenguaje de consulta v1, APIs de paginación y explain.

**Tareas**

- [ ] **P11-T01** Inventariar consultas concretas y establecer una matriz de cobertura funcional.
- [ ] **P11-T02** Diseñar AST y sintaxis de consultas segura, con filtros tipados y sin ejecución arbitraria de código.
- [ ] **P11-T03** Implementar filtro temporal, servicio, EventID/EventName, severidad y `trace_id`.
- [ ] **P11-T04** Añadir predicados sobre campos tipados (`=`, `!=`, rangos, existe, contains, begins-with según tipo).
- [ ] **P11-T05** Diseñar índices por segmentos: min/max, presencia de eventos, conteos y filtros probabilísticos.
- [ ] **P11-T06** Implementar localizadores por trace/operation/event reference.
- [ ] **P11-T07** Investigar filtros Bloom/Xor y medir tamaño/falsos positivos.
- [ ] **P11-T08** Construir índices selectivos por campos de alta utilidad, evitando indexar todo.
- [ ] **P11-T09** Implementar búsqueda de texto en una fase posterior o como adapter, con garantías claras.
- [ ] **P11-T10** Implementar query planner que use índices antes de decodificar streams.
- [ ] **P11-T11** Implementar lecturas parciales de columnas/campos cuando el formato permita.
- [ ] **P11-T12** Crear paginación estable por cursor y versión de snapshot; evitar duplicados/saltos causados por compactación.
- [ ] **P11-T13** Añadir límites configurables de tiempo, memoria, resultados y bytes de respuesta.
- [ ] **P11-T14** Soportar cancelar consultas y aislarlas del writer.
- [ ] **P11-T15** Añadir agregaciones: conteo por evento/severidad, series temporales, top fingerprints.
- [ ] **P11-T16** Añadir `EXPLAIN` para indicar segmentos consultados, índices usados y coste estimado.
- [ ] **P11-T17** Documentar semántica de consistencia: snapshot, near-real-time, eventual, y datos incompletos.
- [ ] **P11-T18** Crear pruebas de corrección con oracle full-scan; verificar igualdad de resultados.
- [ ] **P11-T19** Comparar latencia de 10–20 consultas patrón y uso de recursos con baselines.
- [ ] **P11-T20** Diseñar API estable para consultas locales y futuro servidor remoto.

**Gate G11:** corrección contra full-scan, consultas sin decodificación global, paginación robusta y explicación de lagunas/limitaciones.

### P12 — Lectura AI-friendly sin IA: Context Packs y evidencias

**Objetivo:** que cualquier herramienta externa —humana, script o IA— reciba un contexto más útil y económico que un volcado de logs.  
**Dependencias:** P11.  
**Entregables:** `specs/evidence-protocol.md`, JSONL/JSON compact, CLI context, evaluador de calidad.

**Principio:** **LatticeLog solo estructura, filtra, agrupa y recupera; no interpreta con modelos ni afirma automáticamente una causa raíz.**

**Tareas**

- [ ] **P12-T01** Definir salida pública JSON canónica de eventos, sin exponer directamente el binario.
- [ ] **P12-T02** Especificar `event_ref` estable y endpoint/comando de recuperación del evento original.
- [ ] **P12-T03** Implementar `overview` por ventana: servicios, eventos, errores, recuentos y rangos.
- [ ] **P12-T04** Implementar `get_trace`: timeline ordenada, relaciones explícitas y timestamps relativos.
- [ ] **P12-T05** Implementar `get_related_events` usando trace/span/operation y vínculos declarados; distinguir relación de coincidencia temporal.
- [ ] **P12-T06** Implementar agrupación determinista por fingerprints configurables y entregar conteos y muestras referenciadas.
- [ ] **P12-T07** Diseñar Context Pack v1 por incidente/trace/time range y selección por relevancia heurística explícita.
- [ ] **P12-T08** Aplicar presupuesto en bytes/caracteres y opcionalmente tokens estimados; no garantizar tokens exactos entre modelos.
- [ ] **P12-T09** Preservar cobertura: totales, cantidad devuelta, segmentos excluidos, trunca, sampling, filtros y lagunas.
- [ ] **P12-T10** Asegurar que toda síntesis numérica provenga de datos computados, no de texto inventado.
- [ ] **P12-T11** Añadir niveles de detalle `overview`, `trace`, `evidence` y acceso progresivo.
- [ ] **P12-T12** Incorporar opcionalmente contexto de código `repo/commit/function`, únicamente cuando esté registrado y autorizado.
- [ ] **P12-T13** Exponer `schema`, descripción de eventos, tipos y diccionario semántico legible.
- [ ] **P12-T14** Permitir formatos `json`, `jsonl`, `markdown`, `ndjson`, con normalización de campos.
- [ ] **P12-T15** Aplicar redacción de secretos y autorizaciones en todas las salidas.
- [ ] **P12-T16** Escapar y etiquetar texto no confiable procedente de logs para evitar que clientes lo interpreten como instrucciones.
- [ ] **P12-T17** Diseñar benchmark externo: mismo incidente/modelo/preguntas; comparar texto raw, JSON estándar y Context Pack.
- [ ] **P12-T18** Medir tokens, evidencias relevantes recuperadas, falsos diagnósticos, número de consultas y latencia total.
- [ ] **P12-T19** Crear dataset de «incident ground truth» con errores causales conocidos y rare events.
- [ ] **P12-T20** Versionar `EvidenceProtocol` independientemente del `.llog` físico.

**Gate G12:** salidas deterministas, verificables y sin omisiones silenciosas; demostrar utilidad real para lectores externos sin integrar ningún LLM.

### P13 — Interoperabilidad: OpenTelemetry, trazas y formatos existentes

**Objetivo:** evitar que el producto solo funcione cuando toda la empresa cambia de SDK.  
**Dependencias:** P01, P11, P12.  
**Entregables:** bridges OTel/ILogger, importers, exporters, pruebas de round-trip semántico.

**Tareas**

- [ ] **P13-T01** Definir mapping bidireccional entre LatticeLog y OpenTelemetry Logs Data Model.
- [ ] **P13-T02** Mapear `Resource`, `InstrumentationScope`, `Severity`, `Attributes`, `TraceId`, `SpanId`, timestamps y EventName.
- [ ] **P13-T03** Implementar exportador JSONL y OTLP en versión compatible del protocolo.
- [ ] **P13-T04** Implementar receptor de registros OpenTelemetry/OTLP opcional, con límites y validaciones.
- [ ] **P13-T05** Implementar importador JSONL estructurado y preservación raw cuando se solicite.
- [ ] **P13-T06** Implementar parser de texto libre opcional y señalar precisión/ambigüedad de parsing.
- [ ] **P13-T07** Conservar `TraceContext` y baggage solo según políticas explícitas; no copiar secretos.
- [ ] **P13-T08** Implementar bridge ILogger/Serilog/NLog como adaptadores, verificando versiones.
- [ ] **P13-T09** Definir pérdida de expresividad inevitable entre modelos y cómo indicarla (nunca ocultarla).
- [ ] **P13-T10** Diseñar exportación completa que permita abandonar el formato `.llog`.
- [ ] **P13-T11** Evaluar integración con OpenTelemetry Collector, archivos de Kubernetes y contenedores.
- [ ] **P13-T12** Probar timestamps, niveles de severidad y estructuras de atributo de cada fuente.
- [ ] **P13-T13** Publicar cookbook de migración gradual desde logs existentes.
- [ ] **P13-T14** Validar que integración OTel no impone un segundo pipeline costoso en aplicaciones sensibles.

**Gate G13:** importar/exportar con fidelidad documentada, sin obligar a adoptar un nuevo stack de telemetría.

### P14 — SDK .NET listo para producción (Local v1.0)

**Objetivo:** convertir el prototipo en biblioteca segura para aplicaciones reales.  
**Dependencias:** P03–P13.  
**Entregables:** paquetes NuGet, compatibilidad, API estable, guía de upgrade, release candidate local.

**Tareas**

- [ ] **P14-T01** Congelar contratos públicos mínimos y diseño de errores.
- [ ] **P14-T02** Asegurar semántica de `Dispose/DisposeAsync`, orden de flush, timeout de shutdown y crash handling.
- [ ] **P14-T03** Revisar allocations y p99 en logging sincrónico/asíncrono bajo diversas cargas.
- [ ] **P14-T04** Verificar thread-safety y ausencia de deadlocks en saturación, recursión y logging durante fallo.
- [ ] **P14-T05** Implementar validación de configuración con mensajes de error accionables.
- [ ] **P14-T06** Optimizar arranque en frío, memoria base y consumo de recursos cuando no hay eventos.
- [ ] **P14-T07** Añadir diagnóstico interno mínimo sin bucles de logging recursivos.
- [ ] **P14-T08** Revisar trimming, Native AOT y compatibilidad con single-file deployment.
- [ ] **P14-T09** Probar Linux, Windows y macOS; separar soporte oficial por arquitectura.
- [ ] **P14-T10** Configurar paquetes firmados donde convenga, símbolos, SourceLink y SBOM.
- [ ] **P14-T11** Congelar golden files y pruebas de compatibilidad N-1/N-2.
- [ ] **P14-T12** Ejecutar auditoría interna de secretos, rutas, permisos y evasión de límites.
- [ ] **P14-T13** Probar aplicaciones de muestra con logs reales y perf profiles.
- [ ] **P14-T14** Publicar `getting-started`, breaking changes y troubleshooting.
- [ ] **P14-T15** Liberar RC; gestionar incidencias críticas y reproducir benchmarks públicos.
- [ ] **P14-T16** Liberar v1.0 local únicamente si la política de durabilidad está demostrada.

**Gate Local v1.0:** API estable, lector y escritor compatibles, fallos recuperables, documentación utilizable y SLOs declarados con resultados medidos.

### P15 — CLI, herramientas para desarrolladores y distribución local

**Objetivo:** hacer que LatticeLog sea útil fuera del proceso y desde entornos de desarrollo.  
**Dependencias:** P14.  
**Entregables:** ejecutable CLI y comandos documentados, paquetes de distribución, examples.

**Tareas**

- [ ] **P15-T01** Crear comandos `init`, `tail`, `inspect`, `query`, `trace`, `context`, `verify`, `repair`, `compact`, `stats`, `export`.
- [ ] **P15-T02** Diseñar salida humana por defecto y `--format json/jsonl` estable para scripts.
- [ ] **P15-T03** Establecer códigos de salida predecibles y errores con estructura máquina-legible.
- [ ] **P15-T04** Crear paginación/cursor para salidas grandes; limitar exportación accidental de gigabytes.
- [ ] **P15-T05** Implementar `tail` sobre segmentos en crecimiento con semántica de lecturas parciales.
- [ ] **P15-T06** Incorporar `--budget-bytes` y redacción para `context`.
- [ ] **P15-T07** Implementar comandos de mantenimiento que funcionen sin servidor.
- [ ] **P15-T08** Distribuir binarios self-contained o Native AOT donde sea viable.
- [ ] **P15-T09** Publicar instaladores/paquetes según sistemas operativos; firmar y verificar integridad.
- [ ] **P15-T10** Crear completado shell, ayuda y ejemplos funcionales.
- [ ] **P15-T11** Probar en CI instaladores de Windows/Linux/macOS y sus permisos.
- [ ] **P15-T12** Probar automatización en pipelines y aislamiento de logs de salida de comandos.
- [ ] **P15-T13** Documentar migración, exportación y procedimiento de recuperación de datos.

**Gate G15:** cualquier desarrollador puede crear, consultar, verificar y exportar logs con CLI sin programar ni instalar servidor.

### P16 — Protocolo de ingestión remota y collector

**Objetivo:** recibir eventos de múltiples aplicaciones sin que el almacenamiento local desaparezca como opción.  
**Dependencias:** P14–P15.  
**Entregables:** collector service, protocolo cliente-servidor, política de reintentos/ACK, pruebas de red.

**Tareas**

- [ ] **P16-T01** Definir transporte inicial (p. ej. gRPC/HTTP2 o HTTP batch) y justificar el coste de SDK.
- [ ] **P16-T02** Diseñar envelope de envío con `producer_id`, secuencia, esquema, batch ID y checksum.
- [ ] **P16-T03** Definir semántica de entrega `at-least-once` o equivalente; no prometer exactly-once sin protocolo completo.
- [ ] **P16-T04** Implementar idempotencia y deduplicación por batch/event sequence, preservando eventos distintos.
- [ ] **P16-T05** Configurar ACK según persistencia local del servidor y estado de réplica cuando aplique.
- [ ] **P16-T06** Implementar retry con backoff/jitter y límites.
- [ ] **P16-T07** Implementar spool local acotado para fallos de conectividad.
- [ ] **P16-T08** Definir qué ocurre cuando spool está lleno y cómo se reporta al productor.
- [ ] **P16-T09** Soportar negociación de versiones de esquema/codec y fallbacks.
- [ ] **P16-T10** Implementar TLS, autenticación de cliente y rotación de credenciales desde el inicio.
- [ ] **P16-T11** Aplicar cuotas por cliente, tamaño de mensaje y backpressure de red.
- [ ] **P16-T12** Diseñar protección frente a eventos gigantes y bombas de descompresión.
- [ ] **P16-T13** Crear pruebas de red: packet loss, timeouts, cortes, duplicados, red lenta y reordenación.
- [ ] **P16-T14** Medir throughput end-to-end, memoria de spool y latencia del ACK.
- [ ] **P16-T15** Documentar límites de garantías cuando una aplicación cae antes de persistir o transmitir.

**Gate G16:** productor puede desconectarse, reintentar y recuperarse sin duplicados silenciosos ni confirmaciones falsas.

### P17 — Servidor de consultas, catálogo y multiusuario (Server v1.0)

**Objetivo:** habilitar almacenamiento y lectura centralizados con roles y namespaces.  
**Dependencias:** P11, P16.  
**Entregables:** `latticelog-server`, metadata catalog, HTTP/gRPC read API, deployment Docker.

**Tareas**

- [ ] **P17-T01** Diseñar `tenant`, `project`, `environment`, `service`, `source`, `namespace`.
- [ ] **P17-T02** Separar metadata/control plane de datos pesados, con persistencia transaccional.
- [ ] **P17-T03** Implementar catálogo de segmentos, manifest, schemas, contextos, índices y estado de compactación.
- [ ] **P17-T04** Crear endpoints para ingestion, search, get event, get trace, aggregations, export y context pack.
- [ ] **P17-T05** Implementar autenticación/autorización mínima desde la primera beta del servidor.
- [ ] **P17-T06** Imponer cuotas por usuario/tenant para consultas, bytes, concurrencia e ingesta.
- [ ] **P17-T07** Implementar cursores firmados o protegidos contra manipulación y expiración.
- [ ] **P17-T08** Separar consultas lentas de ingestión, con pools y límites independientes.
- [ ] **P17-T09** Definir snapshots de consultas y consistencia tras compactación.
- [ ] **P17-T10** Habilitar lectura de datos recientes y segmentos compactados con un planificador único.
- [ ] **P17-T11** Documentar semántica de timestamps de llegada vs tiempo de evento.
- [ ] **P17-T12** Implementar tareas administrativas para retención, compacción y reindexación.
- [ ] **P17-T13** Probar migraciones de metadatos y rollback cuando sea viable.
- [ ] **P17-T14** Construir imagen Docker minimalista, configuración por env/secret y health endpoints.
- [ ] **P17-T15** Elaborar guía de despliegue simple en una sola máquina, sin K8s obligatorio.
- [ ] **P17-T16** Ejecutar pruebas de carga con múltiples productores y lectores concurrentes.

**Gate Server v1.0:** múltiples fuentes logran ingestar y consultar de forma segura, con persistencia recuperable y sin exigir clúster.

### P18 — Seguridad, privacidad, gobierno y auditabilidad

**Objetivo:** proteger información que puede incluir secretos, PII y evidencias sensibles.  
**Dependencias:** aplicar mínimos desde P01, endurecer P16–P17.  
**Entregables:** threat model, políticas RBAC, security test suite, informe de revisión.

**Tareas**

- [ ] **P18-T01** Realizar threat modeling de SDK, binario, parser, collector, query, MCP, UI, server y cloud.
- [ ] **P18-T02** Diseñar clasificación de datos y políticas de captura (`never-log`, `redact`, `hash`, `allow-list`).
- [ ] **P18-T03** Implementar redacción en productor donde sea viable, antes de persistir secretos.
- [ ] **P18-T04** Implantar TLS para todas las conexiones remotas y cifrado en reposo según modo de despliegue.
- [ ] **P18-T05** Gestionar claves mediante KMS/secret store cuando exista; no incluir secretos en archivos de configuración publicados.
- [ ] **P18-T06** Definir RBAC/ABAC por tenant, proyecto, servicio, ambiente y campo si aplica.
- [ ] **P18-T07** Establecer acceso read-only específico para herramientas externas y tokens de tiempo limitado.
- [ ] **P18-T08** Crear auditoría inmutable o protegida de accesos, exportaciones y operaciones administrativas.
- [ ] **P18-T09** Mitigar consultas costosas, abuso de filtros, path traversal, parser attacks y resource exhaustion.
- [ ] **P18-T10** Tratar texto de logs como contenido no confiable en HTML, Markdown y herramientas MCP.
- [ ] **P18-T11** Probar aislamiento entre tenants con suites automatizadas de autorización negativa.
- [ ] **P18-T12** Implementar borrado por retención y solicitudes de privacidad, con reglas explícitas para backups y replicación.
- [ ] **P18-T13** Diseñar controles de legal hold y evidencias cuando sea necesario.
- [ ] **P18-T14** Configurar SAST, dependency scanning, secret scanning, SBOM y reporte de vulnerabilidades.
- [ ] **P18-T15** Diseñar respuesta a incidentes: rotación, revocación, disclosure, CVEs, parches.
- [ ] **P18-T16** Obtener revisión independiente/pentest antes de vender SaaS con datos de terceros.
- [ ] **P18-T17** Revisar legislación y obligaciones aplicables por jurisdicción con asesoría especializada, sin prometer certificación automática.
- [ ] **P18-T18** Verificar que Context Packs no omiten etiquetas de datos sensibles ni revelan campos autorizados parcialmente.

**Gate G18:** pruebas negativas multi-tenant exitosas, cifrado/credenciales correctamente implementados y política de vulnerabilidades/privacidad vigente.

### P19 — Escalabilidad horizontal, particionado y replicación

**Objetivo:** soportar crecimiento sin depender de un único nodo y preservar garantías durante fallos de red.  
**Dependencias:** P17–P18.  
**Entregables:** clúster mínimo viable, rebalancing, replicación, pruebas de caos.

**Tareas**

- [ ] **P19-T01** Medir límites del servidor single-node antes de justificar la distribución.
- [ ] **P19-T02** Definir unidades de shard (tenant/project/source/time bucket) y compatibilidad con consultas.
- [ ] **P19-T03** Diseñar router de ingestión y query fan-out con límites de concurrencia.
- [ ] **P19-T04** Elegir control-plane/metadata store para miembros, asignaciones y manifests.
- [ ] **P19-T05** Especificar mecanismo de leader/epoch/fencing para evitar escritores concurrentes válidos tras partición.
- [ ] **P19-T06** Definir modelo de consistencia por índice, metadatos y datos (quorum o leader append, según diseño).
- [ ] **P19-T07** Implementar replicación de segmentos y reconocer ACK solo según política configurada.
- [ ] **P19-T08** Diseñar detección de duplicados por batch/secuencia y reanudación tras failover.
- [ ] **P19-T09** Crear snapshots y reconstrucción de índice/catálogo desde segmentos.
- [ ] **P19-T10** Implementar movimiento de shards, backfill y throttling.
- [ ] **P19-T11** Evitar que la compactación rompa referencias lógicas durante la migración entre nodos.
- [ ] **P19-T12** Aplicar cuotas y aislamiento de recursos en nodos compartidos.
- [ ] **P19-T13** Implementar consultas parciales con `partial_results` y shards ausentes claramente indicados.
- [ ] **P19-T14** Probar particiones de red, caídas de líder, discos llenos, split-brain y lag de réplicas.
- [ ] **P19-T15** Benchmarkear escalado de 1, 3, 5, N nodos y cuantificar coste por TB/evento.
- [ ] **P19-T16** Documentar topologías recomendadas, límites y runbook de recuperación.

**Gate G19:** ningún fallo de nodo conduce a confirmaciones falsas o corrupción silenciosa; escalado horizontal medido y límites de consistencia documentados.

### P20 — Almacenamiento por niveles, object storage y retención

**Objetivo:** reducir coste de histórico sin perder búsqueda selectiva y gobernanza.  
**Dependencias:** P19 (una variante single-node puede prototiparse antes).  
**Entregables:** hot/warm/cold lifecycle, adaptadores de almacenamiento y retention engine.

**Tareas**

- [ ] **P20-T01** Definir segmentos `hot` (escritura), `warm` (indexado/compactado) y `cold` (objetos baratos).
- [ ] **P20-T02** Diseñar ciclo de vida por antigüedad, acceso, tamaño y política de tenant.
- [ ] **P20-T03** Implementar almacenamiento de objetos (S3-compatible primero) y validación de checksums.
- [ ] **P20-T04** Diseñar manifest y commit atómico con object storage eventual y cachés.
- [ ] **P20-T05** Implementar rehidratación selectiva para consultas históricas.
- [ ] **P20-T06** Mantener índice compacto de objetos fríos para no descargarlos enteros.
- [ ] **P20-T07** Diseñar caché LRU/ARC o alternativa basada en métricas y límites por tenant.
- [ ] **P20-T08** Implementar expiración y borrado de datos con verificación de referencias compartidas.
- [ ] **P20-T09** Añadir reglas de retención por proyecto, categoría y severidad, con excepciones legales explícitas.
- [ ] **P20-T10** Diseñar mantenimiento de snapshots/backups y consistencia con object storage.
- [ ] **P20-T11** Probar pérdida de conexión, corrupción de objetos, objetos huérfanos y recuperación de manifiestos.
- [ ] **P20-T12** Medir latencia p95 de query cold, coste por almacenamiento y coste de egress.
- [ ] **P20-T13** Crear simulador de costes por mes/TB y políticas de tiering.
- [ ] **P20-T14** Documentar reversibilidad: exportación y recuperación sin proveedor cloud específico.

**Gate G20:** rotación, rehidratación, borrado y recuperación están probados; referencias e índices siguen siendo correctos en todos los tiers.

### P21 — Ecosistema SDK multilenguaje

**Objetivo:** ampliar adopción sin duplicar la lógica propietaria de codecs en todos los ecosistemas.  
**Dependencias:** P14–P16.  
**Entregables:** SDKs Java, Go, Python y Node.js con pruebas de compatibilidad cruzada.

**Tareas**

- [ ] **P21-T01** Definir arquitectura de SDKs: clientes de transporte sencillos; escritor `.llog` nativo solo donde sea justificable.
- [ ] **P21-T02** Crear especificación de protocolo y contract tests independientes del lenguaje.
- [ ] **P21-T03** Publicar biblioteca de test vectors para tipos, valores, excepciones y contexto.
- [ ] **P21-T04** Implementar SDK Java (SLF4J/Log4j2 bridge opcional, context propagation, batching).
- [ ] **P21-T05** Medir costes del runtime Java y asegurar comportamiento correcto con virtual threads si aplica.
- [ ] **P21-T06** Implementar SDK Go con campos tipados, batching y política de bloqueo.
- [ ] **P21-T07** Evaluar bridge para `log/slog`, sin imponer una interfaz propia a todas las aplicaciones.
- [ ] **P21-T08** Implementar SDK Python compatible con `logging`/structlog, con envíos en segundo plano controlados.
- [ ] **P21-T09** Diseñar semántica de multiproceso y shutdown para Python.
- [ ] **P21-T10** Implementar SDK Node.js/TypeScript con worker transport cuando convenga.
- [ ] **P21-T11** Ofrecer bridge para Pino en Node.js y documentación de comportamiento en event loop.
- [ ] **P21-T12** Mantener el mismo modelo de esquemas, severidad y trace context en todos los SDKs.
- [ ] **P21-T13** Añadir certificados TLS, autenticación y reintentos con semántica consistente.
- [ ] **P21-T14** Publicar paquetes oficiales en NuGet, Maven Central, PyPI, npm y módulos Go.
- [ ] **P21-T15** Probar compatibilidad cruzada de versiones N-1/N-2 del protocolo.
- [ ] **P21-T16** Crear ejemplos de microservicios interconectados en diferentes lenguajes.
- [ ] **P21-T17** Medir sobrecarga de cada SDK frente a logging base del ecosistema; no extrapolar benchmarks .NET.
- [ ] **P21-T18** Crear una política de mantenimiento por versión/runtimes de cada SDK.

**Gate G21:** SDKs cumplen contract tests y semántica de pérdida, reintento, autenticación y exportación; la lectura del formato sigue siendo independiente del productor.

### P22 — Adaptador MCP y herramientas para agentes externos (sin ejecutar IA)

**Objetivo:** facilitar que cualquier IDE/agente/IA externa consulte evidencia con pocas llamadas y sin privilegios excesivos.  
**Dependencias:** P12, P18.  
**Entregables:** `latticelog-mcp` read-only, contratos de herramientas, conformance y pruebas de seguridad.

**Tareas**

- [ ] **P22-T01** Identificar versión del estándar MCP vigente al implementar y congelar versión objetivo.
- [ ] **P22-T02** Crear herramientas `search_events`, `get_event`, `get_trace`, `get_related_events`, `get_error_groups`, `get_context_pack`, `get_event_schema`.
- [ ] **P22-T03** Usar los mismos servicios de Query Engine que CLI/HTTP; evitar lógica duplicada de interpretación.
- [ ] **P22-T04** Definir JSON Schema de entradas/salidas y errores estables.
- [ ] **P22-T05** Habilitar transporte stdio local de mínima exposición; HTTP remoto solo con auth robusta.
- [ ] **P22-T06** Implementar scopes read-only y filtros obligatorios por tenant/proyecto.
- [ ] **P22-T07** Poner límites de consultas, datos devueltos y presupuesto de bytes por sesión.
- [ ] **P22-T08** Evitar inyecciones de instrucciones: outputs separan metadatos fiables de texto de log no confiable.
- [ ] **P22-T09** Redactar o bloquear secretos/PII y aplicar autorizaciones por campo.
- [ ] **P22-T10** Incluir provenance, `partial_results`, truncamiento y `event_ref` recuperable en cada salida.
- [ ] **P22-T11** Crear guías de conexión para Cursor, Claude Code, Codex y otros clientes compatibles cuando se verifique soporte.
- [ ] **P22-T12** Ejecutar suite de conformidad MCP y pruebas de límites/abuso.
- [ ] **P22-T13** Evaluar calidad con IA externa en laboratorio separado: misma incidencia y presupuesto de consultas.
- [ ] **P22-T14** Documentar que MCP es un adaptador opcional y no está presente en la ruta de captura.

**Gate G22:** una IA externa puede consultar logs con contexto y evidencia de forma segura, pero LatticeLog no llama a ningún modelo por sí mismo.

### P23 — Interfaz de usuario de operación y exploración

**Objetivo:** proporcionar un entorno humano para encontrar, consultar y administrar logs sin depender de un terminal.  
**Dependencias:** P17–P18.  
**Entregables:** Web Console, diseño UX, tests E2E, accesibilidad.

**Tareas**

- [ ] **P23-T01** Hacer entrevistas o pruebas con usuarios de desarrollo, DevOps y soporte.
- [ ] **P23-T02** Definir IA de navegación: Overview, Search, Traces, Event Schemas, Storage, Sources, Settings.
- [ ] **P23-T03** Diseñar vista de búsqueda con filtros legibles, búsqueda por campo, fechas y paginación.
- [ ] **P23-T04** Crear inspector de eventos con contexto heredado, campos raw, schema y enlace de referencia.
- [ ] **P23-T05** Crear timeline de operación/trace con relaciones explícitas y marcadores de lagunas.
- [ ] **P23-T06** Añadir vista de fingerprints y volumen de errores con conteos exactos o aproximaciones declaradas.
- [ ] **P23-T07** Mostrar costos operativos: tamaño hot/warm/cold, ratios, retención e ingestión.
- [ ] **P23-T08** Añadir exportación de Context Pack y enlaces verificables a registros.
- [ ] **P23-T09** Diseñar control de permisos, proyectos y API keys con UX seguro.
- [ ] **P23-T10** Implementar dark/light, responsive y accesibilidad WCAG apropiada.
- [ ] **P23-T11** Usar virtualización y streaming para resultados masivos.
- [ ] **P23-T12** Evitar XSS de mensajes de log y rendering peligroso de Markdown/html.
- [ ] **P23-T13** Crear pruebas E2E de búsqueda, exportación, auth y tenant isolation.
- [ ] **P23-T14** Medir usabilidad: tareas completadas y tiempo para hallar la evidencia de una incidencia.
- [ ] **P23-T15** Documentar cuándo la UI muestra datos parciales o retrasados.

**Gate G23:** flujos de usuario básicos funcionan sin terminal, con acceso seguro y claridad sobre la cobertura de resultados.

### P24 — Operación, SRE, backup y disaster recovery

**Objetivo:** operar LatticeLog con visibilidad, recuperación y procedimientos antes de aceptar clientes de producción.  
**Dependencias:** P19–P23.  
**Entregables:** runbooks, dashboards, alertas, backup/restore automático y pruebas DR.

**Tareas**

- [ ] **P24-T01** Definir SLI/SLO para ingestión, disponibilidad, búsqueda, lag de réplica, durabilidad y restauración.
- [ ] **P24-T02** Exponer métricas del propio motor sin crear bucles de logs infinitos.
- [ ] **P24-T03** Crear dashboards de cola, fallos de ACK, compactación, discos, CPU, cache, consultas y tenants.
- [ ] **P24-T04** Diseñar alertas con umbrales y acciones para saturación, datos no replicados, pérdida de segmento y seguridad.
- [ ] **P24-T05** Implementar backup consistente de metadatos, manifests y datos.
- [ ] **P24-T06** Realizar restore en infraestructura nueva y verificar integridad tras recuperación.
- [ ] **P24-T07** Definir RPO/RTO por plan, junto con limitaciones técnicas reales.
- [ ] **P24-T08** Ensayar disaster recovery regional para despliegues que lo requieran.
- [ ] **P24-T09** Crear runbooks para nodo caído, corrupción, disco lleno, cola saturada, claves comprometidas y migraciones fallidas.
- [ ] **P24-T10** Implementar despliegues canary/rolling y verificación de compatibilidad antes de upgrade.
- [ ] **P24-T11** Probar rollback seguro ante cambios de binario, schema y versión de servidor.
- [ ] **P24-T12** Hacer simulacros de incidentes de disponibilidad y pérdida de conectividad.
- [ ] **P24-T13** Crear presupuestos operativos: CPU/hora, costo TB/mes, egress, IO y ancho de banda.
- [ ] **P24-T14** Establecer on-call, escalamiento, severidades y comunicación de incidentes.
- [ ] **P24-T15** Preparar documentación de instalaciones on-premises, Docker y Kubernetes.
- [ ] **P24-T16** Establecer límites de capacidad y guías de dimensionamiento reproducibles.

**Gate G24:** restauración probada, SLOs observables, runbooks ejecutados y políticas de intervención realistas.

### P25 — Producto comercial, licencias, facturación y SaaS

**Objetivo:** convertir tecnología en producto sostenible sin comprometer portabilidad ni confianza.  
**Dependencias:** P18–P24.  
**Entregables:** plan comercial, licencia aprobada, SaaS funcional, billing y documentación legal.

**Tareas**

- [ ] **P25-T01** Validar problema económico: precio actual de logs, coste de almacenamiento, consultas y esfuerzo de diagnóstico.
- [ ] **P25-T02** Entrevistar equipos interesados y cuantificar volumen, requisitos y willingness-to-pay.
- [ ] **P25-T03** Elegir licencia para SDK/format/core y licencias de módulos empresariales con revisión legal.
- [ ] **P25-T04** Asegurar que exportación y lector abierto sean suficientes para portabilidad.
- [ ] **P25-T05** Definir ediciones Community, Server/Enterprise y Cloud con límites claros.
- [ ] **P25-T06** Definir unidades de cobro: GB ingeridos, GB almacenados, consultas, retención o combinaciones.
- [ ] **P25-T07** Prototipar pricing usando costos reales por perfil de cliente; no copiar precios sin medir costes.
- [ ] **P25-T08** Implementar medición de uso auditable por tenant y reconciliación de bytes.
- [ ] **P25-T09** Implementar cuotas, sobreuso y avisos antes de bloqueos.
- [ ] **P25-T10** Integrar billing con proveedor seleccionado y flujo seguro de webhook/pagos.
- [ ] **P25-T11** Crear onboarding self-service: registro, proyecto, token, instalación, primer evento, primera query.
- [ ] **P25-T12** Diseñar aislamiento multitenant y opciones de despliegue dedicado para clientes empresariales.
- [ ] **P25-T13** Preparar políticas de privacidad, DPA, SLA, condiciones de servicio y residencia de datos con asesoría profesional.
- [ ] **P25-T14** Crear panel de facturación, estado de suscripción, uso y presupuestos.
- [ ] **P25-T15** Construir sistema de licencias y actualizaciones para edición Enterprise, sin afectar lectura de datos históricos.
- [ ] **P25-T16** Establecer proceso de ventas, demo, prueba gratuita y feedback de adopción.
- [ ] **P25-T17** Ejecutar beta privada con usuarios reales, priorizar incidencias y medir conversión/retención.
- [ ] **P25-T18** Definir cláusulas de salida: exportación íntegra, borrado verificable y portabilidad.
- [ ] **P25-T19** Medir márgenes por cohorte y escenarios de alto egress/consulta.

**Gate Cloud GA:** costos controlados, identidad y aislamiento sólidos, flujo de pago probado, términos revisados y backups/DR operativos.

### P26 — Calidad de producto, pruebas de caos y certificación interna

**Objetivo:** asegurar comportamiento fiable y resultados repetibles a nivel de producto completo.  
**Dependencias:** todas las fases anteriores; parte de las pruebas comienzan en P02.  
**Entregables:** quality report, matriz de compatibilidad, reporte de seguridad y de rendimiento.

**Tareas**

- [ ] **P26-T01** Consolidar coverage de unit/integration/e2e, priorizando invariantes y rutas críticas en vez de porcentaje aislado.
- [ ] **P26-T02** Ejecutar property-based testing para codecs, schemas, adaptadores y query results.
- [ ] **P26-T03** Ejecutar fuzzing de parser binario, HTTP, importadores y MCP.
- [ ] **P26-T04** Ejecutar benchmarks con workloads reales, adversariales y datasets públicos.
- [ ] **P26-T05** Verificar integridad bajo corrupción de segmentos, cancelación de compactación, upgrade y rollback.
- [ ] **P26-T06** Ejecutar estrés prolongado multi-hilo y multiproceso en Linux/Windows.
- [ ] **P26-T07** Validar jitter, p99.9 y latencias de colas, no solo throughput promedio.
- [ ] **P26-T08** Probar recuperación ante `kill -9`, pérdida de nodo, fallo de red, rollback de metadatos y disco lleno.
- [ ] **P26-T09** Validar aislamiento de tenants, RBAC, redacción, exportación y audit logs.
- [ ] **P26-T10** Verificar interoperabilidad entre SDKs, lectores, versiones y sistemas operativos.
- [ ] **P26-T11** Hacer auditoría de dependencias, SBOM, licencias y cadena de suministro.
- [ ] **P26-T12** Auditar cambios de esquema/archivo con pruebas de retrocompatibilidad automatizadas.
- [ ] **P26-T13** Ejecutar evaluación externa de lectura AI-friendly con incidentes conocidos y estadísticas honestas.
- [ ] **P26-T14** Publicar benchmark metodológico con condiciones suficientes para reproducir resultados.
- [ ] **P26-T15** Crear performance budgets en CI que detengan regresiones confirmadas.
- [ ] **P26-T16** Establecer lista de bugs bloqueantes (corrupción, cross-tenant leak, ACK falso, crash no recuperable).
- [ ] **P26-T17** Cerrar todos los blockers y documentar limitaciones que se aceptan conscientemente.

**Gate G26:** calidad y seguridad demostrables bajo fallos, con benchmarks públicos sin afirmaciones universales.

### P27 — Lanzamiento, documentación, adopción y soporte

**Objetivo:** distribuir un producto comprensible, sostenible y que otros puedan evaluar por sí mismos.  
**Dependencias:** P25–P26.  
**Entregables:** sitio/documentación, paquetes públicos, changelog, demo, política de versiones y comunidad.

**Tareas**

- [ ] **P27-T01** Finalizar README orientado al problema: instalación en minutos, primero local y luego server.
- [ ] **P27-T02** Publicar referencia completa de SDKs, formato `.llog`, API Query, CLI, MCP y configuración.
- [ ] **P27-T03** Publicar guía de migración desde Serilog, ILogger, Log4j, Pino y exportación OTLP.
- [ ] **P27-T04** Publicar ejemplos completos de backend .NET, microservicios mixtos y despliegue local/cloud.
- [ ] **P27-T05** Crear demo de diagnóstico: búsqueda habitual frente a Context Pack verificable, con límites explícitos.
- [ ] **P27-T06** Publicar resultados de benchmarks con hardware, datasets, versiones y scripts.
- [ ] **P27-T07** Crear documentación de codecs, algoritmos, decisiones y limitaciones científicas.
- [ ] **P27-T08** Publicar `CHANGELOG`, guía de upgrade, versioning y matrices de compatibilidad.
- [ ] **P27-T09** Establecer canales de issues, vulnerabilidades privadas, discusiones y soporte.
- [ ] **P27-T10** Crear política de revisión de PR, contribuciones, código de conducta y pruebas obligatorias.
- [ ] **P27-T11** Verificar marca, dominio, licencias y atribuciones antes de publicación comercial.
- [ ] **P27-T12** Crear landing con propuesta concreta basada en métricas verificadas, evitando prometer mejoras universales.
- [ ] **P27-T13** Ejecutar release candidate público, recoger feedback y corregir problemas críticos.
- [ ] **P27-T14** Publicar versión estable y proceso de rollback/soporte del día de lanzamiento.
- [ ] **P27-T15** Medir activación, logs ingeridos, consultas, retention de desarrolladores y fallos de onboarding.

**Gate Product GA:** instalación y migración documentadas, referencias abiertas, producto probado y soporte operativo definido.

### P28 — Mantenimiento sostenible, evolución y nuevas investigaciones

**Objetivo:** evitar que el producto deje de ser útil después del primer lanzamiento.  
**Dependencias:** P27.  
**Entregables:** roadmap trimestral/semestre, política de soporte, changelog continuo y backlog científico.

**Tareas recurrentes**

- [ ] **P28-T01** Mantener versiones soportadas de .NET, Java, Go, Python, Node y sistemas operativos.
- [ ] **P28-T02** Ejecutar benchmark de regresión en cada versión relevante del motor.
- [ ] **P28-T03** Mantener corpus público y datasets sanitizados con cambios controlados.
- [ ] **P28-T04** Actualizar investigación comparativa de codecs y consultas al menos periódicamente.
- [ ] **P28-T05** Evaluar nuevos codecs solo mediante experimentos de ablación y soporte de versionado.
- [ ] **P28-T06** Mantener compatibilidad de archivos históricos según ventana declarada.
- [ ] **P28-T07** Planificar migraciones y deprecaciones con avisos y exportador funcional.
- [ ] **P28-T08** Responder CVEs y comunicar severidad/mitigación a clientes.
- [ ] **P28-T09** Ajustar capacidad y costes del SaaS según consumo real.
- [ ] **P28-T10** Mejorar herramientas para IA externa sin introducir modelos en el núcleo.
- [ ] **P28-T11** Investigar context-aware queries, correlaciones indexadas y lecturas todavía más compactas.
- [ ] **P28-T12** Investigar nuevos lenguajes/arquitecturas solo después de demanda real.
- [ ] **P28-T13** Recopilar métricas de adopción y priorizar problemas verificados.
- [ ] **P28-T14** Revisar licencia, seguridad, privacidad y obligaciones legales al entrar en nuevos mercados.
- [ ] **P28-T15** Publicar roadmap y estado de issues/compatibilidad de forma transparente.

**Gate de continuidad:** cada versión es medible, recuperable, compatible según política y sostenible económicamente.

---

## 3. Especificaciones transversales obligatorias

Estas reglas aplican a todas las fases, incluso si la fase específica aparece mucho después.

### 3.1. Contrato de pérdida, durabilidad y entrega

| Modo | Qué significa | Riesgo permitido | Qué NO debe afirmarse |
|---|---|---|---|
| `Fast` | Evento aceptado en buffer local | Pérdida si el proceso/máquina cae | «persistido» |
| `Balanced` | Escrito por lotes al SO/archivo | Pérdida ante fallos antes de sync | «durable en crash» |
| `Durable` | `fsync`/persistencia confirmada conforme al contrato | Fallos físicos y configuración aún importan | «imposible perder datos» |
| `Remote` | ACK con semántica de servidor explícita | Depende de spool y replicación | «exactly-once» si no está demostrado |

- Nunca indicar éxito de persistencia basándose únicamente en encolado en memoria.
- Toda pérdida por filtros, sampling, saturación o retención debe figurar en contadores/audit records.
- Para *exactly-once* aparente en el consumidor, evaluar idempotencia con IDs y secuencias; distinguir entrega de procesamiento y de almacenamiento.
- Separar reordenación de red de causalidad de negocio; una secuencia de timestamps no basta para deducir causalidad.

### 3.2. Contrato de «sin pérdida»

Dos perfiles distintos:

1. **Structured-exact:** valores tipados, esquemas, identificadores, relaciones y orden observable, recuperados sin cambio según representación lógica. Ejemplos: string NFC vs bytes originales deben definirse expresamente; no normalizar silenciosamente.
2. **Raw byte-exact:** al importar archivos de texto preservar los bytes originales, separadores, codificación y orden si se activa el modo de archivado fiel.

El motor no debe deduplicar eliminando eventos. Puede almacenar un patrón + número de ocurrencias **solo** si conserva los datos necesarios para reproducir las ocurrencias individuales cuando el contrato lo exija.

### 3.3. Modelo de lectura AI-friendly

**No hay componente de inferencia.** El lector debe poder devolver:

- `summary`: intervalos, recuentos y dimensiones agregadas.
- `timeline`: secuencia ordenada con marcas de reloj local y relaciones explícitas.
- `evidence`: eventos originales con IDs estables.
- `schema`: campos, significados declarados y versión.
- `coverage`: filtros, total, entregados, muestras, huecos, errores y truncamiento.
- `provenance`: origen del evento, segmento o identidad lógica, source location si se proporcionó.

La salida por defecto debe priorizar estructura sobre prosa. La prosa es útil para humanos, pero los agentes deben poder hacer consultas reproducibles y recibir JSON tipado.

**Ejemplo contractual de Context Pack (conceptual):**

```json
{
  "format": "latticelog.evidence.v1",
  "scope": {"project": "inventory", "trace_id": "4df..."},
  "coverage": {
    "matched_events": 248,
    "returned_events": 7,
    "sampling": "deterministic-representatives",
    "truncated": true,
    "has_missing_segments": false
  },
  "timeline": [
    {"ref": "llog://p1/e/00851", "dt_us": 0, "event": "request.started"},
    {"ref": "llog://p1/e/00872", "dt_us": 14050, "event": "inventory.stock.timeout", "error_code": "STOCK_TIMEOUT"}
  ],
  "query": {"version": 1, "filter": "trace_id = '4df...'"}
}
```

La presencia de `truncated=true` impide presentar la salida como exhaustiva. `llog://` es un esquema de referencia conceptual cuya sintaxis y autorización deben especificarse antes del lanzamiento.

### 3.4. Concurrencia y semántica del tiempo

- Guardar wall-clock timestamp y, cuando sea posible, reloj monotónico local para duraciones.
- Diseñar secuencias por productor y correlación explícita entre procesos.
- El tiempo de llegada al servidor NO sustituye el tiempo de ocurrencia.
- Contextos heredados deben ser inmutables/versionados al codificar eventos.
- Consultas distribuidas pueden necesitar merge de segmentos con datos fuera de orden; indicar límites y ventanas de consistencia.

### 3.5. Seguridad transversal

- Redacción previa a persistencia si existe certeza de campo secreto.
- Entradas de logs, atributos, archivos importados y texto que vuelve por MCP son **datos no confiables**.
- Mecanismos de autorización para export y búsqueda deben coincidir con los de lectura singular.
- No permitir a clientes ejecutar consultas arbitrarias fuera de su tenant, aunque tengan referencias lógicas válidas.
- Usar criptografía estándar y bibliotecas auditadas; **no inventar cifrado** como parte del algoritmo.

### 3.6. Políticas de versionado

- **Format version** (`.llog`): ruptura mayor solo con migración/compatibility mode bien definida.
- **Schema version**: por evento; mantener descriptors utilizados históricamente.
- **Codec version**: los bitstreams indican exactamente qué decoder necesitan.
- **Wire protocol version**: negociado al conectar.
- **Query API version**: independiente del formato binario.
- **Evidence API version**: independiente del Query API y de la persistencia.
- **SDK version**: semver por lenguaje con matriz de compatibilidad.

---

## 4. Estructura recomendada del monorepo

```text
latticelog/
├── README.md
├── LICENSE
├── ROADMAP.md
├── CHANGELOG.md
├── CONTRIBUTING.md
├── SECURITY.md
├── docs/
│   ├── product/                    # vision, PRD, perfiles de usuarios
│   ├── architecture/               # C4, ADRs, flujos
│   ├── decisions/                  # ADRs y phase reviews
│   ├── operations/                 # runbooks y DR
│   ├── security/                   # threat model, privacy
│   └── user-guides/                # SDKs, CLI, Query
├── specs/
│   ├── event-model.md
│   ├── llog-format-v0.md
│   ├── codec-contract.md
│   ├── wire-protocol.md
│   ├── query-language.md
│   └── evidence-protocol.md
├── src/
│   ├── LatticeLog.Abstractions/
│   ├── LatticeLog.Core/
│   ├── LatticeLog.Generator/
│   ├── LatticeLog.Storage/
│   │   ├── Format/
│   │   ├── Writer/
│   │   ├── Reader/
│   │   ├── Journal/
│   │   ├── Codecs/Structural/
│   │   ├── Codecs/Contextual/
│   │   ├── Codecs/Predictive/
│   │   ├── Compact/
│   │   └── Indexes/
│   ├── LatticeLog.Query/
│   ├── LatticeLog.Evidence/
│   ├── LatticeLog.OpenTelemetry/
│   ├── LatticeLog.Cli/
│   ├── LatticeLog.Transport/
│   ├── LatticeLog.Server/
│   ├── LatticeLog.Mcp/
│   └── LatticeLog.Web/
├── sdks/
│   ├── java/
│   ├── go/
│   ├── python/
│   └── node/
├── research/
│   ├── papers.md
│   ├── datasets/
│   ├── generators/
│   ├── baselines/
│   ├── experiments/
│   └── results/
├── benchmarks/
│   ├── Ingestion/
│   ├── Storage/
│   ├── Compression/
│   ├── Queries/
│   ├── AIReading/
│   └── Chaos/
├── tests/
│   ├── Unit/
│   ├── Integration/
│   ├── GoldenFiles/
│   ├── RoundTrip/
│   ├── PropertyBased/
│   ├── Fuzz/
│   ├── FaultInjection/
│   ├── Compatibility/
│   ├── Security/
│   └── EndToEnd/
├── examples/
│   ├── dotnet-api/
│   ├── worker/
│   ├── mixed-microservices/
│   └── local-cli/
├── deploy/
│   ├── docker/
│   ├── helm/
│   ├── terraform/
│   └── scripts/
├── tools/
│   └── generate-test-vectors/
└── .github/
    ├── workflows/
    ├── ISSUE_TEMPLATE/
    └── PULL_REQUEST_TEMPLATE.md
```

La estructura final debe simplificarse donde convenga: no crear proyectos vacíos por anticipado ni fragmentar la solución prematuramente.


## 5. Matriz de calidad y CI/CD desde el inicio

| Pipeline | Disparador | Validaciones mínimas | Bloquea merge/release |
|---|---|---|---|
| Fast CI | Cada PR | Formato, build, unit, analyzers, contratos | Sí |
| Correctness CI | Cada PR crítico y nightly | Round-trip, property tests, golden files | Sí |
| Security CI | Cada PR y semanal | Secret scanning, SCA, SAST, SBOM | Sí en hallazgos críticos |
| Compatibility CI | Nightly/pre-release | Formato N-1/N-2, SDKs, OS/arch | Sí para release |
| Fuzz CI | Nightly y continuo | Parser, importadores, API, MCP | Sí ante crashes confirmados |
| Benchmark CI | Nightly y release candidate | Latencia, throughput, bytes, consultas | Gate por regresiones confirmadas |
| Chaos CI | Pre-release | Crash, disco lleno, red, failover, upgrade | Sí |
| Documentation CI | Cada PR | Links, snippets, ejemplos compilables | Sí |
| Supply-chain release | Tag | Build reproducible cuando sea viable, firma, artefactos, SBOM | Sí |

### Condiciones globales de terminado (Definition of Done)

Una tarea **no** está terminada sin:

- [ ] Comportamiento esperado definido y casos límite documentados.
- [ ] Código revisado, tipado/analizado y sin errores conocidos críticos.
- [ ] Pruebas unitarias e integraciones relevantes funcionando.
- [ ] Evidencia de round-trip para cambios de formato o codec.
- [ ] Impacto en CPU/RAM/IO/latencias medido si afecta hot path o almacenamiento.
- [ ] Seguridad/PII evaluada si maneja entrada/salida de datos.
- [ ] Documentación y ejemplos actualizados.
- [ ] Tests de compatibilidad y archivos históricos si cambia contrato público.
- [ ] Enlace al PR, resultados de CI y ADR en el issue.
- [ ] Criterio de aceptación aprobado sin salvedades ocultas.

### Criterios de bloqueo absolutos

- Corrupción o eliminación silenciosa de datos bajo modo no-lossy.
- ACK «durable» entregado antes de persistencia conforme al contrato.
- Acceso entre tenants no autorizado.
- Decodificación dependiente de estado no persistido ni recuperable.
- Salida de evidencia que no indique truncamiento relevante.
- Regresión crítica de seguridad o vulnerabilidad conocida sin mitigación.
- Compatibilidad rota con archivos bajo soporte sin migración o deprecación aprobada.

---

## 6. Métricas, experimentos y reglas de decisión

### 6.1. Indicadores de almacenamiento

- `raw_bytes`: tamaño canónico de entrada bajo contrato de comparación.
- `encoded_bytes`: tamaño de event streams.
- `dictionary_bytes`: catálogos y tablas de contexto.
- `index_bytes`: índices, filtros y localizadores.
- `metadata_bytes`: manifests, checkpoints, footers y journal persistente.
- `temporary_bytes`: máximo extra temporal de compactación (reportar por separado).
- `total_stored_bytes = encoded + dictionary + index + metadata`.
- `compression_ratio = raw_bytes / total_stored_bytes`.
- `savings_pct = 100 * (1 - total_stored_bytes / raw_bytes)`.
- `write_amplification = total_bytes_physically_written / logical_input_bytes`.

**Comparación obligatoria:** mismo dataset, preservación equivalente, mismo orden cuando sea parte del contrato, mismo nivel de indización reportado por separado, misma plataforma y medición del proceso completo. Un formato sin índices no debe proclamarse «mejor» que otro indexado si las consultas son necesarias: publicar resultados de ambos modos.

### 6.2. Indicadores de rendimiento

- p50/p95/p99/p99.9 de latencia por llamada al logger.
- Eventos/s y bytes/s sostenidos por productores, writer y servidor.
- CPU total por millón de eventos, no solo CPU del hilo productor.
- Allocations/evento, presión del GC y picos de RSS.
- Tiempo y CPU de compaction; degradación durante compactación simultánea.
- Latencia de ACK durable y tiempo de recuperación post-crash.
- Latencia p50/p95/p99 de consultas patrón y bytes realmente leídos.
- Escalado horizontal vs número de nodos, coste por TB y consultas por CPU-hora.

### 6.3. Indicadores de utilidad para lectores externos/IA

- Tokens (reales en el modelo de prueba o aproximados especificando tokenizador).
- `evidence_recall`: evidencias relevantes halladas / evidencias relevantes conocidas.
- `evidence_precision`: evidencias relevantes / evidencias entregadas.
- `answer_accuracy`: diagnóstico correcto en casos de respuesta conocida.
- `unsupported_claims`: afirmaciones sin evidencia comprobable.
- Número de consultas, tiempo de resolución, bytes de red y coste del proceso externo.
- Porcentaje de resultados con `partial_results` correctamente representado.

**Protocolo de evaluación:** mismo modelo y configuración externa, mismas preguntas, temperatura controlada si aplica, misma autorización, presupuestos equivalentes, múltiples intentos y revisión humana ciega de diagnósticos en incidentes complejos. LatticeLog no forma parte del proceso de inferencia; solo entrega datos.

### 6.4. Umbrales provisionales (a validar con baselines)

| Puerta | Meta de investigación inicial | Decisión si no se logra |
|---|---|---|
| Estructural | Mejora significativa vs binario simple para logs repetitivos | Simplificar codec |
| Contextual | Ahorro neto positivo en varios conjuntos instrumentados | Contexto solo como índice semántico |
| Predictivo | Ahorro neto que justifique su CPU/memoria/consulta | Descarta P, conserva C |
| Local v1 | Sin corrupción en batería de fallos; SDK estable | No lanzar |
| Evidence | Menos material irrelevante con recall de evidencia no inferior al baseline | Revisar algoritmo de selección |
| Distributed | Comportamiento correcto ante failover, cuotas y partitions | Mantener solo single-node |
| Cloud GA | Márgenes y seguridad aceptables en pilotos | No lanzar SaaS público |

Se puede utilizar un objetivo aspiracional de **≥30 % menos bytes frente al mejor baseline aplicable** y **≥50 % menos tokens en ciertas tareas** para orientar I+D, pero no convertir estos números en requisitos absolutos antes de saber qué volumen de datos y qué calidad de evidencia son realistas. Publicar también casos donde el motor sale peor.

### 6.5. Diseño de los cinco experimentos fundamentales

1. **Ablación de codecs:** raw → binary simple → S → C → P. Mantener constantes datos y garantías.
2. **Concurrencia:** 1/2/4/8/16/32 productores con tamaños y proporciones de error cambiantes.
3. **Cardinalidad:** IDs repetitivos frente a UUIDs, hashes, strings aleatorias, grandes stack traces y payloads de alta entropía.
4. **Consultas selectivas:** error/trace/time range/field filter, datos hot y cold, índices activados/desactivados.
5. **Evidencia externa:** misma incidencia, diferentes representaciones, calidad y tokens medidos sin modelos internos.

---

## 7. Riesgos y decisiones de arquitectura pendientes

| ID | Riesgo o incógnita | Impacto | Estrategia de mitigación | Fase de decisión |
|---|---|---|---|---|
| R01 | Predicción incrementa tamaño por metadata y estado | Alto | Ablación, fallback, gate negativo permitido | P09 |
| R02 | Contextos introducen dependencia complicada para random access | Alto | Snapshots/checkpoints, index local | P08–P11 |
| R03 | Codec comprime bien pero consulta mal | Alto | Función coste con query, índice selectivo | P10–P11 |
| R04 | SDK genera latencias extremas bajo saturación | Crítico | Cola bounded, policy explícita, p99 test | P03–P05 |
| R05 | Crashes producen corrupción/ACK falsos | Crítico | Journaling, commits atómicos, fault injection | P05–P06 |
| R06 | Reordenación causa falsas relaciones causales | Alto | Relaciones explícitas y orden parcial | P01/P12 |
| R07 | Formato queda acoplado a .NET | Alto | Especificación binaria pública, golden vectors | P04/P21 |
| R08 | Versionado del predictor impide lectura futura | Crítico | Decoder/version modelo persistente | P09/P14 |
| R09 | Logs contienen secretos y PII | Crítico | Redacción, RBAC, audit, retención | P01/P18 |
| R10 | MCP induce exposición masiva no autorizada | Crítico | Read-only, budgets, auth, output no confiable | P22 |
| R11 | Índices globales resultan más grandes que los datos | Alto | Índices adaptativos, compactos, por workload | P11 |
| R12 | Servidor distribuido complejiza demasiado la operación | Alto | Medir límite single-node; shard solo cuando sea necesario | P19 |
| R13 | SaaS cuesta más que los ingresos | Alto | Simulador y pilotos por cohorte | P25 |
| R14 | Licencias de codecs/benchmarks incompatibles | Alto | Auditoría antes de incorporar código | P02/P25 |
| R15 | Investigación previa cubre supuesta novedad | Alto | Revisión de publicaciones y claims moderados | P02/P09 |
| R16 | Clúster falla ante split-brain | Crítico | Epoch/fencing/quorum, caos | P19 |
| R17 | Diseño no permite borrar datos concretos | Alto | Retención planificada, crypto-erasure cuando aplique | P18/P20 |
| R18 | Exportación da diagnósticos mejores solo por mayor contexto | Medio | Presupuesto de tokens y evidencia controlados | P12 |
| R19 | Clientes no aceptan lock-in del formato | Alto | Decodificador libre, exportadores, formato documentado | P04/P13 |
| R20 | Promesas de performance no replicables | Alto | Metodología transparente, hardware/config publicadas | P02/P26 |

### Decisiones que deben quedar en ADRs

- `ADR-0001`: por qué .NET 10 LTS y no Rust/C++ en el inicio.
- `ADR-0002`: contrato de evento y contexto.
- `ADR-0003`: formato `.llog` y endian/framing.
- `ADR-0004`: semántica de ACK y journaling.
- `ADR-0005`: criterios para S/C/P y política de fallback.
- `ADR-0006`: checkpoints y acceso aleatorio.
- `ADR-0007`: query language vs SQL/DSL.
- `ADR-0008`: event_ref estable bajo compactación.
- `ADR-0009`: protocolo remoto y deduplicación.
- `ADR-0010`: modelo de shards/consistencia.
- `ADR-0011`: seguridad de MCP externo.
- `ADR-0012`: edición open source/comercial y exportación.

---

## 8. Organización de trabajo fase a fase

### 8.1. Plantilla de issue

```md
# [PXX-TYY] Título

## Objetivo
Qué comportamiento/garantía consigue.

## Contexto y dependencias
Links a ADR, spec y issues bloqueantes.

## Alcance
- [ ] Cambio de código
- [ ] Validación
- [ ] Benchmarks (si aplica)
- [ ] Documentación

## Criterios de aceptación
- [ ] Escenario feliz validado
- [ ] Casos límite validados
- [ ] Error/seguridad validados
- [ ] Compatibilidad analizada

## Evidencia
PR, logs de CI, benchmark CSV, golden file o test reproducible.

## Riesgos
Qué puede fallar; cómo hacer rollback.
```

### 8.2. Plantilla de cierre de fase

```md
# PHASE-XX-REVIEW

- Estado: PASS | CONDITIONAL | FAIL
- Fecha y versión/commit:
- PRs e issues cerrados:
- Entregables verificados:
- Evidencia de tests:
- Evidencia de benchmarks:
- Seguridad/compatibilidad:
- Hipótesis aceptadas/refutadas:
- Limitaciones abiertas:
- ADRs nuevas:
- Riesgos residuales:
- Decisión: avanzar | repetir | simplificar | detener
- Próxima fase autorizada:
```

### 8.3. Estrategia de ramas, revisiones y releases

- Usar trunk-based development o branches cortas, evitando ramas de investigación sin reproducibilidad.
- Requerir CI y review para merges al core y cambios de formato.
- Cualquier modificación binaria debe incluir golden vectors, versión y test de migración.
- PR de codecs debe incluir reporte comparativo con baseline y casos adversariales.
- PR de interfaz de evidencia debe incluir ejemplo JSON, presupuesto de salida y `coverage`.
- Releases con `rc` y canal estable; migraciones no triviales deben contar con backout plan.

### 8.4. Ritmo de ejecución recomendado

- Sprint corto de 1–2 semanas: seleccionar tareas desbloqueadas, implementar, validar y cerrar con evidencia.
- Revisión de gate al final de cada fase; no acumular deuda crítica para «después».
- Revisión de investigación mensual mientras estén P07–P10 activos.
- Revisiones de seguridad, formato y observabilidad como prácticas continuas.
- Mantener `ROADMAP.md` con `Current`, `Next`, `Later`, `Blocked` y registrar razones de cambios.

### 8.5. Plan de equipo y capacidad (orientativo, no estimación contractual)

| Hito | Roles mínimos útiles | Notas |
|---|---|---|
| M0–M2 | 1 ingeniero backend/sistemas + revisión técnica puntual | Posible solo si se acota investigación |
| M3 | 1–2 backend/sistemas + QA/seguridad parcial | Motor local y paquetes |
| M4 | 2–4 backend/sistemas + DevOps/seguridad | Servidor y auth |
| M5 | Equipo mayor según volumen real + SRE | Distribuido y storage multi-tier |
| M6 | Backend, frontend, SRE, producto, soporte y asesoría legal | SaaS y operación continua |

Un desarrollador individual puede avanzar mucho en el motor local; un SaaS empresarial distribuido y seguro normalmente exige más capacidad de pruebas, operaciones y soporte. El alcance debe ajustarse a las evidencias y recursos disponibles.

### 8.6. Orden de prioridad por valor/riesgo

1. **No negociable:** contrato, fidelidad, lector, durabilidad, seguridad básica, benchmarks.
2. **Diferenciación:** S/C/P, query selectiva, Context Pack con coverage y referencias.
3. **Adopción:** bridges OTel, NuGet, CLI, documentación, interoperabilidad.
4. **Escala:** server, permisos, replicación, tiers, multi-SDK.
5. **Comercial:** consola, cloud, billing, enterprise, soporte.

No sacrificar el punto 1 para construir antes el punto 5.

---

## 9. Ejemplos esperados al final del proyecto

### 9.1. SDK .NET (API ilustrativa; no implementada)

```csharp
[LatticeEvent(
    Id = 2101,
    Name = "inventory.stock.reservation.failed",
    Level = LatticeLevel.Error)]
public static partial void StockReservationFailed(
    ILatticeLogger logger,
    long productId,
    int requested,
    int available,
    Exception? cause);

using var scope = logger.BeginOperation("ReserveStock", operationId);
AppEvents.StockReservationFailed(logger, productId, requested, available, ex);
```

### 9.2. Comandos locales (sintaxis futura propuesta)

```bash
latticelog init ./logs
latticelog inspect ./logs
latticelog query ./logs --where "service='inventory' AND level>=ERROR" --since 1h
latticelog trace ./logs --trace-id 4df... --format json
latticelog context ./logs --trace-id 4df... --max-bytes 32000 --format json
latticelog verify ./logs
latticelog compact ./logs --policy compact
latticelog export ./logs --format jsonl --output logs.jsonl
```

### 9.3. Flujo centralizado (arquitectura objetivo)

```text
Aplicaciones (.NET / Java / Go / Python / Node.js)
    ├─ captura tipada y contexto explícito
    ├─ buffer local y spool opcional
    └─ ingestión remota autenticada
                   ↓
          Collector / Ingestion
                   ↓
          Hot Append-only Storage
                   ↓
      Compactación estructural (S/C/P)
                   ↓
       Índices y segmentos Warm / Cold
                   ↓
              Query Engine
           ┌───────┼────────┐
           ↓       ↓        ↓
          CLI    HTTP API   Consola
                    ↓
            Evidence Reader
                    ↓
              MCP (opcional)
                    ↓
             IA externa / IDE

      [NO hay modelos ni inferencia en LatticeLog]
```

### 9.4. Publicación de métricas (formato de informe)

```text
Dataset: inventory-repeated-v4
Hardware: CPU, RAM, SSD, SO, runtime, filesystem, power mode
Input: N eventos; tipo structured-exact
Baseline: JSONL + Zstd nivel X, versión Y
Lattice-S: bytes_totales; encode_s; decode_s; query_p95
Lattice-C: bytes_totales; encode_s; decode_s; query_p95
Lattice-P: bytes_totales; encode_s; decode_s; query_p95
Adversarial: datos aleatorios y registros no instrumentados
Conclusión: condiciones de ganancia, pérdida, intervalos de confianza
Verificación: hashes y pruebas round-trip exitosas
```

---

## 10. Biblioteca de referencias e investigación (verificadas en noviembre de 2026)

Estas referencias sirven para fundamentar decisiones; **sus resultados experimentales no equivalen a objetivos automáticamente alcanzables por LatticeLog**.

### Almacenamiento y captura

1. [NanoLog — A Nanosecond Scale Logging System (USENIX ATC 2018)](https://www.usenix.org/conference/atc18/presentation/yang-stephen) — mover trabajo del hot path a compilación/postprocesamiento.
2. [CLP — Efficient and Scalable Search on Compressed Text Logs (OSDI 2021)](https://www.usenix.org/conference/osdi21/presentation/rodrigues) — compresión lossless y búsqueda sobre logs compactados.
3. [μSlope — High Compression and Fast Search on Semi-Structured Logs (OSDI 2024)](https://www.usenix.org/conference/osdi24/technical-sessions) — compresión especializada de logs semiestructurados.
4. [LogCrisp — Fast Aggregated Analysis on Large-scale Compressed Logs (USENIX ATC 2025)](https://www.usenix.org/conference/atc25/presentation/wei) — consultas vectorizadas y extracción de patrones.
5. [Tracezip — Efficient Distributed Tracing via Trace Compression (ISSTA 2025)](https://conf.researchr.org/details/issta-2025/issta-2025-papers/19/Tracezip-Efficient-Distributed-Tracing-via-Trace-Compression) — redundancia de spans y estructuras de recuperación.
6. [LogPrism — Unifying Structure and Variable Encoding for Effective Log Compression (arXiv, enero 2026)](https://arxiv.org/abs/2601.17482) — correlaciones entre estructuras y campos variables.

### Estándares, SDK y acceso externo

7. [OpenTelemetry Logs Data Model](https://opentelemetry.io/docs/specs/otel/logs/data-model/) — interoperabilidad semántica de eventos/logs.
8. [OpenTelemetry Semantic Conventions for Events](https://opentelemetry.io/docs/specs/semconv/general/events/) — nombres y atributos de eventos.
9. [Model Context Protocol — Specification 2026-07-28](https://blog.modelcontextprotocol.io/posts/2026-07-28/) — especificación vigente en la fecha de redacción; volver a verificar al implementar.
10. [MCP — Server Tools Specification](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2026-07-28/server/tools.mdx) — interfaz de herramientas externas.
11. [.NET Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy) — .NET 10 LTS como base inicial; verificar fechas de mantenimiento y parches al desarrollar.

### Uso correcto de estas fuentes

- Leer publicación y código cuando exista, examinar sus datasets y comprobar licencias antes de reutilizar implementación.
- Reproducir las pruebas en **nuestro hardware y con condiciones equivalentes** antes de extraer conclusiones competitivas.
- Anotar en `research/papers.md`: año, dataset, tipo de logs, método, ventajas, limitaciones y resultados reproducidos/no reproducidos.
- Repetir el panorama científico antes de afirmar originalidad o redactar solicitudes de patente.

---

## 11. Checkpoint inicial: primeras acciones verificables

Aunque este documento cubre todo el producto, el **trabajo inmediato** debería seguir esta secuencia:

- [ ] Crear repositorio `latticelog` y agregar este Markdown como `docs/PLAN-MAESTRO.md`.
- [ ] Crear `ROADMAP.md` con P00→P28 y abrir 29 épicas correspondientes.
- [ ] Completar P00 y aprobar objetivos/no-objetivos.
- [ ] Completar P01 con tipos, esquema, contexto, semántica de durabilidad y orden.
- [ ] Construir laboratorio P02 y congelar baselines reproducibles.
- [ ] Desarrollar P03–P06 para obtener un writer/reader estable antes de inventar codificadores complejos.
- [ ] Ejecutar P07, P08 y P09 como experimentos independientes con puertas de decisión; no asumir que P es mejor.
- [ ] Mantener el objetivo de lectura AI-friendly en P12 como contrato de producto desde P01, aunque su implementación venga después.

**Resultado final buscado:** un formato abierto y reversible, un SDK muy eficiente, un motor local/centralizado con consultas selectivas, un sistema de evidencias verificables aprovechable por cualquier IA externa y una plataforma empresarial operable; todo sin ejecutar modelos de inteligencia artificial dentro de LatticeLog.

**Regla maestra:** *la innovación debe demostrarse con benchmarks reproducibles, no con nombres de algoritmos ni promesas de compresión universal.*
