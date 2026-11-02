"""Foreground WSL lifetime lease. No PTY, daemon creation, signals or relaunch.

The Windows wsl.exe invocation must be independent of the UI/PTY host lifetime.
Input is one bounded JSON record; ready is emitted only after opening and
revalidating pidfds. The process exits once all pinned workloads have exited.
"""
import json
import os
from pathlib import Path
import select
import sys


def incarnation(pid):
    if type(pid) is not int or pid <= 1:
        raise ValueError("invalid workload PID")
    text = Path(f"/proc/{pid}/stat").read_text()
    fields = text[text.rfind(")") + 2:].split()
    if fields[0] == "Z":
        raise ProcessLookupError(pid)
    return {"pid": pid, "start": fields[19],
            "boot": Path("/proc/sys/kernel/random/boot_id").read_text().strip()}


def run(request):
    if set(request) != {"version", "workloads"} or request["version"] != 1:
        raise ValueError("unsupported lifetime request")
    workloads = request["workloads"]
    if not isinstance(workloads, list) or not 1 <= len(workloads) <= 64:
        raise ValueError("expected 1..64 workloads")
    handles = []
    try:
        poll = select.poll()
        for expected in workloads:
            if set(expected) != {"pid", "start", "boot"}:
                raise ValueError("malformed incarnation")
            if incarnation(expected["pid"]) != expected:
                raise ValueError("stale workload")
            fd = os.pidfd_open(expected["pid"])
            handles.append(fd)
            # A recycled numeric PID must never satisfy the old lease.
            if incarnation(expected["pid"]) != expected:
                raise ValueError("workload changed during acquisition")
            poll.register(fd, select.POLLIN)
        print(json.dumps({"ok": True, "version": 1, "guardPid": os.getpid()}), flush=True)
        pending = set(handles)
        while pending:
            for fd, _ in poll.poll():
                poll.unregister(fd)
                pending.remove(fd)
    finally:
        for fd in handles:
            os.close(fd)


if __name__ == "__main__":
    try:
        line = sys.stdin.buffer.readline(16385)
        if len(line) > 16384 or not line.endswith(b"\n"):
            raise ValueError("lifetime request too large or incomplete")
        run(json.loads(line))
    except Exception as error:
        print(json.dumps({"ok": False, "error": str(error)}), flush=True)
        sys.exit(1)
