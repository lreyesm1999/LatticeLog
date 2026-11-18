# Contribuir a LatticeLog

- Idioma: documentación de producto y gobierno en español; especificaciones normativas (`specs/`) en inglés para
  implementadores externos; código y comentarios en inglés.
- Cada cambio referencia su tarea `PXX-TYY` del plan (`docs/PLAN-MAESTRO.md`) y cumple la *Definition of Done* (§5):
  pruebas, documentación, medición si toca la ruta caliente o el almacenamiento, evaluación de seguridad si toca E/S.
- **Cambios de formato** (`.llog`, JSONL de intercambio, evidencias): actualizar la especificación, subir la versión
  correspondiente, añadir golden files nuevos sin modificar los existentes, y pasar el lector independiente
  (`python -I tools/pyllog/llog.py verify-golden ...`).
- **Cambios de códec**: adjuntar resultados del laboratorio contra los baselines, incluidos los casos adversariales.
- **Evidencias**: adjuntar un ejemplo JSON con `coverage` y presupuesto.
- Bloqueantes absolutos (§5 del plan): corrupción o eliminación silenciosa, ACK "durable" antes de persistir, acceso
  entre tenants, decodificación dependiente de estado no persistido, salida sin indicar truncamiento, regresión de
  seguridad, ruptura de compatibilidad sin migración.
- Ramas cortas sobre `main`; CI obligatoria (`.github/workflows/ci.yml`).
