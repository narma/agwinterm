"""Hold a test-owned endpoint's existing ledger lock until the controller releases it."""
import fcntl
import pathlib
import sys
import time

ledger, ready, release = map(pathlib.Path, sys.argv[1:])
ready.parent.mkdir(parents=True, exist_ok=True)
with (ledger / "operations.lock").open("r+") as lock:
    fcntl.flock(lock, fcntl.LOCK_EX)
    ready.touch()
    deadline = time.monotonic() + 30
    while not release.exists():
        if time.monotonic() >= deadline:
            raise TimeoutError("restart test did not release its ledger gate")
        time.sleep(0.01)
