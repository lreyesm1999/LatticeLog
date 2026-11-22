//! Server mode: ships the process folder to `latticelog-server` with ingest protocol v1 (specs/ingest-protocol.md).
//!
//! The files the writer produces are already `latticelog.jsonl` documents, so a batch is the file's own record
//! lines, byte for byte, behind a header and the context lines they reference: nothing is decoded or re-encoded.
//! Only the server's acknowledgement moves `cursor.json`; nothing unconfirmed is ever deleted.

use crate::folder::{self, ProcessFolder};
use crate::json;
use crate::logger::{Shared, Stats};
use crate::minijson::{self, Json};
use std::collections::{BTreeSet, HashMap};
use std::fs::{self, File};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

/// Persistence level the server must reach before it acknowledges.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Ack {
    /// The server's own fsync returned (default).
    Fsync,
    /// Handed to the server's OS.
    Os,
}

/// Where and how to ship. `ServerOptions::new("https://logs:7400", token).namespace("acme/prod")`.
#[derive(Clone)]
pub struct ServerOptions {
    pub(crate) endpoint: String,
    pub(crate) token: String,
    pub(crate) namespace: Option<String>,
    pub(crate) ack: Ack,
    pub(crate) batch_records: usize,
    pub(crate) batch_bytes: usize,
    pub(crate) linger: Duration,
    pub(crate) poll_interval: Duration,
    pub(crate) max_spool_bytes: u64,
    pub(crate) delete_acknowledged: bool,
    pub(crate) retry_base: Duration,
    pub(crate) retry_max: Duration,
    pub(crate) timeout: Duration,
}

impl std::fmt::Debug for ServerOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServerOptions")
            .field("endpoint", &self.endpoint)
            .field("token", &"<redacted>")
            .field("namespace", &self.namespace)
            .field("ack", &self.ack)
            .finish_non_exhaustive()
    }
}

impl ServerOptions {
    pub fn new(endpoint: impl Into<String>, token: impl Into<String>) -> Self {
        ServerOptions {
            endpoint: endpoint.into(),
            token: token.into(),
            namespace: None,
            ack: Ack::Fsync,
            batch_records: 5000,
            batch_bytes: 4 * 1024 * 1024,
            linger: Duration::from_millis(100),
            poll_interval: Duration::from_millis(200),
            max_spool_bytes: 512 * 1024 * 1024,
            delete_acknowledged: true,
            retry_base: Duration::from_millis(200),
            retry_max: Duration::from_secs(30),
            timeout: Duration::from_secs(30),
        }
    }

    /// Target namespace (`LatticeLog-Namespace` header).
    pub fn namespace(mut self, namespace: impl Into<String>) -> Self {
        self.namespace = Some(namespace.into());
        self
    }

    pub fn ack(mut self, ack: Ack) -> Self {
        self.ack = ack;
        self
    }

    /// Records per request (default 5,000; the server accepts up to 50,000).
    pub fn batch_records(mut self, n: usize) -> Self {
        self.batch_records = n.clamp(1, 50_000);
        self
    }

    /// Uncompressed bytes per request (default 4 MiB; the server's limits are 8 MiB sent and 64 MiB decoded).
    pub fn batch_bytes(mut self, n: usize) -> Self {
        self.batch_bytes = n.clamp(4096, 48 * 1024 * 1024);
        self
    }

    /// How long a partial batch may wait for more records (default 100 ms). `flush_shipped` skips it.
    pub fn linger(mut self, d: Duration) -> Self {
        self.linger = d;
        self
    }

    pub fn poll_interval(mut self, d: Duration) -> Self {
        self.poll_interval = d;
        self
    }

    /// Unconfirmed backlog at which new records are refused and counted (`dropped_spool_full`). Default 512 MiB.
    pub fn max_spool_bytes(mut self, n: u64) -> Self {
        self.max_spool_bytes = n;
        self
    }

    /// Keep confirmed files (then `Options::max_age`/`max_bytes` govern them) instead of deleting them.
    pub fn keep_acknowledged(mut self) -> Self {
        self.delete_acknowledged = false;
        self
    }

    pub fn retry(mut self, base: Duration, max: Duration) -> Self {
        self.retry_base = base;
        self.retry_max = max;
        self
    }

    /// Timeout of one request, including the response (default 30 s).
    pub fn timeout(mut self, d: Duration) -> Self {
        self.timeout = d;
        self
    }
}

#[derive(Default)]
struct Signal {
    written: bool,
    urgent: bool,
    stop: bool,
}

