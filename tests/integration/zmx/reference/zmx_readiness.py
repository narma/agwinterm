"""Read-only dependency probe. Runtime capability evidence is provisioned explicitly.

The routed dependency suite writes an attestation for the tested binary. This
probe binds it to executable bytes; it never starts a workload to guess support.
"""
import hashlib
import json
import os
from pathlib import Path
import pwd
import sys

REQUIRED = {"managed-leadership", "authoritative-screen", "acknowledged-input",
            "atomic-publication", "snapshot-replay", "existing-fd-attach", "primary-history-replay"}


def probe(executable):
    result = {"version": 1, "provider": "wsl-zmx", "host": "server-rust",
              "distribution": os.environ.get("WSL_DISTRO_NAME"),
              "user": pwd.getpwuid(os.getuid()).pw_name, "uid": os.getuid(),
              "executable": executable, "remoteAttach": False, "presentation": False}
    path = Path(executable)
    if not path.is_absolute() or not path.is_file() or not os.access(path, os.X_OK):
        return dict(result, state="missing-executable", reason="Configured Linux executable is missing or not executable")
    if not hasattr(os, "pidfd_open"):
        return dict(result, state="unsupported", reason="Linux pidfd support is required")
    try:
        fd = os.pidfd_open(os.getpid())
        os.close(fd)
        if path.stat().st_size > 268435456:
            raise ValueError("Executable exceeds the supported size limit")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        attestation_path = Path(executable + ".agwinterm-capabilities.json")
        if attestation_path.stat().st_size > 16384:
            raise ValueError("Oversized capability attestation")
        attestation = json.loads(attestation_path.read_text())
        if not isinstance(attestation, dict) or type(attestation.get("version")) is not int or attestation.get("version") != 1 or attestation.get("sha256") != digest:
            raise ValueError("Executable has no matching runtime capability evidence")
        if not isinstance(attestation.get("capabilities"), list) or not all(isinstance(c, str) for c in attestation["capabilities"]):
            raise ValueError("Malformed capability evidence")
        missing = sorted(REQUIRED - set(attestation.get("capabilities", [])))
        if missing:
            return dict(result, state="unsupported", missingCapabilities=missing, sha256=digest)
        return dict(result, state="ready", sha256=digest,
                    capabilities=sorted(REQUIRED), provenance=attestation.get("provenance"))
    except (OSError, ValueError, TypeError) as error:
        return dict(result, state="unsupported", reason=str(error))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(2)
    result = probe(sys.argv[1])
    print(json.dumps(result))
    sys.exit(0 if result["state"] == "ready" else 1)
