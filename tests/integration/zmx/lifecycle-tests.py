"""Routed real-daemon ledger/retry/lease checks in an isolated Linux namespace."""
import concurrent.futures
import base64
import copy
import json
import os
import pty
import fcntl
import signal
import struct
import termios
from pathlib import Path
import select
import shlex
import subprocess
import sys
import tempfile
import time
import uuid

helper = Path(__file__).resolve().parents[3] / "src/Agwinterm.Pty/WslLive/zmx_helper.py"
sys.path.insert(0, str(helper.parent))
from zmx_exact import ExactDaemon
from zmx_ledger import Ledger
clients = []


def start(request):
    child = subprocess.Popen([sys.executable, "-B", str(helper)], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    clients.append(child)
    child.stdin.write(json.dumps(request) + "\n")
    child.stdin.close()
    return child


def reply(child):
    if not select.select([child.stdout], [], [], 15)[0]:
        raise AssertionError("helper response timed out")
    result = json.loads(child.stdout.readline())
    assert result["ok"], result
    return result["result"]


def refused(request):
    child = start(request)
    assert select.select([child.stdout], [], [], 15)[0]
    result = json.loads(child.stdout.readline())
    assert not result["ok"], result
    assert child.wait(timeout=5) != 0


def attach_client(request, cols=80, rows=24):
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
    encoded = base64.b64encode(json.dumps(request).encode()).decode()
    child = subprocess.Popen([sys.executable, "-B", str(helper), "--request", encoded],
                             stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
    os.close(slave)
    clients.append(child)
    output = b""
    deadline = time.monotonic() + 5
    while b"zmx-role;" not in output and time.monotonic() < deadline:
        if select.select([master], [], [], .2)[0]:
            output += os.read(master, 65536)
    assert b"zmx-role;" in output, output
    return child, master


with tempfile.TemporaryDirectory(prefix="aw-life-") as directory:
    root = Path(directory)
    operation = str(uuid.uuid4())
    request = {"verb": "create", "ledger": str(root / "ledger"), "operation": operation,
               "specification": {"executable": sys.argv[1], "socketDirectory": str(root / "s"),
                                 "cwd": directory, "environment": {"ZMX_DIR": "/wrong"},
                                 "argv": [sys.executable, "-c",
                                          "from pathlib import Path; import time; "
                                          "p=Path('counter'); p.write_text(str(int(p.read_text())+1) if p.exists() else '1'); time.sleep(300)"]}}
    identity = None
    try:
        # The first response is intentionally left unread until retries complete.
        creator = start(request)
        racers = [creator] + [start(request) for _ in range(4)]
        with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
            replies = list(pool.map(reply, racers))
        winners = [(p, r) for p, r in zip(racers, replies) if r["state"] == "ready" and r["guard"]["pid"] == p.pid]
        assert len(winners) == 1, replies
        creator, original = winners[0]
        assert original["state"] == "ready", original
        identity = original["identity"]
        retried = reply(start(request))
        assert retried["identity"] == identity and retried["state"] == "ready", retried
        assert (root / "counter").read_text() == "1"
        assert all(r["name"] == original["name"] for r in replies)
        print("PASS concurrent retries and lost creation reply: one workload", identity, flush=True)
        creator.kill()
        creator.wait(timeout=5)
        reconciler = start({"verb": "reconcile", "ledger": request["ledger"], "operation": operation})
        recovered = reply(reconciler)
        assert recovered["identity"] == identity and recovered["guard"] != original["guard"]
        assert (root / "counter").read_text() == "1"
        print("PASS helper restart acquires lease without executing command", flush=True)
        screen = reply(start({"verb": "screen", "executable": sys.argv[1], "socketDirectory": str(root / "s"),
                              "name": original["name"], "identity": identity, "all": False}))
        assert screen["columns"] > 0 and screen["rows"] > 0 and screen["cursorColumn"] < screen["columns"]
        external_operation = str(uuid.uuid4())
        borrowed = {"verb": "borrow", "ledger": request["ledger"], "operation": external_operation,
                    "executable": sys.argv[1], "socketDirectory": str(root / "s"),
                    "name": original["name"], "identity": identity}
        external = reply(start(borrowed))
        duplicate_external = reply(start(borrowed))
        assert external["identity"] == identity and duplicate_external["guard"] == external["guard"]
        refused({"verb": "terminate", "ledger": request["ledger"], "operation": external_operation, "identity": identity})
        refused({"verb": "restart", "ledger": request["ledger"], "operation": external_operation,
                 "newOperation": str(uuid.uuid4()), "identity": identity, "source": "shell", "command": None})
        print("PASS borrowed binding shares a lease on retries and never grants termination/restart authority", flush=True)
        listed = reply(start({"verb": "inventory", "executable": sys.argv[1], "socketDirectory": str(root / "s")}))
        assert len(listed["sessions"]) == 1 and listed["sessions"][0]["identity"] == identity
        attachment = {"verb": "attach", "executable": sys.argv[1], "socketDirectory": str(root / "s"),
                      "name": original["name"], "identity": identity, "nonce": "test-nonce"}
        refused({"verb": "restart", "ledger": request["ledger"], "operation": operation,
                 "newOperation": str(uuid.uuid4()), "identity": identity, "source": "shell", "command": None})
        screen_request = {"verb": "screen", "executable": sys.argv[1], "socketDirectory": str(root / "s"),
                          "name": original["name"], "identity": identity, "all": False}
        def geometry():
            state = reply(start(screen_request))
            return state["columns"], state["rows"]
        def await_geometry(expected):
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline:
                if geometry() == expected:
                    return
                time.sleep(.05)
            assert geometry() == expected
        viewer1, master1 = attach_client(dict(attachment, claim=True), 91, 31)
        await_geometry((91, 31))
        viewer2, master2 = attach_client(dict(attachment, nonce="second-nonce"), 61, 19)
        assert geometry() == (91, 31), "follower attachment stole geometry"
        fcntl.ioctl(master2, termios.TIOCSWINSZ, struct.pack("HHHH", 22, 67, 0, 0))
        viewer2.send_signal(signal.SIGWINCH)
        time.sleep(.1)
        assert geometry() == (91, 31), "follower resize stole geometry"
        viewer2.terminate()
        viewer2.wait(timeout=5)
        os.close(master2)
        viewer2, master2 = attach_client(dict(attachment, nonce="reattached-nonce"), 67, 22)
        assert geometry() == (91, 31), "automatic reattach stole geometry"
        print("PASS differently sized follower, resize, queries and automatic reattach preserve leader geometry", flush=True)
        viewer1.terminate()
        viewer1.wait(timeout=5)
        os.close(master1)
        assert viewer2.poll() is None and (root / "counter").read_text() == "1"
        with ExactDaemon(identity["path"], sys.argv[1]) as check:
            assert check.identity == identity
        viewer2.terminate()
        viewer2.wait(timeout=5)
        os.close(master2)
        print("PASS helper exact attachment and attachment-only detach preserve other client/workload", flush=True)
        stale = copy.deepcopy(attachment)
        stale["identity"]["workload"]["start"] = "0"
        refused(stale)
        refused(dict(attachment, name="../../wrong"))
        refused(dict(attachment, nonce="bad\nnonce"))
        refused(dict(attachment, unknown=True))
        # Simulate daemon exit after the durable terminating write, before its
        # confirmation is committed: retry must reconcile the exact incarnation.
        with Ledger(request["ledger"]) as ledger:
            record = ledger.read(operation)
            record["state"] = "terminating"
            ledger.write(record)
        with ExactDaemon(identity["path"], sys.argv[1]) as daemon:
            assert daemon.terminate(identity, owned=True, lose_reply=True) == "terminated"
        terminated = reply(start({"verb": "terminate", "ledger": request["ledger"],
                                  "operation": operation, "identity": identity}))
        assert terminated["state"] == "terminated", terminated
        reconciler.wait(timeout=5)
        again = reply(start(request))
        assert again["state"] == "terminated" and (root / "counter").read_text() == "1"
        refused(attachment)
        listed = reply(start({"verb": "inventory", "executable": sys.argv[1], "socketDirectory": str(root / "s")}))
        assert listed["sessions"] == [] and (root / "counter").read_text() == "1"
        print("PASS inventory is read-only; stale/malformed/missing attachment refuses; lost termination reply reconciles", flush=True)
        print("PASS termination releases lease; repeated create never resurrects", flush=True)
        for source in ("shell", "agent", "pin", "captured"):
            restart_request = {"verb": "restart", "ledger": request["ledger"], "operation": operation,
                               "newOperation": str(uuid.uuid4()), "identity": identity, "source": source,
                               "command": None if source == "shell" else shlex.quote(sys.executable) + " -c " + shlex.quote(
                                   "from pathlib import Path; import time; Path('" + source + "-once').open('x').write('1'); time.sleep(300)")}
            restarted = reply(start(restart_request))
            try:
                assert restarted["state"] == "ready" and restarted["identity"] != identity
                again = reply(start(restart_request))
                assert again["identity"] == restarted["identity"]
                if source == "shell":
                    assert (root / "counter").read_text() == "2"
                else:
                    assert (root / (source + "-once")).read_text() == "1"
                    assert (root / "counter").read_text() == "2", "original command replayed alongside selected source"
            finally:
                reply(start({"verb": "terminate", "ledger": request["ledger"],
                             "operation": restart_request["newOperation"], "identity": restarted["identity"]}))
            assert reply(start(restart_request))["state"] == "terminated"
        print("PASS live restart refuses; four explicit sources each run once with new identity; retries never relaunch", flush=True)
        lost = copy.deepcopy(request)
        lost["operation"] = str(uuid.uuid4())
        lost["specification"]["argv"] = [sys.executable, "-c", "import time; time.sleep(300)"]
        abandoned = start(lost)
        abandoned.stdout.close()  # actual EPIPE at the first response, not merely unread data
        lost_record = None
        try:
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                with Ledger(request["ledger"]) as ledger:
                    lost_record = ledger.read(lost["operation"])
                if lost_record and lost_record["state"] == "ready":
                    break
                time.sleep(.05)
            assert lost_record and lost_record["state"] == "ready", lost_record
            time.sleep(.1)
            assert abandoned.poll() is None, "lost response dropped the WSL lease"
            recovered = reply(start({"verb": "reconcile", "ledger": request["ledger"], "operation": lost["operation"]}))
            assert recovered["identity"] == lost_record["identity"] and recovered["guard"]["pid"] == abandoned.pid
            print("PASS broken response pipe retains committed lease and reconciles same workload", flush=True)
        finally:
            if lost_record and lost_record.get("identity"):
                reply(start({"verb": "terminate", "ledger": request["ledger"],
                             "operation": lost["operation"], "identity": lost_record["identity"]}))
    finally:
        if identity:
            reply(start({"verb": "terminate", "ledger": request["ledger"],
                         "operation": operation, "identity": identity}))
        for child in clients:
            if child.poll() is None:
                child.kill()
            child.wait(timeout=5)
