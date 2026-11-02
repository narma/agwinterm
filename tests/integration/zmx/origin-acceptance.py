"""Invoked only by the routed WslOriginExport runner; owns isolated endpoint/peer state."""
import json
import fcntl
import os
from pathlib import Path
import pty
import select
import shlex
import signal
import subprocess
import struct
import termios
import sys
import time
import uuid

mode = sys.argv[1]
run_path = Path(sys.argv[2])
run = json.loads(run_path.read_text(encoding='utf-8-sig'))
root = Path(run['root'])
assert root.parent == Path('/tmp') and root.name.startswith('aw-stage3-origin-')
adapter = root / 'endpoint/agtermctl'

def call(argv, success=True, env=None, timeout=35):
    p = subprocess.run([str(a) for a in argv], capture_output=True, text=True, env=env, timeout=timeout)
    if success and p.returncode: raise RuntimeError((argv, p.returncode, p.stdout, p.stderr))
    if not success and not p.returncode: raise RuntimeError(('unexpected success', argv, p.stdout))
    return p

def tree():
    return json.loads(call([adapter, 'zmx', 'tree', '--json']).stdout)['result']['remote']

def selected(value):
    return next(s for s in value['sessions'] if s['id'] == run['pane'])

def attach(value, role='left', success=True):
    row = selected(value)
    name = next(p['daemon'] for p in row['panes'] if p['pane'] == role)
    marker = 'AW_ORIGIN_' + uuid.uuid4().hex
    pid, fd = pty.fork()
    if pid == 0:
        env = {**os.environ, 'ZMX_DIR': value['endpoint']['socketDirectory'],
               'ZMX_MANAGED': uuid.uuid4().hex, 'ZMX_MANAGED_CLAIM': '1'}
        os.execve(value['endpoint']['executable'], ['attach', 'attach', name], env)
    output = b''
    exited = False
    try:
        deadline = time.monotonic() + 8
        sent = False
        while time.monotonic() < deadline:
            if select.select([fd], [], [], .1)[0]:
                try: data = os.read(fd, 65536)
                except OSError: data = b''
                output += data
                if not data: break
            if not sent and b'zmx-role;' in output:
                os.write(fd, ('printf "' + marker + '\\n"\r').encode())
                sent = True
            if success and output.count(marker.encode()) >= 2: break
            waited, status = os.waitpid(pid, os.WNOHANG)
            if waited:
                exited = True
                if success: raise RuntimeError(('attach exited', status, output))
                assert status != 0
                return
        if success:
            assert marker.encode() in output, output[-3000:]
        else:
            waited, status = os.waitpid(pid, os.WNOHANG)
            if waited: exited = True
            assert waited and status != 0, output
    finally:
        os.close(fd)
        if not exited:
            try: os.kill(pid, signal.SIGHUP)
            except ProcessLookupError: pass
            os.waitpid(pid, 0)
    return name