#[derive(Default)]
struct ShipStatus {
    batches: u64,
    retries: u64,
    error: Option<String>,
    failed: bool,
}

pub(crate) struct ShipShared {
    opts: ServerOptions,
    dir: PathBuf,
    producer: [u8; 16],
    committed: AtomicU64,
    signal: Mutex<Signal>,
    signal_cv: Condvar,
    status: Mutex<ShipStatus>,
    ack_cv: Condvar,
}

impl ShipShared {
    pub(crate) fn new(opts: ServerOptions, pf: &ProcessFolder) -> Self {
        ShipShared {
            committed: AtomicU64::new(folder::committed_through(&pf.dir).unwrap_or(0)),
            opts,
            dir: pf.dir.clone(),
            producer: pf.producer_id,
            signal: Mutex::new(Signal::default()),
            signal_cv: Condvar::new(),
            status: Mutex::new(ShipStatus::default()),
            ack_cv: Condvar::new(),
        }
    }

    fn sig(&self) -> std::sync::MutexGuard<'_, Signal> {
        self.signal.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn st(&self) -> std::sync::MutexGuard<'_, ShipStatus> {
        self.status.lock().unwrap_or_else(|e| e.into_inner())
    }

    pub(crate) fn notify_written(&self) {
        let mut s = self.sig();
        s.written = true;
        self.signal_cv.notify_one();
    }

    pub(crate) fn stop(&self) {
        let mut s = self.sig();
        s.stop = true;
        self.signal_cv.notify_one();
    }

    /// Waits until the server confirmed everything through `seq`. False on timeout or when shipping failed.
    pub(crate) fn wait_for(&self, seq: u64, timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        {
            let mut s = self.sig();
            s.urgent = true;
            self.signal_cv.notify_one();
        }
        let mut st = self.st();
        loop {
            if self.committed.load(Ordering::Acquire) >= seq {
                return true;
            }
            let now = Instant::now();
            if st.failed || now >= deadline {
                return false;
            }
            st = self.ack_cv.wait_timeout(st, (deadline - now).min(Duration::from_millis(100))).unwrap_or_else(|e| e.into_inner()).0;
        }
    }

    pub(crate) fn fill_stats(&self, s: &mut Stats) {
        let st = self.st();
        s.committed_through = self.committed.load(Ordering::Acquire);
        s.batches_shipped = st.batches;
        s.ship_retries = st.retries;
        s.ship_error = st.error.clone();
        s.ship_failed = st.failed;
    }

    /// Waits for new records, the timeout or a stop. With `lingering`, only a stop or an urgent flush ends the wait
    /// early (a partial batch waits for more records).
    fn wait(&self, timeout: Duration, lingering: bool) -> bool {
        let mut s = self.sig();
        let deadline = Instant::now() + timeout;
        loop {
            if s.stop {
                return false;
            }
            if s.urgent || (!lingering && s.written) {
                s.written = false;
                return true;
            }
            let now = Instant::now();
            if now >= deadline {
                s.written = false;
                return true;
            }
            s = self.signal_cv.wait_timeout(s, deadline - now).unwrap_or_else(|e| e.into_inner()).0;
        }
    }

    fn acknowledge(&self, committed: u64) {
        let mut doc = b"{\"committed_through\":".to_vec();
        json::write_u64(&mut doc, committed);
        doc.push(b'}');
        let r = folder::atomic_write(&self.dir.join("cursor.json"), &doc);
        self.committed.store(committed, Ordering::Release);
        let mut st = self.st();
        if let Err(e) = r {
            st.error = Some(format!("cursor.json: {e}"));
        }
        self.ack_cv.notify_all();
    }

    fn report(&self, error: Option<String>, retry: bool, failed: bool) {
        let mut st = self.st();
        st.error = error;
        if retry {
            st.retries += 1;
        }
        if failed {
            st.failed = true;
            self.ack_cv.notify_all();
        }
    }
}

/// One batch ready to send.
struct Batch {
    first: u64,
    count: u64,
    gap: bool,
    body: Vec<u8>,
}

/// Incremental reader of the process folder's files. Keeps the bytes from the first unconfirmed record on, and the
/// context lines of the current file seen so far.
struct Reader {
    dir: PathBuf,
    producer_hex: Vec<u8>,
    cur: Option<Cursor>,
}

struct Cursor {
    first_seq: u64,
    path: PathBuf,
    file: File,
    buf: Vec<u8>,
    buf_start: u64,
    contexts: HashMap<u32, (Option<u32>, Vec<u8>)>,
}

