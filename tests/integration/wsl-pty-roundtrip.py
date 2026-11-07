"""Measure a Linux application's terminal response time on the private WSL test desktop."""
import json
import os
import re
import select
import sys
import termios
import time
import tty

original = termios.tcgetattr(0)
try:
    tty.setraw(0)
    results = {}
    for name, payload in (("query", b""), ("64KiB_and_query", b"x" * 65536)):
        samples = []
        for attempt in range(55):
            start = time.perf_counter_ns()
            data = memoryview(payload + b"\x1b[6n")
            while data:
                data = data[os.write(1, data):]
            reply = b""
            while not reply.endswith(b"R"):
                if len(reply) > 64 or not select.select([0], [], [], 3)[0]:
                    raise RuntimeError(f"missing cursor response: {reply!r}")
                chunk = os.read(0, 64)
                if not chunk:
                    raise RuntimeError("terminal closed during cursor query")
                reply += chunk
            elapsed = (time.perf_counter_ns() - start) / 1_000_000
            if not re.fullmatch(rb"\x1b\[\d+;\d+R", reply):
                raise RuntimeError(f"invalid cursor response: {reply!r}")
            if attempt >= 5:
                samples.append(elapsed)
        samples.sort()
        results[name] = {"median_ms": (samples[24] + samples[25]) / 2, "p95_ms": samples[47]}
    with open(sys.argv[1] + ".tmp", "w") as report:
        json.dump(results, report)
    os.replace(sys.argv[1] + ".tmp", sys.argv[1])
finally:
    termios.tcsetattr(0, termios.TCSANOW, original)
time.sleep(30)  # Keep the pane available until the fixture collects the result.
