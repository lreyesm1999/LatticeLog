//! `tracing` integration: `tracing_subscriber::registry().with(latticelog::layer(&log)).init()`.
//!
//! Mapping:
//! - span → context `Scope` named after the span, with its fields as typed attributes; a span with an
//!   `operation_id` field becomes an `Operation` (records inside carry `operation_id` and the enclosing operation
//!   as `parent_operation_id`). Spans follow futures, so this is the way to get contexts in `async` code.
//! - event → record: the message is the body, `event` (identifier-like) the event name, `trace_id`/`span_id`
//!   (hex) the trace fields, `correlation_id` the correlation id, an error value (`error = &e as &dyn Error`) the
//!   exception; every other field stays a typed attribute (`i64`, `u64`, `f64`, `bool`, `str`, `Debug` as text).
//!   The target is kept as `logger.name`.

use crate::context::{self, Context, ContextBuilder, ContextKind};
use crate::exception::Exception;
use crate::json;
use crate::logger::{Level, Logger};
use crate::names::{is_event_name, is_hex};
use std::cell::Cell;
use std::fmt;
use tracing_core::field::{Field, Visit};
use tracing_core::span::{Attributes, Id, Record as SpanValues};
use tracing_core::{Event, Subscriber};
use tracing_subscriber::Layer;
use tracing_subscriber::layer::Context as LayerContext;
use tracing_subscriber::registry::LookupSpan;

/// The layer. Built by [`layer`].
#[derive(Debug)]
pub struct LatticeLayer {
    log: Logger,
    target: bool,
}

/// A `tracing_subscriber` layer that writes into `log`.
pub fn layer(log: &Logger) -> LatticeLayer {
    LatticeLayer { log: log.clone(), target: true }
}

impl LatticeLayer {
    /// Whether the event target is written as the `logger.name` attribute (default true).
    pub fn with_target(mut self, on: bool) -> Self {
        self.target = on;
        self
    }
}

fn level(l: &tracing_core::Level) -> Level {
    match *l {
        tracing_core::Level::TRACE => Level::Trace,
        tracing_core::Level::DEBUG => Level::Debug,
        tracing_core::Level::INFO => Level::Info,
        tracing_core::Level::WARN => Level::Warn,
        _ => Level::Error,
    }
}

struct SpanData {
    ctx: Context,
    parent: Context,
    name: &'static str,
    attrs: Vec<u8>,
    op: Option<String>,
}

impl SpanData {
    fn build(&self) -> Context {
        let b = match &self.op {
            Some(id) => ContextBuilder::new(ContextKind::Operation, Some(self.name), Some(self.parent.clone()))
                .attr("operation.name", self.name)
                .operation(id.clone()),
            None => ContextBuilder::new(ContextKind::Scope, Some(self.name), Some(self.parent.clone())),
        };
        b.raw_attrs(&self.attrs).build()
    }
}

/// Encodes visited fields as `["k",v]` pairs.
struct PairVisitor<'a> {
    pairs: &'a mut Vec<u8>,
    op: Option<&'a mut Option<String>>,
}

impl PairVisitor<'_> {
    fn key(&mut self, name: &str) -> &mut Vec<u8> {
        if !self.pairs.is_empty() {
            self.pairs.push(b',');
        }
        self.pairs.push(b'[');
        json::write_str(self.pairs, name);
        self.pairs.push(b',');
        self.pairs
    }
}

macro_rules! scalar {
    ($name:ident, $t:ty, $enc:expr) => {
        fn $name(&mut self, field: &Field, value: $t) {
            let out = self.key(field.name());
            $enc(out, value);
            out.push(b']');
        }
    };
}

impl Visit for PairVisitor<'_> {
    scalar!(record_i64, i64, json::write_i64);
    scalar!(record_u64, u64, json::write_unsigned);
    scalar!(record_f64, f64, json::write_f64);
    scalar!(record_bool, bool, |o: &mut Vec<u8>, v: bool| crate::value::Value::encode(&v, o));
    scalar!(record_i128, i128, |o: &mut Vec<u8>, v: i128| crate::value::Value::encode(&v, o));
    scalar!(record_u128, u128, |o: &mut Vec<u8>, v: u128| crate::value::Value::encode(&v, o));

    fn record_str(&mut self, field: &Field, value: &str) {
        if field.name() == "operation_id"
            && let Some(op) = self.op.as_deref_mut()
        {
            *op = Some(value.to_owned());
            return;
        }
        let out = self.key(field.name());
        json::write_str(out, value);
        out.push(b']');
    }

    fn record_debug(&mut self, field: &Field, value: &dyn fmt::Debug) {
        if field.name() == "operation_id"
            && let Some(op) = self.op.as_deref_mut()
        {
            *op = Some(format!("{value:?}"));
            return;
        }
        let out = self.key(field.name());
        out.push(b'"');
        let _ = fmt::write(&mut json::EscapeWriter(out), format_args!("{value:?}"));
        out.extend_from_slice(b"\"]");
    }
}

thread_local! {
    static PAIRS: Cell<Vec<u8>> = const { Cell::new(Vec::new()) };
    static FIELDS: Cell<Vec<u8>> = const { Cell::new(Vec::new()) };
}

struct EventVisitor<'r, 'a> {
    rec: &'r mut crate::logger::Record<'a>,
    pairs: PairVisitor<'r>,
    fields: &'r mut Vec<u8>,
    exception: Option<Exception>,
}

