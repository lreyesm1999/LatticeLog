//! `Logger`, `Record` (the hot path) and the hand-off to the writer thread.
//!
//! The calling thread encodes the record into a thread-local buffer (no allocation once warm), then copies the
//! bytes into one shared batch under a short lock. The writer thread swaps the whole batch out, assigns dense
//! sequences, resolves contexts to file-local refs and appends everything with one `write` per batch.

use crate::context::{self, Context, ContextBuilder, ContextKind};
use crate::exception::Exception;
use crate::folder::{self, ProcessFolder};
use crate::json;
use crate::value::{Timestamp, Value};
use crate::writer;
use std::cell::Cell;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

/// Severity (OpenTelemetry numbers).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
#[repr(u8)]
pub enum Level {
    Trace = 1,
    Debug = 5,
    Info = 9,
    Warn = 13,
    Error = 17,
    Fatal = 21,
}

impl Level {
    pub fn parse(s: &str) -> Option<Level> {
        Some(match s.to_ascii_lowercase().as_str() {
            "trace" => Level::Trace,
            "debug" => Level::Debug,
            "info" | "information" => Level::Info,
            "warn" | "warning" => Level::Warn,
            "error" | "err" => Level::Error,
            "fatal" | "critical" => Level::Fatal,
            _ => return None,
        })
    }
}

/// When accepted records reach the disk (specs/durability.md). Writing is asynchronous in every mode:
/// [`Logger::flush`] reports the level actually reached.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Durability {
    /// Batches every ≤ 100 ms, handed to the OS per batch; fsync only at flush, rotation and close.
    Fast,
    /// Batches every ≤ 10 ms (the OS timer may round up), handed to the OS per batch, fsync every second.
    Balanced,
    /// Group commit: the writer wakes for every record and fsyncs each batch.
    Durable,
}

/// What a full queue does to the calling thread.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Saturation {
    /// Wait for space up to the timeout, then drop the record (counted).
    Block(Duration),
    /// Drop the new record at once (counted).
    DropNewest,
}

/// Configuration for [`open`](crate::open).
#[derive(Clone, Debug)]
pub struct Options {
    pub(crate) service: String,
    pub(crate) environment: Option<String>,
    pub(crate) durability: Durability,
    pub(crate) min_level: Level,
    pub(crate) resource: Vec<u8>,
    pub(crate) max_age: Option<Duration>,
    pub(crate) max_bytes: Option<u64>,
    pub(crate) file_max_bytes: u64,
    pub(crate) queue_bytes: usize,
    pub(crate) saturation: Saturation,
    pub(crate) close_timeout: Duration,
    #[cfg(feature = "server")]
    pub(crate) server: Option<crate::ship::ServerOptions>,
}

impl Options {
    /// Options for `service` (required: it is the name LatticeLog finds the source by).
    pub fn new(service: impl Into<String>) -> Self {
        Options {
            service: service.into(),
            environment: None,
            durability: Durability::Balanced,
            min_level: Level::Info,
            resource: Vec::new(),
            max_age: None,
            max_bytes: None,
            file_max_bytes: 16 * 1024 * 1024,
            queue_bytes: 16 * 1024 * 1024,
            saturation: Saturation::Block(Duration::from_secs(5)),
            close_timeout: Duration::from_secs(5),
            #[cfg(feature = "server")]
            server: None,
        }
    }

    /// `deployment.environment.name` of the resource, also written to the manifest.
    pub fn environment(mut self, environment: impl Into<String>) -> Self {
        self.environment = Some(environment.into());
        self
    }

    pub fn durability(mut self, durability: Durability) -> Self {
        self.durability = durability;
        self
    }

    pub fn min_level(mut self, level: Level) -> Self {
        self.min_level = level;
        self
    }

    /// Adds a resource attribute (inherited by every record), e.g. `service.version`.
    pub fn resource(mut self, key: &str, value: impl Value) -> Self {
        if !self.resource.is_empty() {
            self.resource.push(b',');
        }
        self.resource.push(b'[');
        json::write_str(&mut self.resource, key);
        self.resource.push(b',');
        value.encode(&mut self.resource);
        self.resource.push(b']');
        self
    }

