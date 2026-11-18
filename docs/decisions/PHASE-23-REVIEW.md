# PHASE-23-REVIEW — Consola web

- Estado: **CONDITIONAL** (consola mínima servida por el propio servidor; sin pruebas con usuarios ni E2E en navegador)
- Fecha: 2026-11-18
- Entregables: `src/LatticeLog.Server/ui/` (`index.html`, `app.js`, `app.css`, recursos embebidos), rutas `/ui` en
  `LatticeServer.Ops.cs`, prueba `ServerApiTests.Console_metrics_and_backup`

| tarea | estado | evidencia |
|---|---|---|
| T01 entrevistas | bloqueado | requiere usuarios |
| T02 navegación | parcial | Búsqueda, Estadísticas y Catálogo; sin Overview/Sources/Settings |
| T03 búsqueda | hecho | filtro, desde, orden, paginación con cursor firmado, cobertura visible |
| T04 inspector | parcial | JSON completo del evento con contexto; sin vista de schema |
| T05 timeline de traza | parcial | documento de `/v1/traces/{id}` en el inspector; sin gráfico |
| T06 errores y volumen | parcial | barras por event/level/service con conteos exactos |
| T07 costes de almacenamiento | parcial | catálogo de segmentos con bytes y registros |
| T08 Context Pack desde la UI | no hecho | — |
| T09 permisos y claves | no hecho | el token lo escribe el usuario y se guarda solo en `sessionStorage` |
| T10 claro/oscuro, responsive | parcial | `prefers-color-scheme` y diseño flexible; sin auditoría WCAG |
| T11 virtualización | no hecho | páginas de 100 con "Más" |
| T12 XSS | hecho | todo con `textContent`; CSP `default-src 'self'` sin inline; `X-Frame-Options: DENY`; la prueba comprueba que el JS no usa `innerHTML` |
| T13 E2E | parcial | prueba HTTP de servido y cabeceras; sin navegador automatizado |
| T14 usabilidad | bloqueado | requiere usuarios |
| T15 datos parciales | hecho | la línea de cobertura muestra `PARCIAL: motivo` y los problemas de almacenamiento |
