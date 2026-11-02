import importlib.util
import json
import multiprocessing
import os
from pathlib import Path
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent / "reference"))
from zmx_ledger import Ledger


def interrupt_owner_write(directory, record, after_publish):
    original_link = os.link

    def interrupted_link(source, destination):
        if after_publish:
            original_link(source, destination)
        os._exit(73)

    os.link = interrupted_link
    with Ledger(directory) as ledger:
        ledger.write(record)


def reserve(directory, operation, queue):
    with Ledger(directory) as ledger:
        record, fresh = ledger.reserve(operation, {"argv": ["bash"]})
        queue.put((record["name"], fresh))


class LedgerTests(unittest.TestCase):
    def test_owner_publication_survives_process_exit_before_and_after_link(self):
        for after_publish in (False, True):
            with self.subTest(after_publish=after_publish), tempfile.TemporaryDirectory() as root:
                operation = str(uuid.uuid4())
                with Ledger(root) as ledger:
                    record, _ = ledger.reserve(operation, {})
                record["identity"] = {"test": "committed"}
                child = multiprocessing.Process(target=interrupt_owner_write,
                                                args=(root, record, after_publish))
                child.start()
                child.join(10)
                self.assertEqual(73, child.exitcode)
                with Ledger(root) as ledger:
                    self.assertEqual(record, ledger.read(operation))
                    self.assertEqual(operation if after_publish else None,
                                     ledger.owner_operation(record["name"]))
                    ledger.write(record)
                    self.assertEqual(operation, ledger.owner_operation(record["name"]))
                    ledger.index_owner(record)
                    conflicting = dict(record, operation=str(uuid.uuid4()))
                    with self.assertRaisesRegex(ValueError, "conflicting"):
                        ledger.index_owner(conflicting)
                    self.assertEqual(operation, ledger.owner_operation(record["name"]))

    def test_parallel_repeated_id_reserves_only_once(self):
        with tempfile.TemporaryDirectory() as root:
            operation = str(uuid.uuid4())
            queue = multiprocessing.Queue()
            children = [multiprocessing.Process(target=reserve, args=(root, operation, queue)) for _ in range(4)]
            for child in children:
                child.start()
            rows = [queue.get(timeout=10) for _ in children]
            for child in children:
                child.join(10)
                self.assertEqual(0, child.exitcode)
            self.assertEqual(1, sum(fresh for _, fresh in rows))
            self.assertEqual(1, len({name for name, _ in rows}))
            with Ledger(root) as ledger:
                record, fresh = ledger.reserve(operation, {"argv": ["bash"]})
                self.assertFalse(fresh)
                self.assertEqual("reserved", record["state"])
                with self.assertRaises(ValueError):
                    ledger.reserve(operation, {"argv": ["different-command"]})

    def test_partial_record_and_symlink_fail_closed(self):
        with tempfile.TemporaryDirectory() as root:
            operation = str(uuid.uuid4())
            path = Path(root) / (operation + ".json")
            path.write_text('{"version":')
            with Ledger(root) as ledger:
                with self.assertRaises(ValueError):
                    ledger.reserve(operation, {})
            path.unlink()
            target = Path(root) / "other"
            target.write_text("private")
            path.symlink_to(target)
            with Ledger(root) as ledger:
                with self.assertRaises(OSError):
                    ledger.read(operation)
            self.assertEqual("private", target.read_text())

    def test_interrupted_temporary_write_does_not_replace_committed_record(self):
        with tempfile.TemporaryDirectory() as root:
            operation = str(uuid.uuid4())
            with Ledger(root) as ledger:
                record, _ = ledger.reserve(operation, {})
                record["state"] = "launching"
                ledger.write(record)
            (Path(root) / ".pending-crashed").write_text("partial")
            with Ledger(root) as ledger:
                recovered, fresh = ledger.reserve(operation, {})
                self.assertFalse(fresh)
                self.assertEqual("launching", recovered["state"])


if __name__ == "__main__":
    unittest.main()
