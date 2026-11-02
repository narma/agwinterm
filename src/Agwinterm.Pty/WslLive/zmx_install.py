"""Install only the application's embedded helper sources, never dependencies."""
import base64
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import stat
import sys


def private(path):
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    value = path.lstat()
    if not stat.S_ISDIR(value.st_mode) or value.st_uid != os.getuid() or value.st_mode & 0o077:
        raise ValueError("helper directory must be private and owned by this user")


def main():
    if sys.version_info < (3, 11) or not hasattr(os, "pidfd_open"):
        raise ValueError("Python 3.11+ and Linux pidfds are required")
    raw = sys.stdin.buffer.readline(262145)
    if len(raw) > 262144:
        raise ValueError("oversized helper package")
    package = json.loads(raw)
    if set(package) != {"endpoint", "modules"} or not re.fullmatch(r"[0-9a-f]{24}", package["endpoint"]):
        raise ValueError("invalid helper package")
    required = {"zmx_helper.py", "zmx_exact.py", "zmx_ledger.py", "zmx_readiness.py"}
    if set(package["modules"]) != required:
        raise ValueError("invalid helper modules")
    modules = {name: base64.b64decode(value, validate=True) for name, value in package["modules"].items()}
    digest = hashlib.sha256(b"".join(name.encode() + modules[name] for name in sorted(modules))).hexdigest()
    ledger = Path.home() / ".local/state/agwinterm/zmx" / package["endpoint"]
    private(ledger)
    helpers = ledger / "helpers" / digest
    private(helpers)
    for name, content in modules.items():
        path = helpers / name
        try:
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
        except FileExistsError:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
            with os.fdopen(fd, "rb") as stream:
                if stream.read(65537) != content:
                    raise ValueError("helper content differs; refusing overwrite")
        else:
            with os.fdopen(fd, "wb") as stream:
                stream.write(content)
                stream.flush()
                os.fsync(stream.fileno())
    sockets = Path("/tmp") / ("agw-" + str(os.getuid()) + "-" + package["endpoint"])
    private(sockets)
    print(json.dumps({"distribution": os.environ["WSL_DISTRO_NAME"],
                      "user": pwd.getpwuid(os.getuid()).pw_name, "cwd": os.getcwd(),
                      "ledger": str(ledger), "helper": str(helpers / "zmx_helper.py"),
                      "socketDirectory": str(sockets)}), flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"error": str(error)}), flush=True)
        sys.exit(1)
