#![cfg(feature = "server")]

mod common;

use common::*;
use latticelog::{Durability, Options, ServerOptions};
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
use std::sync::{Arc, Mutex};
use std::time::Duration;

/// One decoded request.
#[derive(Debug, Clone)]
struct Req {
    headers: Vec<(String, String)>,
    first: u64,
    count: u32,
    gap: bool,
    producer: [u8; 16],
    body: String,
    /// The HTTP status the server answered.
    status: u16,
}

impl Req {
    fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }
    fn seqs(&self) -> Vec<u64> {
        self.body
            .lines()
            .filter_map(|l| serde_json::from_str::<serde_json::Value>(l).ok())
            .filter(|j| j["type"] == "record")
            .map(|j| j["seq"].as_str().unwrap().parse().unwrap())
            .collect()
    }
}

type Script = dyn Fn(&Req, u64) -> (u16, String) + Send + Sync;

/// A minimal ingest server: decodes and validates every envelope, keeps a committed cursor, and answers what the
/// script says (given the request and the cursor). Returns the address and the requests it saw.
fn server(script: Arc<Script>) -> (String, Arc<Mutex<Vec<Req>>>) {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = format!("http://{}", listener.local_addr().unwrap());
    let seen = Arc::new(Mutex::new(Vec::new()));
    let seen2 = seen.clone();
    std::thread::spawn(move || {
        let mut committed = 0u64;
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { continue };
            let mut reader = BufReader::new(stream.try_clone().unwrap());
            let mut headers = Vec::new();
            let mut line = String::new();
            reader.read_line(&mut line).unwrap();
            loop {
                line.clear();
                if reader.read_line(&mut line).unwrap() == 0 || line == "\r\n" {
                    break;
                }
                let (k, v) = line.trim_end().split_once(':').unwrap();
                headers.push((k.trim().to_owned(), v.trim().to_owned()));
            }
            let len: usize = headers.iter().find(|(k, _)| k.eq_ignore_ascii_case("content-length")).unwrap().1.parse().unwrap();
            let mut env = vec![0u8; len];
            reader.read_exact(&mut env).unwrap();
            assert_eq!(&env[0..4], b"LLIB");
            assert_eq!(env[4..7], [1, 1, 1]);
            let first = u64::from_le_bytes(env[24..32].try_into().unwrap());
            let last = u64::from_le_bytes(env[32..40].try_into().unwrap());
            let count = u32::from_le_bytes(env[40..44].try_into().unwrap());
            let crc = u32::from_le_bytes(env[44..48].try_into().unwrap());
            assert_eq!(crc32fast::hash(&env[48..]), crc, "CRC of the compressed body");
            assert_eq!(last - first + 1, u64::from(count), "dense batch");
            let mut body = String::new();
            flate2::read::GzDecoder::new(&env[48..]).read_to_string(&mut body).unwrap();
            let mut req = Req { headers, first, count, gap: env[7] & 1 == 1, producer: env[8..24].try_into().unwrap(), body, status: 0 };
            assert_eq!(req.seqs(), (first..=last).collect::<Vec<_>>(), "the body holds exactly the announced records");
            let (status, mut reply) = script(&req, committed);
            req.status = status;
            if status == 200 && reply.is_empty() {
                if last > committed && (first <= committed + 1 || req.gap) {
                    committed = last;
                }
                reply = format!(r#"{{"status":"stored","committed_through":"{committed}","ack":"FsyncConfirmed"}}"#);
            }
            seen2.lock().unwrap().push(req);
            let _ = write!(
                stream,
                "HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{reply}",
                reply.len()
            );
        }
    });
    (addr, seen)
}

fn ok() -> Arc<Script> {
    Arc::new(|_, _| (200, String::new()))
}

fn opts(addr: &str) -> Options {
    Options::new("orders-api").server(ServerOptions::new(addr, "secret").namespace("acme/prod").linger(Duration::from_millis(5)))
}

