//! The source folder layout shared with the Python SDK (specs/local-sources.md §1):
//!
//! ```text
//! <folder>/latticelog.source.json         manifest: service, environment, format version, producer id
//! <folder>/p-N/LOCK                        OS lock held by the process writing p-N
//! <folder>/p-N/process.json                {"producer_id": "<32 hex>"}
//! <folder>/p-N/cursor.json                 {"committed_through": N} (server mode: what the server confirmed)
//! <folder>/p-N/latticelog-<seq>.jsonl      self-contained latticelog.jsonl documents named after their first seq
//! ```

use crate::json;
use crate::minijson::{self, Json};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub const MANIFEST: &str = "latticelog.source.json";
const PREFIX: &str = "latticelog-";
const SUFFIX: &str = ".jsonl";

fn invalid(msg: String) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, msg)
}

/// Random bytes without a dependency: SipHash keyed by `RandomState` (whose keys come from the OS CSPRNG) over a
/// process counter, the time and the process id. Used for producer and operation ids (uniqueness, not secrecy).
pub fn random_bytes<const N: usize>() -> [u8; N] {
    use std::collections::hash_map::RandomState;
    use std::hash::{BuildHasher, Hasher};
    use std::sync::atomic::{AtomicU64, Ordering};
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let mut out = [0u8; N];
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_nanos());
    for chunk in out.chunks_mut(8) {
        let mut h = RandomState::new().build_hasher();
        h.write_u64(COUNTER.fetch_add(1, Ordering::Relaxed));
        h.write_u128(nanos);
        h.write_u32(std::process::id());
        chunk.copy_from_slice(&h.finish().to_le_bytes()[..chunk.len()]);
    }
    out
}

pub fn random_id() -> [u8; 16] {
    loop {
        let b = random_bytes::<16>();
        if b != [0; 16] {
            return b; // the ingest protocol forbids an all-zero producer id
        }
    }
}

pub fn file_name(first_seq: u64) -> String {
    format!("{PREFIX}{first_seq:020}{SUFFIX}")
}

/// Writes `bytes` to `path` through a fsynced temporary file and a rename.
pub fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let tmp = path.with_extension("json.tmp");
    {
        let mut f = File::create(&tmp)?;
        io::Write::write_all(&mut f, bytes)?;
        f.sync_all()?;
    }
    fs::rename(&tmp, path)?;
    sync_dir(path.parent().unwrap_or(Path::new(".")));
    Ok(())
}

/// Makes a new directory entry durable (Unix; NTFS journals metadata itself).
pub fn sync_dir(_dir: &Path) {
    #[cfg(unix)]
    if let Ok(d) = File::open(_dir) {
        let _ = d.sync_all();
    }
}

/// Creates the manifest once (`create_new`: two processes starting together agree on one) or checks it.
pub fn ensure_manifest(folder: &Path, service: &str, environment: Option<&str>) -> io::Result<()> {
    let path = folder.join(MANIFEST);
    for _ in 0..50 {
        match fs::read_to_string(&path) {
            Ok(text) if text.trim().is_empty() => {
                // Another process is between create_new and its write: wait for it.
                std::thread::sleep(Duration::from_millis(10));
                continue;
            }
            Ok(text) => {
                let mut m = minijson::parse(&text).map_err(|e| invalid(format!("{}: {e}", path.display())))?;
                if m.get("format").and_then(Json::as_str) != Some("latticelog.source") || m.get("version").and_then(Json::as_u64) != Some(1)
                {
                    return Err(invalid(format!("{}: not a latticelog.source v1 manifest", path.display())));
                }
                let owner = m.get("service").and_then(Json::as_str).unwrap_or("");
                if owner != service {
                    return Err(invalid(format!(
                        "{} belongs to service {owner:?}, not {service:?}; use one folder per service",
                        folder.display()
                    )));
                }
                if let Some(env) = environment
                    && m.get("environment").and_then(Json::as_str) != Some(env)
                {
                    m.set("environment", Json::Str(env.to_owned()));
                    let mut out = Vec::new();
                    m.write(&mut out);
                    atomic_write(&path, &out)?;
                }
                return Ok(());
            }
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                let mut m = Vec::new();
                m.extend_from_slice(b"{\"format\":\"latticelog.source\",\"version\":1,\"service\":");
                json::write_str(&mut m, service);
                m.extend_from_slice(b",\"environment\":");
                match environment {
                    Some(e) => json::write_str(&mut m, e),
                    None => m.extend_from_slice(b"null"),
                }
                m.extend_from_slice(b",\"data_format\":\"latticelog.jsonl\",\"data_version\":1,\"producer_id\":\"");
                json::write_hex(&mut m, &random_id());
                m.extend_from_slice(b"\",\"layout\":\"process-folders\",\"created\":\"");
                let now = SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs() as i64);
                m.extend_from_slice(json::rfc3339_seconds(now).as_bytes());
                m.extend_from_slice(b"\",\"producer\":\"latticelog-rust\"}");
                match OpenOptions::new().write(true).create_new(true).open(&path) {
                    Ok(mut f) => {
                        io::Write::write_all(&mut f, &m)?;
                        f.sync_all()?;
                        sync_dir(folder);
                        return Ok(());
                    }
                    Err(e) if e.kind() == io::ErrorKind::AlreadyExists => continue,
                    Err(e) => return Err(e),
                }
            }
            Err(e) => return Err(e),
        }
    }
    Err(invalid(format!("{}: manifest is still empty", path.display())))
}

