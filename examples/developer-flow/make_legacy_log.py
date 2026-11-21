"""Creates old-logs/legacy-demo.log: a traditional structlog JSON log (one JSON object per line), including a
Python traceback rendered by format_exc_info and a few non-JSON lines, as an old service would have left it.

    python make_legacy_log.py [--append N]"""
import datetime as dt
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PATH = os.path.join(HERE, "old-logs", "legacy-demo.log")

TRACEBACK = (
    "Traceback (most recent call last):\n"
    '  File "/srv/legacy/billing.py", line 88, in charge\n'
    "    resp.raise_for_status()\n"
    '  File "/venv/lib/python3.11/site-packages/httpx/_models.py", line 761, in raise_for_status\n'
    "    raise HTTPStatusError(message, request=request, response=self)\n"
    "httpx.HTTPStatusError: Server error '503 Service Unavailable' for url 'https://payments.internal/charge'"
)


def lines(start, count):
    t0 = dt.datetime(2026, 6, 1, 8, 0, 0, tzinfo=dt.timezone.utc)
    for i in range(start, start + count):
        ts = (t0 + dt.timedelta(seconds=i)).isoformat().replace("+00:00", "Z")
        if i % 50 == 17:
            yield json.dumps({"event": "charge_failed", "level": "error", "timestamp": ts, "invoice": i, "exception": TRACEBACK})
        elif i % 97 == 5:
            yield "WARNING: legacy banner line that is not JSON"
        else:
            yield json.dumps({"event": "invoice_sent", "level": "info", "timestamp": ts, "invoice": i, "amount": round(10 + i * 0.25, 2)})


def main():
    os.makedirs(os.path.dirname(PATH), exist_ok=True)
    if len(sys.argv) > 2 and sys.argv[1] == "--append":
        with open(PATH, encoding="utf-8") as f:
            existing = sum(1 for _ in f)
        with open(PATH, "a", encoding="utf-8", newline="\n") as f:
            for line in lines(existing, int(sys.argv[2])):
                f.write(line + "\n")
        print(f"appended {sys.argv[2]} lines to {PATH}")
        return
    with open(PATH, "w", encoding="utf-8", newline="\n") as f:
        for line in lines(0, 1000):
            f.write(line + "\n")
    print(f"wrote 1000 lines to {PATH}")


if __name__ == "__main__":
    main()