fn find(hay: &[u8], needle: &[u8]) -> Option<usize> {
    hay.windows(needle.len()).position(|w| w == needle)
}

fn number_after(line: &[u8], key: &[u8]) -> Option<u32> {
    let at = find(line, key)? + key.len();
    let digits = line[at..].iter().take_while(|c| c.is_ascii_digit()).count();
    std::str::from_utf8(&line[at..at + digits]).ok()?.parse().ok()
}

impl Reader {
    fn new(dir: &Path, producer: &[u8; 16]) -> Self {
        let mut producer_hex = Vec::new();
        json::write_hex(&mut producer_hex, producer);
        Reader { dir: dir.to_path_buf(), producer_hex, cur: None }
    }

    fn reset(&mut self) {
        self.cur = None;
    }

    /// Opens the file that holds `committed + 1` (or the oldest file when that one is gone).
    fn open_for(&mut self, committed: u64) -> io::Result<bool> {
        let files = folder::data_files(&self.dir)?;
        let pick = files.iter().rev().find(|(first, _)| *first <= committed + 1).or(files.first());
        let Some((first, path)) = pick else { return Ok(false) };
        self.cur = Some(Cursor {
            first_seq: *first,
            path: path.clone(),
            file: File::open(path)?,
            buf: Vec::new(),
            buf_start: 0,
            contexts: HashMap::new(),
        });
        Ok(true)
    }

    fn next_batch(&mut self, committed: u64, max_records: usize, max_bytes: usize) -> io::Result<Option<Batch>> {
        for _ in 0..1000 {
            if self.cur.is_none() && !self.open_for(committed)? {
                return Ok(None);
            }
            let cur = self.cur.as_mut().expect("open");
            let eof_before = cur.buf_start + cur.buf.len() as u64;
            folder::read_at(&mut cur.file, eof_before, &mut cur.buf, 64 * 1024 * 1024)?;

            let mut pos = 0usize;
            let mut drop_to = 0usize;
            let mut records: Vec<(usize, usize)> = Vec::new();
            let mut needed: BTreeSet<u32> = BTreeSet::new();
            let mut first = 0u64;
            let mut last = 0u64;
            let mut bytes = 0usize;
            let mut pending_after = false; // unconfirmed records remain in this file beyond the batch
            while let Some(nl) = cur.buf[pos..].iter().position(|&c| c == b'\n') {
                let line = &cur.buf[pos..pos + nl];
                let end = pos + nl + 1;
                if line.starts_with(b"{\"type\":\"context\"") {
                    if let Some(r) = number_after(line, b"\"ref\":") {
                        cur.contexts.insert(r, (number_after(line, b"\"parent\":"), line.to_vec()));
                    }
                    if records.is_empty() {
                        drop_to = end;
                    }
                } else if let Some(seq) = folder::record_seq(line) {
                    if seq <= committed {
                        if records.is_empty() {
                            drop_to = end;
                        }
                    } else if records.is_empty() || (seq == last + 1 && records.len() < max_records && bytes + line.len() < max_bytes) {
                        if records.is_empty() {
                            first = seq;
                        }
                        last = seq;
                        bytes += line.len() + 1;
                        records.push((pos, end));
                        if let Some(r) = number_after(line, b"\"context\":") {
                            needed.insert(r);
                        }
                    } else {
                        pending_after = true;
                        break;
                    }
                } else if records.is_empty() {
                    drop_to = end; // header or a line we do not ship
                }
                pos = end;
            }

            if !records.is_empty() {
                // Contexts referenced by the records, with their ancestors, in definition order.
                let mut all = BTreeSet::new();
                for r in needed {
                    let mut c = Some(r);
                    while let Some(x) = c {
                        if !all.insert(x) {
                            break;
                        }
                        c = cur.contexts.get(&x).and_then(|(p, _)| *p);
                    }
                }
                let mut body = Vec::with_capacity(bytes + 256);
                body.extend_from_slice(b"{\"type\":\"header\",\"format\":\"latticelog.jsonl\",\"version\":1,\"store_id\":\"");
                body.extend_from_slice(&self.producer_hex);
                body.extend_from_slice(b"\"}\n");
                for r in all {
                    if let Some((_, line)) = cur.contexts.get(&r) {
                        body.extend_from_slice(line);
                        body.push(b'\n');
                    }
                }
                for &(s, e) in &records {
                    body.extend_from_slice(&cur.buf[s..e]);
                }
                let count = records.len() as u64;
                cur.buf.drain(..drop_to);
                cur.buf_start += drop_to as u64;
                return Ok(Some(Batch { first, count, gap: first != committed + 1, body }));
            }

            cur.buf.drain(..drop_to);
            cur.buf_start += drop_to as u64;
            if pending_after || !cur.buf.is_empty() {
                return Ok(None); // a partial line is being written
            }
            // Everything in this file is confirmed. Move on only when a newer file exists (this one is closed).
            let files = folder::data_files(&self.dir)?;
            if !files.iter().any(|(f, _)| *f > cur.first_seq) {
                return Ok(None);
            }
            // Read once more: the writer finished this file before creating the next one.
            if fs::metadata(&cur.path)?.len() > cur.buf_start {
                continue;
            }
            self.cur = None;
        }
        Ok(None)
    }
}

