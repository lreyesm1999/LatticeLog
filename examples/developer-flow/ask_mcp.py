"""Talks to `latticelog mcp` over stdio (JSON-RPC) the way an AI client does, and prints what it answers.

    python ask_mcp.py latticelog            # or: python ask_mcp.py dotnet path/to/latticelog.dll

Run it from this folder: the server finds latticelog.toml here (roots = ["logs"], migration allowed in old-logs)."""
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


class Mcp:
    def __init__(self, cmd):
        self.p = subprocess.Popen(cmd + ["mcp", "--project", HERE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, encoding="utf-8", cwd=HERE)
        self.next_id = 0

    def request(self, method, params=None, meta=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}}
        if meta:
            msg["params"]["_meta"] = meta
        self.p.stdin.write(json.dumps(msg) + "\n")
        self.p.stdin.flush()
        while True:
            line = json.loads(self.p.stdout.readline())
            if line.get("method") == "notifications/progress":
                print(f"   progress: {line['params']['message']}")
                continue
            return line

    def call(self, name, **arguments):
        r = self.request("tools/call", {"name": name, "arguments": arguments}, meta={"progressToken": f"t{self.next_id}"})
        res = r["result"]
        if res["isError"]:
            raise RuntimeError(res["content"][0]["text"])
        return res["structuredContent"]

    def close(self):
        self.p.stdin.close()
        self.p.wait(10)


def main():
    mcp = Mcp(sys.argv[1:] or ["latticelog"])
    mcp.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "ask_mcp", "version": "1"}})
    tools = {t["name"]: t["annotations"] for t in mcp.request("tools/list")["result"]["tools"]}
    print("tools:", ", ".join(f"{n}{'' if a['readOnlyHint'] else ' (writes)'}" for n, a in tools.items()))

    print("\n> list_sources")
    for s in mcp.call("list_sources")["sources"]:
        print(f"   {s['name']:<14} {s['kind']:<13} {s.get('records', 0):>6} records  {s['path']}")

    legacy = os.path.join(HERE, "old-logs", "legacy-demo.log")
    print("\n> preview_migration(old-logs/legacy-demo.log)")
    p = mcp.call("preview_migration", path=legacy)
    print(f"   format: {p['detected_format']} via {p['importer']}; {p['line_count']} lines; estimated store size {p['estimated_store_bytes']} bytes")
    print(f"   first sample: {json.dumps(p['sample_records'][0])[:160]}...")
    print(f"   cannot keep: {p['cannot_keep'][0]}")

    print("\n> migrate_logs(old-logs/legacy-demo.log, source_name='legacy-demo')")
    m = mcp.call("migrate_logs", path=legacy, source_name="legacy-demo")
    print(f"   imported {m['records_imported']} records ({m['unparsed_lines_kept_as_text']} unparsed lines kept as text); "
          f"{m['original_bytes']} bytes -> {m['final_store_bytes']} bytes; originals: {m['originals']}")

    for source in ("demo-api", "legacy-demo"):
        print(f"\n> get_error_groups(source='{source}')")
        for g in mcp.call("get_error_groups", source=source)["groups"]:
            print(f"   [{g['source']}] {g['count']}x {g.get('event')} {g.get('exception_type')}: {g.get('sample_message', '')[:70]}")

    print("\n> search_events(source=['demo-api','legacy-demo'], where=\"level >= ERROR\", limit=4, newest_first=true)")
    for e in mcp.call("search_events", source=["demo-api", "legacy-demo"], where="level >= ERROR", limit=4, newest_first=True)["events"]:
        print(f"   {e['time']} [{e['source']}] {e.get('event')} {e.get('exception', {}).get('type')}")
    mcp.close()


if __name__ == "__main__":
    main()
