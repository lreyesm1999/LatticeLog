//! Driven by tests/LatticeLog.Tests (SdkTests.cs, SourcesTests.cs). Usage:
//!
//! ```text
//! smoke offline <folder>                      write 300 records, no server
//! smoke ship <folder> <endpoint> <token>      write 200 more (record 300 has every value type), ship everything
//! smoke local <folder>                        two concurrent writers (p-1, p-2) of service rs-svc, with errors
//! ```

use latticelog::{Bytes, Decimal, Durability, Map, Options, ServerOptions, Timestamp, Utf16, Uuid, Value};
use std::fmt;
use std::time::Duration;

#[derive(Debug)]
struct KeyError(&'static str);
impl fmt::Display for KeyError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{:?}", self.0)
    }
}
impl std::error::Error for KeyError {}

#[derive(Debug)]
struct PaymentError(KeyError);
impl fmt::Display for PaymentError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("payment failed")
    }
}
impl std::error::Error for PaymentError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.0)
    }
}

#[derive(Debug)]
struct QuotaExceeded(u32);
impl fmt::Display for QuotaExceeded {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "tenant {} over quota", self.0)
    }
}
impl std::error::Error for QuotaExceeded {}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let (mode, folder) = (args[1].as_str(), args[2].as_str());
    match mode {
        "offline" => {
            let log = latticelog::open(folder, Options::new("rs-orders").durability(Durability::Durable)).expect("open");
            for i in 0..300 {
                latticelog::info!(log, "order {i}", i = i);
            }
            log.close();
        }
        "ship" => {
            let server = ServerOptions::new(&args[3], &args[4]).namespace("acme/prod").poll_interval(Duration::from_millis(20));
            let log = latticelog::open(folder, Options::new("rs-orders").server(server)).expect("open");
            for i in 300..500 {
                if i == 300 {
                    let _op = log.operation("checkout", Some("op-300")).attr("cart", 7).enter();
                    let mixed: [&dyn Value; 3] = [&1, &2.5, &"x"];
                    log.error("special {i}")
                        .attr("i", i)
                        .attr("ratio", 1.0)
                        .attr("big", 1u64 << 63)
                        .attr("nan", f64::NAN)
                        .attr("raw", Bytes(&[0x00, 0xFF]))
                        .attr("when", Timestamp(1_767_323_045_123_456_000))
                        .attr("id", Uuid::from_u128(255))
                        .attr("price", Decimal("19.990"))
                        .attr("nested", Map(&[("a", &mixed)]))
                        .attr("odd", Utf16(&[0x61, 0xD800, 0x62]))
                        .attr("unicode", "héllo 世界 🎉")
                        .trace_hex("4bf92f3577b34da6a3ce929d0e0e4736", None)
                        .error(&PaymentError(KeyError("sku")))
                        .emit();
                } else {
                    latticelog::info!(log, "order {i}", i = i);
                }
            }
            let ok = log.flush_shipped(Duration::from_secs(30));
            if ok {
                println!("shipped");
            } else {
                println!("not shipped: {:?}", log.stats());
            }
            log.close();
            std::process::exit(if ok { 0 } else { 1 });
        }
        "local" => {
            let first = latticelog::open(folder, Options::new("rs-svc").environment("test").durability(Durability::Durable)).expect("open");
            let second = latticelog::open(folder, Options::new("rs-svc").environment("test")).expect("open"); // its own p-2
            for (target, n) in [(&first, 30), (&second, 10)] {
                for i in 0..n {
                    target.info("request completed").event("request_completed").attr("i", i).emit();
                }
                if std::ptr::eq(target, &first) {
                    target.info("order placed").event("order_placed").attr("items", 7).attr("total", 19.5).emit();
                    let parse = "{oops".parse::<i32>().unwrap_err();
                    target.error("parse failed").event("parse_failed").attr("source", "upload").error(&parse).emit();
                    target.error("quota check").event("quota_check").error(&QuotaExceeded(9)).emit();
                } else {
                    target.error("quota check").event("quota_check").error(&QuotaExceeded(3)).emit();
                }
            }
            first.close();
            second.close();
        }
        other => panic!("unknown mode {other}"),
    }
}
