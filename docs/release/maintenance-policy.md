# Política de mantenimiento (P28, borrador)

Borrador a 18-nov-2026. Se publica junto con la primera versión estable.

## Versiones soportadas (P28-T01)
- **Motor y SDK .NET**: la última menor de la mayor actual recibe correcciones; la anterior, solo de seguridad durante
  6 meses. Runtime: .NET LTS vigente y la versión actual.
- **SDK Python**: CPython con soporte oficial upstream (hoy 3.10+). **SDK PHP**: 8.1+ mientras PHP la mantenga.
- **Sistemas**: Linux x64/ARM64, Windows x64 y macOS ARM64, cuando el CI los cubra (hoy solo se prueba en Windows
  x64 local).

## Compatibilidad de datos (P28-T06, T07)
- Los archivos `.llog` de una mayor se leen durante toda esa mayor y al menos **2 años** después de publicarse la
  siguiente, con una herramienta de migración publicada antes de retirar la lectura.
- Las retiradas se anuncian una menor antes (`docs/compatibility.md`).

## Calidad continua (P28-T02, T03, T05)
- Benchmark de regresión en cada versión, en la misma máquina de referencia estable, con el corpus congelado
  (`tests/GoldenFiles`, Loghub-2.0). Una regresión confirmada > 5 % bloquea la versión.
- Un codec nuevo solo entra con un experimento de ablación en `reports/` y una versión de formato que lo declare.

## Seguridad (P28-T08)
- Canal privado de reporte; acuse en 3 días hábiles; corrección o mitigación de severidad alta en 30 días; aviso
  público con CVE cuando aplique.

## Evolución (P28-T10..T13, T15)
- Las herramientas para IA externa (MCP, Context Packs) mejoran sin meter modelos en el núcleo.
- Lenguajes nuevos de SDK solo cuando haya demanda real verificada.
- `ROADMAP.md` es público y refleja el estado verdadero de cada fase, incluidas las no superadas.