    /// Retention by age: whole closed files older than this are deleted, oldest first.
    pub fn max_age(mut self, age: Duration) -> Self {
        self.max_age = Some(age);
        self
    }

    /// Retention by size of the whole source folder.
    pub fn max_bytes(mut self, bytes: u64) -> Self {
        self.max_bytes = Some(bytes);
        self
    }

    /// A new file is started when the current one reaches this size (default 16 MiB).
    pub fn file_max_bytes(mut self, bytes: u64) -> Self {
        self.file_max_bytes = bytes.max(4096);
        self
    }

    /// Bytes of encoded records waiting for the writer before [`Saturation`] applies (default 16 MiB).
    pub fn queue_bytes(mut self, bytes: usize) -> Self {
        self.queue_bytes = bytes.max(64 * 1024);
        self
    }

    pub fn saturation(mut self, saturation: Saturation) -> Self {
        self.saturation = saturation;
        self
    }

    /// How long [`Logger::close`] waits for the server to confirm what is left (server mode, default 5 s).
    pub fn close_timeout(mut self, timeout: Duration) -> Self {
        self.close_timeout = timeout;
        self
    }

    /// Ships the folder to `latticelog-server` with ingest protocol v1.
    #[cfg(feature = "server")]
    pub fn server(mut self, server: crate::ship::ServerOptions) -> Self {
        self.server = Some(server);
        self
    }
}

/// Persistence level actually reached by a flush.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum FlushLevel {
    /// Nothing could be written (see `error`).
    None,
    WrittenToOs,
    FsyncConfirmed,
}

/// Outcome of [`Logger::flush`]: covers every record accepted before the call.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FlushResult {
    pub level: FlushLevel,
    /// Highest sequence written so far.
    pub last_seq: u64,
    /// The last write or fsync error since the previous flush, if any (its records are counted as dropped).
    pub error: Option<String>,
}

/// Counters. Every loss is counted; nothing is dropped silently.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Stats {
    /// Records written to a file (handed to the OS).
    pub written: u64,
    /// Highest sequence written.
    pub last_seq: u64,
    pub dropped_saturation: u64,
    pub dropped_write_error: u64,
    /// Server mode: refused because the unconfirmed backlog reached `max_spool_bytes`.
    pub dropped_spool_full: u64,
    pub dropped_after_close: u64,
    /// Server mode: highest sequence the server confirmed.
    pub committed_through: u64,
    /// Server mode: batches confirmed, retries, and the last shipping error.
    pub batches_shipped: u64,
    pub ship_retries: u64,
    pub ship_error: Option<String>,
    /// Server mode: true when shipping stopped for good (authentication, invalid batch: see `ship_error`).
    pub ship_failed: bool,
}

#[derive(Default)]
pub(crate) struct Counters {
    pub written: AtomicU64,
    pub last_seq: AtomicU64,
    pub dropped_saturation: AtomicU64,
    pub dropped_write_error: AtomicU64,
    pub dropped_spool_full: AtomicU64,
    pub dropped_after_close: AtomicU64,
}

/// Frame header in the shared batch: body length and context index.
pub(crate) const FRAME_HEADER: usize = 8;
/// Context index meaning "the logger's resource context".
pub(crate) const CTX_ROOT: u32 = u32::MAX;

pub(crate) struct State {
    pub buf: Vec<u8>,
    pub ctxs: Vec<Context>,
    pub last_ctx_id: u64,
    pub closed: bool,
    /// The writer is waiting and wants to be woken by the next record (deep sleep or durable mode).
    pub notify_on_push: bool,
    pub writer_waiting: bool,
    pub blocked: u32,
    pub flush_requested: u64,
    pub flush_fsync: bool,
    pub flush_done: u64,
    pub flush_result: FlushResult,
}