/// `p-N` subfolders, sorted by N.
pub fn process_folders(folder: &Path) -> io::Result<Vec<(u64, PathBuf)>> {
    let mut out = Vec::new();
    for e in fs::read_dir(folder)? {
        let e = e?;
        let name = e.file_name();
        let Some(name) = name.to_str() else { continue };
        if let Some(n) = name.strip_prefix("p-").and_then(|n| n.parse::<u64>().ok())
            && e.file_type()?.is_dir()
        {
            out.push((n, e.path()));
        }
    }
    out.sort();
    Ok(out)
}

/// Data files of a process folder, sorted by first sequence.
pub fn data_files(dir: &Path) -> io::Result<Vec<(u64, PathBuf)>> {
    let mut out = Vec::new();
    for e in fs::read_dir(dir)? {
        let e = e?;
        let name = e.file_name();
        let Some(name) = name.to_str() else { continue };
        if let Some(seq) = name.strip_prefix(PREFIX).and_then(|n| n.strip_suffix(SUFFIX)).and_then(|n| n.parse::<u64>().ok()) {
            out.push((seq, e.path()));
        }
    }
    out.sort();
    Ok(out)
}

/// A process folder held by this process.
pub struct ProcessFolder {
    pub dir: PathBuf,
    pub producer_id: [u8; 16],
    pub next_seq: u64,
    _lock: File,
}

impl ProcessFolder {
    /// Takes the first `p-N` whose `LOCK` is free (continuing its sequence), else creates `p-<max+1>`. The OS
    /// releases the lock when the process dies.
    pub fn acquire(folder: &Path) -> io::Result<ProcessFolder> {
        for _ in 0..1000 {
            let existing = process_folders(folder)?;
            for (_, dir) in &existing {
                let lock = OpenOptions::new().read(true).write(true).create(true).truncate(false).open(dir.join("LOCK"))?;
                match lock.try_lock() {
                    Ok(()) => return Self::open(dir.clone(), lock),
                    Err(fs::TryLockError::WouldBlock) => continue,
                    Err(fs::TryLockError::Error(e)) => return Err(e),
                }
            }
            let n = existing.last().map_or(1, |(n, _)| n + 1);
            match fs::create_dir(folder.join(format!("p-{n}"))) {
                Ok(()) => sync_dir(folder),
                Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {}
                Err(e) => return Err(e),
            }
        }
        Err(io::Error::other(format!("could not acquire a process folder in {}", folder.display())))
    }

    fn open(dir: PathBuf, lock: File) -> io::Result<ProcessFolder> {
        let meta = dir.join("process.json");
        let producer_id = match fs::read_to_string(&meta) {
            Ok(text) => {
                let j = minijson::parse(&text).map_err(|e| invalid(format!("{}: {e}", meta.display())))?;
                let hex =
                    j.get("producer_id").and_then(Json::as_str).ok_or_else(|| invalid(format!("{}: no producer_id", meta.display())))?;
                parse_hex16(hex).ok_or_else(|| invalid(format!("{}: bad producer_id", meta.display())))?
            }
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                let id = random_id();
                let mut m = b"{\"producer_id\":\"".to_vec();
                json::write_hex(&mut m, &id);
                m.extend_from_slice(b"\"}");
                atomic_write(&meta, &m)?;
                id
            }
            Err(e) => return Err(e),
        };
        let next_seq = recover_next_seq(&dir)?;
        Ok(ProcessFolder { dir, producer_id, next_seq, _lock: lock })
    }
}

