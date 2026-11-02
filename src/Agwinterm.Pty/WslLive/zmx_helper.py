"""Bounded local lifecycle requests plus an explicit foreground lifetime lease.

One JSON request on stdin, one JSON result on stdout. A successful create may
remain alive as a WSL lifetime lease after sending its result. Never retry a
launching operation by spawning again. The private ledger is its authority.
"""
import fcntl
import base64
import json
import os
from pathlib import Path
import pty
import re
import select
import struct
import subprocess
import sys
import termios
import time

from zmx_exact import ExactDaemon, process_identity
from zmx_ledger import Ledger
from zmx_readiness import probe


class RecoveryError(Exception):
    def __init__(self, state, message):
        super().__init__(message)
        self.state = state


def require_dependency(executable):
    result = probe(executable)
    if result["state"] != "ready":
        raise RecoveryError("unsupported" if result["state"] == "unsupported" else "unavailable",
                            result.get("reason") or ("Missing ZMX capabilities: " + ", ".join(result["missingCapabilities"])
                                                     if result.get("missingCapabilities") else "ZMX dependency is not ready"))


def clean_environment(values):
    if not isinstance(values, dict) or len(values) > 256:
        raise ValueError("invalid environment")
    result = {k: v for k, v in os.environ.items() if not k.startswith("ZMX_") and k != "WSLENV"}
    for key, value in values.items():
        if not isinstance(key, str) or not key or "=" in key or "\0" in key or not isinstance(value, str) or "\0" in value:
            raise ValueError("invalid environment entry")
        if not key.startswith("ZMX_") and key != "WSLENV":
            result[key] = value
    return result


def validate_spec(spec):
    if set(spec) - {"shellArgv"} != {"executable", "socketDirectory", "cwd", "argv", "environment"}:
        raise ValueError("unknown or missing launch fields")
    for key in ("executable", "socketDirectory", "cwd"):
        value = spec[key]
        if not isinstance(value, str) or not value.startswith("/") or any(ord(c) < 32 for c in value) or ".." in Path(value).parts:
            raise ValueError("invalid Linux path: " + key)
    argv = spec["argv"]
    if not isinstance(argv, list) or not argv or len(argv) > 256 or not all(isinstance(a, str) and "\0" not in a for a in argv) or not argv[0]:
        raise ValueError("invalid workload argv")
    shell = spec.get("shellArgv", argv)
    if not isinstance(shell, list) or not shell or len(shell) > 256 or not all(isinstance(a, str) and "\0" not in a for a in shell) or not shell[0]:
        raise ValueError("invalid original shell argv")
    clean_environment(spec["environment"])
    require_dependency(spec["executable"])
    if spec["socketDirectory"].startswith("/mnt/") or len(spec["socketDirectory"].encode()) + 40 >= 108:
        raise ValueError("unsupported Linux socket directory")
    # Reuse private-directory validation, but do not give it ledger authority.
    Ledger(spec["socketDirectory"])


def public(record):
    return {key: record[key] for key in ("operation", "name", "state", "identity", "guard", "error", "rollback") if key in record}


def observe(record):
    spec = record["specification"]
    with ExactDaemon(str(Path(spec["socketDirectory"]) / record["name"]), spec["executable"]) as daemon:
        if record.get("identity") is not None and daemon.identity != record["identity"]:
            raise ValueError("daemon incarnation replaced")
        return daemon.identity


def marker_path(ledger, operation):
    return ledger.path(operation).with_suffix(".started")


def workload(directory, operation):
    with Ledger(directory) as ledger:
        record = ledger.read(operation)
        if record is None or record["state"] != "launching":
            raise ValueError("workload launch lacks a reserved operation")
        # Record before exec. O_EXCL prevents duplicate command execution forever,
        # including after workload death or a lost creation response.
        fd = os.open(marker_path(ledger, operation), os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "w") as stream:
            identity, _ = process_identity(os.getpid())
            json.dump(identity, stream)
            stream.flush()
            os.fsync(stream.fileno())
        spec = record["specification"]
    os.chdir(spec["cwd"])
    os.execvpe(spec["argv"][0], spec["argv"], clean_environment(spec["environment"]))


