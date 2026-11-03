"""Actual pinned agterm viewer in a dedicated macOS state/app directory."""
import hashlib
import json
import os
from pathlib import Path
import shlex
import signal
import socket
import struct
import subprocess
import sys
import time
import uuid

action = sys.argv[1]
root = Path(sys.argv[2])
assert root.parent == Path('/tmp') and root.name.startswith('aw-stage3-viewer-')
config = json.loads((root/'run.json').read_text())
bundle = root/'agterm.app'
control = str(root/'control.sock')
cli = bundle/'Contents/MacOS/agtermctl'

def ctl(*args):
    p = subprocess.run([str(cli),*args,'--socket',control,'--json'],capture_output=True,text=True,timeout=40)
    if p.returncode: raise RuntimeError((args,p.stdout,p.stderr))
    reply = json.loads(p.stdout)
    if not reply['ok']: raise RuntimeError(reply)
    return reply['result']

def peer_pid():
    with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as channel:
        channel.connect(control)
        return struct.unpack('i',channel.getsockopt(0,2,4))[0]

def start():
    env = {'AGTERM_STATE_DIR':str(root),'AGTERM_CONTROL_SOCKET':control,
           'AGTERM_UITEST_ENABLE_ZMX':'1','AGTERM_UITEST_FORCE_SIDEBAR_VISIBLE':'1',
           'XDG_CONFIG_HOME':str(root/'xdg'),'ZDOTDIR':str(root/'zsh'),
           'SSH_AUTH_SOCK':config['macAgentSocket'],'PATH':str(root/'bin')+':/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin'}
    args=['open','-g','-n',str(bundle)]
    for key,value in env.items(): args += ['--env',key+'='+value]
    args += ['--args','-ApplePersistenceIgnoreState','YES','-NSQuitAlwaysKeepsWindows','NO']
    subprocess.run(args,check=True,timeout=20)
    for _ in range(150):
        try:
            windows=ctl('window','list')['windows']
            if any(w['open'] for w in windows): break
        except (OSError,RuntimeError): pass
        time.sleep(.2)
    else: raise RuntimeError('isolated viewer did not become ready')
    (root/'app.pid').write_text(str(peer_pid()))
    for window in windows:
        if window['open']: ctl('window','minimize',window['id'],'on')

def stop(crash=False):
    if not (root/'app.pid').exists(): return
    pid=int((root/'app.pid').read_text())
    try: current=peer_pid()
    except (OSError,RuntimeError): return
    if pid != current: raise RuntimeError('isolated viewer process changed')
    if not crash:
        for window in ctl('window','list')['windows']:
            if window['open']: ctl('window','close',window['id'])
    try: os.kill(pid, signal.SIGKILL if crash else signal.SIGTERM)
    except ProcessLookupError: pass
    time.sleep(1)

def discover():
    remote=ctl('zmx','tree',config['peerHost'])['remote']
    row=next(s for s in remote['sessions'] if s['id']==config['pane'])
    assert remote.get('presentation') == 1
    assert '/manifests/' in remote['endpoint']['executable']
    return remote,row

def imported_surface(kind='left'):
    imported=json.loads((root/'imported.json').read_text())['id']
    rows=[s for w in ctl('tree')['tree']['workspaces'] for s in w['sessions'] if s['id']==imported]
    return next(s for s in rows[0]['surfaces'] if s['kind']==kind)

