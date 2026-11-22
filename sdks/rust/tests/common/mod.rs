#![allow(dead_code)]

use serde_json::Value as J;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU32, Ordering};

/// A fresh directory under Cargo's per-target temporary folder.
pub fn temp_dir(name: &str) -> PathBuf {
    static N: AtomicU32 = AtomicU32::new(0);
    let base = option_env!("CARGO_TARGET_TMPDIR").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    let dir = base.join(format!("ll-{name}-{}-{}", std::process::id(), N.fetch_add(1, Ordering::Relaxed)));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

/// Data files of a process folder, sorted by first sequence.
pub fn files(process_dir: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(process_dir)
        .unwrap()
        .map(|e| e.unwrap().path())
        .filter(|p| p.file_name().unwrap().to_str().unwrap().starts_with("latticelog-"))
        .collect();
    v.sort();
    v
}

/// One parsed file: every line must be valid JSON.
pub fn lines(file: &Path) -> Vec<J> {
    let text = std::fs::read_to_string(file).unwrap();
    assert!(text.is_empty() || text.ends_with('\n'), "{} ends without a newline", file.display());
    text.lines().map(|l| serde_json::from_str(l).unwrap_or_else(|e| panic!("{e}: {l}"))).collect()
}

/// Records of a process folder with their contexts resolved per file (as LatticeLog's reader does), checking the
/// document rules on the way: a header first, every context defined before use, refs unique within a file.
pub fn records(process_dir: &Path) -> Vec<(J, Vec<J>)> {
    let mut out = Vec::new();
    for f in files(process_dir) {
        let ls = lines(&f);
        assert_eq!(ls[0]["type"], "header", "{}", f.display());
        assert_eq!(ls[0]["format"], "latticelog.jsonl");
        let mut ctx: std::collections::HashMap<u64, J> = Default::default();
        for l in &ls[1..] {
            match l["type"].as_str().unwrap() {
                "context" => {
                    let r = l["ref"].as_u64().unwrap();
                    if let Some(p) = l.get("parent") {
                        assert!(ctx.contains_key(&p.as_u64().unwrap()), "parent defined later: {l}");
                    }
                    assert!(ctx.insert(r, l.clone()).is_none(), "ref {r} defined twice in {}", f.display());
                }
                "record" => {
                    let mut chain = Vec::new();
                    let mut c = l.get("context").and_then(J::as_u64);
                    while let Some(r) = c {
                        let def = ctx.get(&r).unwrap_or_else(|| panic!("context {r} used before definition")).clone();
                        c = def.get("parent").and_then(J::as_u64);
                        chain.push(def);
                    }
                    out.push((l.clone(), chain));
                }
                other => panic!("unexpected line type {other}"),
            }
        }
    }
    out
}

pub fn seqs(recs: &[(J, Vec<J>)]) -> Vec<u64> {
    recs.iter().map(|(r, _)| r["seq"].as_str().unwrap().parse().unwrap()).collect()
}

/// Attribute value by key from `[[k, v], ...]`.
pub fn attr<'a>(rec: &'a J, key: &str) -> Option<&'a J> {
    rec.get("attributes")?.as_array()?.iter().find(|p| p[0] == key).map(|p| &p[1])
}