def create(request):
    spec = request["specification"]
    validate_spec(spec)
    operation = request["operation"]
    directory = request["ledger"]
    # Do not hold the lock while the trusted workload wrapper acquires it.
    with Ledger(directory) as ledger:
        record, fresh = ledger.reserve(operation, spec)
        if fresh:
            record["state"] = "launching"
            record["creator"] = process_identity(os.getpid())[0]
            ledger.write(record)
    if not fresh:
        return reconcile(directory, operation)
    master, slave = pty.openpty()
    child = None
    try:
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
        env = clean_environment(spec["environment"])
        env.update(ZMX_DIR=spec["socketDirectory"], ZMX_MANAGED="creator", ZMX_NO_DETACH_KEY="1", TERM="xterm-256color")
        child = subprocess.Popen([spec["executable"], "attach", record["name"], sys.executable, "-B",
                                  str(Path(__file__).resolve()), "--workload", directory, operation],
                                 env=env, stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
        os.close(slave)
        slave = None
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            try:
                with Ledger(directory) as ledger:
                    current = ledger.read(operation)
                    marker = marker_path(ledger, operation)
                    if not marker.exists():
                        raise FileNotFoundError("creation not yet acknowledged")
                    identity = observe(current)
                    if json.loads(marker.read_text()) != identity["workload"]:
                        raise ValueError("creation marker does not match workload")
                    fd = pin_workload(identity["workload"])
                    current.update(state="ready", identity=identity)
                    guard, _ = process_identity(os.getpid())
                    current["guard"] = guard
                    try:
                        ledger.write(current)
                    except BaseException:
                        os.close(fd)
                        raise
                    return current, fd
            except (FileNotFoundError, ConnectionRefusedError, ProcessLookupError):
                time.sleep(.05)
        raise TimeoutError("creation is uncertain; reconcile the same operation")
    finally:
        if slave is not None:
            os.close(slave)
        os.close(master)
        if child and child.poll() is None:
            child.terminate()  # attachment only; daemon is independently double-forked
            try:
                child.wait(timeout=2)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=2)


def pin_workload(expected):
    fd = os.pidfd_open(expected["pid"])
    try:
        if process_identity(expected["pid"])[0] != expected:
            raise ValueError("stale workload")
        return fd
    except BaseException:
        os.close(fd)
        raise


def reconcile(directory, operation):
    with Ledger(directory) as ledger:
        record = ledger.read(operation)
        if record is None:
            raise ValueError("unknown operation; reconcile never creates")
        if record["state"] == "terminated":
            return record, None
        if record.get("identity") and incarnation_gone(record["identity"]["workload"]):
            record["state"] = "missing"
            ledger.write(record)
            return record, None
        require_dependency(record["specification"]["executable"])
        # A concurrent retry must not change the launch state before the wrapper
        # commits its one-shot marker. Only the live creator finishes this phase.
        creator = record.get("creator")
        if record["state"] == "launching" and creator:
            try:
                if process_identity(creator["pid"])[0] == creator:
                    return record, None
            except (OSError, ProcessLookupError):
                pass
        try:
            identity = observe(record)
            marker = marker_path(ledger, operation)
            if not record.get("external") and (not marker.exists() or json.loads(marker.read_text()) != identity["workload"]):
                raise ValueError("ownership evidence unavailable")
            guard = record.get("guard")
            try:
                healthy = guard is not None and process_identity(guard["pid"])[0] == guard
            except (OSError, ProcessLookupError):
                healthy = False
            fd = None if healthy else pin_workload(identity["workload"])
            record.update(state="ready", identity=identity)
            if not healthy:
                record["guard"] = process_identity(os.getpid())[0]
            try:
                ledger.write(record)
            except BaseException:
                if fd is not None:
                    os.close(fd)
                raise
            return record, fd
        except (FileNotFoundError, ConnectionRefusedError, ProcessLookupError):
            # A launching operation without identity may have executed and lost all
            # replies. It remains uncertain, not a fresh-create permission.
            record["state"] = "missing" if record.get("identity") and incarnation_gone(record["identity"]["workload"]) else "uncertain"
            ledger.write(record)
            return record, None


def incarnation_gone(expected):
    try:
        return process_identity(expected["pid"])[0] != expected
    except (FileNotFoundError, ProcessLookupError):
        return True


