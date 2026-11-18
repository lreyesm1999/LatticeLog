"""Local durable spool: the SDK's equivalent of the .NET local store (ADR-0009). Records are appended as JSON lines
with a dense local sequence; the sender ships them and only the server's acknowledgement moves the cursor. Files that
are fully acknowledged are deleted; nothing unacknowledged is ever deleted."""
from __future__ import annotations

import json
import os
import threading
import uuid

from ._jsonl import dumps

_FILE_RECORDS = 10_000  # records per spool file


class SpoolFull(Exception):
    """Raised (or counted) when the spool reached its size limit: records are refused, never silently deleted."""


class Spool:
    def __init__(self, directory: str, max_bytes: int = 512 * 1024 * 1024, durability: str = "balanced"):
        if durability not in ("fast", "balanced", "durable"):
            raise ValueError("durability must be fast, balanced or durable")
        self.dir = directory
        self.max_bytes = max_bytes
        self.durability = durability
        self._lock = threading.Lock()
        os.makedirs(directory, exist_ok=True)
        meta = os.path.join(directory, "producer.json")
        if os.path.exists(meta):
            with open(meta, encoding="utf-8") as f:
                self.producer_id = json.load(f)["producer_id"]
        else:
            self.producer_id = uuid.uuid4().hex
            _atomic_write(meta, json.dumps({"producer_id": self.producer_id}))
        self.next_seq = self._recover_next_seq()
        self._file = None
        self._file_first = 0
        self.dropped = 0

    # ------------------------------------------------------------------ files

    def _files(self) -> list[tuple[int, str]]:
        out = []
        for name in os.listdir(self.dir):
            if name.startswith("spool-") and name.endswith(".jsonl"):
                try:
                    out.append((int(name[6:-6]), os.path.join(self.dir, name)))
                except ValueError:
                    pass
        return sorted(out)

    def _recover_next_seq(self) -> int:
        """Highest complete line of the newest file + 1; a torn last line (crash mid-write) is cut off."""
        files = self._files()
        cursor = self.committed_through()
        if not files:
            return cursor + 1
        first, path = files[-1]
        last = first - 1
        with open(path, "rb+") as f:
            data = f.read()
            good = data.rfind(b"\n") + 1
            if good != len(data):
                f.truncate(good)  # torn tail: never shipped, never acknowledged
            for line in data[:good].splitlines():
                if line:
                    last = json.loads(line)["seq_n"]
        return max(last + 1, cursor + 1)

    def committed_through(self) -> int:
        path = os.path.join(self.dir, "cursor.json")
        if not os.path.exists(path):
            return 0
        with open(path, encoding="utf-8") as f:
            return int(json.load(f)["committed_through"])

    def size(self) -> int:
        return sum(os.path.getsize(p) for _, p in self._files())

    # ------------------------------------------------------------------ append

    def append(self, record: dict) -> int | None:
        """Assigns the next local sequence and persists the record. Returns the sequence, or None if refused."""
        with self._lock:
            if self.max_bytes and self.size() >= self.max_bytes:
                self.dropped += 1
                return None
            seq = self.next_seq
            if self._file is None or seq - self._file_first >= _FILE_RECORDS:
                if self._file is not None:
                    self._file.close()
                self._file_first = seq
                self._file = open(os.path.join(self.dir, f"spool-{seq:020d}.jsonl"), "ab")
            record["seq"] = str(seq)
            record["seq_n"] = seq
            self._file.write((dumps(record) + "\n").encode("ascii"))
            if self.durability != "fast":
                self._file.flush()
            if self.durability == "durable":
                os.fsync(self._file.fileno())
            self.next_seq = seq + 1
            return seq

    def flush(self, fsync: bool = False):
        with self._lock:
            if self._file is not None:
                self._file.flush()
                if fsync:
                    os.fsync(self._file.fileno())

    # ------------------------------------------------------------------ read / acknowledge

    def _last_seq(self, files: list, i: int) -> int:
        """Last sequence in files[i]: the next file's first − 1, or the last complete line of the newest file."""
        if i + 1 < len(files):
            return files[i + 1][0] - 1
        last = files[i][0] - 1
        with open(files[i][1], "rb") as f:
            for line in f:
                if line.endswith(b"\n") and line.strip():
                    last = json.loads(line)["seq_n"]
        return last

    def read_after(self, after: int, max_records: int) -> list[dict]:
        """Up to max_records records with seq > after, in order (complete lines only)."""
        self.flush()
        out = []
        files = self._files()
        for i, (first, path) in enumerate(files):
            if i + 1 < len(files) and files[i + 1][0] - 1 <= after:
                continue  # entirely acknowledged or before the cursor
            with open(path, "rb") as f:
                for line in f:
                    if not line.endswith(b"\n"):
                        break  # being written
                    r = json.loads(line)
                    if r["seq_n"] <= after:
                        continue
                    out.append(r)
                    if len(out) >= max_records:
                        return out
        return out

    def acknowledge(self, committed: int):
        _atomic_write(os.path.join(self.dir, "cursor.json"), json.dumps({"committed_through": committed}))
        with self._lock:
            files = self._files()
            for i, (first, path) in enumerate(files):
                if self._file is not None and os.path.abspath(path) == os.path.abspath(self._file.name):
                    continue  # still being written
                if self._last_seq(files, i) <= committed:
                    os.remove(path)  # every record in it is acknowledged

    def close(self):
        with self._lock:
            if self._file is not None:
                self._file.flush()
                os.fsync(self._file.fileno())
                self._file.close()
                self._file = None


def _atomic_write(path: str, text: str):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)
