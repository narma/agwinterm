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