def restart(request):
    source = request["source"]
    command = request["command"]
    if source not in ("shell", "agent", "pin", "captured"):
        raise ValueError("unknown fresh-start source")
    if source == "shell":
        if command is not None:
            raise ValueError("shell restart does not accept a saved command")
    elif not isinstance(command, str) or not command.strip() or "\0" in command or len(command.encode()) > 32768:
        raise ValueError("selected launch source is unavailable or invalid")
    with Ledger(request["ledger"]) as ledger:
        previous = ledger.read(request["operation"])
        if previous is None or previous.get("external") or previous.get("identity") != request["identity"] or request["identity"] is None:
            raise ValueError("restart requires exact owned origin evidence")
        if not incarnation_gone(previous["identity"]["workload"]):
            raise ValueError("workload loss is not confirmed")
        if request["operation"] == request["newOperation"]:
            raise ValueError("fresh start requires a new operation ID")
        specification = json.loads(json.dumps(previous["specification"]))
        specification["shellArgv"] = specification.get("shellArgv", specification["argv"])
        specification["argv"] = list(specification["shellArgv"])
        if source != "shell":
            original = specification["argv"]
            # A chosen command replaces the creation command. It is exec'd once
            # by the ledger wrapper, never injected into a recovered terminal.
            if Path(original[0]).name in ("bash", "sh", "dash", "zsh", "ksh", "fish"):
                flags = []
                value_pending = False
                for arg in original[1:]:
                    if value_pending:
                        flags.append(arg)
                        value_pending = False
                        continue
                    if arg == "-c":
                        break
                    if arg.startswith("-") and not arg.startswith("--") and "c" in arg[1:]:
                        remaining = arg.replace("c", "")
                        if remaining != "-":
                            flags.append(remaining)
                        break
                    if not arg.startswith("-"):
                        break
                    flags.append(arg)
                    value_pending = arg in ("--rcfile", "--init-file", "-O", "-o")
                specification["argv"] = [original[0], *flags, "-c", command]
            else:
                specification["argv"] = ["/bin/sh", "-c", 'exec "${SHELL:-/bin/sh}" -lc "$1"', "agwinterm-restart", command]
    return create({"ledger": request["ledger"], "operation": request["newOperation"], "specification": specification})


def reconcile_restart(request):
    # The Windows checkpoint precedes the Linux request. If nothing was reserved,
    # atomically fence that operation before restoring its predecessor. A delayed
    # restart request must not create work after rollback or a subsequent restart.
    with Ledger(request["ledger"]) as ledger:
        previous = ledger.read(request["operation"])
        if previous is None or previous.get("external") or previous.get("identity") != request["identity"]:
            raise ValueError("restart recovery requires exact owned origin evidence")
        if request["operation"] == request["newOperation"]:
            raise ValueError("restart recovery requires distinct operations")
        pending = ledger.read(request["newOperation"])
        rollback = pending is None or pending.get("state") == "cancelled"
        if pending is None:
            ledger.write({"version": 1, "operation": request["newOperation"], "state": "cancelled"})
    result, fd = reconcile(request["ledger"], request["operation"] if rollback else request["newOperation"])
    return dict(result, rollback=rollback), fd


def terminate(request):
    with Ledger(request["ledger"]) as ledger:
        record = ledger.read(request["operation"])
        if record is None or record.get("external") or record.get("identity") != request["identity"]:
            raise ValueError("no exact owned operation")
        if record["state"] == "terminated":
            return record, None
        # A stopped distribution removes sockets as well as processes. Prove
        # both recorded incarnations are gone before treating this as success;
        # a missing socket alone still grants no termination confirmation.
        if incarnation_gone(record["identity"]["daemon"]) and incarnation_gone(record["identity"]["workload"]):
            record["state"] = "terminated"
            ledger.write(record)
            return record, None
        # Reconcile a lost kill reply against the recorded process incarnation,
        # never against whichever daemon currently occupies the old name.
        if record["state"] == "terminating":
            expected = record["identity"]["daemon"]
            try:
                gone = process_identity(expected["pid"])[0] != expected
            except (FileNotFoundError, ProcessLookupError):
                gone = True
            if gone:
                record["state"] = "terminated"
                ledger.write(record)
                return record, None
        spec = record["specification"]
        with ExactDaemon(str(Path(spec["socketDirectory"]) / record["name"]), spec["executable"]) as daemon:
            record["state"] = "terminating"
            ledger.write(record)
            record["state"] = daemon.terminate(request["identity"], owned=True)
            ledger.write(record)
            return record, None


def validate_endpoint(request):
    executable = request["executable"]
    directory = request["socketDirectory"]
    if not isinstance(executable, str):
        raise ValueError("invalid executable")
    require_dependency(executable)
    if not isinstance(directory, str) or not directory.startswith("/") or ".." in Path(directory).parts or directory.startswith("/mnt/"):
        raise ValueError("invalid socket directory")
    info = Path(directory).lstat()
    import stat
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise ValueError("socket directory is not private")
    return executable, directory


