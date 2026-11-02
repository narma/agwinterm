"""Host-free native protocol/legacy ledger checks; no ZMX daemon or UI required."""
import json
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import uuid

helper = str(Path(sys.argv.pop(1)).resolve())
sys.path.insert(0, str(Path(__file__).resolve().parent / "reference"))
from zmx_ledger import Ledger


def invoke(request, raw=None):
    run = subprocess.run([helper, "--zmx"], input=raw or json.dumps(request) + "\n",
                         text=True, capture_output=True, timeout=10,
                         env={**os.environ, "PATH": "/no-interpreters-or-tools"})
    response = json.loads(run.stdout)
    assert run.returncode == (0 if response["ok"] else 1), (run, response)
    return response


class NativeProtocolTests(unittest.TestCase):
    def test_inventory_is_not_disabled_by_historical_operation_count(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            binary = root / "never-executed-zmx"
            binary.write_text("#!/bin/sh\nexit 99\n")
            binary.chmod(0o700)
            Path(str(binary) + ".agwinterm-capabilities.json").write_text(json.dumps({
                "version": 1, "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
                "capabilities": ["managed-leadership", "authoritative-screen", "acknowledged-input",
                                 "atomic-publication", "snapshot-replay", "existing-fd-attach", "primary-history-replay"]}))
            sockets = root / "s"
            sockets.mkdir(mode=0o700)
            ledger_root = root / "ledger"
            with Ledger(str(ledger_root)) as ledger:
                for _ in range(1025):
                    operation = str(uuid.uuid4())
                    ledger.write({"version": 1, "operation": operation, "state": "terminated"})
            response = invoke({"verb": "inventory", "executable": str(binary),
                               "socketDirectory": str(sockets), "ledger": str(ledger_root)})
            self.assertTrue(response["ok"], response)
            self.assertEqual([], response["result"]["sessions"])

    def test_unknown_fields_oversized_input_and_missing_newline_refuse(self):
        for raw in ('{"verb":"unknown"}\n', '{"verb":"probe","executable":"/missing","extra":1}\n',
                    ' ' * 65537 + '\n', '{"verb":"probe","executable":"/missing"}'):
            self.assertFalse(invoke(None, raw)["ok"])

    def test_legacy_lost_restart_rolls_back_and_fences_late_creation(self):
        with tempfile.TemporaryDirectory() as root:
            old, new = str(uuid.uuid4()), str(uuid.uuid4())
            process = {"pid": 2147483647, "start": "1", "boot": "previous-boot", "namespace": "pid:[1]"}
            identity = {"daemon": process, "workload": process, "path": root + "/socket", "socket": [1, 2]}
            with Ledger(root) as ledger:
                record, _ = ledger.reserve(old, {"executable": "/missing"})
                record.update(identity=identity, state="ready")
                ledger.write(record)
            request = {"verb": "reconcile-restart", "ledger": root, "operation": old,
                       "newOperation": new, "identity": identity}
            for _ in range(2):
                response = invoke(request)
                self.assertTrue(response["ok"], response)
                self.assertTrue(response["result"]["rollback"])
                self.assertEqual("missing", response["result"]["state"])
                self.assertEqual(identity, response["result"]["identity"])
            with Ledger(root) as ledger:
                self.assertEqual("cancelled", ledger.read(new)["state"])
                with self.assertRaises(ValueError):
                    ledger.reserve(new, {"executable": "/missing"})

    def test_legacy_committed_restart_recovers_ended_new_identity(self):
        with tempfile.TemporaryDirectory() as root:
            operations = [str(uuid.uuid4()), str(uuid.uuid4())]
            identities = []
            with Ledger(root) as ledger:
                for index, operation in enumerate(operations):
                    process = {"pid": 2147483647-index, "start": "1", "boot": "old", "namespace": "pid:[1]"}
                    identity = {"daemon": process, "workload": process, "path": root + "/" + str(index), "socket": [1, index]}
                    record, _ = ledger.reserve(operation, {"executable": "/missing"})
                    record.update(identity=identity, state="ready")
                    ledger.write(record)
                    identities.append(identity)
            response = invoke({"verb": "reconcile-restart", "ledger": root, "operation": operations[0],
                               "newOperation": operations[1], "identity": identities[0]})
            self.assertTrue(response["ok"], response)
            self.assertFalse(response["result"]["rollback"])
            self.assertEqual(identities[1], response["result"]["identity"])


unittest.main()
