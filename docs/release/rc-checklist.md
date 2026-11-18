# Lista de verificación para un release candidate (P14, P27)

Estado a 18-nov-2026. Un RC solo se publica cuando **todas** las casillas bloqueantes están marcadas.

## Bloqueantes (decisión o acción del responsable)

- [x] **Licencia** elegida y aplicada: MIT (LICENSE, `PackageLicenseExpression`, cabeceras si aplica). Sin ella no se
      publica ni el repositorio ni un paquete.
- [ ] **Nombre y marca** "LatticeLog" verificados (registros de marca, dominios, NuGet/PyPI/Packagist).
- [ ] **Repositorio remoto** creado; CI en verde en Linux, macOS y Windows, incluido el job de Native AOT.
- [ ] **Hardware estable** para las mediciones (el equipo actual corrompe memoria bajo carga multinúcleo:
      `reports/ingest-v1.md` §3). Rendimiento repetido en esa máquina o en CI.

## Técnicos

- [x] Suite completa en verde, más el lector Python sobre los archivos de prueba congelados.
- [x] Pruebas de caída real, retención, deduplicación tras reinicio y redes con pérdidas.
- [x] Análisis de trimming/AOT activo y sin avisos en `src/`.
- [ ] TLS probado de extremo a extremo (Kestrel con certificado real) y una red real entre máquinas.
- [ ] Imagen Docker construida, escaneada y arrancada (`deploy/Dockerfile`).
- [ ] SBOM generado y vulnerabilidades revisadas (job `security` del CI).
- [ ] Paquetes firmados y SourceLink apuntando al repositorio público.
- [ ] Revisión de seguridad externa (pentest) del servidor: ver `docs/security/pentest-scope.md`.
- [ ] Documentación de usuario revisada por alguien que no la escribió.

## Procedimiento

1. Rama `release/0.x`. Versión `0.x.0-rc.N` en `Directory.Build.props`.
2. CI completo en verde. Artefactos: paquetes, imagen, SBOM y binarios AOT.
3. Probar el RC durante al menos una semana en un entorno real, siguiendo `docs/deploy/single-node.md`.
4. Sin incidencias bloqueantes → etiqueta `v0.x.0` y notas de versión desde `CHANGELOG.md`.