def peer(action):
    env = {**os.environ, 'SSH_AUTH_SOCK': run['agentSocket']}
    remote_root = '/tmp/aw-stage3-viewer-' + root.name.removeprefix('aw-stage3-origin-')
    ssh = ['ssh', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', run['destination']]
    def remote(argv, stdin=None):
        result = subprocess.run(ssh + [shlex.join(argv)], input=stdin, text=True, capture_output=True, env=env, timeout=180)
        if result.returncode: raise RuntimeError((result.returncode, result.stdout, result.stderr))
        return result.stdout
    if action == 'single':
        source = Path(__file__).with_name('origin-viewer.py').read_text()
        payload = json.dumps({'source':source, 'config':{**run, 'remoteRoot':remote_root}})
        remote(['python3','-c',
            'import pathlib,json,sys; d=json.load(sys.stdin); p=pathlib.Path(sys.argv[1]); p.mkdir(mode=0o700); (p/"viewer.py").write_text(d["source"]); (p/"run.json").write_text(json.dumps(d["config"]))', remote_root], payload)
    elif action == 'stop':
        # No stop action before this run created its own viewer directory.
        result = subprocess.run(ssh + ['test -f ' + shlex.quote(remote_root+'/run.json')], env=env, timeout=10)
        if result.returncode: return
    output = remote(['python3',remote_root+'/viewer.py',action,remote_root])
    (root/('peer-'+action+'.log')).write_text(output)
    print(output.strip())

def geometry(value):
    row=selected(value)
    name=row['panes'][0]['daemon']
    # A different-size explicit local leader makes the real Mac viewer a follower.
    pid,fd=pty.fork()
    if pid==0:
        os.execve(value['endpoint']['executable'],['attach','attach',name],
            {**os.environ,'ZMX_DIR':value['endpoint']['socketDirectory'],'ZMX_MANAGED':uuid.uuid4().hex,'ZMX_MANAGED_CLAIM':'1'})
    try:
        fcntl.ioctl(fd,termios.TIOCSWINSZ,struct.pack('HHHH',31,91,0,0))
        time.sleep(.5)
        peer('follower-resize')
        # Ask the workload itself for dimensions, with the local client still leading.
        os.write(fd,b"printf 'AW_GRID '; stty size\r")
        output=b''
        deadline=time.monotonic()+5
        while time.monotonic()<deadline:
            if select.select([fd],[],[],.1)[0]: output+=os.read(fd,65536)
            if b'31 91' in output: break
        assert b'31 91' in output,output[-3000:]
        # Keep an independent leader alive during the outage. With no remaining leader,
        # pinned agterm deliberately reattaches an unowned pane again and may lead it.
        peer('outage')
        peer('lead')
        print('PASS actual viewer follower resize preserves leader grid; explicit viewer lead succeeds')
    finally:
        os.close(fd)
        try: os.kill(pid,signal.SIGHUP)
        except ProcessLookupError: pass
        os.waitpid(pid,0)

if mode == 'local':
    value = tree()
    assert len(selected(value)['panes']) == 1
    (root/'single.json').write_text(json.dumps(value))
    name = attach(value)
    assert name == selected(tree())['panes'][0]['daemon']
    screen = json.loads(call([adapter,'zmx','screen',name,'--json']).stdout)
    assert 'AW_ORIGIN_' in screen['result']['text']
    for command in [['zmx','kill',name,'--json'],['zmx','present',run['pane']],['session','close',run['pane']]]:
        call([adapter,*command],success=False)
    print('PASS single discovery, exact PTY input/read, borrowed close, unsupported controls')
elif mode == 'split':
    value = tree()
    assert [p['pane'] for p in selected(value)['panes']] == ['left','right']
    (root/'split.json').write_text(json.dumps(value))
    attach(value, 'left'); attach(value, 'right')
    print('PASS split publication and independent per-pane terminal input')
elif mode == 'ui-down':
    call([adapter,'zmx','tree','--json'],success=False)
    value=json.loads((root/'split.json').read_text())
    attach(value)
    name=selected(value)['panes'][0]['daemon']
    call([adapter,'zmx','screen',name,'--json'],success=False)
    print('PASS UI unavailable discovery/control refusal and surviving exact attachment')
elif mode == 'restored':
    value=tree()
    old=json.loads((root/'split.json').read_text())
    assert selected(value)['panes'] == selected(old)['panes']
    attach(old)
    print('PASS origin UI restart preserves workload identities and old immutable endpoint')
elif mode == 'partial':
    assert not any(s['id']==run['pane'] for s in tree()['sessions'])
    attach(json.loads((root/'split.json').read_text()),'right',False)
    print('PASS partial split omitted and old missing pane cannot create a replacement')
elif mode == 'terminated':
    attach(json.loads((root/'split.json').read_text()),'left',False)
    print('PASS explicit origin termination invalidates borrowed attachments')
elif mode.startswith('peer-'):
    peer(mode.removeprefix('peer-'))
    if mode=='peer-split':
        geometry(json.loads((root/'split.json').read_text()))
else:
    raise RuntimeError('unknown test mode')
