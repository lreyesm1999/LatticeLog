"""Local mode and structlog integration. Run: python -m unittest discover -s sdks/python/tests"""
import json
import logging
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import latticelog  # noqa: E402

try:
    import structlog
except ImportError:  # pragma: no cover
    structlog = None


class PaymentDeclined(Exception):
    """A non-builtin exception: its real type must survive as '<module>.PaymentDeclined'."""


def read_records(folder):
    """All record lines of all process folders, parsed. Also checks every file is a valid latticelog.jsonl doc."""
    out = []
    for proc in sorted(os.listdir(folder)):
        d = os.path.join(folder, proc)
        if not os.path.isdir(d):
            continue
        for name in sorted(os.listdir(d)):
            if not name.endswith(".jsonl"):
                continue
            with open(os.path.join(d, name), encoding="ascii") as f:
                lines = [json.loads(l) for l in f if l.strip()]
            assert lines[0]["type"] == "header" and lines[0]["format"] == "latticelog.jsonl" and lines[0]["version"] == 1
            contexts = set()
            for line in lines[1:]:
                if line["type"] == "context":
                    contexts.add(line["ref"])
                elif line["type"] == "record":
                    assert line.get("context", 0) == 0 or line["context"] in contexts, "context defined before use"
                    out.append((proc, line))
    return out


class LocalModeTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="llog-py-")
        self.folder = os.path.join(self.dir, "logs", "orders-api")

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def test_manifest_dense_sequence_and_reopen(self):
        log = latticelog.open(self.folder, service="orders-api", environment="dev", durability="durable")
        for i in range(5):
            log.info("order {i}", i=i, ratio=1.0)
        log.close()
        with open(os.path.join(self.folder, "latticelog.source.json"), encoding="utf-8") as f:
            m = json.load(f)
        self.assertEqual(("latticelog.source", 1, "orders-api", "dev", "latticelog.jsonl", 1),
                         (m["format"], m["version"], m["service"], m["environment"], m["data_format"], m["data_version"]))
        self.assertEqual(32, len(m["producer_id"]))
        log = latticelog.open(self.folder, service="orders-api", environment="dev")
        log.warn("again")
        log.close()
        recs = read_records(self.folder)
        self.assertEqual([str(i) for i in range(1, 7)], [r["seq"] for _, r in recs])  # dense across restarts
        self.assertEqual({"p-1"}, {p for p, _ in recs})
        self.assertEqual(["ratio", 1.0], recs[0][1]["attributes"][1])
        first_file = os.path.join(self.folder, "p-1", "latticelog-00000000000000000001.jsonl")
        with open(first_file, encoding="ascii") as f:
            self.assertIn('["ratio",1.0]', f.read())  # float64 keeps its marker on disk

    def test_folder_belongs_to_one_service(self):
        latticelog.open(self.folder, service="orders-api").close()
        with self.assertRaises(ValueError):
            latticelog.open(self.folder, service="billing")

    def test_concurrent_writers_get_one_process_folder_each(self):
        a = latticelog.open(self.folder, service="orders-api")
        b = latticelog.open(self.folder, service="orders-api")
        a.info("from a")
        b.info("from b")
        a.close()
        b.close()
        recs = read_records(self.folder)
        self.assertEqual({"p-1", "p-2"}, {p for p, _ in recs})
        self.assertTrue(all(r["seq"] == "1" for _, r in recs))
        c = latticelog.open(self.folder, service="orders-api")  # reuses the first free folder, continues its sequence
        c.info("from c")
        c.close()
        self.assertEqual(["1", "2"], [r["seq"] for p, r in read_records(self.folder) if p == "p-1"])

    def test_torn_tail_is_cut_and_sequence_continues(self):
        log = latticelog.open(self.folder, service="orders-api")
        log.info("one")
        log.info("two")
        log.close()
        f = [n for n in os.listdir(os.path.join(self.folder, "p-1")) if n.endswith(".jsonl")][0]
        with open(os.path.join(self.folder, "p-1", f), "ab") as fh:
            fh.write(b'{"type":"record","seq":"3","ti')  # crash mid-write
        log = latticelog.open(self.folder, service="orders-api")
        log.info("three")
        log.close()
        self.assertEqual(["1", "2", "3"], [r["seq"] for _, r in read_records(self.folder)])

    def test_retention_never_deletes_the_active_file(self):
        log = latticelog.open(self.folder, service="orders-api", max_bytes=4096, file_max_bytes=1024)
        for i in range(200):
            log.info("x" * 50, i=i)
        files = [n for n in os.listdir(os.path.join(self.folder, "p-1")) if n.endswith(".jsonl")]
        active = os.path.basename(log.spool._file.name)
        self.assertIn(active, files)
        self.assertLessEqual(len(files), 6)
        log.close()
        recs = read_records(self.folder)
        seqs = [int(r["seq"]) for _, r in recs]
        self.assertEqual(200, seqs[-1])
        self.assertEqual(list(range(seqs[0], 201)), seqs)  # what is left is contiguous: whole old files were removed

    def test_retention_by_age_keeps_recent_files(self):
        log = latticelog.open(self.folder, service="orders-api", max_age=3600, file_max_bytes=512)
        for i in range(50):
            log.info("y" * 40)
        log.close()
        self.assertEqual(50, len(read_records(self.folder)))


