"""Create/stop an isolated real agterm instance. Never addresses the user's control socket."""
import json
import os
from pathlib import Path
import signal
import shutil
import socket
import struct
import subprocess
import sys
import time

root = Path(sys.argv[2]).absolute()
if root.parent != Path('/tmp') or not root.name.startswith('aw-stage2-peer-'):
    raise SystemExit('a dedicated /tmp/aw-stage2-peer-* directory is required')
control = str(root / 'control.sock')
cli = '/usr/local/bin/agtermctl'

def ctl(*args):
    result = subprocess.run([cli, *args, '--socket', control, '--json'], capture_output=True, text=True, timeout=15)
    if result.returncode != 0: raise RuntimeError(result.stdout + result.stderr)
    reply = json.loads(result.stdout)
    if not reply['ok']: raise RuntimeError(reply)
    return reply['result']

if sys.argv[1] == 'start':
    root.mkdir(mode=0o700)
    (root/'settings.json').write_text(json.dumps({'restoreMode':'live'}))
    (root/'zsh').mkdir()
    (root/'zsh/.zshrc').write_text('')
    bundle = '/Applications/agterm.app'
    source_bundle = Path(sys.argv[4]).resolve() if len(sys.argv) > 4 else Path(bundle)
    if len(sys.argv) > 4 and not str(source_bundle).startswith('/private/tmp/aw-stage2-build-'):
        raise RuntimeError('only an isolated pinned build may replace the fixture source app')
    if len(sys.argv) > 3:
        client = Path(sys.argv[3]).resolve()
        if not str(client).startswith('/private/tmp/aw-stage2-build-') and not str(client).startswith('/tmp/aw-stage2-build-'):
            raise RuntimeError('only explicitly built isolated ZMX clients may be used')
        bundle = str(root/'agterm.app')
        subprocess.run(['ditto', str(source_bundle), bundle], check=True, timeout=120)
        shutil.copy2(client, Path(bundle)/'Contents/MacOS/zmx')
        subprocess.run(['codesign','--force','--deep','--sign','-',bundle], check=True, timeout=60)
    env = dict(AGTERM_STATE_DIR=str(root), AGTERM_CONTROL_SOCKET=control,
        AGTERM_UITEST_ENABLE_ZMX='1', AGTERM_UITEST_FORCE_SIDEBAR_VISIBLE='1',
        XDG_CONFIG_HOME=str(root/'xdg'), ZDOTDIR=str(root/'zsh'))
    args = ['open','-g','-n',bundle]
    for key,value in env.items(): args += ['--env',key+'='+value]
    args += ['--args', '-ApplePersistenceIgnoreState', 'YES', '-NSQuitAlwaysKeepsWindows', 'NO']
    subprocess.run(args, check=True, timeout=15)
    for _ in range(100):
        try:
            inventory = ctl('zmx','tree')
            if inventory['remote']['sessions']: break
        except (RuntimeError, OSError): pass
        time.sleep(.2)
    else: raise RuntimeError('isolated live agterm did not become ready')
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
        channel.connect(control)
        pid = struct.unpack('i', channel.getsockopt(0,2,4))[0]
    (root/'app.pid').write_text(str(pid))
    # Minimize only the isolated instance's windows; no user's app/window selectors are used.
    windows = ctl('window','list')['windows']
    for window in windows:
        if window.get('open'):
            ctl('window','minimize',window['id'],'on')
    (root/'tree.json').write_text(json.dumps(inventory))
    print(json.dumps({'socket':control,'pid':pid,'inventory':inventory}))
elif sys.argv[1] == 'inspect':
    print(json.dumps(ctl('zmx','tree')))
elif sys.argv[1] == 'diagnose':
    pid = int(sys.argv[3])
    owned = subprocess.run(['lsof', '-a', '-p', str(pid), '-Fn', str(root/'agterm.lock')], capture_output=True, text=True)
    if owned.returncode != 0: raise RuntimeError('not the isolated fixture process')
    subprocess.run(['/usr/bin/log', 'show', '--last', '20m', '--style', 'compact', '--predicate', f'processIdentifier == {pid}'], timeout=30)
elif sys.argv[1] == 'abort-start':
    pid = int(sys.argv[3])
    owned = subprocess.run(['lsof', '-a', '-p', str(pid), '-Fn', str(root/'agterm.lock')], capture_output=True)
    if owned.returncode != 0: raise RuntimeError('not the isolated fixture process')
    os.kill(pid, signal.SIGTERM)
elif sys.argv[1] == 'stop':
    # Ask only this instance to close its fixture windows, then end its exact peer process.
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
        channel.connect(control)
        current = struct.unpack('i', channel.getsockopt(0,2,4))[0]
    if current != int((root/'app.pid').read_text()): raise RuntimeError('fixture process changed')
    for window in ctl('window','list')['windows']:
        if window.get('open'): ctl('window','close',window['id'])
    os.kill(current, signal.SIGTERM)
else:
    raise SystemExit('start|inspect|stop required')