#[test]
fn ships_every_record_once_with_its_contexts_and_deletes_confirmed_files() {
    let dir = temp_dir("ship");
    // Offline first: a local-only process writes 300 records.
    let offline = latticelog::open(&dir, Options::new("orders-api").durability(Durability::Durable)).unwrap();
    for i in 0..300 {
        latticelog::info!(offline, "order {i}", i = i);
    }
    offline.close();

    let (addr, seen) = server(ok());
    let log = latticelog::open(&dir, opts(&addr)).unwrap();
    {
        let _op = log.operation("checkout", Some("op-300")).enter();
        for i in 300..500 {
            latticelog::info!(log, "order {i}", i = i);
        }
    }
    assert!(log.flush_shipped(Duration::from_secs(20)), "{:?}", log.stats());
    let stats = log.stats();
    assert_eq!(stats.committed_through, 500);
    log.close();

    let reqs = seen.lock().unwrap().clone();
    let shipped: Vec<u64> = reqs.iter().flat_map(Req::seqs).collect();
    assert_eq!(shipped, (1..=500).collect::<Vec<_>>(), "each record once, in order");
    let r = &reqs[0];
    assert_eq!(r.header("Authorization"), Some("Bearer secret"));
    assert_eq!(r.header("LatticeLog-Namespace"), Some("acme/prod"));
    assert_eq!(r.header("LatticeLog-Ack"), Some("fsync"));
    assert_eq!(r.header("Content-Type"), Some("application/vnd.latticelog.batch"));
    assert!(!r.gap);
    assert_eq!(latticelog_hex(&r.producer), log.producer_id());
    // Every batch is a self-contained document: header first, contexts before use.
    for r in &reqs {
        let lines: Vec<serde_json::Value> = r.body.lines().map(|l| serde_json::from_str(l).unwrap()).collect();
        assert_eq!(lines[0]["type"], "header");
        assert_eq!(lines[0]["store_id"], log.producer_id());
        let mut defined = std::collections::HashSet::new();
        for l in &lines[1..] {
            if l["type"] == "context" {
                defined.insert(l["ref"].as_u64().unwrap());
            } else if let Some(c) = l.get("context") {
                assert!(defined.contains(&c.as_u64().unwrap()), "context used before definition in a batch");
            }
        }
    }
    // The first file (1..300) is confirmed and closed: deleted. The newest file stays.
    let left: Vec<String> = files(&dir.join("p-1")).iter().map(|p| p.file_name().unwrap().to_string_lossy().into_owned()).collect();
    assert_eq!(left, ["latticelog-00000000000000000301.jsonl"]);
    let cursor = std::fs::read_to_string(dir.join("p-1").join("cursor.json")).unwrap();
    assert_eq!(cursor, r#"{"committed_through":500}"#);
}

fn latticelog_hex(b: &[u8; 16]) -> String {
    b.iter().map(|x| format!("{x:02x}")).collect()
}

#[test]
fn retries_after_503_and_splits_after_413() {
    let dir = temp_dir("retry");
    // The 100 records are on disk before shipping starts, so the first batch holds all of them: written while the
    // shipper runs, a first batch could be small enough to never meet the 413.
    let offline = latticelog::open(&dir, Options::new("svc").durability(Durability::Durable)).unwrap();
    for i in 0..100 {
        latticelog::info!(offline, "x", i = i);
    }
    offline.close();
    let calls = Arc::new(Mutex::new(0u32));
    let c2 = calls.clone();
    let (addr, seen) = server(Arc::new(move |req: &Req, _| {
        let mut n = c2.lock().unwrap();
        *n += 1;
        match *n {
            1 => (503, r#"{"error":"overloaded"}"#.into()),
            _ if req.count > 40 => (413, r#"{"error":"too_large"}"#.into()),
            _ => (200, String::new()),
        }
    }));
    let log = latticelog::open(
        &dir,
        Options::new("svc").server(
            ServerOptions::new(&addr, "t").retry(Duration::from_millis(1), Duration::from_millis(5)).linger(Duration::from_millis(200)),
        ),
    )
    .unwrap();
    assert!(log.flush_shipped(Duration::from_secs(20)), "{:?}", log.stats());
    let s = log.stats();
    assert!(s.ship_retries >= 1);
    log.close();
    let reqs = seen.lock().unwrap().clone();
    assert_eq!((reqs[0].status, reqs[0].count), (503, 100), "the first batch is refused whole");
    assert!(reqs.iter().any(|r| r.status == 413), "a batch over 40 records is refused as too large");
    // Only what the server answered 200 to is stored; a refused batch is sent again.
    let stored: Vec<u64> = reqs.iter().filter(|r| r.status == 200).flat_map(Req::seqs).collect();
    assert_eq!(stored, (1..=100).collect::<Vec<_>>());
}

#[test]
fn a_sequence_gap_answer_resends_from_the_server_cursor() {
    let dir = temp_dir("gap");
    // The server pretends it lost everything after 3 once: answers 409 with committed_through 3.
    let once = Arc::new(Mutex::new(false));
    let o2 = once.clone();
    let (addr, seen) = server(Arc::new(move |req: &Req, committed| {
        let mut done = o2.lock().unwrap();
        if committed >= 5 && !*done && req.first > 3 {
            *done = true;
            return (409, r#"{"error":"sequence_gap","committed_through":"3"}"#.into());
        }
        (200, String::new())
    }));
    let log = latticelog::open(&dir, opts(&addr)).unwrap();
    for i in 0..5 {
        latticelog::info!(log, "a", i = i);
    }
    assert!(log.flush_shipped(Duration::from_secs(10)));
    for i in 5..10 {
        latticelog::info!(log, "b", i = i);
    }
    assert!(log.flush_shipped(Duration::from_secs(10)), "{:?}", log.stats());
    log.close();
    let reqs = seen.lock().unwrap().clone();
    let firsts: Vec<u64> = reqs.iter().map(|r| r.first).collect();
    assert!(firsts.contains(&4), "resent from 4 after the 409: {firsts:?}");
}

#[test]
fn authentication_failure_stops_shipping_and_keeps_the_records() {
    let dir = temp_dir("auth");
    let (addr, _) = server(Arc::new(|_, _| (401, r#"{"error":"unauthorized","message":"bad token"}"#.into())));
    let log = latticelog::open(&dir, opts(&addr).close_timeout(Duration::from_millis(100))).unwrap();
    latticelog::info!(log, "kept locally");
    assert!(!log.flush_shipped(Duration::from_secs(10)));
    let s = log.stats();
    assert!(s.ship_failed);
    assert!(s.ship_error.unwrap().contains("unauthorized"));
    log.close();
    assert_eq!(seqs(&records(&dir.join("p-1"))), [1]);
}

#[test]
fn without_a_server_records_wait_in_the_folder() {
    let dir = temp_dir("offline");
    // Nothing listens on this port.
    let port = TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port();
    let log = latticelog::open(
        &dir,
        Options::new("svc")
            .close_timeout(Duration::from_millis(100))
            .server(ServerOptions::new(format!("http://127.0.0.1:{port}"), "t").retry(Duration::from_millis(5), Duration::from_millis(20))),
    )
    .unwrap();
    for _ in 0..10 {
        latticelog::info!(log, "queued");
    }
    assert!(!log.flush_shipped(Duration::from_millis(300)));
    // Windows takes about 2 s to refuse a connection to a closed port.
    let deadline = std::time::Instant::now() + Duration::from_secs(15);
    while log.stats().ship_error.is_none() && std::time::Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(50));
    }
    let s = log.stats();
    assert_eq!(s.committed_through, 0);
    assert!(s.ship_error.unwrap().starts_with("network"));
    log.close();
    assert_eq!(seqs(&records(&dir.join("p-1"))).len(), 10);
}