pub(crate) struct Shared {
    pub state: Mutex<State>,
    pub writer_cv: Condvar,
    pub space_cv: Condvar,
    pub flush_cv: Condvar,
    pub min_level: AtomicU8,
    pub counters: Counters,
    pub root: Context,
    pub durability: Durability,
    pub saturation: Saturation,
    pub capacity: usize,
    pub folder: PathBuf,
    pub process_dir: PathBuf,
    pub producer_id: [u8; 16],
    pub max_age: Option<Duration>,
    pub max_bytes: Option<u64>,
    pub file_max_bytes: u64,
    /// Set by the shipper while the unconfirmed backlog is over `max_spool_bytes`.
    pub spool_full: AtomicBool,
    #[cfg(feature = "server")]
    pub ship: Option<Arc<crate::ship::ShipShared>>,
}

impl Shared {
    pub(crate) fn lock(&self) -> MutexGuard<'_, State> {
        // A panic while holding the lock cannot leave the batch half-written (frames are appended whole), so a
        // poisoned lock is still usable: logging must keep working after an unrelated panic.
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Copies one encoded record into the shared batch. Returns false when it was dropped (counted).
    #[inline]
    fn enqueue(&self, body: &[u8], ctx: Option<&Context>) -> bool {
        let need = FRAME_HEADER + body.len();
        let mut st = self.lock();
        if st.closed {
            drop(st);
            self.counters.dropped_after_close.fetch_add(1, Ordering::Relaxed);
            return false;
        }
        if st.buf.len() + need > self.capacity && !st.buf.is_empty() {
            match self.saturation {
                Saturation::DropNewest => {
                    drop(st);
                    self.counters.dropped_saturation.fetch_add(1, Ordering::Relaxed);
                    return false;
                }
                Saturation::Block(timeout) => {
                    let deadline = Instant::now() + timeout;
                    st.blocked += 1;
                    while st.buf.len() + need > self.capacity && !st.buf.is_empty() && !st.closed {
                        self.writer_cv.notify_one();
                        let now = Instant::now();
                        if now >= deadline {
                            break;
                        }
                        st = self.space_cv.wait_timeout(st, deadline - now).unwrap_or_else(|e| e.into_inner()).0;
                    }
                    st.blocked -= 1;
                    if st.closed || (st.buf.len() + need > self.capacity && !st.buf.is_empty()) {
                        let closed = st.closed;
                        drop(st);
                        let c = if closed { &self.counters.dropped_after_close } else { &self.counters.dropped_saturation };
                        c.fetch_add(1, Ordering::Relaxed);
                        return false;
                    }
                }
            }
        }
        let idx = match ctx {
            None => CTX_ROOT,
            Some(c) if c.0.id == st.last_ctx_id => (st.ctxs.len() - 1) as u32,
            Some(c) => {
                st.last_ctx_id = c.0.id;
                st.ctxs.push(c.clone());
                (st.ctxs.len() - 1) as u32
            }
        };
        let st = &mut *st;
        st.buf.reserve(need);
        st.buf.extend_from_slice(&(body.len() as u32).to_ne_bytes());
        st.buf.extend_from_slice(&idx.to_ne_bytes());
        st.buf.extend_from_slice(body);
        if st.notify_on_push || (st.writer_waiting && st.buf.len() > self.capacity / 2) {
            st.notify_on_push = false;
            self.writer_cv.notify_one();
        }
        true
    }
}

struct Handle {
    shared: Arc<Shared>,
    writer: Mutex<Option<JoinHandle<ProcessFolder>>>,
    close_timeout: Duration,
    #[cfg(feature = "server")]
    shipper: Mutex<Option<JoinHandle<()>>>,
}

impl Drop for Handle {
    fn drop(&mut self) {
        close_handle(self);
    }
}

fn close_handle(h: &Handle) {
    let Some(writer) = h.writer.lock().unwrap_or_else(|e| e.into_inner()).take() else { return };
    #[cfg(feature = "server")]
    let last = {
        let r = flush_shared(&h.shared, true);
        r.last_seq
    };
    {
        let mut st = h.shared.lock();
        st.closed = true;
        h.shared.writer_cv.notify_one();
        h.shared.space_cv.notify_all();
    }
    // The process folder (and its OS lock) is released only after the shipper stopped: two processes never ship
    // the same folder at once.
    let _process_folder = writer.join();
    #[cfg(feature = "server")]
    if let Some(ship) = &h.shared.ship {
        ship.wait_for(last, h.close_timeout);
        ship.stop();
        if let Some(t) = h.shipper.lock().unwrap_or_else(|e| e.into_inner()).take() {
            let _ = t.join();
        }
    }
    let _ = h.close_timeout;
}

