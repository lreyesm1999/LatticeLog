mod common;

use common::*;
use latticelog::{Bytes, Decimal, Durability, Exception, FlushLevel, Level, Map, Options, Saturation, Timestamp, Utf16, Uuid, Value};
use serde_json::json;

fn opts() -> Options {
    Options::new("orders-api").environment("dev")
}

#[test]
fn writes_official_jsonl_with_exact_types_and_a_manifest() {
    let dir = temp_dir("types");
    let log = latticelog::open(&dir, opts().resource("service.version", "1.2.3")).unwrap();
    let mixed: [&dyn Value; 3] = [&1, &2.5, &"x"];
    log.error("special {i}")
        .event("payment_failed")
        .attr("i", 300)
        .attr("ratio", 1.0)
        .attr("big", 1u64 << 63)
        .attr("nan", f64::NAN)
        .attr("raw", Bytes(&[0, 0xFF]))
        .attr("when", Timestamp(1_767_323_045_123_456_000))
        .attr("id", Uuid::from_u128(255))
        .attr("price", Decimal("19.990"))
        .attr("nested", Map(&[("a", &mixed)]))
        .attr("odd", Utf16(&[0x61, 0xD800, 0x62]))
        .attr("unicode", "héllo 世界 🎉 \"quoted\"\n")
        .attr("none", Option::<i32>::None)
        .trace_hex("4bf92f3577b34da6a3ce929d0e0e4736", Some("00f067aa0ba902b7"))
        .correlation_id("order-991")
        .exception(&Exception::new("PaymentError", "payment failed").with_inner(Exception::new("KeyError", "sku")))
        .emit();
    latticelog::info!(log, "order {id}", id = 991, "http.status" = 200);
    assert_eq!(log.flush(true).level, FlushLevel::FsyncConfirmed);

    let manifest: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(dir.join("latticelog.source.json")).unwrap()).unwrap();
    assert_eq!(manifest["format"], "latticelog.source");
    assert_eq!(manifest["service"], "orders-api");
    assert_eq!(manifest["environment"], "dev");
    assert_eq!(manifest["data_format"], "latticelog.jsonl");
    assert_eq!(manifest["layout"], "process-folders");
    assert_eq!(log.process_dir(), dir.join("p-1"));
    let pj: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(dir.join("p-1").join("process.json")).unwrap()).unwrap();
    assert_eq!(pj["producer_id"], log.producer_id());

    let recs = records(log.process_dir());
    assert_eq!(seqs(&recs), [1, 2]);
    let (r, chain) = &recs[0];
    assert_eq!(r["severity_number"], 17);
    assert_eq!(r["body"], "special {i}");
    assert_eq!(r["event"], "payment_failed");
    assert_eq!(r["trace_id"], "4bf92f3577b34da6a3ce929d0e0e4736");
    assert_eq!(r["span_id"], "00f067aa0ba902b7");
    assert_eq!(r["correlation_id"], "order-991");
    assert_eq!(r["exception"], json!({"type":"PaymentError","message":"payment failed","inner":[{"type":"KeyError","message":"sku"}]}));
    assert_eq!(attr(r, "i"), Some(&json!(300)));
    // float64 keeps its '.' in the raw line (serde_json would hide it), checked on the text:
    let text = std::fs::read_to_string(&files(log.process_dir())[0]).unwrap();
    assert!(text.contains(r#"["ratio",1.0]"#), "{text}");
    assert_eq!(attr(r, "big"), Some(&json!({"$u64":"9223372036854775808"})));
    assert_eq!(attr(r, "nan"), Some(&json!({"$f64bits":"7FF8000000000000"})));
    assert_eq!(attr(r, "raw"), Some(&json!({"$bytes":"AP8="})));
    assert_eq!(attr(r, "when"), Some(&json!({"$ts":"2026-01-02T03:04:05.123456000Z"})));
    assert_eq!(attr(r, "id"), Some(&json!({"$uuid":"00000000-0000-0000-0000-0000000000ff"})));
    assert_eq!(attr(r, "price"), Some(&json!({"$dec":"19.990"})));
    assert_eq!(attr(r, "nested"), Some(&json!({"$map":[["a",[1,2.5,"x"]]]})));
    assert_eq!(attr(r, "odd"), Some(&json!({"$utf16le":"YQAA2GIA"})));
    assert_eq!(attr(r, "unicode"), Some(&json!("héllo 世界 🎉 \"quoted\"\n")));
    assert_eq!(attr(r, "none"), Some(&json!(null)));
    // The resource context: service, environment and the user's resource attributes.
    assert_eq!(chain.len(), 1);
    assert_eq!(chain[0]["kind"], "Resource");
    assert_eq!(
        chain[0]["attributes"],
        json!([["service.name", "orders-api"], ["deployment.environment.name", "dev"], ["service.version", "1.2.3"]])
    );
    let (r2, _) = &recs[1];
    assert_eq!(attr(r2, "id"), Some(&json!(991)));
    assert_eq!(attr(r2, "http.status"), Some(&json!(200)));
    assert!(chrono_like(r2["time"].as_str().unwrap()));
    log.close();
}

fn chrono_like(t: &str) -> bool {
    t.len() == 30 && t.ends_with('Z') && &t[10..11] == "T" && &t[19..20] == "."
}

#[test]
fn sequences_stay_dense_across_restarts_and_concurrent_writers_get_their_own_folder() {
    let dir = temp_dir("restart");
    {
        let a = latticelog::open(&dir, opts()).unwrap();
        for i in 0..10 {
            latticelog::info!(a, "a", i = i);
        }
        // A second logger while the first holds p-1: it gets p-2 and its own producer id.
        let b = latticelog::open(&dir, opts()).unwrap();
        assert_eq!(b.process_dir(), dir.join("p-2"));
        assert_ne!(a.producer_id(), b.producer_id());
        latticelog::info!(b, "b");
        a.close();
        b.close();
    }
    let again = latticelog::open(&dir, opts()).unwrap();
    assert_eq!(again.process_dir(), dir.join("p-1"));
    for i in 10..15 {
        latticelog::info!(again, "a", i = i);
    }
    again.close();
    let recs = records(&dir.join("p-1"));
    assert_eq!(seqs(&recs), (1..=15).collect::<Vec<_>>());
    assert_eq!(files(&dir.join("p-1")).len(), 2, "a new file at every start");
    assert!(files(&dir.join("p-1"))[1].ends_with("latticelog-00000000000000000011.jsonl"));
}

#[test]
fn a_torn_last_line_is_cut_off_and_the_sequence_continues() {
    let dir = temp_dir("torn");
    let log = latticelog::open(&dir, opts()).unwrap();
    for _ in 0..3 {
        latticelog::info!(log, "x");
    }
    log.close();
    let f = files(&dir.join("p-1"))[0].clone();
    let mut text = std::fs::read_to_string(&f).unwrap();
    text.push_str(r#"{"type":"record","seq":"4","time":"2026"#); // crash mid-write
    std::fs::write(&f, text).unwrap();
    let log = latticelog::open(&dir, opts()).unwrap();
    latticelog::info!(log, "y");
    log.close();
    assert_eq!(seqs(&records(&dir.join("p-1"))), [1, 2, 3, 4]);
}

#[test]
fn a_folder_belongs_to_one_service() {
    let dir = temp_dir("owner");
    latticelog::open(&dir, opts()).unwrap().close();
    let err = latticelog::open(&dir, Options::new("billing")).unwrap_err();
    assert!(err.to_string().contains("belongs to service"), "{err}");
}

#[test]
fn contexts_are_defined_once_per_file_and_again_after_rotation() {
    let dir = temp_dir("ctx");
    let log = latticelog::open(&dir, opts().file_max_bytes(4096)).unwrap();
    {
        let _op = log.operation("checkout", Some("op-1")).attr("cart", 7).enter();
        let _inner = log.operation("pay", None).enter();
        let _scope = log.scope("card").attr("brand", "visa").enter();
        for i in 0..100 {
            latticelog::info!(log, "step", i = i);
        }
    }
    latticelog::info!(log, "outside");
    // Rotated files are fsynced by the durability thread; a flush with fsync waits for them too.
    let r = log.flush(true);
    assert_eq!((r.level, r.last_seq, r.error), (FlushLevel::FsyncConfirmed, 101, None));
    log.close();
    let recs = records(&dir.join("p-1"));
    assert_eq!(seqs(&recs), (1..=101).collect::<Vec<_>>());
    assert!(files(&dir.join("p-1")).len() > 3, "rotated");
    let (r, chain) = &recs[50];
    let kinds: Vec<_> = chain.iter().map(|c| c["kind"].as_str().unwrap()).collect();
    assert_eq!(kinds, ["Scope", "Operation", "Operation", "Resource"]);
    assert_eq!(chain[1]["name"], "pay");
    assert_eq!(chain[2]["attributes"], json!([["operation.name", "checkout"], ["cart", 7]]));
    assert_eq!(chain[0]["attributes"], json!([["brand", "visa"]]));
    let pay_id = r["operation_id"].as_str().unwrap();
    assert_eq!(pay_id.len(), 16);
    assert_eq!(r["parent_operation_id"], "op-1");
    let (last, chain) = recs.last().unwrap();
    assert!(last.get("operation_id").is_none());
    assert_eq!(chain.len(), 1);
}

#[test]
fn explicit_contexts_work_without_entering_them() {
    let dir = temp_dir("explicit");
    let log = latticelog::open(&dir, opts()).unwrap();
    let req = log.context(latticelog::ContextKind::Request, Some("GET /orders")).attr("http.route", "/orders").build();
    log.info("handled").context(&req).emit();
    log.close();
    let recs = records(&dir.join("p-1"));
    assert_eq!(recs[0].1[0]["kind"], "Request");
    assert_eq!(recs[0].1[0]["name"], "GET /orders");
}

#[test]
fn many_threads_get_unique_dense_sequences() {
    let dir = temp_dir("threads");
    let log = latticelog::open(&dir, opts().durability(Durability::Fast)).unwrap();
    std::thread::scope(|s| {
        for t in 0..4 {
            let log = &log;
            s.spawn(move || {
                let _g = log.scope("worker").attr("t", t).enter();
                for i in 0..5000 {
                    latticelog::info!(log, "work", t = t, i = i);
                }
            });
        }
    });
    assert_eq!(log.flush(true).last_seq, 20_000);
    let stats = log.stats();
    assert_eq!(stats.written, 20_000);
    assert_eq!(stats.dropped_saturation, 0);
    log.close();
    let recs = records(&dir.join("p-1"));
    assert_eq!(seqs(&recs), (1..=20_000).collect::<Vec<_>>());
    // Each thread's records keep their own order and their own scope.
    for t in 0..4 {
        let mine: Vec<i64> = recs
            .iter()
            .filter(|(r, _)| attr(r, "t") == Some(&json!(t)))
            .map(|(r, c)| {
                assert_eq!(c[0]["attributes"], json!([["t", t]]));
                attr(r, "i").unwrap().as_i64().unwrap()
            })
            .collect();
        assert_eq!(mine, (0..5000).collect::<Vec<_>>());
    }
}

#[test]
fn levels_filter_and_closed_loggers_count_what_they_refuse() {
    let dir = temp_dir("levels");
    let log = latticelog::open(&dir, opts().min_level(Level::Warn)).unwrap();
    assert!(!latticelog::info!(log, "filtered"));
    assert!(latticelog::warn!(log, "kept"));
    log.set_min_level(Level::Debug);
    assert!(latticelog::debug!(log, "now kept"));
    log.close();
    assert!(!latticelog::error!(log, "after close"));
    assert_eq!(log.stats().dropped_after_close, 1);
    assert_eq!(seqs(&records(&dir.join("p-1"))), [1, 2]);
}

#[test]
fn a_full_queue_drops_newest_and_counts() {
    let dir = temp_dir("saturation");
    let log =
        latticelog::open(&dir, opts().queue_bytes(64 * 1024).saturation(Saturation::DropNewest).durability(Durability::Fast)).unwrap();
    let big = "x".repeat(2000);
    let mut accepted = 0u64;
    for _ in 0..2000 {
        if log.info(&big).emit() {
            accepted += 1;
        }
    }
    let r = log.flush(false);
    let stats = log.stats();
    assert_eq!(accepted + stats.dropped_saturation, 2000);
    assert_eq!(r.last_seq, accepted);
    log.close();
}

#[test]
fn retention_deletes_closed_files_only() {
    let dir = temp_dir("retention");
    let log = latticelog::open(&dir, opts().file_max_bytes(4096).max_bytes(10_000)).unwrap();
    for i in 0..400 {
        latticelog::info!(log, "filler line with some text", i = i);
    }
    log.close();
    let fs = files(&dir.join("p-1"));
    let total: u64 = fs.iter().map(|f| std::fs::metadata(f).unwrap().len()).sum();
    assert!(total <= 10_000 + 4096 + 1024, "total {total}");
    // What is left is a gap-free suffix ending with the newest record.
    let s = seqs(&records(&dir.join("p-1")));
    assert_eq!(*s.last().unwrap(), 400);
    assert!(s.windows(2).all(|w| w[1] == w[0] + 1));
}

#[test]
fn errors_keep_their_type_and_causes() {
    #[derive(Debug)]
    struct PaymentError(std::io::Error);
    impl std::fmt::Display for PaymentError {
        fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
            f.write_str("payment failed")
        }
    }
    impl std::error::Error for PaymentError {
        fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
            Some(&self.0)
        }
    }
    let dir = temp_dir("errors");
    let log = latticelog::open(&dir, opts()).unwrap();
    log.error("checkout failed").error(&PaymentError(std::io::Error::other("card declined"))).emit();
    log.close();
    let recs = records(&dir.join("p-1"));
    let ex = &recs[0].0["exception"];
    assert!(ex["type"].as_str().unwrap().ends_with("PaymentError"), "{ex}");
    assert_eq!(ex["inner"][0]["message"], "card declined");
}

