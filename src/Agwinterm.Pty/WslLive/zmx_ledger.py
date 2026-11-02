"""Private atomic operation ledger; uncertainty is never permission to recreate."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import stat
import tempfile
import time
import uuid


class Ledger:
    def __init__(self, directory):
        self.root = Path(directory)
        if not self.root.is_absolute() or ".." in self.root.parts or str(self.root).startswith("/mnt/"):
            raise ValueError("ledger requires an absolute Linux filesystem directory")
        self.root.mkdir(mode=0o700, parents=True, exist_ok=True)
        info = self.root.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
            raise ValueError("ledger must be a private directory owned by this Linux user")
        self.lock = None

    def __enter__(self):
        self.lock = os.open(self.root / "operations.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
        deadline = time.monotonic() + 5
        while True:
            try:
                fcntl.flock(self.lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return self
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    os.close(self.lock)
                    self.lock = None
                    raise TimeoutError("endpoint operation already in flight")
                time.sleep(.02)

    def __exit__(self, *_):
        if self.lock is not None:
            os.close(self.lock)
            self.lock = None

    def path(self, operation):
        if str(uuid.UUID(operation)) != operation:
            raise ValueError("operation must be a canonical UUID")
        return self.root / (operation + ".json")

    def read(self, operation):
        path = self.path(operation)
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
        except FileNotFoundError:
            return None
        with os.fdopen(fd, "rb") as stream:
            raw = stream.read(65537)
        if len(raw) > 65536:
            raise ValueError("oversized operation record")
        record = json.loads(raw)
        if not isinstance(record, dict) or record.get("version") != 1 or record.get("operation") != operation:
            raise ValueError("invalid operation record; manual reconciliation required")
        return record

    def write(self, record):
        if self.lock is None:
            raise RuntimeError("mutation requires endpoint lock")
        path = self.path(record["operation"])
        raw = json.dumps(record, sort_keys=True).encode()
        if len(raw) > 65536:
            raise ValueError("oversized operation record")
        fd, temporary = tempfile.mkstemp(prefix=".pending-", dir=self.root)
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
            directory = os.open(self.root, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def reserve(self, operation, specification):
        fingerprint = hashlib.sha256(json.dumps(specification, sort_keys=True).encode()).hexdigest()
        existing = self.read(operation)
        if existing:
            if existing.get("fingerprint") != fingerprint:
                raise ValueError("operation ID reused with different launch data")
            return existing, False
        record = {"version": 1, "operation": operation, "fingerprint": fingerprint,
                  "name": "agterm-" + uuid.uuid4().hex, "state": "reserved", "specification": specification}
        self.write(record)
        return record, True