fn flush_shared(shared: &Shared, fsync: bool) -> FlushResult {
    let mut st = shared.lock();
    if st.closed {
        return FlushResult { level: FlushLevel::FsyncConfirmed, ..st.flush_result.clone() };
    }
    st.flush_requested += 1;
    let mine = st.flush_requested;
    st.flush_fsync |= fsync;
    st.notify_on_push = false;
    shared.writer_cv.notify_one();
    while st.flush_done < mine && !st.closed {
        st = shared.flush_cv.wait(st).unwrap_or_else(|e| e.into_inner());
    }
    st.flush_result.clone()
}

/// A LatticeLog logger. Cheap to clone; the writer thread stops (after writing everything accepted) when the last
/// clone is dropped or [`Logger::close`] is called.
#[derive(Clone)]
pub struct Logger {
    shared: Arc<Shared>,
    handle: Arc<Handle>,
}

impl std::fmt::Debug for Logger {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Logger").field("folder", &self.shared.folder).field("process_dir", &self.shared.process_dir).finish()
    }
}

/// Opens (or creates) the source folder of one service and starts the writer thread.
///
/// Records become official `latticelog.jsonl` v1 lines in `<folder>/p-N/latticelog-<seq>.jsonl`, next to the
/// manifest `latticelog.source.json`. Register the parent folder once (`latticelog roots add logs`) and the CLI and
/// MCP server find the service by name. Several processes may share the folder: each writes its own `p-N`.
pub fn open(folder: impl AsRef<Path>, options: Options) -> io::Result<Logger> {
    if options.service.is_empty() {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "service is required"));
    }
    let folder = std::path::absolute(folder.as_ref())?;
    std::fs::create_dir_all(&folder)?;
    folder::ensure_manifest(&folder, &options.service, options.environment.as_deref())?;
    let pf = ProcessFolder::acquire(&folder)?;

    let mut root = ContextBuilder::new(ContextKind::Resource, None, None).attr("service.name", options.service.as_str());
    if let Some(env) = &options.environment {
        root = root.attr("deployment.environment.name", env.as_str());
    }
    let root = root.raw_attrs(&options.resource).build();

    folder::apply_retention(&folder, options.max_age, options.max_bytes);

    #[cfg(feature = "server")]
    let ship = options.server.as_ref().map(|s| Arc::new(crate::ship::ShipShared::new(s.clone(), &pf)));

    let shared = Arc::new(Shared {
        state: Mutex::new(State {
            buf: Vec::with_capacity(64 * 1024),
            ctxs: Vec::new(),
            last_ctx_id: 0,
            closed: false,
            notify_on_push: false,
            writer_waiting: false,
            blocked: 0,
            flush_requested: 0,
            flush_fsync: false,
            flush_done: 0,
            flush_result: FlushResult { level: FlushLevel::FsyncConfirmed, last_seq: pf.next_seq - 1, error: None },
        }),
        writer_cv: Condvar::new(),
        space_cv: Condvar::new(),
        flush_cv: Condvar::new(),
        min_level: AtomicU8::new(options.min_level as u8),
        counters: Counters { last_seq: AtomicU64::new(pf.next_seq - 1), ..Default::default() },
        root,
        durability: options.durability,
        saturation: options.saturation,
        capacity: options.queue_bytes,
        folder,
        process_dir: pf.dir.clone(),
        producer_id: pf.producer_id,
        max_age: options.max_age,
        max_bytes: options.max_bytes,
        file_max_bytes: options.file_max_bytes,
        spool_full: AtomicBool::new(false),
        #[cfg(feature = "server")]
        ship: ship.clone(),
    });

    let s = shared.clone();
    let writer = std::thread::Builder::new().name("latticelog-writer".into()).spawn(move || writer::run(s, pf))?;
    #[cfg(feature = "server")]
    let shipper = match &ship {
        Some(ship) => {
            let (ship, s) = (ship.clone(), shared.clone());
            Some(std::thread::Builder::new().name("latticelog-shipper".into()).spawn(move || crate::ship::run(ship, s))?)
        }
        None => None,
    };
    let handle = Arc::new(Handle {
        shared: shared.clone(),
        writer: Mutex::new(Some(writer)),
        close_timeout: options.close_timeout,
        #[cfg(feature = "server")]
        shipper: Mutex::new(shipper),
    });
    Ok(Logger { shared, handle })
}

