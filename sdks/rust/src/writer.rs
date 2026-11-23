//! The writer thread: turns batches of encoded frames into `latticelog.jsonl` lines with dense sequences.

use crate::context::Context;
use crate::folder::{self, ProcessFolder};
use crate::json;
use crate::logger::{CTX_ROOT, Durability, FRAME_HEADER, FlushLevel, FlushResult, Shared};
use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::hash::{BuildHasherDefault, Hasher};
use std::io::Write;
use std::path::PathBuf;
use std::sync::atomic::Ordering;
use std::sync::mpsc;
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

/// Hasher for process-unique context ids (already well distributed after one multiply).
#[derive(Default)]
struct IdHasher(u64);

impl Hasher for IdHasher {
    fn finish(&self) -> u64 {
        self.0
    }
    fn write(&mut self, bytes: &[u8]) {
        for &b in bytes {
            self.0 = (self.0 ^ u64::from(b)).wrapping_mul(0x0100_0000_01b3);
        }
    }
    fn write_u64(&mut self, v: u64) {
        self.0 = v.wrapping_mul(0x9E37_79B9_7F4A_7C15);
    }
}

type RefMap = HashMap<u64, u32, BuildHasherDefault<IdHasher>>;

const OUT_CHUNK: usize = 1 << 20;
const BALANCED_FSYNC: Duration = Duration::from_secs(1);
/// Empty ticks before the writer sleeps until the next record wakes it.
const IDLE_TICKS: u32 = 100;

/// The durability thread: fsyncs rotated files and runs the periodic `Balanced` fsync, so a slow fsync (hundreds
/// of milliseconds on a busy disk) never stops the writer. `wait_idle` gives `flush(fsync)` and `Durable` mode the
/// guarantee that every earlier file is on disk too.
struct Syncer {
    tx: Option<mpsc::Sender<File>>,
    state: Arc<(Mutex<SyncState>, Condvar)>,
    thread: Option<std::thread::JoinHandle<()>>,
}

#[derive(Default)]
struct SyncState {
    submitted: u64,
    done: u64,
    error: Option<String>,
}