/// Deletes closed files whose records are all confirmed.
fn delete_confirmed(dir: &Path, committed: u64) {
    let Ok(files) = folder::data_files(dir) else { return };
    for w in files.windows(2) {
        if w[1].0 - 1 <= committed {
            let _ = fs::remove_file(&w[0].1);
        }
    }
}

/// Bytes of files that still hold unconfirmed records.
fn unconfirmed_bytes(dir: &Path, committed: u64) -> u64 {
    let Ok(files) = folder::data_files(dir) else { return 0 };
    let mut total = 0;
    for (i, (_, path)) in files.iter().enumerate() {
        let last = files.get(i + 1).map_or(u64::MAX, |n| n.0 - 1);
        if last > committed {
            total += fs::metadata(path).map_or(0, |m| m.len());
        }
    }
    total
}

fn envelope(producer: &[u8; 16], batch: &Batch) -> io::Result<Vec<u8>> {
    let mut gz = flate2::write::GzEncoder::new(Vec::with_capacity(batch.body.len() / 4 + 64), flate2::Compression::fast());
    gz.write_all(&batch.body)?;
    let payload = gz.finish()?;
    let mut out = Vec::with_capacity(48 + payload.len());
    out.extend_from_slice(b"LLIB");
    out.push(1); // version
    out.push(1); // body_encoding: latticelog.jsonl v1
    out.push(1); // compression: gzip
    out.push(u8::from(batch.gap)); // flags: gap_acknowledged
    out.extend_from_slice(producer);
    out.extend_from_slice(&batch.first.to_le_bytes());
    out.extend_from_slice(&(batch.first + batch.count - 1).to_le_bytes());
    out.extend_from_slice(&(batch.count as u32).to_le_bytes());
    out.extend_from_slice(&crc32fast::hash(&payload).to_le_bytes());
    out.extend_from_slice(&payload);
    Ok(out)
}

enum Reply {
    Http { status: u16, body: Json, retry_after: Option<f64> },
    Network(String),
}

fn post(agent: &ureq::Agent, opts: &ServerOptions, url: &str, envelope: &[u8]) -> Reply {
    let mut req = agent
        .post(url)
        .header("Authorization", &format!("Bearer {}", opts.token))
        .header("Content-Type", "application/vnd.latticelog.batch")
        .header("LatticeLog-Ack", if opts.ack == Ack::Fsync { "fsync" } else { "os" });
    if let Some(ns) = &opts.namespace {
        req = req.header("LatticeLog-Namespace", ns);
    }
    match req.send(envelope) {
        Ok(mut r) => {
            let status = r.status().as_u16();
            let retry_after = r.headers().get("Retry-After").and_then(|v| v.to_str().ok()).and_then(|s| s.trim().parse::<f64>().ok());
            let text = r.body_mut().read_to_string().unwrap_or_default();
            Reply::Http { status, body: minijson::parse(&text).unwrap_or(Json::Obj(Vec::new())), retry_after }
        }
        Err(e) => Reply::Network(format!("network: {e}")),
    }
}

fn backoff(ship: &ShipShared, attempt: u32, retry_after: Option<f64>) {
    let cap = ship.opts.retry_max.min(ship.opts.retry_base.saturating_mul(1u32 << attempt.min(20)));
    let r = u64::from_le_bytes(folder::random_bytes::<8>()) as f64 / u64::MAX as f64;
    let mut delay = cap.mul_f64(r); // full jitter
    if let Some(ra) = retry_after.filter(|s| s.is_finite() && *s > 0.0) {
        delay = delay.max(Duration::from_secs_f64(ra.min(3600.0)));
    }
    let mut s = ship.sig();
    let deadline = Instant::now() + delay;
    while !s.stop {
        let now = Instant::now();
        if now >= deadline {
            break;
        }
        s = ship.signal_cv.wait_timeout(s, deadline - now).unwrap_or_else(|e| e.into_inner()).0;
    }
}