impl Logger {
    /// True when records of `level` are written. One relaxed atomic load.
    #[inline(always)]
    pub fn enabled(&self, level: Level) -> bool {
        level as u8 >= self.shared.min_level.load(Ordering::Relaxed)
    }

    pub fn set_min_level(&self, level: Level) {
        self.shared.min_level.store(level as u8, Ordering::Relaxed);
    }

    /// Starts a record with a message body (a template such as `"order {id}"` is kept as written).
    #[inline]
    pub fn log<'a>(&'a self, level: Level, body: &str) -> Record<'a> {
        Record::new(self, level, Some(body))
    }

    /// Starts a record without a body (typically with [`Record::event`]).
    #[inline]
    pub fn record(&self, level: Level) -> Record<'_> {
        Record::new(self, level, None)
    }

    #[inline]
    pub fn trace<'a>(&'a self, body: &str) -> Record<'a> {
        self.log(Level::Trace, body)
    }
    #[inline]
    pub fn debug<'a>(&'a self, body: &str) -> Record<'a> {
        self.log(Level::Debug, body)
    }
    #[inline]
    pub fn info<'a>(&'a self, body: &str) -> Record<'a> {
        self.log(Level::Info, body)
    }
    #[inline]
    pub fn warn<'a>(&'a self, body: &str) -> Record<'a> {
        self.log(Level::Warn, body)
    }
    #[inline]
    pub fn error<'a>(&'a self, body: &str) -> Record<'a> {
        self.log(Level::Error, body)
    }
    #[inline]
    pub fn fatal<'a>(&'a self, body: &str) -> Record<'a> {
        self.log(Level::Fatal, body)
    }

    /// The resource context (service, environment, resource attributes).
    pub fn root(&self) -> &Context {
        &self.shared.root
    }

    fn parent(&self) -> Context {
        context::current().unwrap_or_else(|| self.shared.root.clone())
    }

    /// A named scope under the thread's current context: `let _g = log.scope("import").attr("file", f).enter();`
    pub fn scope(&self, name: &str) -> ContextBuilder {
        ContextBuilder::new(ContextKind::Scope, Some(name), Some(self.parent()))
    }

    /// An operation: records inside carry `operation_id` (random when `id` is `None`) and the enclosing
    /// operation as `parent_operation_id`.
    pub fn operation(&self, name: &str, id: Option<&str>) -> ContextBuilder {
        let id = id.map_or_else(context::random_operation_id, str::to_owned);
        ContextBuilder::new(ContextKind::Operation, Some(name), Some(self.parent())).attr("operation.name", name).operation(id)
    }

    /// A context of any kind.
    pub fn context(&self, kind: ContextKind, name: Option<&str>) -> ContextBuilder {
        ContextBuilder::new(kind, name, Some(self.parent()))
    }

    /// Waits until every record accepted before the call is handed to the OS, and fsynced when `fsync` is true
    /// (always in [`Durability::Durable`]).
    pub fn flush(&self, fsync: bool) -> FlushResult {
        flush_shared(&self.shared, fsync)
    }

    /// Server mode: flushes (with fsync), then waits until the server confirmed every record written so far.
    #[cfg(feature = "server")]
    pub fn flush_shipped(&self, timeout: Duration) -> bool {
        let r = self.flush(true);
        match &self.shared.ship {
            Some(ship) => ship.wait_for(r.last_seq, timeout),
            None => true,
        }
    }

    pub fn stats(&self) -> Stats {
        let c = &self.shared.counters;
        #[allow(unused_mut)]
        let mut s = Stats {
            written: c.written.load(Ordering::Relaxed),
            last_seq: c.last_seq.load(Ordering::Relaxed),
            dropped_saturation: c.dropped_saturation.load(Ordering::Relaxed),
            dropped_write_error: c.dropped_write_error.load(Ordering::Relaxed),
            dropped_spool_full: c.dropped_spool_full.load(Ordering::Relaxed),
            dropped_after_close: c.dropped_after_close.load(Ordering::Relaxed),
            ..Stats::default()
        };
        #[cfg(feature = "server")]
        if let Some(ship) = &self.shared.ship {
            ship.fill_stats(&mut s);
        }
        s
    }

    /// The source folder and this process's `p-N` folder.
    pub fn folder(&self) -> &Path {
        &self.shared.folder
    }

    pub fn process_dir(&self) -> &Path {
        &self.shared.process_dir
    }

    /// 32 hex digits: the `store_id` of this process folder (and the producer id when shipping).
    pub fn producer_id(&self) -> String {
        json::hex_string(&self.shared.producer_id)
    }

    /// Writes everything accepted, fsyncs, releases the process folder and (server mode) waits up to
    /// `close_timeout` for the server. Later records are counted in `dropped_after_close`. Idempotent.
    pub fn close(&self) {
        close_handle(&self.handle);
    }

    /// A guard that closes the logger when dropped: keep it in `main` so records are on disk at exit even when a
    /// clone of the logger lives in a global subscriber.
    pub fn guard(&self) -> CloseGuard {
        CloseGuard(self.clone())
    }

    /// Writes panics as fatal records (message, location, thread) and flushes with fsync before the previous
    /// hook runs.
    pub fn log_panics(&self) {
        let log = self.clone();
        let previous = std::panic::take_hook();
        std::panic::set_hook(Box::new(move |info| {
            let msg = info
                .payload()
                .downcast_ref::<&str>()
                .map(|s| (*s).to_owned())
                .or_else(|| info.payload().downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "panic".to_owned());
            let mut ex = Exception::new("panic", msg.clone());
            if let Some(loc) = info.location() {
                ex = ex.with_stack(format!("{}:{}:{}", loc.file(), loc.line(), loc.column()));
            }
            log.fatal(&msg).event("panic").attr("thread.name", std::thread::current().name().unwrap_or("unnamed")).exception(&ex).emit();
            log.flush(true);
            previous(info);
        }));
    }
}

