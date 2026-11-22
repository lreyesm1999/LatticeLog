//! `log` crate bridge: `latticelog::init_log(&log)?` routes `log::info!(…)` into LatticeLog.
//!
//! The formatted message is the body (formatted straight into the record, no temporary `String`), the target is
//! `logger.name`, and structured key-values (`log::info!(order_id = 991; "placed")`) stay typed attributes, with the
//! same special keys as the `tracing` layer (`event`, `trace_id`, `span_id`, `correlation_id`).

use crate::json;
use crate::logger::{Level, Logger};
use crate::names::{is_event_name, is_hex};

/// The `log::Log` implementation. Usually installed with [`init_log`].
#[derive(Debug)]
pub struct LogBridge {
    log: Logger,
}

impl LogBridge {
    pub fn new(log: &Logger) -> Self {
        LogBridge { log: log.clone() }
    }
}

/// Installs the bridge as the global `log` logger, with the logger's current minimum level.
pub fn init_log(log: &Logger) -> Result<(), log::SetLoggerError> {
    let max = [Level::Trace, Level::Debug, Level::Info, Level::Warn, Level::Error].into_iter().find(|l| log.enabled(*l)).map_or(
        log::LevelFilter::Error,
        |l| match l {
            Level::Trace => log::LevelFilter::Trace,
            Level::Debug => log::LevelFilter::Debug,
            Level::Info => log::LevelFilter::Info,
            Level::Warn => log::LevelFilter::Warn,
            _ => log::LevelFilter::Error,
        },
    );
    log::set_boxed_logger(Box::new(LogBridge::new(log)))?;
    log::set_max_level(max);
    Ok(())
}

fn level(l: log::Level) -> Level {
    match l {
        log::Level::Trace => Level::Trace,
        log::Level::Debug => Level::Debug,
        log::Level::Info => Level::Info,
        log::Level::Warn => Level::Warn,
        log::Level::Error => Level::Error,
    }
}

struct Kv<'a> {
    pairs: &'a mut Vec<u8>,
    fields: &'a mut Vec<u8>,
}

impl<'kvs> log::kv::VisitSource<'kvs> for Kv<'_> {
    fn visit_pair(&mut self, key: log::kv::Key<'kvs>, value: log::kv::Value<'kvs>) -> Result<(), log::kv::Error> {
        let k = key.as_str();
        if let Some(s) = value.to_borrowed_str() {
            match k {
                "event" if is_event_name(s) => {
                    self.fields.extend_from_slice(b",\"event\":");
                    json::write_str(self.fields, s);
                    return Ok(());
                }
                "trace_id" | "span_id" if is_hex(s, if k == "trace_id" { 32 } else { 16 }) => {
                    self.fields.extend_from_slice(if k == "trace_id" { b",\"trace_id\":\"" } else { b",\"span_id\":\"" });
                    self.fields.extend(s.bytes().map(|c| c.to_ascii_lowercase()));
                    self.fields.push(b'"');
                    return Ok(());
                }
                "correlation_id" => {
                    self.fields.extend_from_slice(b",\"correlation_id\":");
                    json::write_str(self.fields, s);
                    return Ok(());
                }
                _ => {}
            }
        }
        if !self.pairs.is_empty() {
            self.pairs.push(b',');
        }
        self.pairs.push(b'[');
        json::write_str(self.pairs, k);
        self.pairs.push(b',');
        if let Some(v) = value.to_i64() {
            json::write_i64(self.pairs, v);
        } else if let Some(v) = value.to_u64() {
            json::write_unsigned(self.pairs, v);
        } else if let Some(v) = value.to_f64() {
            json::write_f64(self.pairs, v);
        } else if let Some(v) = value.to_bool() {
            self.pairs.extend_from_slice(if v { b"true" } else { b"false" });
        } else if let Some(s) = value.to_borrowed_str() {
            json::write_str(self.pairs, s);
        } else {
            json::write_display(self.pairs, &value);
        }
        self.pairs.push(b']');
        Ok(())
    }
}

impl log::Log for LogBridge {
    fn enabled(&self, metadata: &log::Metadata<'_>) -> bool {
        self.log.enabled(level(metadata.level()))
    }

    fn log(&self, record: &log::Record<'_>) {
        let lvl = level(record.level());
        if !self.log.enabled(lvl) {
            return;
        }
        let mut rec = self.log.record(lvl);
        if let Some(out) = rec.body_writer() {
            json::write_args(out, *record.args());
        }
        let mut pairs = Vec::new();
        let mut fields = Vec::new();
        let _ = record.key_values().visit(&mut Kv { pairs: &mut pairs, fields: &mut fields });
        if !pairs.is_empty() {
            pairs.push(b',');
        }
        pairs.extend_from_slice(b"[\"logger.name\",");
        json::write_str(&mut pairs, record.target());
        pairs.push(b']');
        rec.push_raw_pairs(&pairs);
        rec.push_raw_fields(&fields);
        rec.emit();
    }

    fn flush(&self) {
        self.log.flush(false);
    }
}
