# PHASE-18-REVIEW — Seguridad, privacidad y auditabilidad

- Estado: **NOT PASSED** (controles principales del servidor hechos y probados; el gate G18 exige cifrado en reposo,
  política de vulnerabilidades operativa y pentest, que no existen)
- Fecha: 2026-11-18
- Entregables: `src/LatticeLog.Server/AuditLog.cs`, `LatticeServer.Admin.cs` (erase), freno de autenticación en
  `LatticeServer.cs`, cabeceras de seguridad en `LatticeServer.Ops.cs`, `docs/security/threat-model.md` §2,
  `docs/security/pentest-scope.md`, `docs/operations/runbook.md` (token comprometido, privacidad)

| tarea | estado | evidencia |
|---|---|---|
| T01 threat modeling | parcial | motor local + tabla del servidor (ingestión, lectura, admin, consola, réplica); falta cloud |
| T02 clasificación y políticas de captura | parcial | `RedactionPolicy` (claves sensibles redactadas en salidas y en el SDK .NET); sin `never-log`/`hash` por campo configurables por tenant |
| T03 redacción en productor | parcial | SDK .NET (`LatticeLoggerOptions`); los SDKs Python/PHP no redactan |
| T04 TLS y cifrado en reposo | parcial | TLS obligatorio fuera de loopback (no probado con certificado real entre máquinas); **sin cifrado en reposo** (delegado al disco cifrado del sistema) |
| T05 claves | parcial | secretos solo por entorno; `server.key` con modo 0600 en Unix; sin KMS |
| T06 RBAC | parcial | scopes `ingest`/`read`/`admin` × namespaces; sin ABAC ni permisos por campo |
| T07 acceso read-only para herramientas | hecho | scope `read`, MCP de solo lectura, cursores con caducidad; sin tokens de tiempo limitado |
| T08 auditoría protegida | parcial | `data/audit/*.jsonl` con CRC por línea: detecta corrupción accidental, **no** manipulación deliberada |
| T09 consultas costosas, traversal, parser | hecho | límites de consulta (P17), nombres de namespace validados, límites de descompresión/línea, fuzz del cuerpo binario |
| T10 texto de logs no confiable | hecho | consola con `textContent` y CSP estricta; JSON en la API; MCP con marcado de procedencia |
| T11 aislamiento entre tenants | hecho | pruebas negativas en `ServerApiTests` (403/404 por namespace, cursores prestados) |
| T12 borrado por retención y privacidad | hecho con límites | retención y `erase` en línea, probados (sin rastro en los ficheros; los reenvíos no resucitan datos). No alcanza backups ni réplica: documentado |
| T13 legal hold | no hecho | — |
| T14 SAST/SCA/secret scanning/SBOM | parcial | job SBOM (CycloneDX) en `ci.yml`; CI nunca ejecutado (sin remoto) |
| T15 respuesta a incidentes | parcial | runbook de token comprometido; `SECURITY.md` sin canal operativo |
| T16 pentest | bloqueado | requiere un tercero; alcance en `docs/security/pentest-scope.md` |
| T17 legislación | bloqueado | asesoría especializada |
| T18 Context Packs y datos sensibles | parcial | redacción aplicada al construir evidencias en el servidor; sin prueba específica de etiquetas parciales |

- Freno de fuerza bruta: 20 fallos/min por dirección → 429 (probado).
- Decisión: no avanzar a SaaS con datos de terceros hasta tener cifrado en reposo, canal de vulnerabilidades y pentest.
