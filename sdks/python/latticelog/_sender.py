"""Background shipping of the spool with the ingestion protocol v1 (specs/ingest-protocol.md)."""
from __future__ import annotations

import json
import random
import threading
import time
import urllib.error
import urllib.request

from ._jsonl import build_body, envelope


class Sender:
    def __init__(self, spool, endpoint: str, token: str, namespace: str | None = None, batch_records: int = 1000,
                 poll_interval: float = 0.2, retry_base: float = 0.2, retry_max: float = 30.0, timeout: float = 30.0,
                 ack: str = "fsync", on_error=None):
        self.spool = spool
        self.url = endpoint.rstrip("/") + "/v1/ingest"
        self.token = token
        self.namespace = namespace
        self.batch_records = batch_records
        self.poll_interval = poll_interval
        self.retry_base = retry_base
        self.retry_max = retry_max
        self.timeout = timeout
        self.ack = ack
        self.on_error = on_error
        self.state = "running"
        self.last_error: str | None = None
        self.retries = 0
        self.batches = 0
        self._stop = threading.Event()
        self._wake = threading.Event()
        self._thread = threading.Thread(target=self._loop, name="latticelog-sender", daemon=True)
        self._thread.start()

    def wake(self):
        self._wake.set()

    def _post(self, body: bytes):
        req = urllib.request.Request(self.url, data=body, method="POST")
        req.add_header("Authorization", "Bearer " + self.token)
        req.add_header("Content-Type", "application/vnd.latticelog.batch")
        req.add_header("LatticeLog-Ack", self.ack)
        if self.namespace:
            req.add_header("LatticeLog-Namespace", self.namespace)
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                return resp.status, json.loads(resp.read() or b"{}"), resp.headers
        except urllib.error.HTTPError as e:
            raw = e.read() or b"{}"
            try:
                payload = json.loads(raw)
            except ValueError:
                payload = {}
            return e.code, payload, e.headers
        except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as e:
            return None, {"error": f"network: {e}"}, None

    def _backoff(self, attempt: int, retry_after: float | None):
        cap = min(self.retry_max, self.retry_base * (2 ** min(attempt, 20)))
        delay = random.random() * cap  # full jitter
        if retry_after and retry_after > delay:
            delay = retry_after
        self._stop.wait(delay)

    def _loop(self):
        attempt = 0
        max_records = self.batch_records
        while not self._stop.is_set():
            after = self.spool.committed_through()
            records = self.spool.read_after(after, max_records)
            if not records:
                self._wake.wait(self.poll_interval)
                self._wake.clear()
                continue
            # Dense run only (a hole means records were lost locally: declare it explicitly).
            run = [records[0]]
            for r in records[1:]:
                if r["seq_n"] != run[-1]["seq_n"] + 1:
                    break
                run.append(r)
            first = run[0]["seq_n"]
            gap = first != after + 1
            spool_records = [{k: v for k, v in r.items() if k != "seq_n"} for r in run]
            body = envelope(self.spool.producer_id, first, len(run), build_body(self.spool.producer_id, spool_records), gap)
            status, payload, headers = self._post(body)
            if status == 200:
                attempt, max_records = 0, self.batch_records
                self.batches += 1
                if payload.get("ack") == "FsyncConfirmed" or self.ack == "os":
                    committed = int(payload["committed_through"])
                    if committed > after:
                        self.spool.acknowledge(committed)
                    self.last_error = None
                else:
                    self.last_error = f"server acknowledged only {payload.get('ack')}"
                    attempt += 1
                    self._backoff(attempt, None)
            elif status == 409 and payload.get("error") == "sequence_gap":
                self.spool.acknowledge(int(payload["committed_through"]))  # the server's truth
            elif status == 413 and max_records > 1:
                max_records = max(1, len(run) // 2)
            elif status is None or status in (429, 503) or status >= 500:
                self.retries += 1
                self.last_error = payload.get("error") or f"HTTP {status}"
                retry_after = None
                if headers is not None and headers.get("Retry-After"):
                    try:
                        retry_after = float(headers.get("Retry-After"))
                    except ValueError:
                        # Retry-After as an HTTP date (or garbage): fall back to the normal backoff.
                        pass
                attempt += 1
                self._backoff(attempt, retry_after)
            else:
                self.state = "failed"
                self.last_error = f"HTTP {status} {payload.get('error')}: {payload.get('message')}"
                if self.on_error:
                    self.on_error(self.last_error)
                return

    def wait_for(self, seq: int, timeout: float) -> bool:
        deadline = time.monotonic() + timeout
        while self.spool.committed_through() < seq:
            if self.state != "running" or time.monotonic() > deadline:
                return False
            self.wake()
            time.sleep(0.02)
        return True

    def stop(self):
        self._stop.set()
        self._wake.set()
        self._thread.join(timeout=5)