impl EventVisitor<'_, '_> {
    fn special_str(&mut self, name: &str, value: &str) -> bool {
        match name {
            "event" if is_event_name(value) => {
                self.fields.extend_from_slice(b",\"event\":");
                json::write_str(self.fields, value);
            }
            "trace_id" if is_hex(value, 32) => {
                self.fields.extend_from_slice(b",\"trace_id\":\"");
                self.fields.extend(value.bytes().map(|c| c.to_ascii_lowercase()));
                self.fields.push(b'"');
            }
            "span_id" if is_hex(value, 16) => {
                self.fields.extend_from_slice(b",\"span_id\":\"");
                self.fields.extend(value.bytes().map(|c| c.to_ascii_lowercase()));
                self.fields.push(b'"');
            }
            "correlation_id" => {
                self.fields.extend_from_slice(b",\"correlation_id\":");
                json::write_str(self.fields, value);
            }
            _ => return false,
        }
        true
    }
}

impl Visit for EventVisitor<'_, '_> {
    fn record_i64(&mut self, f: &Field, v: i64) {
        self.pairs.record_i64(f, v)
    }
    fn record_u64(&mut self, f: &Field, v: u64) {
        self.pairs.record_u64(f, v)
    }
    fn record_f64(&mut self, f: &Field, v: f64) {
        self.pairs.record_f64(f, v)
    }
    fn record_bool(&mut self, f: &Field, v: bool) {
        self.pairs.record_bool(f, v)
    }
    fn record_i128(&mut self, f: &Field, v: i128) {
        self.pairs.record_i128(f, v)
    }
    fn record_u128(&mut self, f: &Field, v: u128) {
        self.pairs.record_u128(f, v)
    }

    fn record_str(&mut self, field: &Field, value: &str) {
        let name = field.name();
        if name == "message" {
            if let Some(out) = self.rec.body_writer() {
                json::write_str(out, value);
            }
        } else if !name.starts_with("log.") && !self.special_str(name, value) {
            self.pairs.record_str(field, value);
        }
    }

    fn record_error(&mut self, field: &Field, value: &(dyn std::error::Error + 'static)) {
        if self.exception.is_none() {
            self.exception = Some(Exception::from_dyn(value));
        } else {
            self.pairs.record_debug(field, &format_args!("{value}"));
        }
    }

    fn record_debug(&mut self, field: &Field, value: &dyn fmt::Debug) {
        let name = field.name();
        if name == "message" {
            if let Some(out) = self.rec.body_writer() {
                out.push(b'"');
                let _ = fmt::write(&mut json::EscapeWriter(out), format_args!("{value:?}"));
                out.push(b'"');
            }
        } else if !name.starts_with("log.") {
            if matches!(name, "event" | "trace_id" | "span_id" | "correlation_id") {
                let text = format!("{value:?}");
                if self.special_str(name, &text) {
                    return;
                }
            }
            self.pairs.record_debug(field, value);
        }
    }
}

impl<S> Layer<S> for LatticeLayer
where
    S: Subscriber + for<'a> LookupSpan<'a>,
{
    fn on_new_span(&self, attrs: &Attributes<'_>, id: &Id, ctx: LayerContext<'_, S>) {
        let Some(span) = ctx.span(id) else { return };
        let parent = span
            .parent()
            .and_then(|p| p.extensions().get::<SpanData>().map(|d| d.ctx.clone()))
            .or_else(context::current)
            .unwrap_or_else(|| self.log.root().clone());
        let mut pairs = Vec::new();
        let mut op = None;
        attrs.record(&mut PairVisitor { pairs: &mut pairs, op: Some(&mut op) });
        let mut data = SpanData { ctx: parent.clone(), parent, name: attrs.metadata().name(), attrs: pairs, op };
        data.ctx = data.build();
        span.extensions_mut().insert(data);
    }

    fn on_record(&self, id: &Id, values: &SpanValues<'_>, ctx: LayerContext<'_, S>) {
        let Some(span) = ctx.span(id) else { return };
        let mut ext = span.extensions_mut();
        let Some(data) = ext.get_mut::<SpanData>() else { return };
        let mut op = data.op.clone();
        values.record(&mut PairVisitor { pairs: &mut data.attrs, op: Some(&mut op) });
        data.op = op;
        data.ctx = data.build();
    }

    fn on_event(&self, event: &Event<'_>, ctx: LayerContext<'_, S>) {
        let meta = event.metadata();
        let lvl = level(meta.level());
        if !self.log.enabled(lvl) {
            return;
        }
        let span_ctx = ctx.event_span(event).and_then(|s| s.extensions().get::<SpanData>().map(|d| d.ctx.clone()));
        let mut rec = self.log.record(lvl);
        rec.set_context(span_ctx.as_ref());
        let mut pairs = PAIRS.with(Cell::take);
        let mut fields = FIELDS.with(Cell::take);
        pairs.clear();
        fields.clear();
        let exception = {
            let mut v =
                EventVisitor { rec: &mut rec, pairs: PairVisitor { pairs: &mut pairs, op: None }, fields: &mut fields, exception: None };
            event.record(&mut v);
            v.exception
        };
        if self.target {
            let mut p = PairVisitor { pairs: &mut pairs, op: None };
            let out = p.key("logger.name");
            json::write_str(out, meta.target());
            out.push(b']');
        }
        rec.push_raw_pairs(&pairs);
        rec.push_raw_fields(&fields);
        let rec = match &exception {
            Some(e) => rec.exception(e),
            None => rec,
        };
        rec.emit();
        PAIRS.with(|c| c.set(pairs));
        FIELDS.with(|c| c.set(fields));
    }
}
