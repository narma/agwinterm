"""Verify the local ZMX attachment patch before issuing capability evidence."""
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
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent / "reference"))
from zmx_exact import ExactDaemon

clients = []


def client(executable, name, env, command=(), fd=None):
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
    child = subprocess.Popen([executable, "attach", name, *command], stdin=slave,
                             stdout=slave, stderr=slave, env=env,
                             pass_fds=() if fd is None else (fd,), start_new_session=True)
    os.close(slave)
    clients.append((child, master))
    return child, master


with tempfile.TemporaryDirectory(prefix="aw-fd-") as root:
    name = "agterm-" + uuid.uuid4().hex
    missing = "agterm-" + uuid.uuid4().hex
    env = {k: v for k, v in os.environ.items() if not k.startswith("ZMX_")}
    env.update(ZMX_DIR=root, ZMX_MANAGED="first", TERM="xterm-256color")
    daemon = None
    try:
        first, _ = client(sys.argv[1], name, env, ["sh", "-c", "printf 'EXACT-FD-SENTINEL'; exec sleep 300"])
        for _ in range(100):
            try:
                daemon = ExactDaemon(str(Path(root) / name), sys.argv[1])
                break
            except (FileNotFoundError, ConnectionRefusedError):
                time.sleep(.05)
        assert daemon is not None
        # The argument's name deliberately has no socket: an ordinary attach
        # would create it. The inherited connection must ignore that path.
        fd = daemon.channel.fileno()
        daemon.channel.settimeout(None)
        second_env = dict(env, ZMX_ATTACH_FD=str(fd), ZMX_MANAGED="second")
        second, master = client(sys.argv[1], missing, second_env, fd=fd)
        output = b""
        deadline = time.monotonic() + 5
        while b"EXACT-FD-SENTINEL" not in output and time.monotonic() < deadline:
            if select.select([master], [], [], .2)[0]:
                output += os.read(master, 65536)
        assert b"EXACT-FD-SENTINEL" in output, output
        assert not (Path(root) / missing).exists(), "inherited attachment created a daemon"
        second.terminate()
        second.wait(timeout=5)
        assert first.poll() is None
        with ExactDaemon(str(Path(root) / name), sys.argv[1]) as check:
            assert check.identity == daemon.identity
        bad, _ = client(sys.argv[1], missing, dict(env, ZMX_ATTACH_FD="999999"))
        assert bad.wait(timeout=5) != 0
        assert not (Path(root) / missing).exists()
        print("PASS exact connected fd attaches without creation; detach preserves first client; bad fd refuses", flush=True)
    finally:
        if daemon:
            # Second client used this channel, so acquire a fresh verified one
            # only for fixture cleanup, still checking the original identity.
            with ExactDaemon(str(Path(root) / name), sys.argv[1]) as cleanup:
                cleanup.terminate(daemon.identity, owned=True)
            daemon.close()
        for child, master in clients:
            if child.poll() is None:
                child.terminate()
            child.wait(timeout=5)
            os.close(master)