/// Closes the logger when dropped (see [`Logger::guard`]).
#[derive(Debug)]
pub struct CloseGuard(Logger);

impl Drop for CloseGuard {
    fn drop(&mut self) {
        self.0.close();
    }
}

// ---------------------------------------------------------------------------------------------------- Record

thread_local! {
    static SCRATCH: Cell<Vec<u8>> = const { Cell::new(Vec::new()) };
}

const TIME_KEY: &[u8] = b"\"time\":";
const TIME_END: usize = TIME_KEY.len() + 32;

#[inline]
fn take_scratch() -> Vec<u8> {
    let mut v = SCRATCH.with(Cell::take);
    v.clear();
    if v.capacity() == 0 {
        v.reserve(1024);
    }
    v
}

#[inline]
fn give_scratch(v: Vec<u8>) {
    if v.capacity() != 0 && v.capacity() <= 256 * 1024 {
        SCRATCH.with(|c| c.set(v));
    }
}

/// One record being built. Finish it with [`Record::emit`]; a record that is dropped without `emit` is
/// discarded. Encoding happens as the methods are called, into a reused thread-local buffer.
#[must_use = "a record is written only by .emit()"]
pub struct Record<'a> {
    log: &'a Logger,
    buf: Vec<u8>,
    enabled: bool,
    attrs_open: bool,
    ctx: Option<&'a Context>,
    event: Option<&'a str>,
    trace: Option<[u8; 16]>,
    span: Option<[u8; 8]>,
    correlation: Option<&'a str>,
    exception: Option<Vec<u8>>,
    raw_fields: Vec<u8>,
}