def exercise(split):
    remote,row=discover()
    assert len(row['panes']) == (2 if split else 1), row
    windows=ctl('window','list')['windows']
    window=next(w['id'] for w in windows if w['open'])
    imported=ctl('zmx','attach',config['peerHost'],row['id'],'--window',window)['id']
    (root/'imported.json').write_text(json.dumps({'id':imported,'window':window,'tree':remote}))
    for pane in ['left','right'] if split else ['left']:
        marker='AW_MAC_VIEWER_'+uuid.uuid4().hex
        for _ in range(80):
            try:
                text=ctl('session','text','--target',imported,'--pane',pane).get('text','')
                if 'bash' in text or 'AW_ORIGIN_' in text or '$' in text: break
            except RuntimeError: pass
            time.sleep(.25)
        else: raise RuntimeError('viewer did not attach: '+repr(text))
        ctl('session','lead','--target',imported,'--pane',pane)
        time.sleep(.5)
        ctl('session','type',"printf '%s%s\\n' 'AW_MAC_VIEWER_' '"+marker.removeprefix('AW_MAC_VIEWER_')+"'\n",'--target',imported,'--pane',pane)
        for _ in range(40):
            text=ctl('session','text','--target',imported,'--pane',pane).get('text','')
            if marker in text: break
            time.sleep(.2)
        else: raise RuntimeError(('viewer input/read missing',pane,text))
    (root/('acceptance-split.json' if split else 'acceptance-single.json')).write_text(json.dumps(ctl('tree'),indent=2))
    print('PASS actual pinned agterm discovery/attach/input/read/leadership '+('split' if split else 'single'))
    return imported

if action == 'single':
    source=Path(config['macApp']).resolve()
    if not str(source).startswith(('/private/tmp/aw-stage2-build-', '/private/tmp/aw-stage4-build-')):
        raise RuntimeError('only the isolated pinned build is allowed')
    subprocess.run(['ditto',str(source),str(bundle)],check=True,timeout=120)
    (root/'settings.json').write_text(json.dumps({'restoreMode':'live'}))
    (root/'bin').mkdir()
    (root/'zsh').mkdir()
    path=str(root/'bin')+':/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin'
    (root/'zsh/.zshenv').write_text('export PATH='+shlex.quote(path)+'\nexport SSH_AUTH_SOCK='+shlex.quote(config['macAgentSocket'])+'\n')
    (root/'zsh/.zshrc').write_text('')
    # Test-only PATH provisioning. Forward every ssh option/host unchanged; only give the
    # isolated remote command the adapter's private PATH. No user shell or SSH files change.
    wrapper='''#!/usr/bin/python3
import os,sys,shlex,json,pathlib
args=sys.argv[1:]
if '-G' not in args and len(args)>=2 and args[-2] == HOST:
    if '/manifests/' in args[-1]:
        pathlib.Path(LOG_ROOT+'/ssh-'+str(os.getpid())+'.json').write_text(json.dumps({'pid':os.getpid(),'command':args[-1]}))
    args[-1]=shlex.join(['/usr/bin/env','PATH='+REMOTE_PATH+':/usr/bin:/bin','/bin/sh','-c',args[-1]])
os.execv('/usr/bin/ssh',['ssh']+args)
'''.replace('HOST',repr(config['peerHost'])).replace('REMOTE_PATH',repr(config['root']+'/endpoint')).replace('LOG_ROOT',repr(str(root)))
    (root/'bin/ssh').write_text(wrapper)
    (root/'bin/ssh').chmod(0o700)
    hashes={name:hashlib.sha256((bundle/'Contents/MacOS'/name).read_bytes()).hexdigest() for name in ['agterm','agtermctl','zmx']}
    (root/'build-hashes.json').write_text(json.dumps(hashes,indent=2))
    start()
    imported=exercise(False)
    ctl('session','close','--target',imported)
    discover()
    print('PASS viewer close preserves origin workload')
elif action == 'split':
    exercise(True)
elif action == 'passive':
    imported=json.loads((root/'imported.json').read_text())['id']
    for _ in range(80):
        rows=[s for w in ctl('tree')['tree']['workspaces'] for s in w['sessions'] if s['id']==imported]
        if rows and rows[0].get('context')=='STAGE4_ORIGIN_CONTEXT' and rows[0].get('status')=='blocked' and rows[0].get('hud',{}).get('message')=='STAGE4_ORIGIN_HUD': break
        time.sleep(.25)
    else:
        (root/'passive-failure.json').write_text(json.dumps(rows,indent=2))
        raise RuntimeError('passive replica did not converge: '+repr(rows))
    print('PASS actual Agwinterm -> agterm context/status/HUD replica')
