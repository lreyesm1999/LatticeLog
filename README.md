# LatticeLog

Motor de logs y eventos **local primero**: captura tipada de baja sobrecarga, formato binario abierto `.llog` con
reconstrucción exacta, consultas selectivas con índices y evidencias deterministas para lectores externos (humanos,
scripts o IA externa). **LatticeLog no ejecuta IA**: solo estructura, filtra, agrupa y recupera.

> Estado: **0.1.0-alpha.1** (18-nov-2026). Motor local funcional y probado en Windows x64; ingestión remota v1
> (collector) probada en loopback; servidor de consultas, clúster, SDK multilenguaje, consola web y SaaS **no
> existen todavía**. Nombre provisional (verificar marca antes de publicar).
> Licencia: **pendiente de decisión** (no se ha elegido ninguna).

## En 2 minutos

```csharp
using LatticeLog;
using LatticeLog.Core;

using var logger = LatticeLogger.CreateLocal("./logs", o =>
{
    o.Durability = DurabilityMode.Balanced;          // Fast | Balanced | Durable
    o.Resource.Add(new("service.name", "orders-api"));
});

using (logger.BeginOperation("PlaceOrder", "order-991"))
    AppEvents.StockTimeout(logger, productId: 42, elapsedMs: 12_000);

await logger.FlushAsync(AckLevel.FsyncConfirmed);    // devuelve el nivel realmente alcanzado

static partial class AppEvents
{
    [LatticeEvent(Id = 2101, Name = "inventory.stock.timeout", Level = Severity.Error,
                  Message = "Stock query for {productId} timed out after {elapsedMs} ms")]
    public static partial void StockTimeout(ILatticeLogger logger, long productId, int elapsedMs);
}
```

```bash
latticelog query ./logs --where "level >= ERROR AND service = 'orders-api'" --since 1h --explain
latticelog trace ./logs --trace-id 4bf92f3577b34da6a3ce929d0e0e4736 --format markdown
latticelog context ./logs --where "level >= WARN" --budget-bytes 32000 --format json
latticelog verify ./logs
latticelog export ./logs --format llog-jsonl --output logs.jsonl     # salida sin pérdida, sin bloqueo de proveedor
latticelog mcp ./logs                                                 # servidor MCP de solo lectura por stdio
```

### Enviar a un collector

```bash
latticelog-collector hash-token "<secreto>"          # el hash va en tokens.json
latticelog-collector --store ./server-logs --tokens tokens.json --listen https://0.0.0.0:7400 --cert server.pfx
```

```csharp
// En la aplicación: se sigue escribiendo en local; el forwarder envía lo confirmado por el servidor y reintenta.
await using var forwarder = Forwarder.Start("./logs", new ForwarderOptions
{
    Endpoint = new Uri("https://collector:7400"), Token = Environment.GetEnvironmentVariable("LATTICELOG_TOKEN")!,
});
```

## Qué hay

| componente | proyecto | estado |
|---|---|---|
| Modelo de eventos y API | `src/LatticeLog.Abstractions` | alpha |
| Logger (cola acotada, modos de durabilidad, scopes, redacción, Activity) | `src/LatticeLog.Core` | alpha |
| Generador Roslyn de eventos tipados (diagnósticos LL0001–LL0007) | `src/LatticeLog.Generator` | alpha |
| Formato `.llog`, códecs Raw / Lattice-S / Lattice-C, escritor, lector, recuperación, compactación | `src/LatticeLog.Storage` | alpha |
| Lenguaje de consulta, planificador con índices, cursores, agregaciones | `src/LatticeLog.Query` | alpha |
| Evidencias y Context Packs | `src/LatticeLog.Evidence` | alpha |
| OTLP/JSON, importadores JSONL/texto | `src/LatticeLog.OpenTelemetry` | alpha |
| Bridge `Microsoft.Extensions.Logging` | `src/LatticeLog.Extensions.Logging` | alpha |
| CLI `latticelog` (también como `dotnet tool`, compatible con trimming) | `src/LatticeLog.Cli` | alpha |
| Adaptador MCP de solo lectura | `src/LatticeLog.Mcp` | alpha |
| Ingestión remota: protocolo, cliente y `Forwarder` (el almacén local es el spool) | `src/LatticeLog.Ingest` | alpha |
| Collector HTTP (`latticelog-collector`): deduplicación, group commit, tokens, cuotas, límites | `src/LatticeLog.Server`, `src/LatticeLog.Collector` | alpha |
| Lector independiente en Python (verificación del formato) | `tools/pyllog` | referencia |
| Laboratorio y benchmarks | `research/`, `benchmarks/`, `reports/` | — |

## Resultados medidos (una máquina, ver `reports/`)

- Formato 0.2: Lattice-S+DEFLATE ocupa entre −1 % y −23 % menos que JSONL+zstd-3 en 7 de 8 cargas sintéticas y +9 %
  más en la adversarial; zstd-19 (offline, ~100× más lento) es más pequeño en todas. No se alcanza el objetivo
  aspiracional de −30 %. Un bloque de catálogo corrupto ya no hace perder registros.
- Datos públicos (Loghub-2.0, 14 conjuntos): eventos tipados −25 % frente al mismo contenido en CSV+zstd-3 (mejor en
  los 14, +17 % frente a zstd-19); la importación de texto crudo byte a byte ocupa 1,5–2,7× más que el texto con
  zstd-3 (sirve para conservar y consultar, no para ahorrar espacio).
- Consultas por ventana de tiempo/severidad 20–40× más rápidas que un escaneo completo; escaneo completo de `.llog`
  4–9× más rápido que descomprimir y parsear JSONL+zstd.
- Ejemplo de app: 80 k eventos tipados en 0,63 s (modo Balanced, fsync confirmado, ~96 B/evento).
- Ruta caliente: evento tipado habilitado 56 ns y 5 B asignados por evento; deshabilitado 1,1 ns y 0 B.
- Durabilidad con caídas reales: proceso matado a mitad de escritura en los tres modos, ningún registro confirmado
  perdido. El sellado y el fsync periódico corren en un hilo propio. Los bloqueos que quedan bajo carga sostenida
  vienen del disco (fsync de hasta 5,3 s en la máquina de referencia, ver `reports/latency-v1.md` §4).
- Lattice-P (predictivo) descartado por el estudio previo (ahorro optimista ≤ 0,6 %).

## Desarrollo

Requiere .NET SDK 10.0.404 (`global.json`) y Python 3.10+ para el lector de referencia.

```bash
dotnet test LatticeLog.sln
python -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2
dotnet run -c Release --project examples/dotnet-console -- ./demo-logs 20000
```

Documentación: `docs/PLAN-MAESTRO.md` (plan), `ROADMAP.md` (estado por fase), `specs/` (normativo),
`docs/decisions/` (ADRs y revisiones de fase), `docs/product/` (visión y PRD).
