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
import queue
import threading

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

def presentation():
    observer=None
    process = subprocess.Popen([str(adapter), 'zmx', 'present', run['pane']], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    frames = queue.Queue()
    def read():
        for line in process.stdout: frames.put(json.loads(line))
        frames.put(None)
    threading.Thread(target=read, daemon=True).start()
    try:
        process.stdin.write(json.dumps({'kind':'hello','gen':0,'rev':0,'hello':{'version':1,'mode':'presenter','kinds':['context','status','hud','layout','notify','sound','ask.form','ask.other']}})+'\n')
        process.stdin.flush()
        hello = frames.get(timeout=15)
        snapshot = frames.get(timeout=15)
        assert hello['kind']=='hello' and snapshot['kind']=='snapshot', (hello,snapshot)
        assert snapshot['snapshot']['layout']['panes'], snapshot
        generation=hello['gen']
        def send(kind, payload=None):
            frame={'kind':kind,'gen':generation,'rev':0}
            if payload is not None: frame['ask']=payload
            process.stdin.write(json.dumps(frame)+'\n'); process.stdin.flush()
        def until(kind):
            deadline=time.monotonic()+15
            while True:
                frame=frames.get(timeout=max(.01,deadline-time.monotonic()))
                assert frame is not None, 'presentation EOF'
                if frame['kind']=='ping': send('ack')
                if frame['kind']==kind: return frame
        send('presenter.acquire'); assert until('presenter.granted')
        observer=subprocess.Popen([str(adapter),'zmx','present',run['pane']],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        observed=queue.Queue()
        def observe():
            for line in observer.stdout: observed.put(json.loads(line))
            observed.put(None)
        threading.Thread(target=observe,daemon=True).start()
        observer.stdin.write(json.dumps({'kind':'hello','gen':0,'rev':0,'hello':{'version':1,'mode':'presenter','kinds':['context','notify','sound']}})+'\n'); observer.stdin.flush()
        observer_hello=observed.get(timeout=15); assert observer_hello['kind']=='hello'
        assert observed.get(timeout=15)['kind']=='snapshot'
        observer.stdin.write(json.dumps({'kind':'presenter.acquire','gen':observer_hello['gen'],'rev':0})+'\n'); observer.stdin.flush()
        assert observed.get(timeout=10)['kind']=='presenter.refused'
        cli=run['config']['cli']; selectors=['--pipe',run['config']['pipe'],'--window',run['window']]
        def native(*args):
            return call([cli,*args,*selectors])
        native('notify','STAGE4_LIVE_EVENT','--target',run['pane'])
        assert until('notify')['notify']['body']=='STAGE4_LIVE_EVENT'
        native('session','status','active','--sound','beep','--target',run['pane'])
        assert until('sound')['sound']['name']=='beep'
        native('session','status','blocked','--target',run['pane'])
        assert until('sound')['sound'].get('name') is None
        native('session','status','idle','--target',run['pane'])
        native('session','context','EVENT_BARRIER','--target',run['pane'])
        secondary=[]
        while True:
            event=observed.get(timeout=10); secondary.append(event)
            if event['kind']=='context' and event['context']=='EVENT_BARRIER': break
        assert len([e for e in secondary if e['kind']=='notify'])==1,secondary
        assert not any(e['kind']=='sound' for e in secondary),secondary
        native('session','context','--clear','--target',run['pane'])
        observer.kill(); observer.wait(timeout=5); observer=None
        print('PASS actual Windows origin live notification, explicit sound and blocked viewer-policy marker')
        form={'fields':[{'id':'original-field','label':'Input','type':'text'}]}
        for cancel in [False,True]:
            opened=subprocess.run([cli,'ask','open','--no-block','--target',run['pane'],*selectors],input=json.dumps(form),text=True,capture_output=True,timeout=15)
            assert opened.returncode==0,(opened.stdout,opened.stderr)
            ask_id=json.loads(opened.stdout)['id']
            request=until('ask.request')['ask']; assert request['id']==ask_id
            answer={'id':ask_id,'owner':request['owner']}
            if not cancel: answer.update(button='submit',text='AUTHORITATIVE_WSL_ANSWER')
            send('ask.resolve',answer); until('ask.dismiss')
            outcome=subprocess.run([cli,'ask','result',ask_id,*selectors],text=True,capture_output=True,timeout=10)
            result=json.loads(outcome.stdout)
            if cancel: assert result['result']=='cancelled' and result['reason']=='escape',result
            else: assert result['result']=='answered' and result['values']['original-field']=='AUTHORITATIVE_WSL_ANSWER',result
        print('PASS actual WSL origin presenter grant, input answer/cancel and authoritative retained result')
        # Native form -> actual bundled relay -> origin validation. IDs stay origin-owned.
        for kind in ['single','multi','multiline']:
            field={'id':'original-field','label':'Choose','type':kind}
            answer_fields={'text':'строка\nnext'} if kind=='multiline' else {'selected':[],'text':'другое'}
            if kind!='multiline': field.update(allowOther=True,choices=[{'id':'a','label':'A'},{'id':'b','label':'B'}])
            opened=subprocess.run([cli,'ask','open','--no-block','--target',run['pane'],*selectors],input=json.dumps({'fields':[field]}),text=True,capture_output=True,timeout=15)
            assert opened.returncode==0,(opened.stdout,opened.stderr)
            ask_id=json.loads(opened.stdout)['id']; request=until('ask.request')['ask']
            send('ask.resolve',{'id':ask_id,'owner':request['owner'],'button':'submit',**answer_fields}); until('ask.dismiss')
            result=json.loads(native('ask','result',ask_id).stdout)
            assert result['result']=='answered',result
            value=result['values']['original-field']
            assert (value=='строка\nnext' if kind=='multiline' else value['other']=='другое'),result
        # Cancellation wins before a late remote answer; the retained outcome cannot be overwritten.
        opened=subprocess.run([cli,'ask','open','--no-block','--target',run['pane'],*selectors],input='{}',text=True,capture_output=True,timeout=15)
        ask_id=json.loads(opened.stdout)['id']; request=until('ask.request')['ask']
        native('ask','cancel',ask_id); until('ask.dismiss')
        send('ask.resolve',{'id':ask_id,'owner':request['owner'],'button':'submit'})
        result=json.loads(call([cli,'ask','result',ask_id,*selectors],success=False).stdout)
        assert result['result']=='cancelled',result
        # Explicit refusal hands the same question back; local modal ownership then refuses another.
        opened=subprocess.run([cli,'ask','open','--no-block','--target',run['pane'],*selectors],input='{}',text=True,capture_output=True,timeout=15)
        ask_id=json.loads(opened.stdout)['id']; request=until('ask.request')['ask']
        send('ask.rejected',{'id':ask_id,'owner':request['owner']}); until('ask.dismiss')
        assert json.loads(call([cli,'ask','result',ask_id,*selectors],success=False).stdout)['result']=='pending'
        busy=subprocess.run([cli,'ask','open','--no-block','--target',run['pane'],*selectors],input='{}',text=True,capture_output=True,timeout=15)
        assert busy.returncode!=0,busy.stdout
        native('ask','cancel',ask_id)
        print('PASS actual Windows origin choice/Other/multiline, cancel-before-late-answer, same-ID handback and busy refusal')
        opened=subprocess.run([cli,'ask','open','--no-block','--target',run['pane'],*selectors],input='{}',text=True,capture_output=True,timeout=15)
        lost_id=json.loads(opened.stdout)['id']; until('ask.request')
        process.stdin.write('{"cmd":"session.type","target":"active","args":{"text":"UNAUTHORIZED"}}\n')
        process.stdin.flush()
        process.wait(timeout=10)
        assert frames.get(timeout=2) is None
        result=json.loads(call([cli,'ask','result',lost_id,*selectors],success=False).stdout)
        assert result['result']=='pending',result
        native('ask','cancel',lost_id)
        print('PASS disconnect returns pending question with unchanged origin ID; two viewers receive one notification each and only presenter receives sound')
        print('PASS actual Rust/CLI/broker hello and snapshot; generic command refused')
    finally:
        if observer is not None:
            observer.kill(); observer.wait(timeout=5)
        if process.poll() is None: process.kill()
        process.wait(timeout=5)

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
                # The expected marker must not occur in terminal input echo.
                os.write(fd, ("printf '%s%s\\n' 'AW_ORIGIN_' '" + marker.removeprefix('AW_ORIGIN_') + "'\r").encode())
                sent = True
            if success and marker.encode() in output: break
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
    ssh = ['ssh', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ControlMaster=no', '-o', 'ControlPath=none', run['destination']]
    def remote(argv, stdin=None):
        result = subprocess.run(ssh + [shlex.join(argv)], input=stdin, text=True, capture_output=True, env=env, timeout=180)
        if result.returncode: raise RuntimeError((result.returncode, result.stdout, result.stderr))
        return result.stdout
    if action == 'accessibility-preflight':
        print(remote(['/usr/bin/swift','-'], '''import ApplicationServices
import Foundation
guard AXIsProcessTrusted(), let session = CGSessionCopyCurrentDictionary() as? [String: Any],
      session["CGSSessionScreenIsLocked"] as? Bool != true else {
    fputs("Enable Accessibility for SSH and unlock the Mac before the UI run\\n", stderr); exit(1)
}
print("PASS Mac Accessibility permission and unlocked graphical session")
''').strip())
        return
    if action == 'single':
        source = Path(__file__).with_name('origin-viewer.py').read_text()
        payload = json.dumps({'source':source, 'ax':Path(__file__).with_name('mac-accessibility.swift').read_text(), 'config':{**run, 'remoteRoot':remote_root}})
        remote(['python3','-c',
            'import pathlib,json,sys; d=json.load(sys.stdin); p=pathlib.Path(sys.argv[1]); p.mkdir(mode=0o700); (p/"viewer.py").write_text(d["source"]); (p/"mac-accessibility.swift").write_text(d["ax"]); (p/"run.json").write_text(json.dumps(d["config"]))', remote_root], payload)
    elif action == 'stop':
        # No stop action before this run created its own viewer directory.
        result = subprocess.run(ssh + ['test -f ' + shlex.quote(remote_root+'/run.json')], env=env, timeout=10)
        if result.returncode: return
    needs_id = action in ['manual-show','question-visible'] or (action.startswith('ax-question-') and action!='ax-question-absent')
    output = remote(['python3',remote_root+'/viewer.py',action,remote_root] + ([run['manualQuestionId']] if needs_id else []))
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
    assert value.get('presentation') == 1
    presentation()
    print(call(['python3', Path(__file__).with_name('origin-job-acceptance.py'), run_path], timeout=150).stdout.strip())
    for command in [['zmx','kill',name,'--json'],['session','close',run['pane']]]:
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
