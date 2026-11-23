//! Producer-side cost and end-to-end throughput of the Rust SDK, with a common Rust baseline for comparison.
//!
//! ```text
//! cargo run --release --example bench -- <scratch-folder> [records-per-thread]
//! ```
//!
//! Every scenario writes the same logical record (message + 3 attributes: int, float, string) to a file on disk:
//! - `latticelog builder`: `log.info(..).attr(..)…emit()` (the SDK's own API);
//! - `latticelog tracing`: `tracing::info!` through `latticelog::layer` (spans and fields mapped);
//! - `fmt json + appender`: `tracing_subscriber::fmt().json()` writing through `tracing_appender::non_blocking`
//!   (lossless mode, so it blocks instead of dropping, like the SDK's default).
//!
//! "call" is the time spent in the calling thread per record; "end to end" includes waiting until everything is
//! handed to the OS (SDK: `flush`, baseline: dropping the worker guard). Disabled-level cost is measured separately.

use latticelog::{Durability, Level, Options};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};
use tracing_subscriber::prelude::*;

struct Row {
    name: String,
    threads: usize,
    records: u64,
    call: Duration,
    end_to_end: Duration,
    bytes: u64,
}

fn dir_bytes(p: &Path) -> u64 {
    let mut total = 0;
    if let Ok(rd) = std::fs::read_dir(p) {
        for e in rd.flatten() {
            let md = e.metadata().unwrap();
            total += if md.is_dir() { dir_bytes(&e.path()) } else { md.len() };
        }
    }
    total
}

fn fresh(base: &Path, name: &str) -> PathBuf {
    let d = base.join(name);
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}

/// Runs `body(thread, n)` on `threads` threads; returns the mean time spent in the calls per thread.
fn run_threads(threads: usize, n: u64, body: impl Fn(usize, u64) + Sync) -> Duration {
    let total: Duration = std::thread::scope(|s| {
        let hs: Vec<_> = (0..threads)
            .map(|t| {
                let body = &body;
                s.spawn(move || {
                    let start = Instant::now();
                    body(t, n);
                    start.elapsed()
                })
            })
            .collect();
        hs.into_iter().map(|h| h.join().unwrap()).sum()
    });
    total / threads as u32
}

fn sdk_builder(base: &Path, threads: usize, n: u64, durability: Durability) -> Row {
    let dir = fresh(base, "builder");
    let log = latticelog::open(&dir, Options::new("bench").durability(durability)).unwrap();
    let start = Instant::now();
    let call = run_threads(threads, n, |t, n| {
        for i in 0..n {
            log.info("order placed").attr("order_id", i as i64).attr("total", 19.99).attr("user", "alice").attr("t", t as i64).emit();
        }
    });
    let r = log.flush(false);
    let end_to_end = start.elapsed();
    assert_eq!(r.last_seq, threads as u64 * n, "{:?}", log.stats());
    log.close();
    let bytes = dir_bytes(&dir);
    let _ = std::fs::remove_dir_all(&dir);
    Row { name: format!("latticelog builder ({durability:?})"), threads, records: threads as u64 * n, call, end_to_end, bytes }
}

fn sdk_tracing(base: &Path, threads: usize, n: u64) -> Row {
    let dir = fresh(base, "tracing");
    let log = latticelog::open(&dir, Options::new("bench")).unwrap();
    let dispatch = tracing::Dispatch::new(tracing_subscriber::registry().with(latticelog::layer(&log)));
    let start = Instant::now();
    let call = run_threads(threads, n, |t, n| {
        tracing::dispatcher::with_default(&dispatch, || {
            let span = tracing::info_span!("worker", t = t as u64);
            let _e = span.enter();
            for i in 0..n {
                tracing::info!(order_id = i, total = 19.99, user = "alice", "order placed");
            }
        })
    });
    let r = log.flush(false);
    let end_to_end = start.elapsed();
    assert_eq!(r.last_seq, threads as u64 * n);
    log.close();
    let bytes = dir_bytes(&dir);
    let _ = std::fs::remove_dir_all(&dir);
    Row { name: "latticelog tracing layer".into(), threads, records: threads as u64 * n, call, end_to_end, bytes }
}

