"""Deterministic helper failures; no PTYs or real workloads are started."""
import json
import os
from pathlib import Path
import sys
import tempfile
import struct
import unittest
import uuid
from unittest.mock import patch, MagicMock

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "src/Agwinterm.Pty/WslLive"))
import zmx_helper as helper
from zmx_ledger import Ledger


class RecoveryTests(unittest.TestCase):
    def test_input_acknowledgement_never_retries_a_mutation(self):
        request = {"verb": "type", "name": "agterm-" + "a" * 32,
                   "identity": {"workload": "exact"}, "text": "once"}
        for replies, outcome in [([struct.pack("<BI3x", 25, 1), b"\0"], "accepted"),
                                  ([struct.pack("<BI3x", 25, 1), b"\1"], "unavailable"),
                                  ([struct.pack("<BI3x", 24, 0)], "uncertain"),
                                  ([TimeoutError("lost reply")], "timeout")]:
            daemon = MagicMock()
            daemon.identity = request["identity"]
            daemon.receive.side_effect = replies
            with patch.object(helper, "validate_endpoint", return_value=("/zmx", "/sockets")), patch.object(helper, "ExactDaemon") as exact:
                exact.return_value.__enter__.return_value = daemon
                if outcome == "accepted":
                    self.assertEqual({"accepted": True, "executionComplete": False}, helper.terminal(request))
                elif outcome == "timeout":
                    with self.assertRaises(TimeoutError):
                        helper.terminal(request)
                else:
                    with self.assertRaises(helper.RecoveryError) as error:
                        helper.terminal(request)
                    self.assertEqual(outcome, error.exception.state)
                daemon.send.assert_called_once_with(25, b"once")

    def test_dependency_failures_are_distinct_and_never_create(self):
        for readiness, outcome in [("missing-executable", "unavailable"), ("unsupported", "unsupported")]:
            with patch.object(helper, "probe", return_value={"state": readiness}), patch.object(helper.subprocess, "Popen") as spawn:
                with self.assertRaises(helper.RecoveryError) as error:
                    helper.require_dependency("/no/zmx")
                self.assertEqual(outcome, error.exception.state)
                spawn.assert_not_called()
        with patch.object(helper, "probe", return_value={"state": "unsupported", "missingCapabilities": ["primary-history-replay"]}):
            with self.assertRaisesRegex(helper.RecoveryError, "primary-history-replay"):
                helper.require_dependency("/no/zmx")

    def test_lost_socket_is_not_evidence_of_lost_workload(self):
        with tempfile.TemporaryDirectory() as root:
            operation = str(uuid.uuid4())
            identity = {"workload": {"pid": 456}, "daemon": {"pid": 123}}
            with Ledger(root) as ledger:
                record, _ = ledger.reserve(operation, {"executable": "/zmx"})
                record.update(state="ready", identity=identity)
                ledger.write(record)
            with patch.object(helper, "require_dependency"), patch.object(helper, "incarnation_gone", return_value=False), patch.object(helper, "observe", side_effect=FileNotFoundError), patch.object(helper.subprocess, "Popen") as spawn:
                result, lease = helper.reconcile(root, operation)
                self.assertEqual("uncertain", result["state"])
                self.assertIsNone(lease)
                spawn.assert_not_called()
            with patch.object(helper, "incarnation_gone", return_value=True), patch.object(helper, "observe") as observe:
                result, lease = helper.reconcile(root, operation)
                self.assertEqual("missing", result["state"])
                observe.assert_not_called()

    def test_unknown_operation_is_not_creation_permission(self):
        with tempfile.TemporaryDirectory() as root, patch.object(helper.subprocess, "Popen") as spawn:
            with self.assertRaisesRegex(ValueError, "unknown operation"):
                helper.reconcile(root, str(uuid.uuid4()))
            spawn.assert_not_called()

    def test_terminate_after_distribution_loss_needs_no_socket_or_signal(self):
        with tempfile.TemporaryDirectory() as root:
            operation = str(uuid.uuid4())
            identity = {"daemon": {"pid": 1}, "workload": {"pid": 2}}
            with Ledger(root) as ledger:
                record, _ = ledger.reserve(operation, {"executable": "/zmx"})
                record.update(state="missing", identity=identity)
                ledger.write(record)
            with patch.object(helper, "incarnation_gone", return_value=True), patch.object(helper, "ExactDaemon") as socket:
                result, lease = helper.terminate({"ledger": root, "operation": operation, "identity": identity})
                self.assertEqual("terminated", result["state"])
                self.assertIsNone(lease)
                socket.assert_not_called()


if __name__ == "__main__":
    unittest.main()