elif action == 'manual-show':
    info=json.loads((root/'imported.json').read_text())
    for _ in range(80):
        rows=[s for w in ctl('tree')['tree']['workspaces'] for s in w['sessions'] if s['id']==info['id']]
        if rows and rows[0].get('ask',{}).get('id')==sys.argv[3] and rows[0]['ask'].get('replica'): break
        time.sleep(.25)
    else:
        (root/'manual-question-failure.json').write_text(json.dumps(rows,indent=2))
        raise RuntimeError('manual question not visible in Mac session state: '+repr(rows))
    ctl('window','minimize',info['window'],'off')
    ctl('window','select',info['window'])
    (root/'manual-ready.json').write_text(json.dumps({'window':info['window'],'session':info['id'],'ask':rows[0]['ask']},indent=2))
    print('MANUAL MAC READY: Stage 4: manual Mac answer; type STAGE4_MAC_ANSWER and Confirm test; isolated app='+str(bundle))
elif action == 'follower-resize':
    info=json.loads((root/'imported.json').read_text())
    for _ in range(30):
        if imported_surface().get('lead')=='follower': break
        time.sleep(.1)
    else: raise RuntimeError('Mac did not become follower')
    ctl('window','resize',info['window'],'--width','1050','--height','750')
    assert imported_surface().get('lead')=='follower'
    print('PASS actual agterm remains follower after window resize')
elif action == 'lead':
    info=json.loads((root/'imported.json').read_text())
    ctl('session','lead','--target',info['id'],'--pane','left')
    for _ in range(100):
        if imported_surface().get('lead')=='leader': break
        time.sleep(.1)
    else: raise RuntimeError('explicit Mac leadership claim failed')
    print('PASS actual agterm explicit leadership transition')
elif action == 'outage':
    info=json.loads((root/'imported.json').read_text())
    before=set(root.glob('ssh-*.json'))
    killed=[]
    for record in root.glob('ssh-*.json'):
        entry=json.loads(record.read_text())
        p=subprocess.run(['ps','-p',str(entry['pid']),'-o','comm=','-o','args='],capture_output=True,text=True)
        if p.returncode==0 and 'ssh' in p.stdout and config['root']+'/endpoint/manifests/' in p.stdout:
            os.kill(entry['pid'],signal.SIGTERM)
            killed.append(entry['pid'])
    assert killed, 'no isolated SSH transport found'
    for _ in range(100):
        try:
            surface=imported_surface()
            if surface.get('lead')=='follower' and set(root.glob('ssh-*.json'))-before: break
        except (RuntimeError,KeyError): pass
        time.sleep(.2)
    else:
        state=ctl('tree')
        (root/'outage-failure.json').write_text(json.dumps(state,indent=2))
        raise RuntimeError('viewer did not reconnect as follower: '+repr(imported_surface()))
    ctl('session','lead','--target',info['id'],'--pane','left')
    marker='AW_RECONNECTED_'+uuid.uuid4().hex
    time.sleep(.5)
    ctl('session','type',"printf '%s%s\\n' 'AW_RECONNECTED_' '"+marker.removeprefix('AW_RECONNECTED_')+"'\n",'--target',info['id'],'--pane','left')
    for _ in range(30):
        if marker in ctl('session','text','--target',info['id'],'--pane','left').get('text',''): break
        time.sleep(.2)
    else: raise RuntimeError('reconnected viewer input missing')
    print('PASS real SSH transport loss reconnects as follower and resumes exact workload')
elif action == 'restart':
    # agterm intentionally drops remote rows on relaunch; explicit re-import must reach the
    # same WSL identities, never launch a replacement workload or replay a saved command.
    previous=json.loads((root/'imported.json').read_text())['tree']
    stop(crash=True)
    start()
    current,_=discover()
    before=next(s for s in previous['sessions'] if s['id']==config['pane'])
    after=next(s for s in current['sessions'] if s['id']==config['pane'])
    assert before['panes']==after['panes']
    exercise(True)
    print('PASS viewer crash/restart and explicit re-import preserve origin daemon identities')
elif action == 'stop':
    stop()
    print('PASS isolated viewer stopped')
else:
    raise RuntimeError('unknown peer test action')
