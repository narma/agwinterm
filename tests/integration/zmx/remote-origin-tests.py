"""Isolated origin acceptance. Python is test tooling, never a runtime dependency."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import pty
import select
import socket
import struct
import subprocess
import sys
import tempfile
import termios
import time
import uuid

prefix = Path(sys.argv[1]).resolve()
client = prefix / 'bin/zmx-existing-client'
bridge = prefix / 'bin/agwinterm-remote'
children = []

def launch(argv, env, fd=None):
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
    child = subprocess.Popen(argv, stdin=slave, stdout=slave, stderr=slave, env=env,
        pass_fds=() if fd is None else (fd,), start_new_session=True)
    os.close(slave)
    children.append((child, master))
    return child, master

def request(value, success=True):
    result = subprocess.run([str(bridge)], input=json.dumps(value), text=True, capture_output=True, timeout=10)
    data = json.loads(result.stdout)
    assert data['ok'] == success, (result.returncode, data)
    return data.get('result')

def wait_text(master, expected):
    output = b''
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline and expected not in output:
        if select.select([master], [], [], .2)[0]:
            try: output += os.read(master, 65536)
            except OSError: break
    assert expected in output, output[-4000:]

def connect(path):
    channel = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    channel.settimeout(3)
    channel.connect(str(path))
    return channel

with tempfile.TemporaryDirectory(prefix='aw-rem-', dir='/tmp') as directory:
    root = Path(directory)
    name = 'agterm-' + uuid.uuid4().hex
    missing = 'agterm-' + uuid.uuid4().hex
    env = {k:v for k,v in os.environ.items() if not k.startswith('ZMX_')}
    env.update(ZMX_DIR=directory, ZMX_MANAGED='origin', TERM='xterm-256color')
    try:
        first, master = launch([str(client), 'attach', name, '/bin/sh', '-c',
            "printf 'REMOTE-FD-SENTINEL\\n'; exec /bin/cat"], env)
        wait_text(master, b'zmx-role;origin:leader:')
        os.write(master, b'ORIGIN-READY\n')
        wait_text(master, b'ORIGIN-READY')
        with connect(root/name) as channel:
            fd = channel.fileno()
            second, second_master = launch([str(client), 'attach', missing],
                dict(env, ZMX_ATTACH_FD=str(fd), ZMX_MANAGED='viewer'), fd)
            wait_text(second_master, b'REMOTE-FD-SENTINEL')
            assert not (root/missing).exists()
            second.terminate(); second.wait(timeout=5)
        bad, _ = launch([str(client), 'attach', missing], dict(env, ZMX_ATTACH_FD='999999'))
        assert bad.wait(timeout=5) != 0 and not (root/missing).exists()
        # Publish only after no-create behavior passes on this exact client binary.
        digest = hashlib.sha256(client.read_bytes()).hexdigest()
        client.with_suffix('.sha256').write_text(digest + '\n')
        base = dict(executable=str(client), socketDirectory=directory, name=name)
        probe = request(dict(base, verb='probe'))
        identity = probe['identity']
        assert identity['platform'] == 'macos'
        bound = dict(base, identity=identity, clientSha256=digest)
        assert 'REMOTE-FD-SENTINEL' in request(dict(bound, verb='screen', all=False))['text']
        assert request(dict(bound, verb='type', text='EXACT-INPUT\n'))['accepted']
        time.sleep(.2)
        assert 'EXACT-INPUT' in request(dict(bound, verb='screen', all=False))['text']
        assert request(dict(base, verb='probe'))['identity'] == identity
        stale = json.loads(json.dumps(identity)); stale['daemon']['startMicros'] += 1
        request(dict(bound, verb='screen', all=False, identity=stale), success=False)
        request(dict(bound, verb='terminate'), success=False)
        request(dict(base, verb='probe', name=missing), success=False)
        assert not (root/missing).exists()
        old_path = root/name
        renamed = root/(name+'.old')
        old_path.rename(renamed)
        replacement, replacement_master = launch([str(client), 'attach', name, '/bin/sh', '-c',
            "printf 'REPLACEMENT\\n'; exec /bin/cat"], env)
        wait_text(replacement_master, b'zmx-role;origin:leader:')
        assert 'REPLACEMENT' in subprocess.run([str(client), 'screen', name], env=env,
            capture_output=True, text=True, timeout=5).stdout
        request(dict(bound, verb='screen', all=False), success=False)
        print('PASS macOS identity, inherited FD no-create, input/screen, borrowed detach, replacement refusal')
    finally:
        # Only sockets created in this private test directory are eligible for cleanup.
        for path in (root/name, root/(name+'.old')):
            if path.exists():
                try:
                    with connect(path) as channel: channel.sendall(struct.pack('<BI3x',5,0))
                except OSError: pass
        for child, master in children:
            if child.poll() is None: child.terminate()
            child.wait(timeout=5)
            os.close(master)
