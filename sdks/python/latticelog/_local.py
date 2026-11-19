"""Local mode (no server): a service writes official ``latticelog.jsonl`` v1 documents into its own folder.

Layout (specs/local-sources.md)::

    <folder>/latticelog.source.json          manifest: service, environment, format version, producer id
    <folder>/p-1/LOCK                         held by the process that writes p-1 (one subfolder per live process)
    <folder>/p-1/process.json                 {"producer_id": ...} of this process folder
    <folder>/p-1/latticelog-<first seq>.jsonl self-contained latticelog.jsonl documents (header, contexts, records)

Every record carries a dense ``seq`` per process folder. A process reuses the first unlocked process folder (so the
number of folders stays at the number of concurrent processes) and continues its sequence. Standard library only."""
from __future__ import annotations

import datetime as _dt
import json
import os
import threading
import time
import uuid

from ._jsonl import dumps

MANIFEST = "latticelog.source.json"
SOURCE_FORMAT = "latticelog.source"
SOURCE_VERSION = 1
_PREFIX, _SUFFIX = "latticelog-", ".jsonl"


class _ProcessLock:
    """Exclusive, non-blocking lock on <dir>/LOCK held for the life of the process (released by the OS on exit)."""

    def __init__(self, directory: str):
        self.path = os.path.join(directory, "LOCK")
        self._f = open(self.path, "a+b")
        try:
            if os.name == "nt":
                import msvcrt
                self._f.seek(0)
                msvcrt.locking(self._f.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._f.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self._f.close()
            raise

    def release(self):
        try:
            if os.name == "nt":
                import msvcrt
                self._f.seek(0)
                msvcrt.locking(self._f.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._f.fileno(), fcntl.LOCK_UN)
        except OSError:
            pass
        self._f.close()


def _seconds(v) -> float | None:
    if v is None:
        return None
    if isinstance(v, _dt.timedelta):
        return v.total_seconds()
    return float(v)


class LocalStore:
    """Duck-types the server spool (append/flush/close/next_seq/dropped) so ``Logger`` works unchanged."""

    def __init__(self, folder: str, service: str, environment: str | None = None, durability: str = "balanced",
                 max_age=None, max_bytes: int | None = None, file_max_bytes: int = 16 * 1024 * 1024):
        if durability not in ("fast", "balanced", "durable"):
            raise ValueError("durability must be fast, balanced or durable")
        if not service or not isinstance(service, str):
            raise ValueError("service is required")
        self.folder = os.path.abspath(folder)
        self.service = service
        self.environment = environment
        self.durability = durability
        self.max_age = _seconds(max_age)
        self.max_bytes = max_bytes
        self.file_max_bytes = file_max_bytes
        self.dropped = 0
        self._lock = threading.Lock()
        os.makedirs(self.folder, exist_ok=True)
        self.manifest = self._ensure_manifest()
        self._file = None
        self._acquire_process_folder()
        if hasattr(os, "register_at_fork"):
            # A forked child must not share the parent's folder, file or sequence: it takes its own process folder.
            os.register_at_fork(after_in_child=self._after_fork)

    # ------------------------------------------------------------------ manifest and process folders

    def _ensure_manifest(self) -> dict:
        path = os.path.join(self.folder, MANIFEST)
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                m = json.load(f)
            if m.get("format") != SOURCE_FORMAT or m.get("version") != SOURCE_VERSION:
                raise ValueError(f"{path}: not a {SOURCE_FORMAT} v{SOURCE_VERSION} manifest")
            if m.get("service") != self.service:
                raise ValueError(f"{self.folder} belongs to service {m.get('service')!r}, not {self.service!r}; use one folder per service")
            if self.environment is not None and m.get("environment") != self.environment:
                m["environment"] = self.environment
                _atomic_write(path, json.dumps(m, indent=2))
            return m
        m = {
            "format": SOURCE_FORMAT,
            "version": SOURCE_VERSION,
            "service": self.service,
            "environment": self.environment,
            "data_format": "latticelog.jsonl",
            "data_version": 1,
            "producer_id": uuid.uuid4().hex,
            "layout": "process-folders",
            "created": _dt.datetime.now(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        }
        try:
            # O_EXCL: two processes starting at once must not both write a manifest with different producer ids.
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL)
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(json.dumps(m, indent=2))
                f.flush()
                os.fsync(f.fileno())
            return m
        except FileExistsError:
            return self._ensure_manifest()

    def _process_folders(self) -> list[tuple[int, str]]:
        out = []
        for name in os.listdir(self.folder):
            if name.startswith("p-") and name[2:].isdigit() and os.path.isdir(os.path.join(self.folder, name)):
                out.append((int(name[2:]), os.path.join(self.folder, name)))
        return sorted(out)

    def _acquire_process_folder(self):
        for _ in range(1000):
            existing = self._process_folders()
            for _, path in existing:
                try:
                    self._plock = _ProcessLock(path)
                except OSError:
                    continue  # another live process writes there
                self._open_process_folder(path)
                return
            n = (existing[-1][0] + 1) if existing else 1
            try:
                os.mkdir(os.path.join(self.folder, f"p-{n}"))
            except FileExistsError:
                pass  # raced with another process: rescan
        raise OSError(f"could not acquire a process folder in {self.folder}")

    def _open_process_folder(self, path: str):
        self.process_dir = path
        meta = os.path.join(path, "process.json")
        if os.path.exists(meta):
            with open(meta, encoding="utf-8") as f:
                self.producer_id = json.load(f)["producer_id"]
        else:
            self.producer_id = uuid.uuid4().hex
            _atomic_write(meta, json.dumps({"producer_id": self.producer_id}))
        self.next_seq = self._recover_next_seq()
        self._apply_retention()

    def _after_fork(self):
        self._lock = threading.Lock()
        self._file = None  # the parent keeps writing its own file; never touch it from the child
        try:
            self._plock._f.close()
        except OSError:
            pass
        self._acquire_process_folder()

    # ------------------------------------------------------------------ files

    def _files(self, directory: str | None = None) -> list[tuple[int, str]]:
        directory = directory or self.process_dir
        out = []
        for name in os.listdir(directory):
            if name.startswith(_PREFIX) and name.endswith(_SUFFIX):
                try:
                    out.append((int(name[len(_PREFIX):-len(_SUFFIX)]), os.path.join(directory, name)))
                except ValueError:
                    pass
        return sorted(out)

    def _recover_next_seq(self) -> int:
        """Continues the dense sequence after the last complete record; a torn last line (crash mid-write) is cut off."""
        for first, path in reversed(self._files()):
            with open(path, "rb+") as f:
                data = f.read()
                good = data.rfind(b"\n") + 1
                if good != len(data):
                    f.truncate(good)
                    f.flush()
                    os.fsync(f.fileno())
            last = None
            for line in data[:good].splitlines():
                if line.startswith(b'{"type":"record"'):
                    last = int(json.loads(line)["seq"])
            if last is not None:
                return last + 1
            return first  # a file is named after the first sequence it was opened for
        return 1

    def _open_new_file(self):
        if self._file is not None:
            self._file.flush()
            os.fsync(self._file.fileno())  # a rotated (closed) file is always durable: retention may delete it later
            self._file.close()
        path = os.path.join(self.process_dir, f"{_PREFIX}{self.next_seq:020d}{_SUFFIX}")
        self._file = open(path, "ab")
        self._file_bytes = self._file.tell()
        self._contexts: dict[str, int] = {}
        self._write_line({"type": "header", "format": "latticelog.jsonl", "version": 1, "store_id": self.producer_id})
        self._apply_retention()

    def _write_line(self, obj: dict):
        data = (dumps(obj) + "\n").encode("ascii")
        self._file.write(data)
        self._file_bytes += len(data)

    def _context_ref(self, ctx) -> int:
        if ctx is None:
            return 0
        key = dumps(ctx)
        ref = self._contexts.get(key)
        if ref is not None:
            return ref
        parent = self._context_ref(ctx.get("parent"))
        ref = len(self._contexts) + 1
        self._contexts[key] = ref
        line = {"type": "context", "ref": ref}
        if parent:
            line["parent"] = parent
        line["kind"] = ctx.get("kind", "Custom")
        if ctx.get("name") is not None:
            line["name"] = ctx["name"]
        line["attributes"] = ctx.get("attributes", [])
        self._write_line(line)
        return ref

    # ------------------------------------------------------------------ append (spool interface)

    def append(self, record: dict) -> int | None:
        """Assigns the next sequence and writes one latticelog.jsonl record line. Returns the sequence."""
        with self._lock:
            if self._file is None or self._file_bytes >= self.file_max_bytes:
                self._open_new_file()
            seq = self.next_seq
            ref = self._context_ref(record.get("ctx"))
            line = {"type": "record", "seq": str(seq)}
            for k, v in record.items():
                if k not in ("ctx", "type", "seq", "seq_n"):
                    line[k] = v
            if ref:
                line["context"] = ref
            self._write_line(line)
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

    def size(self) -> int:
        total = 0
        for _, d in self._process_folders():
            total += sum(os.path.getsize(p) for _, p in self._files(d))
        return total

    # ------------------------------------------------------------------ retention

    def _apply_retention(self):
        """Deletes whole closed files, oldest first, by age and/or total size of the source folder.

        Only files that were closed (and fsynced when rotated) are candidates: the newest file of each process folder
        may still be written by a live process and is never deleted, so nothing unconfirmed is ever removed."""
        if self.max_age is None and self.max_bytes is None:
            return
        closed = []
        total = 0
        for _, d in self._process_folders():
            files = self._files(d)
            for i, (first, path) in enumerate(files):
                try:
                    st = os.stat(path)
                except FileNotFoundError:
                    continue
                total += st.st_size
                if i + 1 < len(files):
                    closed.append((st.st_mtime, path, st.st_size))
        closed.sort()
        now = time.time()
        for mtime, path, size in closed:
            too_old = self.max_age is not None and now - mtime > self.max_age
            too_big = self.max_bytes is not None and total > self.max_bytes
            if not (too_old or too_big):
                continue
            try:
                os.remove(path)
                total -= size
            except OSError:
                pass  # in use or already removed: retried at the next rotation

    def close(self):
        with self._lock:
            if self._file is not None:
                self._file.flush()
                os.fsync(self._file.fileno())
                self._file.close()
                self._file = None
            plock = getattr(self, "_plock", None)
            if plock is not None:
                plock.release()
                self._plock = None


def _atomic_write(path: str, text: str):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)
