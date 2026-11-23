//! # LatticeLog SDK for Rust
//!
//! Typed, structured records written **straight into LatticeLog's format** in a local folder (found by service
//! name by the `latticelog` CLI and MCP server), optionally **shipped to `latticelog-server`** with ingest protocol
//! v1. Built for the hot path: the calling thread encodes into a reused thread-local buffer (no allocation once
//! warm) and copies the bytes into a shared batch; a background thread does all file I/O.
//!
//! ```no_run
//! use latticelog::{Options, Level};
//!
//! let log = latticelog::open("logs/orders-api", Options::new("orders-api").environment("dev"))?;
//! let _guard = log.guard(); // writes everything and fsyncs when main returns
//!
//! latticelog::info!(log, "order {id} placed", id = 991, total = 19.99);
//! log.error("payment failed")
//!     .event("payment_failed")
//!     .attr("order_id", 991)
//!     .error(&std::io::Error::other("card declined"))
//!     .emit();
//! # Ok::<(), std::io::Error>(())
//! ```
//!
//! With `tracing` (feature `tracing`, on by default):
//!
//! ```no_run
//! use tracing_subscriber::prelude::*;
//! let log = latticelog::open("logs/orders-api", latticelog::Options::new("orders-api"))?;
//! tracing_subscriber::registry().with(latticelog::layer(&log)).init();
//! # Ok::<(), std::io::Error>(())
//! ```
//!
//! Guarantees and formats: `sdks/rust/README.md`, `specs/local-sources.md`, `specs/ingest-protocol.md`.

#![warn(missing_debug_implementations)]

mod context;
mod exception;
mod folder;
mod json;
mod logger;
mod minijson;
mod names;
mod value;
mod writer;

#[cfg(feature = "log")]
mod log_bridge;
#[cfg(feature = "server")]
mod ship;
#[cfg(feature = "tracing")]
mod tracing_layer;

pub use context::{Context, ContextBuilder, ContextGuard, ContextKind, current as current_context};
pub use exception::Exception;
pub use logger::{CloseGuard, Durability, FlushLevel, FlushResult, Level, Logger, Options, Record, Saturation, Stats, open};
pub use value::{Bytes, Debug, Decimal, Display, Map, Timestamp, Utf16, Uuid, Value};

#[cfg(feature = "log")]
pub use log_bridge::{LogBridge, init_log};
#[cfg(feature = "server")]
pub use ship::{Ack, ServerOptions};
#[cfg(feature = "tracing")]
pub use tracing_layer::{LatticeLayer, layer};

/// Writes one record: `latticelog::log!(log, Level::Info, "order {id}", id = 1, "http.status" = 200)`.
/// Keys are identifiers or string literals; values are borrowed. Nothing is evaluated when the level is off.
/// Returns true when the record was accepted.
#[macro_export]
macro_rules! log {
    ($logger:expr, $level:expr, $body:expr $(, $key:tt = $value:expr)* $(,)?) => {{
        let __ll: &$crate::Logger = &$logger;
        let __lv: $crate::Level = $level;
        if __ll.enabled(__lv) {
            __ll.log(__lv, $body)$(.attr($crate::__key!($key), &$value))*.emit()
        } else {
            false
        }
    }};
}

#[doc(hidden)]
#[macro_export]
macro_rules! __key {
    ($k:ident) => {
        stringify!($k)
    };
    ($k:literal) => {
        $k
    };
}

/// `latticelog::trace!(log, "body", key = value, …)`
#[macro_export]
macro_rules! trace {
    ($logger:expr, $($rest:tt)+) => { $crate::log!($logger, $crate::Level::Trace, $($rest)+) };
}

/// `latticelog::debug!(log, "body", key = value, …)`
#[macro_export]
macro_rules! debug {
    ($logger:expr, $($rest:tt)+) => { $crate::log!($logger, $crate::Level::Debug, $($rest)+) };
}

/// `latticelog::info!(log, "body", key = value, …)`
#[macro_export]
macro_rules! info {
    ($logger:expr, $($rest:tt)+) => { $crate::log!($logger, $crate::Level::Info, $($rest)+) };
}

/// `latticelog::warn!(log, "body", key = value, …)`
#[macro_export]
macro_rules! warn {
    ($logger:expr, $($rest:tt)+) => { $crate::log!($logger, $crate::Level::Warn, $($rest)+) };
}

/// `latticelog::error!(log, "body", key = value, …)`
#[macro_export]
macro_rules! error {
    ($logger:expr, $($rest:tt)+) => { $crate::log!($logger, $crate::Level::Error, $($rest)+) };
}

/// `latticelog::fatal!(log, "body", key = value, …)`
#[macro_export]
macro_rules! fatal {
    ($logger:expr, $($rest:tt)+) => { $crate::log!($logger, $crate::Level::Fatal, $($rest)+) };
}
