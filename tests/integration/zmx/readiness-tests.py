"""Host-free schema/readiness tests; fake executable must never be executed."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import subprocess
import sys
import platform

source = Path(__file__).resolve().parent / "reference/zmx_readiness.py"
spec = importlib.util.spec_from_file_location("readiness", source)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
native = Path(sys.argv.pop(1)) if len(sys.argv) > 1 else (
    Path(__file__).resolve().parents[3] / "artifacts/wsl-hook" /
    ("agwinterm-hook-linux-arm64" if platform.machine() in ("aarch64", "arm64") else "agwinterm-hook-linux-x64"))
reference_probe = module.probe


def probe(executable):
    # Run the production native probe, and independently compare the old wire contract.
    result = subprocess.run([str(native), "--zmx"],
                            input=json.dumps({"verb": "probe", "executable": executable}) + "\n",
                            text=True, capture_output=True, timeout=10, check=True)
    actual = json.loads(result.stdout)["result"]
    expected = reference_probe(executable)
    for key in ("state", "sha256", "missingCapabilities", "capabilities", "remoteAttach", "presentation"):
        assert actual.get(key) == expected.get(key), (key, actual, expected)
    return actual


module.probe = probe


class ReadinessTests(unittest.TestCase):
    def test_missing_unsupported_and_verified_are_distinct(self):
        with tempfile.TemporaryDirectory() as root:
            binary = Path(root) / "quoted ' executable"
            self.assertEqual("missing-executable", module.probe(str(binary))["state"])
            binary.write_text("#!/bin/sh\nexit 99\n")
            binary.chmod(0o700)
            self.assertEqual("unsupported", module.probe(str(binary))["state"])
            record = {"version": 1, "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
                      "capabilities": sorted(module.REQUIRED)}
            evidence = Path(str(binary) + ".agwinterm-capabilities.json")
            evidence.write_text(json.dumps(record))
            self.assertEqual("ready", module.probe(str(binary))["state"])
            record["capabilities"].remove("managed-leadership")
            evidence.write_text(json.dumps(record))
            self.assertEqual(["managed-leadership"], module.probe(str(binary))["missingCapabilities"])
            for malformed in ([], {"version": True}, {**record, "capabilities": None}):
                evidence.write_text(json.dumps(malformed))
                self.assertEqual("unsupported", module.probe(str(binary))["state"])
            evidence.write_text(json.dumps(record))
            binary.write_text("changed bytes")
            self.assertEqual("unsupported", module.probe(str(binary))["state"])
            self.assertFalse(module.probe(str(binary))["remoteAttach"])


unittest.main()
