# Política de seguridad

Estado: proyecto en alpha, sin versión publicada ni canal de reporte operativo todavía.

- **Reporte de vulnerabilidades**: pendiente de habilitar un canal privado (p. ej. GitHub Security Advisories) cuando
  exista el repositorio remoto. Hasta entonces, no publicar detalles en issues.
- Superficies y controles actuales: `docs/security/threat-model.md`.
- Principios: no registrar secretos por defecto (redacción antes de persistir), tratar todo texto de logs como no
  confiable en las salidas, herramientas externas de solo lectura, sin criptografía propia.
- Pendiente antes de manejar datos de terceros: SAST/SCA/secret scanning en CI, fuzzing continuo, revisión independiente
  y pentest (P18-T14, P18-T16), proceso de CVE y parches (P18-T15).
