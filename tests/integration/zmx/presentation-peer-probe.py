"""Additional connections/faults scoped to the isolated Mac origin; invoked by private-desktop UI tests."""
import json
import queue
import shlex
import subprocess
import sys
import threading

action, host, control, session = sys.argv[1:]
assert control.startswith('/tmp/aw-stage4-peer-') and control.endswith('/control.sock')
cli = control.removesuffix('/control.sock') + '/agterm.app/Contents/MacOS/agtermctl'
ssh = ['ssh', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', host]
if action == 'refused':
    child = subprocess.Popen(ssh+[shlex.join([cli,'zmx','present',session,'--socket',control])],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    frames=queue.Queue()
    def read():
        for line in child.stdout: frames.put(json.loads(line))
        frames.put(None)
    threading.Thread(target=read,daemon=True).start()
    try:
        child.stdin.write(json.dumps({'kind':'hello','gen':0,'rev':0,'hello':{'version':1,'mode':'presenter','kinds':['sound','notify','ask.form','ask.other']}})+'\n'); child.stdin.flush()
        hello=frames.get(timeout=15); assert hello['kind']=='hello',hello
        assert frames.get(timeout=10)['kind']=='snapshot'
        child.stdin.write(json.dumps({'kind':'presenter.acquire','gen':hello['gen'],'rev':0})+'\n'); child.stdin.flush()
        assert frames.get(timeout=10)['kind']=='presenter.refused'
        print('PASS actual Mac origin refuses a second presenter while Windows holds its grant')
    finally:
        child.kill(); child.wait(timeout=5)
elif action == 'origin-restart':
    script='''import json,os,pathlib,signal,socket,struct,subprocess,sys,time
root=pathlib.Path(sys.argv[1]); control=str(root/'control.sock')
assert root.parent==pathlib.Path('/tmp') and root.name.startswith('aw-stage4-peer-')
with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as channel:
 channel.connect(control); pid=struct.unpack('i',channel.getsockopt(0,2,4))[0]
executable=subprocess.run(['ps','-p',str(pid),'-o','comm='],capture_output=True,text=True,check=True).stdout.strip()
assert pathlib.Path(executable).resolve()==(root/'agterm.app/Contents/MacOS/agterm').resolve(),'not the isolated app executable'
owned=subprocess.run(['lsof','-a','-p',str(pid),'-Fn',str(root/'agterm.lock')],capture_output=True,text=True)
assert owned.returncode==0,'not the isolated state owner'
os.kill(pid,signal.SIGTERM)
for _ in range(100):
 try: os.kill(pid,0)
 except ProcessLookupError: break
 time.sleep(.1)
else: raise RuntimeError('fixture UI did not stop')
args=['open','-g','-n',str(root/'agterm.app')]
for key,value in dict(AGTERM_STATE_DIR=str(root),AGTERM_CONTROL_SOCKET=control,AGTERM_UITEST_ENABLE_ZMX='1',AGTERM_UITEST_FORCE_SIDEBAR_VISIBLE='1',XDG_CONFIG_HOME=str(root/'xdg'),ZDOTDIR=str(root/'zsh')).items(): args+=['--env',key+'='+value]
args+=['--args','-ApplePersistenceIgnoreState','YES','-NSQuitAlwaysKeepsWindows','NO']
subprocess.run(args,check=True,timeout=15)
for _ in range(150):
 try:
  with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as channel:
   channel.connect(control); current=struct.unpack('i',channel.getsockopt(0,2,4))[0]
  if current!=pid: (root/'app.pid').write_text(str(current)); break
 except OSError: pass
 time.sleep(.2)
else: raise RuntimeError('fixture UI did not restart')
'''
    subprocess.run(ssh+[shlex.join(['python3','-c',script,control.removesuffix('/control.sock')])],check=True,timeout=55)
    print('PASS restarted only the isolated Mac origin UI')
elif action in ['disconnect','job-disconnect','page-disconnect']:
    listing=subprocess.run(ssh+['ps -axo pid=,args='],text=True,capture_output=True,check=True,timeout=15).stdout
    pids=[]
    for line in listing.splitlines():
        pid,args=line.strip().split(None,1)
        # Match the executable's argument vector, never a shell command containing its text.
        try: words=shlex.split(args)
        except ValueError: continue
        if words[0].rsplit('/',1)[-1]!='agtermctl': continue
        if action=='disconnect' and words[1:]==['zmx','present',session,'--socket',control]: pids.append(pid)
        if action=='job-disconnect' and len(words)==7 and words[1:4]==['session','overlay','run-job'] and words[-2:]==['--socket',control]: pids.append(pid)
        if action=='page-disconnect' and len(words)==7 and words[1:4]==['session','overlay','page-serve'] and words[-2:]==['--socket',control]: pids.append(pid)
    assert pids, 'isolated presentation process not found'
    subprocess.run(ssh+[shlex.join(['kill','-TERM',*pids])],check=True,timeout=10)
    print('PASS stopped only isolated origin presentation CLI; terminal processes untouched')
else:
    raise ValueError(action)