impl<'a> Record<'a> {
    #[inline]
    fn new(log: &'a Logger, level: Level, body: Option<&str>) -> Self {
        let enabled = log.enabled(level);
        let mut buf = Vec::new();
        if enabled {
            buf = take_scratch();
            buf.extend_from_slice(TIME_KEY);
            json::write_ts(&mut buf, Timestamp::now().0);
            buf.extend_from_slice(b",\"severity_number\":");
            json::write_i64(&mut buf, level as i64);
            if let Some(b) = body {
                buf.extend_from_slice(b",\"body\":");
                json::write_str(&mut buf, b);
            }
        }
        Record {
            log,
            buf,
            enabled,
            attrs_open: false,
            ctx: None,
            event: None,
            trace: None,
            span: None,
            correlation: None,
            exception: None,
            raw_fields: Vec::new(),
        }
    }

    /// Internal: a record whose body is written by the caller (bridges format `fmt::Arguments` in place).
    #[cfg_attr(not(any(feature = "tracing", feature = "log")), allow(dead_code))]
    #[inline]
    pub(crate) fn body_writer(&mut self) -> Option<&mut Vec<u8>> {
        if self.enabled {
            self.buf.extend_from_slice(b",\"body\":");
            Some(&mut self.buf)
        } else {
            None
        }
    }

    /// Internal: appends pre-encoded pairs (`["k",v],["k",v]`).
    #[cfg_attr(not(any(feature = "tracing", feature = "log")), allow(dead_code))]
    #[inline]
    pub(crate) fn push_raw_pairs(&mut self, pairs: &[u8]) {
        if self.enabled && !pairs.is_empty() {
            self.buf.extend_from_slice(if self.attrs_open { b"," } else { b",\"attributes\":[" });
            self.attrs_open = true;
            self.buf.extend_from_slice(pairs);
        }
    }

    /// Internal: fields encoded by a bridge (`,"event":…`), written after the attributes.
    #[cfg_attr(not(any(feature = "tracing", feature = "log")), allow(dead_code))]
    #[inline]
    pub(crate) fn push_raw_fields(&mut self, fields: &[u8]) {
        if self.enabled && !fields.is_empty() {
            self.raw_fields.extend_from_slice(fields);
        }
    }

    /// Adds a typed attribute. Order and duplicate keys are kept.
    #[inline]
    pub fn attr(mut self, key: &str, value: impl Value) -> Self {
        if self.enabled {
            self.buf.extend_from_slice(if self.attrs_open { b",[" } else { b",\"attributes\":[[" });
            self.attrs_open = true;
            json::write_str(&mut self.buf, key);
            self.buf.push(b',');
            value.encode(&mut self.buf);
            self.buf.push(b']');
        }
        self
    }

    /// Event name (e.g. `order_placed`), the identity LatticeLog groups and queries by.
    #[inline]
    pub fn event(mut self, name: &'a str) -> Self {
        self.event = Some(name);
        self
    }

    /// Overrides the record time (default: when the record was started).
    pub fn time(mut self, time: impl Into<Timestamp>) -> Self {
        if self.enabled {
            let mut t = Vec::with_capacity(32);
            json::write_ts(&mut t, time.into().0);
            self.buf[TIME_KEY.len()..TIME_END].copy_from_slice(&t);
        }
        self
    }

    /// W3C trace id and optional span id (all-zero ids are ignored).
    pub fn trace(mut self, trace_id: [u8; 16], span_id: Option<[u8; 8]>) -> Self {
        self.trace = (trace_id != [0; 16]).then_some(trace_id);
        self.span = span_id.filter(|s| *s != [0; 8]);
        self
    }

    /// Trace id (32 hex) and optional span id (16 hex), as found in a `traceparent` header. Invalid text is ignored.
    pub fn trace_hex(self, trace_id: &str, span_id: Option<&str>) -> Self {
        let t = folder::parse_hex16(trace_id);
        let s = span_id.and_then(parse_hex8);
        match t {
            Some(t) => self.trace(t, s),
            None => self,
        }
    }