def inventory(request):
    executable, directory = validate_endpoint(request)
    result = []
    owned = {}
    deadline = time.monotonic() + 10
    entries = []
    for index, entry in enumerate(Path(directory).iterdir()):
        if index >= 1024 or time.monotonic() >= deadline:
            raise ValueError("inventory exceeds endpoint limit")
        if re.fullmatch(r"agterm-[0-9a-f]{32}", entry.name):
            entries.append(entry)
    if request.get("ledger") and entries:
        with Ledger(request["ledger"]) as ledger:
            unresolved = set()
            for entry in entries:
                operation = ledger.owner_operation(entry.name)
                if operation is None:
                    unresolved.add(entry.name)
                    continue
                record = ledger.read(operation)
                if record is None or record.get("name") != entry.name or record.get("external"):
                    raise ValueError("invalid daemon owner index")
                owned[entry.name] = record
            # Compatibility with pre-index development ledgers. Scan lazily only
            # for current sockets; historical count is never an inventory limit.
            for path in ledger.root.glob("*.json") if unresolved else ():
                if not unresolved:
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError("legacy ownership lookup timed out")
                record = ledger.read(path.stem)
                if record and not record.get("external") and record.get("name") in unresolved:
                    owned[record["name"]] = record
                    ledger.index_owner(record)
                    unresolved.remove(record["name"])
    for entry in entries:
        if len(result) >= 64 or time.monotonic() >= deadline:
            raise ValueError("inventory exceeds endpoint limit")
        try:
            with ExactDaemon(str(entry), executable) as daemon:
                owner = owned.get(entry.name)
                proven = owner is not None and owner.get("identity") == daemon.identity
                result.append({"name": entry.name, "identity": daemon.identity, "attachments": daemon.clients,
                               "operation": owner["operation"] if proven else None,
                               "originPane": owner["specification"].get("environment", {}).get("AGWINTERM_SESSION_ID") if proven else None})
        except (OSError, ValueError, EOFError):
            continue
    return {"state": "ready", "sessions": result}


def borrow(request):
    executable, directory = validate_endpoint(request)
    name = request["name"]
    if not isinstance(name, str) or not re.fullmatch(r"agterm-[0-9a-f]{32}", name):
        raise ValueError("invalid daemon name")
    with ExactDaemon(str(Path(directory) / name), executable) as daemon:
        if daemon.identity != request["identity"]:
            raise ValueError("external incarnation changed")
        with Ledger(request["ledger"]) as ledger:
            record, fresh = ledger.reserve(request["operation"], {"executable": executable, "socketDirectory": directory})
            if fresh:
                record.update(name=name, external=True, state="ready", identity=daemon.identity)
                ledger.write(record)
            elif not record.get("external") or record.get("identity") != daemon.identity:
                raise ValueError("external binding operation reused")
    return reconcile(request["ledger"], request["operation"])


def attach(request):
    executable, directory = validate_endpoint(request)
    name, nonce = request["name"], request["nonce"]
    if not isinstance(name, str) or not re.fullmatch(r"agterm-[0-9a-f]{32}", name):
        raise ValueError("invalid daemon name")
    if not isinstance(nonce, str) or not re.fullmatch(r"[A-Za-z0-9-]{1,64}", nonce):
        raise ValueError("invalid attachment nonce")
    with ExactDaemon(str(Path(directory) / name), executable) as daemon:
        if daemon.identity != request["identity"]:
            raise ValueError("daemon incarnation replaced")
        fd = daemon.channel.fileno()
        daemon.channel.settimeout(None)
        os.set_inheritable(fd, True)
        env = clean_environment({})
        env.update(ZMX_DIR=directory, ZMX_ATTACH_FD=str(fd), ZMX_MANAGED=nonce, ZMX_NO_DETACH_KEY="1")
        if request.get("claim", False) is True:
            env["ZMX_MANAGED_CLAIM"] = "1"
        elif request.get("claim", False) is not False:
            raise ValueError("claim must be boolean")
        os.execve(executable, [executable, "attach", name], env)


