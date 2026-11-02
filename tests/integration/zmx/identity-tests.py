import copy
import fcntl
import os
from pathlib import Path
import pty
import select
import struct
import subprocess
import sys
import tempfile
import termios
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "src/Agwinterm.Pty/WslLive"))
from zmx_exact import ExactDaemon

zmx = sys.argv[1]
clients = []
name = "agterm-" + os.urandom(16).hex()


def spawn(root):
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
    env = {k: v for k, v in os.environ.items() if not k.startswith("ZMX_")}
    env.update(ZMX_DIR=root, ZMX_MANAGED="identity-test", TERM="xterm-256color")
    process = subprocess.Popen([zmx, "attach", name, "bash", "--noprofile", "--norc", "-c", "exec sleep 600"],
                               stdin=slave, stdout=slave, stderr=slave, env=env, start_new_session=True)
    os.close(slave)
    clients.append((process, master))
    for _ in range(100):
        try:
            return ExactDaemon(str(Path(root) / name), zmx)
        except (FileNotFoundError, ConnectionRefusedError, ProcessLookupError):
            time.sleep(.05)
    raise RuntimeError("daemon did not become ready")


def refused(operation):
    try:
        operation()
    except ValueError:
        return
    raise AssertionError("mutation unexpectedly accepted")


with tempfile.TemporaryDirectory(prefix="aw-id-") as root:
    current = None
    try:
        current = spawn(root)
        old = copy.deepcopy(current.identity)
        refused(lambda: current.terminate(old, owned=False))
        print("PASS external binding cannot terminate", flush=True)
        assert current.terminate(old, owned=True, lose_reply=True) == "terminated"
        oldfd = os.dup(current.pidfd)
        current.close()
        current = spawn(root)
        replacement = copy.deepcopy(current.identity)
        assert old != replacement
        refused(lambda: current.terminate(old, owned=True))
        print("PASS reused daemon name refuses stale incarnation", flush=True)
        # Deterministic PID reuse model: the new numeric PID with old start evidence.
        recycled = copy.deepcopy(replacement)
        recycled["daemon"]["start"] = str(int(replacement["daemon"]["start"]) - 1)
        refused(lambda: current.terminate(recycled, owned=True))
        assert current.info() == (replacement["workload"], replacement["daemon"])
        print("PASS reused numeric PID with stale start evidence refuses", flush=True)
        poll = select.poll()
        poll.register(oldfd, select.POLLIN)
        assert poll.poll(0)
        os.close(oldfd)
        print("PASS lost termination reply reconciles using old pidfd, replacement stays live", flush=True)
        assert current.terminate(replacement, owned=True) == "terminated"
        print("PASS exact owned termination confirms daemon exit", flush=True)
    finally:
        if current:
            try:
                current.terminate(current.identity, owned=True)
            except (OSError, EOFError, ValueError):
                pass
            current.close()
        for process, master in clients:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=5)
            os.close(master)
