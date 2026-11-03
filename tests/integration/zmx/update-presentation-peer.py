"""Update only a verified isolated Mac fixture; retain its previous bundle for legacy tests."""
import hashlib
import json
import os
from pathlib import Path
import signal
import socket
import struct
import subprocess
import sys
import time

root, source = map(Path, sys.argv[1:])
assert root.parent == Path('/tmp') and root.name.startswith('aw-stage4-peer-')
assert str(source.resolve()).startswith('/private/tmp/aw-stage4-build-') and source.name == 'agterm.app'
bundle = root/'agterm.app'
backup = root/'agterm-legacy-20261103.app'
assert not backup.exists() or (root/'revision-build.sha256.json').exists(), 'unrecognized existing backup'
control = str(root/'control.sock')
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
    channel.connect(control)
    pid = struct.unpack('i', channel.getsockopt(0, 2, 4))[0]
executable = subprocess.check_output(['ps', '-p', str(pid), '-o', 'comm='], text=True).strip()
assert Path(executable).resolve() == (bundle/'Contents/MacOS/agterm').resolve()
subprocess.run(['lsof', '-a', '-p', str(pid), '-Fn', str(root/'agterm.lock')], check=True, stdout=subprocess.DEVNULL)
os.kill(pid, signal.SIGTERM)
for _ in range(100):
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        break
    time.sleep(.1)
else:
    raise RuntimeError('isolated fixture did not stop')
if not backup.exists():
    subprocess.run(['ditto', str(bundle), str(backup)], check=True, timeout=120)
# Preserve the inode as well as bytes: already-running ZMX daemons are attested
# against the executable at this exact path.
saved_zmx = root/'verified-zmx-during-update'
assert not saved_zmx.exists()
(bundle/'Contents/MacOS/zmx').rename(saved_zmx)
try:
    subprocess.run(['ditto', str(source), str(bundle)], check=True, timeout=120)
finally:
    # An app rebuild re-signs embedded helpers. Preserve the verified ZMX signature too.
    os.replace(saved_zmx, bundle/'Contents/MacOS/zmx')
subprocess.run(['codesign', '--force', '--sign', '-', str(bundle)], check=True, timeout=30)
args = ['open', '-g', '-n', str(bundle)]
for key, value in dict(AGTERM_STATE_DIR=str(root), AGTERM_CONTROL_SOCKET=control,
                      AGTERM_UITEST_ENABLE_ZMX='1', AGTERM_UITEST_FORCE_SIDEBAR_VISIBLE='1',
                      XDG_CONFIG_HOME=str(root/'xdg'), ZDOTDIR=str(root/'zsh')).items():
    args += ['--env', key+'='+value]
args += ['--args', '-ApplePersistenceIgnoreState', 'YES', '-NSQuitAlwaysKeepsWindows', 'NO']
subprocess.run(args, check=True, timeout=20)
for _ in range(150):
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
            channel.connect(control)
            current = struct.unpack('i', channel.getsockopt(0, 2, 4))[0]
        if current != pid:
            (root/'app.pid').write_text(str(current))
            break
    except OSError:
        pass
    time.sleep(.2)
else:
    raise RuntimeError('updated fixture did not start; retained bundle is '+str(backup))
hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (bundle/'Contents/MacOS').iterdir() if p.is_file()}
(root/'revision-build.sha256.json').write_text(json.dumps(hashes, indent=2)+'\n')
print(json.dumps({'updated': str(bundle), 'legacy': str(backup), 'sha256': hashes}))
