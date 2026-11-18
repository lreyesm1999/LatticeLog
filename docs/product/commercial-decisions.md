# Licencia y modelo del proyecto (P25)

**Decidido el 18-nov-2026**: LatticeLog es gratuito y abierto, con licencia **MIT** para todo el repositorio. No hay
ediciones de pago, SaaS ni facturación previstos.

## Por qué MIT

- Es lo que pidió el responsable: un proyecto gratis para cualquiera, como sus otros repositorios públicos.
- Es la licencia más corta y conocida. Cualquiera puede usarlo, modificarlo y redistribuirlo, también en proyectos
  comerciales, siempre que mantenga el aviso de copyright.
- Es compatible con todas las dependencias (K4os.Compression.LZ4, ZstdSharp y BenchmarkDotNet son MIT; xUnit es
  Apache-2.0, solo en pruebas).
- Protege la promesa de portabilidad: el formato `.llog`, su especificación y el lector independiente
  (`tools/pyllog`) se pueden reutilizar sin restricciones.

## Alternativa considerada

**Apache-2.0** añade una concesión explícita de patentes y exige anotar los cambios. Sería preferible si colaboraran
empresas con patentes en juego. Para un proyecto personal gratuito, MIT es más simple.

## Lo que el código ya garantiza a quien lo use

Exportación completa sin pérdida (`GET /v1/export`, `latticelog export`), borrado verificable (`/v1/admin/erase`,
con sus límites sobre backups y réplica) y formato documentado con lector independiente.