pub fn parse_hex16(hex: &str) -> Option<[u8; 16]> {
    if hex.len() != 32 {
        return None;
    }
    let mut out = [0u8; 16];
    for (i, b) in out.iter_mut().enumerate() {
        *b = u8::from_str_radix(hex.get(i * 2..i * 2 + 2)?, 16).ok()?;
    }
    Some(out)
}

/// `seq` of a record line written by any LatticeLog SDK (`{"type":"record","seq":"N",…`).
pub fn record_seq(line: &[u8]) -> Option<u64> {
    let rest = line.strip_prefix(b"{\"type\":\"record\",\"seq\":\"")?;
    let end = rest.iter().position(|&c| c == b'"')?;
    std::str::from_utf8(&rest[..end]).ok()?.parse().ok()
}

/// Continues the dense sequence after the last complete record; a torn last line (crash mid-write) is cut off.
pub fn recover_next_seq(dir: &Path) -> io::Result<u64> {
    let files = data_files(dir)?;
    let Some((first, path)) = files.last() else { return Ok(committed_through(dir)? + 1) };
    let mut f = OpenOptions::new().read(true).write(true).open(path)?;
    let mut data = Vec::new();
    f.read_to_end(&mut data)?;
    let good = data.iter().rposition(|&c| c == b'\n').map_or(0, |p| p + 1);
    if good != data.len() {
        f.set_len(good as u64)?;
        f.sync_all()?;
    }
    let last = data[..good].split(|&c| c == b'\n').rev().find_map(record_seq);
    Ok(match last {
        Some(seq) => seq + 1,
        None => *first, // a file is named after the first sequence it was opened for
    }
    .max(committed_through(dir)? + 1))
}

/// Server mode: what the server confirmed (0 when nothing was shipped yet).
pub fn committed_through(dir: &Path) -> io::Result<u64> {
    match fs::read_to_string(dir.join("cursor.json")) {
        Ok(text) => Ok(minijson::parse(&text).ok().and_then(|j| j.get("committed_through").and_then(Json::as_u64)).unwrap_or(0)),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(0),
        Err(e) => Err(e),
    }
}

fn shipped_cursor(dir: &Path) -> Option<u64> {
    let text = fs::read_to_string(dir.join("cursor.json")).ok()?;
    minijson::parse(&text).ok()?.get("committed_through")?.as_u64()
}

/// Deletes whole closed files, oldest first, by age and/or total size of the source folder. The newest file of
/// each process folder may still be written and is never deleted; in a shipped process folder (with a
/// `cursor.json`) a file whose records the server has not all confirmed is never deleted either.
pub fn apply_retention(folder: &Path, max_age: Option<Duration>, max_bytes: Option<u64>) {
    if max_age.is_none() && max_bytes.is_none() {
        return;
    }
    let Ok(dirs) = process_folders(folder) else { return };
    let mut closed = Vec::new();
    let mut total = 0u64;
    for (_, dir) in &dirs {
        let Ok(files) = data_files(dir) else { continue };
        let cursor = shipped_cursor(dir);
        for (i, (_, path)) in files.iter().enumerate() {
            let Ok(md) = fs::metadata(path) else { continue };
            total += md.len();
            if i + 1 < files.len() {
                let last_seq = files[i + 1].0 - 1;
                if cursor.is_some_and(|c| last_seq > c) {
                    continue;
                }
                closed.push((md.modified().unwrap_or(UNIX_EPOCH), path.clone(), md.len()));
            }
        }
    }
    closed.sort();
    let now = SystemTime::now();
    for (mtime, path, size) in closed {
        let too_old = max_age.is_some_and(|a| now.duration_since(mtime).unwrap_or_default() > a);
        let too_big = max_bytes.is_some_and(|m| total > m);
        if (too_old || too_big) && fs::remove_file(&path).is_ok() {
            total -= size;
        }
    }
}

/// Reads up to `max` bytes at `offset` (fewer at end of file).
#[cfg(feature = "server")]
pub fn read_at(file: &mut File, offset: u64, buf: &mut Vec<u8>, max: usize) -> io::Result<usize> {
    use std::io::{Seek, SeekFrom};
    file.seek(SeekFrom::Start(offset))?;
    let start = buf.len();
    let n = file.take(max as u64).read_to_end(buf)?;
    debug_assert_eq!(buf.len(), start + n);
    Ok(n)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn record_seq_reads_python_and_rust_lines() {
        assert_eq!(record_seq(br#"{"type":"record","seq":"42","time":"x"}"#), Some(42));
        assert_eq!(record_seq(br#"{"type":"context","ref":1}"#), None);
    }
}