def terminal(request):
    executable, directory = validate_endpoint(request)
    name = request["name"]
    if not isinstance(name, str) or not re.fullmatch(r"agterm-[0-9a-f]{32}", name):
        raise ValueError("invalid daemon name")
    with ExactDaemon(str(Path(directory) / name), executable) as daemon:
        if daemon.identity != request["identity"]:
            raise ValueError("terminal incarnation changed")
        if request["verb"] == "type":
            text = request["text"]
            if not isinstance(text, str) or not text or len(text.encode()) > 32768:
                raise ValueError("input must contain 1..32768 UTF-8 bytes")
            daemon.send(25, text.encode())
            tag, length = struct.unpack("<BI3x", daemon.receive(8))
            if tag != 25 or length != 1:
                raise RecoveryError("uncertain", "input acknowledgement was not received; do not resend automatically")
            accepted = daemon.receive(1) == b"\0"
            if not accepted:
                raise RecoveryError("unavailable", "daemon rejected input; nothing was queued")
            return {"accepted": True, "executionComplete": False}
        if type(request["all"]) is not bool:
            raise ValueError("screen mode must be boolean")
        daemon.send(24, bytes([int(request["all"])]))
        tag, length = struct.unpack("<BI3x", daemon.receive(8))
        if tag != 24 or length < 24 or length > 1048576:
            raise ValueError("unsupported or oversized daemon screen response")
        payload = daemon.receive(length)
        revision, cols, rows, x, y, alternate = struct.unpack_from("<QHHHHB", payload)
        if not cols or not rows or x >= cols or y >= rows or alternate > 1:
            raise ValueError("malformed daemon geometry")
        return {"revision": revision, "columns": cols, "rows": rows, "cursorColumn": x,
                "cursorRow": y, "alternate": bool(alternate), "text": payload[24:].decode("utf-8")}


def main():
    if len(sys.argv) == 4 and sys.argv[1] == "--workload":
        workload(sys.argv[2], sys.argv[3])
        return
    if len(sys.argv) == 3 and sys.argv[1] == "--request" and len(sys.argv[2]) <= 87384:
        raw = base64.b64decode(sys.argv[2], validate=True) + b"\n"
    elif len(sys.argv) == 1:
        raw = sys.stdin.buffer.readline(65537)
    else:
        raise ValueError("invalid helper arguments")
    if len(raw) > 65536 or not raw.endswith(b"\n"):
        raise ValueError("request too large or incomplete")
    request = json.loads(raw)
    verb = request.get("verb")
    allowed = {"create": {"verb", "ledger", "operation", "specification"},
               "reconcile": {"verb", "ledger", "operation"},
               "terminate": {"verb", "ledger", "operation", "identity"},
               "restart": {"verb", "ledger", "operation", "newOperation", "identity", "source", "command"},
               "reconcile-restart": {"verb", "ledger", "operation", "newOperation", "identity"},
               "inventory": {"verb", "executable", "socketDirectory", "ledger"},
               "borrow": {"verb", "executable", "socketDirectory", "name", "identity", "ledger", "operation"},
               "screen": {"verb", "executable", "socketDirectory", "name", "identity", "all"},
               "type": {"verb", "executable", "socketDirectory", "name", "identity", "text"},
               "attach": {"verb", "executable", "socketDirectory", "name", "identity", "nonce", "claim"}}
    if verb == "inventory" and "ledger" not in request:
        allowed[verb] = allowed[verb] - {"ledger"}
    if verb == "attach" and "claim" not in request:
        allowed[verb] = allowed[verb] - {"claim"}
    if verb not in allowed or set(request) != allowed[verb]:
        raise ValueError("unknown operation or fields")
    if verb == "attach":
        attach(request)
        return
    if verb == "inventory":
        print(json.dumps({"ok": True, "result": inventory(request)}), flush=True)
        return
    if verb in ("screen", "type"):
        print(json.dumps({"ok": True, "result": terminal(request)}, ensure_ascii=False), flush=True)
        return
    if verb == "create":
        record, fd = create(request)
    elif verb == "borrow":
        record, fd = borrow(request)
    elif verb == "restart":
        record, fd = restart(request)
    elif verb == "reconcile-restart":
        record, fd = reconcile_restart(request)
    elif verb == "reconcile":
        record, fd = reconcile(request["ledger"], request["operation"])
    else:
        record, fd = terminate(request)
    try:
        try:
            print(json.dumps({"ok": True, "result": public(record)}), flush=True)
        except BrokenPipeError:
            # A lost caller/reply must not drop an already committed lease.
            # Reconciliation finds this helper by its recorded incarnation.
            if fd is None:
                raise
            null = os.open(os.devnull, os.O_WRONLY)
            os.dup2(null, sys.stdout.fileno())
            os.close(null)
        if fd is not None:
            poll = select.poll()
            poll.register(fd, select.POLLIN)
            poll.poll()  # explicit lifetime lease, not a request still awaiting reply
    finally:
        if fd is not None:
            os.close(fd)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        state = error.state if isinstance(error, RecoveryError) else "unavailable" if isinstance(error, PermissionError) else "uncertain"
        print(json.dumps({"ok": False, "state": state, "error": str(error)}), flush=True)
        sys.exit(1)
