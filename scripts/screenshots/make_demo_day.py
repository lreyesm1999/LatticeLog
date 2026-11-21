"""Synthetic demo data for screenshots: one day of a checkout service's structlog JSON log with an incident window."""
import datetime as dt, json, random, sys
random.seed(7)
t0 = dt.datetime(2026, 11, 20, 0, 0, 0, tzinfo=dt.timezone.utc)
out = open(sys.argv[1], "w", encoding="utf-8", newline="\n")
n = 0
for minute in range(24 * 60):
    hour = minute / 60
    rate = int(20 + 60 * max(0.0, 1 - abs(hour - 13) / 7))           # busier around midday
    incident = 14 * 60 + 5 <= minute < 14 * 60 + 35                  # 14:05-14:35 UTC
    for k in range(rate):
        ts = (t0 + dt.timedelta(seconds=minute * 60 + k * 60 / rate)).isoformat().replace("+00:00", "Z")
        order = 50_000 + n; n += 1
        if incident and random.random() < 0.35:
            rec = {"event": "payment_failed", "level": "error", "timestamp": ts, "order_id": order, "provider": "payments-eu",
                   "exception": "Traceback (most recent call last):\n  File \"/srv/checkout/pay.py\", line 41, in charge\n    resp.raise_for_status()\nhttpx.ReadTimeout: timed out after 10.0 s"}
        elif random.random() < 0.01:
            rec = {"event": "cart_validation_failed", "level": "warning", "timestamp": ts, "order_id": order, "reason": "price_changed"}
        else:
            rec = {"event": "order_paid", "level": "info", "timestamp": ts, "order_id": order, "amount": round(random.uniform(5, 300), 2), "currency": "EUR"}
        out.write(json.dumps(rec) + "\n")
print(n, "lines")
