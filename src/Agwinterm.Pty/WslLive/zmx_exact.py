"""Pinned x86_64 Linux ZMX 0.8.1 IPC identity/termination prototype.

Never reconnect between verification and mutation. The connected Unix socket
is the exact daemon handle; the pidfd confirms exit even if the reply is lost.
"""
import os
from pathlib import Path
import select
import socket
import stat
import struct


def process_identity(pid):
    value = Path(f"/proc/{pid}/stat").read_text()
    fields = value[value.rfind(")") + 2:].split()
    if fields[0] == "Z":
        raise ProcessLookupError(pid)
    return {"pid": pid, "start": fields[19],
            "boot": Path("/proc/sys/kernel/random/boot_id").read_text().strip(),
            "namespace": os.readlink(f"/proc/{pid}/ns/pid")}, int(fields[1])


class ExactDaemon:
    def __init__(self, path, executable):
        self.channel = None
        self.pidfd = None
        try:
            before = os.stat(path, follow_symlinks=False)
            if not stat.S_ISSOCK(before.st_mode) or before.st_uid != os.getuid():
                raise ValueError("not a socket owned by the selected Linux user")
            self.channel = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            self.channel.settimeout(3)
            self.channel.connect(path)
            workload, daemon = self.info()
            self.pidfd = os.pidfd_open(daemon["pid"])
            if os.path.realpath(f'/proc/{daemon["pid"]}/exe') != os.path.realpath(executable):
                raise ValueError("unexpected daemon executable")
            if self.info() != (workload, daemon):
                raise ValueError("identity changed while pinning daemon")
            after = os.stat(path, follow_symlinks=False)
            if (before.st_dev, before.st_ino) != (after.st_dev, after.st_ino):
                raise ValueError("socket replaced during acquisition")
            self.identity = {"workload": workload, "daemon": daemon,
                             "socket": [before.st_dev, before.st_ino], "path": path}
        except BaseException:
            self.close()
            raise

    def send(self, tag, payload=b""):
        self.channel.sendall(struct.pack("<BI3x", tag, len(payload)) + payload)

    def receive(self, count):
        result = bytearray()
        while len(result) < count:
            chunk = self.channel.recv(count - len(result))
            if not chunk:
                raise EOFError("daemon disconnected")
            result.extend(chunk)
        return bytes(result)

    def info(self):
        self.send(6)
        tag, length = struct.unpack("<BI3x", self.receive(8))
        if tag != 6 or length != 552:
            raise ValueError("unsupported Info ABI")
        payload = self.receive(length)
        self.clients = struct.unpack_from("<Q", payload, 0)[0]
        pid = struct.unpack_from("<i", payload, 8)[0]
        workload, parent = process_identity(pid)
        daemon, _ = process_identity(parent)
        return workload, daemon

    def terminate(self, expected, owned, lose_reply=False):
        if not owned or self.identity != expected:
            raise ValueError("external or stale binding")
        if self.info() != (expected["workload"], expected["daemon"]):
            raise ValueError("daemon changed before mutation")
        # This connected channel cannot be redirected by a new socket at path.
        self.send(5)
        if lose_reply:
            self.channel.close()
            self.channel = None
        poll = select.poll()
        poll.register(self.pidfd, select.POLLIN)
        return "terminated" if poll.poll(5000) else "uncertain"

    def close(self):
        if self.channel is not None:
            self.channel.close()
            self.channel = None
        if self.pidfd is not None:
            os.close(self.pidfd)
            self.pidfd = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
