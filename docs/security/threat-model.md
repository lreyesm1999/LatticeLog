# Modelo de amenazas — motor local (P18-T01, alcance parcial)

Alcance: SDK, formato/lector, CLI y MCP local (tabla 1) y, desde el 18-nov-2026, el servidor de un nodo (tabla 2).
Clúster, UI y cloud **no** están cubiertos (no existen).

| activo / superficie | amenaza | control implementado | pendiente |
|---|---|---|---|
| Datos del productor | secretos persistidos | redacción en el hilo escritor antes de persistir (campos `Secret`, claves sensibles), flag `redacted` | redacción en el hilo productor; política `hash`/`allow-list` |
| Archivos `.llog` | archivo malicioso o corrupto (parser) | CRC por cabecera/payload, límites de tamaño/expansión/profundidad/conteos, errores explícitos, 300 casos de fuzzing por bit-flip | fuzzing continuo con cobertura (SharpFuzz/libFuzzer) |
| Archivos `.llog` | manipulación intencional | ninguno (CRC no autentica) | firma/MAC de segmentos para servidor (P18-T04) |
| Directorio del almacén | dos escritores | lock exclusivo del SO | — |
| Directorio del almacén | permisos | heredados del SO | documentar permisos recomendados (0700) |
| CLI | exportación accidental masiva | `export` exige `--output` o `--stdout`, no sobrescribe sin `--force` | — |
| Consultas | agotamiento de recursos | límites de longitud, profundidad, IN, resultados, escaneo y tiempo | cuotas por usuario (servidor) |
| MCP / evidencias | inyección de instrucciones vía texto de logs | texto marcado como no confiable, fences Markdown seguros, sin herramientas de escritura | evaluación con agentes reales (P22-T13) |
| MCP | path traversal | almacén fijo al arranque, sin parámetros de ruta | — |
| Importadores | entradas gigantes / anidamiento | límites de bytes, registros, atributos y profundidad | fuzzing de importadores |
| Dependencias | cadena de suministro | dependencias mínimas en runtime (solo `System.IO.Hashing`); Zstd/LZ4 solo en el laboratorio | SBOM, SCA y secret scanning en CI (workflow definido, no ejecutado) |

Criptografía: no se inventa cifrado. El cifrado en reposo y TLS se delegarán a bibliotecas y mecanismos estándar (P18-T04).

## 2. Servidor (`latticelog-server`, P16–P18)

| activo / superficie | amenaza | control implementado | pendiente |
|---|---|---|---|
| Credenciales | robo de tokens en tránsito | TLS obligatorio fuera de loopback (el servidor no arranca en HTTP plano salvo opción explícita) | mTLS opcional; TLS probado de extremo a extremo |
| Credenciales | robo del fichero de tokens | solo se guardan SHA-256; comparación en tiempo constante | tokens con caducidad; integración con un gestor de secretos |
| Credenciales | fuerza bruta | límite de 20 fallos por IP y minuto (429), auditado | bloqueo distribuido (no aplica en un nodo) |
| Autorización | acceso entre tenants | namespaces aislados (un almacén por namespace), scopes `ingest`/`read`/`admin`, nombres validados contra recorrido de rutas | pruebas de penetración |
| Cursores | manipulación o reutilización | HMAC-SHA256 con caducidad, ligados a namespace y token; clave `server.key` (0600 en Unix) | rotación de la clave |
| Ingestión | duplicados o pérdida por reintentos | deduplicación por (productor, secuencia) reconstruida desde los datos; los huecos solo se aceptan declarados (y el cliente los verifica con un recorrido completo) | — |
| Ingestión | denegación de servicio | límites de petición, descompresión, ratio, registros y línea; cuota por token; cola acotada (503); tasa mínima de cuerpo (slowloris) | límites por IP para peticiones anónimas |
| Lectura | consultas abusivas | concurrencia global y por token, timeout, tope de registros escaneados (resultado parcial explícito) | cuotas de bytes por tenant |
| Datos personales | derecho al olvido | `POST /v1/admin/erase` reescribe los segmentos sin los registros afectados, con verificación y auditoría; los reenvíos no los reintroducen | borrado en copias externas (almacenes de productores, exportaciones, backups, réplicas) |
| Datos personales | retención excesiva | retención por antigüedad o tamaño (CLI, automática al rotar, endpoint de administración) | política por namespace en configuración |
| Auditoría | acciones sin rastro | registro de solo-añadir con CRC por línea: administración, lecturas (filtros, nunca datos) y fallos de autenticación | firma encadenada (tamper-evident fuerte); envío a un SIEM |
| Consola web (`/ui`) | XSS por contenido de logs, clickjacking, robo del token | todo se pinta con `textContent`; CSP `default-src 'self'` sin scripts inline; `X-Frame-Options: DENY` y `frame-ancestors 'none'`; token solo en `sessionStorage` de la pestaña | SSO/OIDC en lugar de tokens pegados; auditoría de accesibilidad y E2E en navegador |
| Métricas (`/metrics`) | reconocimiento (namespaces y volúmenes) | requiere token admin salvo `--metrics-anonymous` explícito | — |
| Backups | escritura en rutas arbitrarias; copia inconsistente | el destino lo fija el operador (`--backup-dir`), nunca el cliente; copia verificada leyéndola | cifrado de los backups |
| Réplica | token de réplica expuesto; datos sin cifrar en tránsito | token solo por entorno; HTTPS fuera de loopback igual que la ingestión | fencing entre primarios |
| Respuestas de error | fuga de información interna | al cliente solo tipo y mensaje, nunca la pila; el detalle va a `Diagnostics` | — |
| Cadena de suministro | dependencias del servidor | solo el framework ASP.NET Core; imagen *chiseled* sin shell y con usuario no root | escaneo de la imagen; SBOM en CI |

Revisión externa: el alcance propuesto para un pentest está en `docs/security/pentest-scope.md`. **No se ha hecho
ningún pentest.**