    /// Business correlation id (message id, order id…).
    pub fn correlation_id(mut self, id: &'a str) -> Self {
        self.correlation = Some(id);
        self
    }

    /// The context this record belongs to (default: the thread's current context, else the resource context).
    #[inline]
    pub fn context(mut self, ctx: &'a Context) -> Self {
        self.ctx = Some(ctx);
        self
    }

    /// Internal: the context chosen by a bridge (tracing span), or none for the default.
    #[cfg_attr(not(any(feature = "tracing", feature = "log")), allow(dead_code))]
    #[inline]
    pub(crate) fn set_context(&mut self, ctx: Option<&'a Context>) {
        self.ctx = ctx;
    }

    /// Attaches an error with its real type and its `source()` chain.
    pub fn error<E: std::error::Error + 'static>(self, error: &E) -> Self {
        if self.enabled { self.exception(&Exception::from_error(error)) } else { self }
    }

    /// Attaches a type-erased error (`&dyn Error`, `anyhow::Error::as_ref()`).
    pub fn error_dyn(self, error: &(dyn std::error::Error + 'static)) -> Self {
        if self.enabled { self.exception(&Exception::from_dyn(error)) } else { self }
    }

    /// Attaches an exception built by hand.
    pub fn exception(mut self, exception: &Exception) -> Self {
        if self.enabled {
            let mut e = Vec::with_capacity(128);
            exception.encode(&mut e);
            self.exception = Some(e);
        }
        self
    }

    #[inline]
    fn close_attrs(&mut self) {
        if self.attrs_open {
            self.buf.push(b']');
            self.attrs_open = false;
        }
    }

    /// Hands the record to the writer. Returns false when it was filtered by level or dropped (counted).
    #[inline]
    pub fn emit(mut self) -> bool {
        if !self.enabled {
            return false;
        }
        self.close_attrs();
        let mut buf = std::mem::take(&mut self.buf);
        if let Some(e) = self.event {
            buf.extend_from_slice(b",\"event\":");
            json::write_str(&mut buf, e);
        }
        if let Some(t) = self.trace {
            buf.extend_from_slice(b",\"trace_id\":\"");
            json::write_hex(&mut buf, &t);
            buf.push(b'"');
            if let Some(s) = self.span {
                buf.extend_from_slice(b",\"span_id\":\"");
                json::write_hex(&mut buf, &s);
                buf.push(b'"');
            }
        }
        if let Some(c) = self.correlation {
            buf.extend_from_slice(b",\"correlation_id\":");
            json::write_str(&mut buf, c);
        }
        if let Some(e) = &self.exception {
            buf.extend_from_slice(b",\"exception\":");
            buf.extend_from_slice(e);
        }
        if !self.raw_fields.is_empty() {
            buf.extend_from_slice(&self.raw_fields);
        }
        let shared = &self.log.shared;
        let ok = match self.ctx {
            Some(ctx) => {
                buf.extend_from_slice(&ctx.0.op_fields);
                shared.enqueue(&buf, Some(ctx))
            }
            None => context::CURRENT.with(|c| {
                let cur = c.borrow();
                if let Some(ctx) = cur.as_ref() {
                    buf.extend_from_slice(&ctx.0.op_fields);
                }
                shared.enqueue(&buf, cur.as_ref())
            }),
        };
        give_scratch(buf);
        ok
    }
}

impl std::fmt::Debug for Record<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Record").field("enabled", &self.enabled).field("encoded", &String::from_utf8_lossy(&self.buf)).finish()
    }
}

impl Drop for Record<'_> {
    fn drop(&mut self) {
        give_scratch(std::mem::take(&mut self.buf));
    }
}

fn parse_hex8(s: &str) -> Option<[u8; 8]> {
    if s.len() != 16 {
        return None;
    }
    let mut out = [0u8; 8];
    for (i, b) in out.iter_mut().enumerate() {
        *b = u8::from_str_radix(s.get(i * 2..i * 2 + 2)?, 16).ok()?;
    }
    Some(out)
}