@unittest.skipIf(structlog is None, "structlog not installed")
class StructlogTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="llog-sl-")
        self.folder = os.path.join(self.dir, "demo-api")
        self.log = latticelog.open(self.folder, service="demo-api", environment="test")

    def tearDown(self):
        self.log.close()
        structlog.reset_defaults()
        shutil.rmtree(self.dir, ignore_errors=True)

    def records(self):
        self.log.spool.flush()
        return [r for _, r in read_records(self.folder)]

    def test_processor_maps_event_level_attributes_and_real_exception_type(self):
        processor = latticelog.structlog_processor(self.log)
        out = []
        structlog.configure(processors=[structlog.processors.add_log_level, structlog.processors.TimeStamper(fmt="iso"),
                                        processor, lambda _l, _m, ed: out.append(ed) or ""],
                            logger_factory=structlog.ReturnLoggerFactory())
        sl = structlog.get_logger()
        sl.info("order_placed", order_id=991, amount=19.99, paid=True, time="not-a-param", tags=["a", 1])
        try:
            try:
                raise KeyError("card")
            except KeyError as inner:
                raise PaymentDeclined("card declined") from inner
        except PaymentDeclined:
            sl.exception("payment_failed", order_id=991)
        sl.warning("Heartbeat failed for group x")
        recs = self.records()
        self.assertEqual(0, processor.errors)
        self.assertEqual(3, len(out))  # the event dict is passed on unchanged to the next processor
        self.assertEqual("order_placed", out[0]["event"])
        placed, failed, heartbeat = recs
        self.assertEqual("order_placed", placed["event"])
        self.assertEqual(9, placed["severity_number"])
        self.assertNotIn("body", placed)
        attrs = dict((k, v) for k, v in placed["attributes"])
        self.assertEqual(991, attrs["order_id"])
        self.assertEqual(19.99, attrs["amount"])
        self.assertIs(True, attrs["paid"])
        self.assertEqual("not-a-param", attrs["time"])  # a key named 'time' is an attribute, not the record time
        self.assertEqual(["a", 1], attrs["tags"])
        self.assertNotIn("timestamp", attrs)
        self.assertNotIn("level", attrs)
        self.assertEqual("payment_failed", failed["event"])
        self.assertEqual(17, failed["severity_number"])
        self.assertEqual(f"{__name__}.PaymentDeclined", failed["exception"]["type"])
        self.assertEqual("card declined", failed["exception"]["message"])
        self.assertIn("raise PaymentDeclined", failed["exception"]["stack"])
        self.assertEqual("KeyError", failed["exception"]["inner"][0]["type"])
        self.assertEqual("Heartbeat failed for group x", heartbeat["body"])  # free text is the message, not a name
        self.assertNotIn("event", heartbeat)
        self.assertEqual(13, heartbeat["severity_number"])

    def test_rendered_traceback_keeps_its_python_type(self):
        processor = latticelog.structlog_processor(self.log)
        structlog.configure(processors=[structlog.processors.add_log_level, structlog.processors.format_exc_info,
                                        processor, structlog.processors.JSONRenderer()],
                            logger_factory=structlog.ReturnLoggerFactory())
        try:
            json.loads("{bad")
        except ValueError:
            structlog.get_logger().error("parse_failed", exc_info=True)
        rec = self.records()[0]
        self.assertEqual("json.decoder.JSONDecodeError", rec["exception"]["type"])
        self.assertTrue(rec["exception"]["stack"].startswith("Traceback (most recent call last)"))

    def test_drop_makes_latticelog_the_only_output(self):
        structlog.configure(processors=[structlog.processors.add_log_level, latticelog.structlog_processor(self.log, drop=True)],
                            logger_factory=structlog.PrintLoggerFactory())
        structlog.get_logger().info("only_here", n=1)
        self.assertEqual("only_here", self.records()[0]["event"])

    def test_stdlib_handler_keeps_structlog_event_dict(self):
        root = logging.getLogger("llog-test")
        root.handlers.clear()
        root.propagate = False
        root.setLevel(logging.DEBUG)
        handler = latticelog.LatticeLogHandler(self.log)
        handler.setFormatter(structlog.stdlib.ProcessorFormatter(processor=structlog.dev.ConsoleRenderer()))
        root.addHandler(handler)
        structlog.configure(processors=[structlog.stdlib.add_log_level, structlog.processors.TimeStamper(fmt="iso"),
                                        structlog.stdlib.ProcessorFormatter.wrap_for_formatter],
                            logger_factory=structlog.stdlib.LoggerFactory(), wrapper_class=structlog.stdlib.BoundLogger)
        sl = structlog.get_logger("llog-test")
        sl.info("user_login", user_id=7, ok=True)
        try:
            raise PaymentDeclined("nope")
        except PaymentDeclined:
            sl.exception("checkout_failed", cart=3)
        logging.getLogger("llog-test").warning("plain %s message", "stdlib")
        login, failed, plain = self.records()
        self.assertEqual("user_login", login["event"])
        attrs = dict((k, v) for k, v in login["attributes"])
        self.assertEqual(7, attrs["user_id"])
        self.assertIs(True, attrs["ok"])
        self.assertEqual("llog-test", attrs["logger.name"])
        self.assertNotIn("body", login)
        self.assertEqual(f"{__name__}.PaymentDeclined", failed["exception"]["type"])
        self.assertEqual(17, failed["severity_number"])
        self.assertEqual("plain stdlib message", plain["body"])


if __name__ == "__main__":
    unittest.main()