#[test]
fn durable_mode_confirms_fsync_and_time_can_be_set() {
    let dir = temp_dir("durable");
    let log = latticelog::open(&dir, opts().durability(Durability::Durable)).unwrap();
    log.info("at a fixed time").time(Timestamp(0)).emit();
    let r = log.flush(false);
    assert_eq!(r.level, FlushLevel::FsyncConfirmed);
    assert_eq!(r.last_seq, 1);
    log.close();
    assert_eq!(records(&dir.join("p-1"))[0].0["time"], "1970-01-01T00:00:00.000000000Z");
}

#[test]
fn tracing_spans_become_contexts_and_events_become_records() {
    use tracing_subscriber::prelude::*;
    let dir = temp_dir("tracing");
    let log = latticelog::open(&dir, opts().min_level(Level::Debug)).unwrap();
    let subscriber = tracing_subscriber::registry().with(latticelog::layer(&log));
    tracing::subscriber::with_default(subscriber, || {
        let op = tracing::info_span!("checkout", operation_id = "op-7", cart = 3u64);
        let _e = op.enter();
        let s = tracing::debug_span!("pay", provider = "acme", attempt = tracing::field::Empty);
        s.record("attempt", 2);
        let _s = s.enter();
        let err = std::io::Error::other("declined");
        tracing::error!(
            event = "payment_failed",
            order_id = 991,
            amount = 19.99,
            ok = false,
            big = u64::MAX,
            trace_id = "4BF92F3577B34DA6A3CE929D0E0E4736",
            error = &err as &(dyn std::error::Error + 'static),
            "payment failed for {}",
            991
        );
        tracing::trace!("filtered by level");
    });
    tracing::subscriber::with_default(tracing_subscriber::registry().with(latticelog::layer(&log)), || {
        tracing::warn!(target: "billing", "outside spans");
    });
    log.close();
    let recs = records(&dir.join("p-1"));
    assert_eq!(recs.len(), 2);
    let (r, chain) = &recs[0];
    assert_eq!(r["body"], "payment failed for 991");
    assert_eq!(r["severity_number"], 17);
    assert_eq!(r["event"], "payment_failed");
    assert_eq!(r["trace_id"], "4bf92f3577b34da6a3ce929d0e0e4736");
    assert_eq!(r["operation_id"], "op-7");
    assert_eq!(attr(r, "order_id"), Some(&json!(991)));
    assert_eq!(attr(r, "amount"), Some(&json!(19.99)));
    assert_eq!(attr(r, "ok"), Some(&json!(false)));
    assert_eq!(attr(r, "big"), Some(&json!({"$u64":"18446744073709551615"})));
    assert!(attr(r, "logger.name").unwrap().as_str().unwrap().starts_with("local"));
    assert_eq!(r["exception"]["message"], "declined");
    assert_eq!(chain[0]["kind"], "Scope");
    assert_eq!(chain[0]["name"], "pay");
    assert_eq!(chain[0]["attributes"], json!([["provider", "acme"], ["attempt", 2]]));
    assert_eq!(chain[1]["kind"], "Operation");
    assert_eq!(chain[1]["attributes"], json!([["operation.name", "checkout"], ["cart", 3]]));
    assert_eq!(chain[2]["kind"], "Resource");
    let (r, chain) = &recs[1];
    assert_eq!(r["body"], "outside spans");
    assert_eq!(attr(r, "logger.name"), Some(&json!("billing")));
    assert_eq!(chain.len(), 1);
}

#[test]
fn log_crate_records_keep_typed_key_values() {
    let dir = temp_dir("logcrate");
    let log = latticelog::open(&dir, opts()).unwrap();
    latticelog::init_log(&log).unwrap();
    log::info!(target: "shop", order_id = 991, total = 19.99, event = "order_placed"; "order {} placed", 991);
    log::debug!("filtered");
    log.close();
    let recs = records(&dir.join("p-1"));
    assert_eq!(recs.len(), 1);
    let r = &recs[0].0;
    assert_eq!(r["body"], "order 991 placed");
    assert_eq!(r["event"], "order_placed");
    assert_eq!(attr(r, "order_id"), Some(&json!(991)));
    assert_eq!(attr(r, "total"), Some(&json!(19.99)));
    assert_eq!(attr(r, "logger.name"), Some(&json!("shop")));
}
