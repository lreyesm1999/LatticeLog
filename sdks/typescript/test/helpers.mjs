import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import assert from "node:assert/strict";

/** A fresh directory under the system temp folder, with an unpredictable name and owner-only permissions. */
export function tempDir(name) {
  return fs.mkdtempSync(path.join(os.tmpdir(), `ll-ts-${name}-`));
}

/** Data files of a process folder, sorted by first sequence. */
export function files(processDir) {
  return fs
    .readdirSync(processDir)
    .filter((f) => f.startsWith("latticelog-"))
    .sort()
    .map((f) => path.join(processDir, f));
}

/** One parsed file: every line must be valid JSON, and the file ends with a newline. */
export function lines(file) {
  const text = fs.readFileSync(file, "utf8");
  assert.ok(text === "" || text.endsWith("\n"), `${file} ends without a newline`);
  return text
    .split("\n")
    .filter((l) => l !== "")
    .map((l) => JSON.parse(l));
}

/**
 * Records of a process folder with their contexts resolved per file (as LatticeLog's reader does), checking the
 * document rules on the way: a header first, every context defined before use, refs unique within a file.
 */
export function records(processDir) {
  const out = [];
  for (const f of files(processDir)) {
    const ls = lines(f);
    if (!ls.length) continue;
    assert.equal(ls[0].type, "header", f);
    assert.equal(ls[0].format, "latticelog.jsonl");
    const ctx = new Map();
    for (const l of ls.slice(1)) {
      if (l.type === "context") {
        if (l.parent !== undefined) assert.ok(ctx.has(l.parent), `parent defined later: ${JSON.stringify(l)}`);
        assert.ok(!ctx.has(l.ref), `ref ${l.ref} defined twice in ${f}`);
        ctx.set(l.ref, l);
      } else if (l.type === "record") {
        const chain = [];
        for (let c = l.context; c !== undefined; ) {
          const def = ctx.get(c);
          assert.ok(def, `context ${c} used before definition`);
          chain.push(def);
          c = def.parent;
        }
        out.push({ rec: l, chain, file: f });
      } else {
        assert.fail(`unexpected line type ${l.type}`);
      }
    }
  }
  return out;
}

export function seqs(recs) {
  return recs.map((r) => Number(r.rec.seq));
}

/** Attribute value by key from `[[k, v], ...]`. */
export function attr(rec, key) {
  return rec.attributes?.find((p) => p[0] === key)?.[1];
}

export function range(from, to) {
  return Array.from({ length: to - from + 1 }, (_, i) => from + i);
}

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