impl Syncer {
    fn start() -> Syncer {
        let (tx, rx) = mpsc::channel::<File>();
        let state: Arc<(Mutex<SyncState>, Condvar)> = Arc::default();
        let st = state.clone();
        let thread = std::thread::Builder::new()
            .name("latticelog-durability".into())
            .spawn(move || {
                for f in rx {
                    let r = f.sync_data();
                    drop(f);
                    let mut s = st.0.lock().unwrap_or_else(|e| e.into_inner());
                    if let Err(e) = r {
                        s.error = Some(format!("fsync: {e}"));
                    }
                    s.done += 1;
                    st.1.notify_all();
                }
            })
            .ok();
        Syncer { tx: thread.as_ref().map(|_| tx), state, thread }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, SyncState> {
        self.state.0.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Hands a file (or a duplicate handle of the current one) to the durability thread; fsyncs inline when that
    /// thread could not be started.
    fn submit(&self, f: File) -> Result<(), String> {
        match &self.tx {
            Some(tx) => {
                self.lock().submitted += 1;
                tx.send(f).map_err(|_| "durability thread stopped".to_owned())
            }
            None => f.sync_data().map_err(|e| format!("fsync: {e}")),
        }
    }

    fn busy(&self) -> bool {
        let s = self.lock();
        s.done < s.submitted
    }

    /// Waits until every submitted fsync returned; reports (and clears) the first error since the last call.
    fn wait_idle(&self) -> Option<String> {
        let mut s = self.lock();
        while s.done < s.submitted {
            s = self.state.1.wait(s).unwrap_or_else(|e| e.into_inner());
        }
        s.error.take()
    }

    fn stop(&mut self) {
        self.tx = None;
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

struct Writer {
    shared: Arc<Shared>,
    dir: PathBuf,
    next_seq: u64,
    file: Option<File>,
    file_bytes: u64,
    refs: RefMap,
    next_ref: u32,
    chain: Vec<Context>,
    out: Vec<u8>,
    out_records: u64,
    out_first_seq: u64,
    dirty: bool,
    last_fsync: Instant,
    error: Option<String>,
    syncer: Syncer,
}

pub(crate) fn run(shared: Arc<Shared>, pf: ProcessFolder) -> ProcessFolder {
    let mut w = Writer {
        dir: pf.dir.clone(),
        next_seq: pf.next_seq,
        file: None,
        file_bytes: 0,
        refs: RefMap::default(),
        next_ref: 1,
        chain: Vec::new(),
        out: Vec::with_capacity(256 * 1024),
        out_records: 0,
        out_first_seq: pf.next_seq,
        dirty: false,
        last_fsync: Instant::now(),
        error: None,
        syncer: Syncer::start(),
        shared,
    };
    let shared = w.shared.clone();
    let tick = match shared.durability {
        Durability::Fast => Some(Duration::from_millis(100)),
        Durability::Balanced => Some(Duration::from_millis(10)),
        Durability::Durable => None,
    };
    let mut batch: Vec<u8> = Vec::with_capacity(64 * 1024);
    let mut ctxs: Vec<Context> = Vec::new();
    loop {
        let (epoch, fsync_requested, closing) = {
            let mut st = shared.lock();
            let mut idle = 0u32;
            while !(st.closed || !st.buf.is_empty() || st.flush_requested > st.flush_done) {
                if w.periodic_fsync_due() {
                    break;
                }
                let deep = tick.is_none() || idle >= IDLE_TICKS;
                let mut timeout = if deep { None } else { tick };
                if shared.durability == Durability::Balanced && w.dirty {
                    let left = BALANCED_FSYNC.saturating_sub(w.last_fsync.elapsed());
                    timeout = Some(timeout.map_or(left, |t| t.min(left)));
                }
                st.writer_waiting = true;
                st.notify_on_push = deep;
                st = match timeout {
                    Some(t) => shared.writer_cv.wait_timeout(st, t).unwrap_or_else(|e| e.into_inner()).0,
                    None => shared.writer_cv.wait(st).unwrap_or_else(|e| e.into_inner()),
                };
                st.writer_waiting = false;
                st.notify_on_push = false;
                idle += 1;
            }
            std::mem::swap(&mut st.buf, &mut batch);
            std::mem::swap(&mut st.ctxs, &mut ctxs);
            st.last_ctx_id = 0;
            if st.blocked > 0 {
                shared.space_cv.notify_all();
            }
            (st.flush_requested, std::mem::take(&mut st.flush_fsync), st.closed)
        };

        w.process(&batch, &ctxs);
        w.write_out();
        let fsync = fsync_requested || closing || shared.durability == Durability::Durable;
        let synced = if fsync {
            // The current file inline (group commit), and every file handed to the durability thread before it.
            let current = w.sync();
            let earlier = w.syncer.wait_idle();
            if let Some(e) = &earlier {
                w.error = Some(e.clone());
            }
            current && earlier.is_none()
        } else {
            w.periodic_fsync();
            false
        };
        batch.clear();
        ctxs.clear();
        if batch.capacity() > 4 * shared.capacity.max(1 << 20) {
            batch.shrink_to(1 << 20);
        }

        #[cfg(feature = "server")]
        if let Some(ship) = &shared.ship {
            ship.notify_written();
        }

        if closing {
            w.file = None;
            w.syncer.stop();
        }
        let mut st = shared.lock();
        if epoch > st.flush_done || closing {
            let level = match (&w.error, synced) {
                (Some(_), _) if w.file.is_none() && !closing => FlushLevel::None,
                (_, true) => FlushLevel::FsyncConfirmed,
                _ => FlushLevel::WrittenToOs,
            };
            st.flush_result = FlushResult { level, last_seq: w.next_seq - 1, error: w.error.take() };
            st.flush_done = if closing { st.flush_requested } else { epoch };
            shared.flush_cv.notify_all();
        }
        if closing {
            return pf;
        }
    }
}

impl Writer {
    fn process(&mut self, batch: &[u8], ctxs: &[Context]) {
        let shared = self.shared.clone();
        let mut pos = 0;
        while pos + FRAME_HEADER <= batch.len() {
            let len = u32::from_ne_bytes(batch[pos..pos + 4].try_into().expect("4 bytes")) as usize;
            let idx = u32::from_ne_bytes(batch[pos + 4..pos + 8].try_into().expect("4 bytes"));
            let body = &batch[pos + FRAME_HEADER..pos + FRAME_HEADER + len];
            pos += FRAME_HEADER + len;

            if shared.spool_full.load(Ordering::Relaxed) {
                shared.counters.dropped_spool_full.fetch_add(1, Ordering::Relaxed);
                continue;
            }
            if self.file.is_none() || self.file_bytes + self.out.len() as u64 >= shared.file_max_bytes {
                self.rotate();
                if self.file.is_none() {
                    shared.counters.dropped_write_error.fetch_add(1, Ordering::Relaxed);
                    continue;
                }
            }
            let ctx = if idx == CTX_ROOT { &shared.root } else { &ctxs[idx as usize] };
            let r = self.ctx_ref(ctx);
            let out = &mut self.out;
            out.extend_from_slice(b"{\"type\":\"record\",\"seq\":\"");
            json::write_u64(out, self.next_seq);
            out.extend_from_slice(b"\",\"context\":");
            json::write_u64(out, u64::from(r));
            out.push(b',');
            out.extend_from_slice(body);
            out.extend_from_slice(b"}\n");
            self.next_seq += 1;
            self.out_records += 1;
            if self.out.len() >= OUT_CHUNK {
                self.write_out();
            }
        }
    }

    /// File-local ref of `ctx`, writing the context lines of it and of any ancestor not yet defined in this file.
    fn ctx_ref(&mut self, ctx: &Context) -> u32 {
        if let Some(&r) = self.refs.get(&ctx.0.id) {
            return r;
        }
        let mut chain = std::mem::take(&mut self.chain);
        let mut c = Some(ctx);
        while let Some(x) = c {
            if self.refs.contains_key(&x.0.id) {
                break;
            }
            chain.push(x.clone());
            c = x.parent();
        }
        let mut r = 0;
        for x in chain.drain(..).rev() {
            let parent = x.parent().and_then(|p| self.refs.get(&p.0.id).copied());
            r = self.next_ref;
            self.next_ref += 1;
            self.refs.insert(x.0.id, r);
            let out = &mut self.out;
            out.extend_from_slice(b"{\"type\":\"context\",\"ref\":");
            json::write_u64(out, u64::from(r));
            if let Some(p) = parent {
                out.extend_from_slice(b",\"parent\":");
                json::write_u64(out, u64::from(p));
            }
            out.push(b',');
            out.extend_from_slice(&x.0.def);
            out.extend_from_slice(b"}\n");
        }
        self.chain = chain;
        r
    }

    /// Closes the current file (fsynced: a closed file is always durable, retention may delete it later) and
    /// starts `latticelog-<next seq>.jsonl` with its header.
    fn rotate(&mut self) {
        self.write_out();
        if let Some(f) = self.file.take() {
            // A closed file is always made durable (retention may delete it later), off the writer thread.
            if let Err(e) = self.syncer.submit(f) {
                self.error = Some(e);
            }
            self.dirty = false;
            folder::apply_retention(&self.shared.folder, self.shared.max_age, self.shared.max_bytes);
        }
        let path = self.dir.join(folder::file_name(self.next_seq));
        match OpenOptions::new().create(true).append(true).open(&path) {
            Ok(f) => {
                self.file_bytes = f.metadata().map(|m| m.len()).unwrap_or(0);
                self.file = Some(f);
                folder::sync_dir(&self.dir);
            }
            Err(e) => {
                self.error = Some(format!("open {}: {e}", path.display()));
                return;
            }
        }
        self.refs.clear();
        self.next_ref = 1;
        self.out.extend_from_slice(b"{\"type\":\"header\",\"format\":\"latticelog.jsonl\",\"version\":1,\"store_id\":\"");
        json::write_hex(&mut self.out, &self.shared.producer_id);
        self.out.extend_from_slice(b"\"}\n");
        self.out_first_seq = self.next_seq;
    }

    /// Hands the pending lines to the OS with one write. On failure the file is cut back to its last complete
    /// batch, the records are counted as dropped and their sequences are reused, so the folder never has a gap
    /// or a duplicate; a new file is started for the next record.
    fn write_out(&mut self) {
        if self.out.is_empty() {
            return;
        }
        let counters = &self.shared.counters;
        match self.file.as_mut().map(|f| f.write_all(&self.out)) {
            Some(Ok(())) => {
                self.file_bytes += self.out.len() as u64;
                self.dirty = true;
                counters.written.fetch_add(self.out_records, Ordering::Relaxed);
                counters.last_seq.store(self.next_seq - 1, Ordering::Release);
            }
            result => {
                if let Some(Err(e)) = result {
                    self.error = Some(format!("write: {e}"));
                }
                let cut = self.file.as_ref().is_some_and(|f| f.set_len(self.file_bytes).is_ok());
                if cut {
                    self.next_seq = self.out_first_seq;
                }
                counters.dropped_write_error.fetch_add(self.out_records, Ordering::Relaxed);
                self.file = None;
            }
        }
        self.out.clear();
        self.out_records = 0;
        self.out_first_seq = self.next_seq;
    }

    fn sync(&mut self) -> bool {
        match &self.file {
            Some(f) if self.dirty => match f.sync_data() {
                Ok(()) => {
                    self.dirty = false;
                    self.last_fsync = Instant::now();
                    true
                }
                Err(e) => {
                    self.error = Some(format!("fsync: {e}"));
                    false
                }
            },
            _ => self.error.is_none(),
        }
    }

    fn periodic_fsync_due(&self) -> bool {
        self.shared.durability == Durability::Balanced && self.dirty && self.last_fsync.elapsed() >= BALANCED_FSYNC
    }

    /// `Balanced`: once a second, a duplicate handle of the current file goes to the durability thread (skipped
    /// while the previous one is still running).
    fn periodic_fsync(&mut self) {
        if !self.periodic_fsync_due() || self.syncer.busy() {
            return;
        }
        if let Some(dup) = self.file.as_ref().and_then(|f| f.try_clone().ok()) {
            if let Err(e) = self.syncer.submit(dup) {
                self.error = Some(e);
            }
            self.dirty = false;
            self.last_fsync = Instant::now();
        }
    }
}
