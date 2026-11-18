# Política de seguridad

Estado: proyecto en alpha (`0.1.0-alpha.1`), sin versiones soportadas en producción.

- **Reporte de vulnerabilidades**: usa el reporte privado de GitHub (pestaña *Security* → *Report a vulnerability*)
  en `lreyesm1999/LatticeLog`. No publiques detalles en issues públicos. Respuesta orientativa: acuse en 3 días
  hábiles; corrección o mitigación de severidad alta en 30 días.
- Superficies y controles actuales: `docs/security/threat-model.md`.
- Principios: no registrar secretos por defecto (redacción antes de persistir), tratar todo texto de logs como no
  confiable en las salidas, herramientas externas de solo lectura, sin criptografía propia.
- Pendiente antes de manejar datos de terceros: SAST/SCA/secret scanning en CI, fuzzing continuo, revisión independiente
  y pentest (P18-T14, P18-T16), proceso de CVE y parches (P18-T15).