fn baseline(base: &Path, threads: usize, n: u64) -> Row {
    let dir = fresh(base, "baseline");
    let appender = tracing_appender::rolling::never(&dir, "app.log");
    let (writer, guard) = tracing_appender::non_blocking::NonBlockingBuilder::default().lossy(false).finish(appender);
    let dispatch = tracing::Dispatch::new(tracing_subscriber::registry().with(tracing_subscriber::fmt::layer().json().with_writer(writer)));
    let start = Instant::now();
    let call = run_threads(threads, n, |t, n| {
        tracing::dispatcher::with_default(&dispatch, || {
            let span = tracing::info_span!("worker", t = t as u64);
            let _e = span.enter();
            for i in 0..n {
                tracing::info!(order_id = i, total = 19.99, user = "alice", "order placed");
            }
        })
    });
    drop(dispatch);
    drop(guard); // waits for the worker to write everything
    let end_to_end = start.elapsed();
    let bytes = dir_bytes(&dir);
    let _ = std::fs::remove_dir_all(&dir);
    Row { name: "fmt json + tracing-appender".into(), threads, records: threads as u64 * n, call, end_to_end, bytes }
}

fn latency(base: &Path, samples: usize) -> Vec<u64> {
    let dir = fresh(base, "latency");
    let log = latticelog::open(&dir, Options::new("bench")).unwrap();
    let mut v = Vec::with_capacity(samples);
    for i in 0..samples {
        let t = Instant::now();
        log.info("order placed").attr("order_id", i as i64).attr("total", 19.99).attr("user", "alice").emit();
        v.push(t.elapsed().as_nanos() as u64);
        if i % 64 == 0 {
            std::hint::spin_loop(); // a steady, not saturating, rate
        }
    }
    log.close();
    let _ = std::fs::remove_dir_all(&dir);
    v.sort_unstable();
    v
}

fn disabled(base: &Path, n: u64) -> f64 {
    let dir = fresh(base, "disabled");
    let log = latticelog::open(&dir, Options::new("bench").min_level(Level::Info)).unwrap();
    let start = Instant::now();
    for i in 0..n {
        latticelog::debug!(log, "not written", order_id = std::hint::black_box(i));
    }
    let ns = start.elapsed().as_nanos() as f64 / n as f64;
    log.close();
    let _ = std::fs::remove_dir_all(&dir);
    ns
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let base = PathBuf::from(args.get(1).expect("usage: bench <scratch-folder> [records-per-thread]"));
    let n: u64 = args.get(2).map_or(500_000, |s| s.parse().unwrap());
    std::fs::create_dir_all(&base).unwrap();
    println!("records per thread: {n}; folder: {}", base.display());

    // Warm-up (page cache, thread-local buffers).
    let _ = sdk_builder(&base, 1, n / 10, Durability::Balanced);

    let mut rows = Vec::new();
    for threads in [1usize, 4, 8] {
        rows.push(sdk_builder(&base, threads, n, Durability::Balanced));
        rows.push(sdk_tracing(&base, threads, n));
        rows.push(baseline(&base, threads, n));
    }
    rows.push(sdk_builder(&base, 1, n, Durability::Fast));
    rows.push(sdk_builder(&base, 1, n, Durability::Durable));

    println!();
    println!("| scenario | threads | records | call ns/record (mean per thread) | end to end records/s | bytes/record |");
    println!("|---|---:|---:|---:|---:|---:|");
    for r in &rows {
        let per_thread = r.records / r.threads as u64;
        println!(
            "| {} | {} | {} | {:.0} | {:.0} | {:.0} |",
            r.name,
            r.threads,
            r.records,
            r.call.as_nanos() as f64 / per_thread as f64,
            r.records as f64 / r.end_to_end.as_secs_f64(),
            r.bytes as f64 / r.records as f64
        );
    }

    let lat = latency(&base, 200_000);
    let p = |q: f64| lat[((lat.len() as f64 - 1.0) * q) as usize];
    println!();
    println!(
        "single-thread call latency (200,000 calls, ns, includes ~Instant::now overhead): p50 {} | p99 {} | p99.9 {} | max {}",
        p(0.5),
        p(0.99),
        p(0.999),
        lat.last().unwrap()
    );
    println!("disabled level (debug! with Info minimum): {:.2} ns/call", disabled(&base, 10_000_000));
}
