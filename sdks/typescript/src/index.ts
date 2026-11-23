// LatticeLog SDK for Node.js. Typed, structured records written straight into LatticeLog's format in a local folder
// (found by service name by the `latticelog` CLI and MCP server), optionally shipped to latticelog-server with ingest
// protocol v1. Guarantees and formats: README.md, specs/local-sources.md, specs/ingest-protocol.md.

export {
  Level,
  Logger,
  levelNumber,
  open,
  type Durability,
  type FlushLevel,
  type FlushResult,
  type LevelName,
  type OpenOptions,
  type RecordOptions,
  type Saturation,
  type Stats,
} from "./logger.js";
export { Context, contextStorage, currentContext, type ContextKind } from "./context.js";
export { errorType } from "./exception.js";
export type { ServerOptions, ShipStats } from "./ship.js";
export { TypedValue, decimal, duration, f64, timestamp, u64, utf16, uuid, type Attributes } from "./values.js";
