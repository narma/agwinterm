"""Isolated survival sentinel; never selects a process by name for cleanup."""
import json
import os
from pathlib import Path
import signal
import select
import sys
import time
import tty


def identity(pid):
    stat = Path(f"/proc/{pid}/stat").read_text()
    fields = stat[stat.rfind(")") + 2:].split()
    if fields[0] == "Z":
        raise ProcessLookupError(pid)
    return {"pid": pid, "start": fields[19],
            "boot": Path("/proc/sys/kernel/random/boot_id").read_text().strip()}


mode, directory = sys.argv[1:]
root = Path(directory)
if mode == "run":
    # mkdir is exclusive: a replay is an error, never a second sentinel.
    root.mkdir(mode=0o700)
    record = {"workload": identity(os.getpid()), "daemon": identity(os.getppid()),
              "init": identity(1), "pid_namespace": os.readlink("/proc/self/ns/pid")}
    (root / "identity.json").write_text(json.dumps(record))
    (root / "prompt-token").write_text(os.environ.get("AGWINTERM_PROMPT_TOKEN", ""))
    def terminated(number, frame):
        (root / "signal.json").write_text(json.dumps({"signal": number, "time": time.time()}))
        sys.exit(128 + number)
    signal.signal(signal.SIGHUP, terminated)
    signal.signal(signal.SIGTERM, terminated)
    tty.setraw(sys.stdin.fileno())
    print("AGWINTERM_ZMX_SENTINEL_READY", flush=True)
    while True:
        if select.select([sys.stdin], [], [], 1)[0]:
            data = os.read(sys.stdin.fileno(), 65536)
            if data:
                with (root / "input.bin").open("ab") as stream:
                    stream.write(data)
elif mode == "assert-zero-input":
    if (root / "input.bin").exists() and (root / "input.bin").stat().st_size:
        raise RuntimeError("live workload received replay/input: " + repr((root / "input.bin").read_bytes()))
    print("PASS zero bytes replayed into live sentinel")
elif mode == "assert-control-input":
    data = (root / "input.bin").read_bytes()
    if data != b"CONTROL-PROBE":
        raise RuntimeError("unexpected authoritative input: " + repr(data))
    print("PASS authoritative input delivered once")
elif mode == "diagnose":
    print(json.dumps({"init": identity(1), "pid_namespace": os.readlink("/proc/self/ns/pid"),
                      "saved": json.loads((root / "identity.json").read_text()),
                      "signal": (root / "signal.json").read_text() if (root / "signal.json").exists() else None}))
elif mode in ("inspect", "cleanup"):
    record = json.loads((root / "identity.json").read_text())
    if mode == "cleanup":
        try:
            gone = os.readlink("/proc/self/ns/pid") != record["pid_namespace"] or identity(record["workload"]["pid"]) != record["workload"]
        except (FileNotFoundError, ProcessLookupError):
            gone = True
        if gone:
            print(json.dumps({"alreadyGone": True, "record": record}))
            sys.exit(0)
    for key in ("workload", "daemon"):
        if identity(record[key]["pid"]) != record[key]:
            raise RuntimeError(f"{key} incarnation changed")
    if mode == "cleanup":
        # Pin first, then revalidate while holding the handle. Never signal a PID.
        workload = record["workload"]
        fd = os.pidfd_open(workload["pid"])
        try:
            if identity(workload["pid"]) != workload:
                raise RuntimeError("stale cleanup")
            signal.pidfd_send_signal(fd, signal.SIGTERM)
        finally:
            os.close(fd)
    print(json.dumps(record))
else:
    raise ValueError("unknown operation")