pub(crate) fn run(ship: Arc<ShipShared>, shared: Arc<Shared>) {
    let opts = ship.opts.clone();
    let url = format!("{}/v1/ingest", opts.endpoint.trim_end_matches('/'));
    let agent: ureq::Agent = ureq::Agent::config_builder().http_status_as_error(false).timeout_global(Some(opts.timeout)).build().into();
    let mut reader = Reader::new(&ship.dir, &ship.producer);
    let mut attempt = 0u32;
    let mut max_records = opts.batch_records;
    let mut last_size_check = Instant::now() - Duration::from_secs(10);
    if opts.delete_acknowledged {
        delete_confirmed(&ship.dir, ship.committed.load(Ordering::Acquire));
    }
    loop {
        if ship.sig().stop {
            return;
        }
        let committed = ship.committed.load(Ordering::Acquire);
        if last_size_check.elapsed() >= Duration::from_millis(500) {
            let full = unconfirmed_bytes(&ship.dir, committed) >= opts.max_spool_bytes;
            shared.spool_full.store(full, Ordering::Relaxed);
            last_size_check = Instant::now();
        }
        let batch = match reader.next_batch(committed, max_records, opts.batch_bytes) {
            Ok(Some(b)) => b,
            Ok(None) => {
                {
                    let mut s = ship.sig();
                    s.urgent = false;
                }
                if !ship.wait(opts.poll_interval, false) {
                    return;
                }
                continue;
            }
            Err(e) => {
                ship.report(Some(format!("reading {}: {e}", ship.dir.display())), false, false);
                reader.reset();
                if !ship.wait(opts.poll_interval, false) {
                    return;
                }
                continue;
            }
        };
        let full_batch = batch.count as usize >= max_records;
        let body = match envelope(&ship.producer, &batch) {
            Ok(b) => b,
            Err(e) => {
                ship.report(Some(format!("compression: {e}")), true, false);
                attempt += 1;
                backoff(&ship, attempt, None);
                continue;
            }
        };
        match post(&agent, &opts, &url, &body) {
            Reply::Http { status: 200, body, .. } => {
                attempt = 0;
                max_records = opts.batch_records;
                let ack = body.get("ack").and_then(Json::as_str).unwrap_or("");
                if ack == "FsyncConfirmed" || opts.ack == Ack::Os {
                    let c = body.get("committed_through").and_then(Json::as_u64).unwrap_or(committed);
                    if c > committed {
                        ship.acknowledge(c);
                        if opts.delete_acknowledged {
                            delete_confirmed(&ship.dir, c);
                        }
                    }
                    {
                        let mut st = ship.st();
                        st.batches += 1;
                        st.error = None;
                    }
                    if !full_batch && !ship.sig().urgent && !ship.wait(opts.linger, true) {
                        return;
                    }
                } else {
                    ship.report(Some(format!("server acknowledged only {ack}")), true, false);
                    attempt += 1;
                    backoff(&ship, attempt, None);
                }
            }
            Reply::Http { status: 409, body, .. } if body.get("error").and_then(Json::as_str) == Some("sequence_gap") => {
                // The server's truth: resend from its cursor (or declare the gap when those records are gone).
                let c = body.get("committed_through").and_then(Json::as_u64).unwrap_or(0);
                ship.acknowledge(c);
                reader.reset();
            }
            Reply::Http { status: 413, .. } if batch.count > 1 => {
                max_records = (batch.count as usize / 2).max(1);
            }
            Reply::Http { status, body, retry_after } if status == 429 || status >= 500 => {
                let e = body.get("error").and_then(Json::as_str).map_or_else(|| format!("HTTP {status}"), str::to_owned);
                ship.report(Some(e), true, false);
                attempt += 1;
                backoff(&ship, attempt, retry_after);
            }
            Reply::Network(e) => {
                ship.report(Some(e), true, false);
                attempt += 1;
                backoff(&ship, attempt, None);
            }
            Reply::Http { status, body, .. } => {
                let err = body.get("error").and_then(Json::as_str).unwrap_or("");
                let msg = body.get("message").and_then(Json::as_str).unwrap_or("");
                ship.report(Some(format!("HTTP {status} {err}: {msg}")), false, true);
                return;
            }
        }
    }
}
