"""Real bundled origin job channel on a test-owned PTY; no UI keyboard injection."""
import fcntl
import json
import os
from pathlib import Path
import pty
import queue
import select
import signal
import struct
import subprocess
import sys
import termios
import threading
import time

run=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
root=Path(run['root'])
assert root.parent==Path('/tmp') and root.name.startswith('aw-stage3-origin-')
adapter=str(root/'endpoint/agtermctl')
cli=run['config']['cli']
selectors=['--pipe',run['config']['pipe'],'--window',run['window'],'--target',run['pane'],'--json']

def native(*args, success=True):
    p=subprocess.run([cli,*args,*selectors],capture_output=True,text=True,timeout=25)
    if success: assert p.returncode==0,(args,p.stdout,p.stderr)
    return json.loads(p.stdout)

class Presenter:
    def __init__(self):
        self.process=subprocess.Popen([adapter,'zmx','present',run['pane']],stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True)
        self.frames=queue.Queue()
        self.process.stdin.write(json.dumps({'kind':'hello','gen':0,'rev':0,'hello':{'version':1,'mode':'presenter','kinds':['layout']}})+'\n')
        self.process.stdin.flush()
        hello=json.loads(self.process.stdout.readline()); self.gen=hello['gen']
        assert json.loads(self.process.stdout.readline())['kind']=='snapshot'
        def reader():
            for line in self.process.stdout:
                frame=json.loads(line)
                if frame['kind']=='ping': self.send('ack')
                else: self.frames.put(frame)
        threading.Thread(target=reader,daemon=True).start()
        self.send('presenter.acquire'); assert self.take('presenter.granted')
    def send(self,kind,job=None):
        frame={'kind':kind,'gen':self.gen,'rev':0}
        if job: frame['overlay']={'job':job}
        self.process.stdin.write(json.dumps(frame)+'\n'); self.process.stdin.flush()
    def take(self,kind):
        until=time.monotonic()+25
        while time.monotonic()<until:
            frame=self.frames.get(timeout=max(.01,until-time.monotonic()))
            if frame['kind']==kind: return frame
        raise RuntimeError('missing '+kind)
    def stop(self):
        self.process.terminate(); self.process.wait(timeout=10)

class Job:
    def __init__(self,job):
        self.pid,self.fd=pty.fork()
        if not self.pid: os.execv(adapter,[adapter,'session','overlay','run-job',job])
        self.output=b''; self.status=None
    def read_until(self,marker,timeout=30):
        until=time.monotonic()+timeout
        while time.monotonic()<until:
            if marker in self.output: return
            if select.select([self.fd],[],[],.1)[0]:
                try: self.output+=os.read(self.fd,65536)
                except OSError: break
        raise AssertionError((marker,self.output[-3000:]))
    def wait(self,timeout=12):
        until=time.monotonic()+timeout
        while time.monotonic()<until:
            pid,status=os.waitpid(self.pid,os.WNOHANG)
            if pid: self.status=status; return os.waitstatus_to_exitcode(status)
            if self.fd is not None and select.select([self.fd],[],[],.05)[0]:
                try: self.output+=os.read(self.fd,65536)
                except OSError: pass
        raise AssertionError('job helper did not exit')
    def close(self):
        if self.fd is not None: os.close(self.fd); self.fd=None
        if self.status is None:
            try: os.kill(self.pid,signal.SIGTERM)
            except ProcessLookupError: pass
            self.wait()

presenter=Presenter(); jobs=[]
def open_job(command,wait=False):
    result=native('session','overlay','open',command,'--cwd',str(root),*(['--wait'] if wait else []))
    request=presenter.take('overlay.request')['overlay']
    assert request['job']==result['result'],(request,result)
    assert 'command' not in request and 'environment' not in request
    return request['job']
def outcome(expected):
    for _ in range(60):
        result=native('session','overlay','result',success=False)
        if result.get('ok') and result.get('result')==expected: return
        time.sleep(.1)
    raise AssertionError(result)

try:
    marker=root/'job-executions'
    command='printf x >> '+str(marker)+'; printf "JOB_CONTEXT=%s:%s:%s\\n" "$(uname -s)" "$PWD" "$AGWINTERM_SESSION_ID"; read value; stty size; printf "INPUT=%s\\n" "$value"; exit 7'
    job_id=open_job(command,True)
    job=Job(job_id); jobs.append(job)
    job.read_until(('JOB_CONTEXT=Linux:'+str(root)+':'+run['pane']).encode())
    duplicate=Job(job_id); jobs.append(duplicate); assert duplicate.wait()!=0
    fcntl.ioctl(job.fd,termios.TIOCSWINSZ,struct.pack('HHHH',29,83,0,0))
    os.write(job.fd,b'accepted\n'); job.read_until(b'INPUT=accepted'); assert job.wait()==7
    assert b'29 83' in job.output,job.output
    assert marker.read_text()=='x'
    outcome('exit 7')
    assert not native('session','overlay','open','exit 0',success=False)['ok']
    native('session','overlay','close'); outcome('exit 7')
    print('PASS real origin job: single claim, immutable context, PTY input/resize, authoritative exit, held slot and close')

    blocked=subprocess.Popen([cli,'session','overlay','open','exit 11','--cwd',str(root),'--block',*selectors],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    job_id=presenter.take('overlay.request')['overlay']['job']
    job=Job(job_id); jobs.append(job); assert job.wait()==11
    output,error=blocked.communicate(timeout=15)
    assert blocked.returncode==0 and json.loads(output)['result']=='exit 11',(output,error)
    print('PASS origin block caller receives its own job exit without waiting for a held surface')

    rejected=open_job('printf BAD >> '+str(marker))
    presenter.send('overlay.rejected',rejected); outcome('refused')
    refused=Job(rejected); jobs.append(refused); assert refused.wait()!=0
    expired=open_job('printf BAD >> '+str(marker))
    time.sleep(16); outcome('expired')
    refused=Job(expired); jobs.append(refused); assert refused.wait()!=0
    assert marker.read_text()=='x'
    print('PASS actual refused and expired reservations cannot execute')

    abandoned=open_job('printf BAD >> '+str(marker))
    presenter.stop(); presenter=Presenter()
    outcome('presenter-lost')
    refused=Job(abandoned); jobs.append(refused); assert refused.wait()!=0
    assert marker.read_text()=='x'
    print('PASS presenter loss before claim retires reservation without execution')

    job_id=open_job('printf READY; read value; printf "DONE=%s\\n" "$value"; exit 9',True)
    job=Job(job_id); jobs.append(job); job.read_until(b'READY')
    presenter.stop(); presenter=Presenter()
    assert not native('session','overlay','open','exit 0',success=False)['ok']
    os.write(job.fd,b'after-loss\n'); job.read_until(b'DONE=after-loss'); assert job.wait()==9; outcome('exit 9')
    print('PASS presentation loss leaves claimed PTY job alive; reconnect cannot adopt or replace it')

    for loss in ['close','channel']:
        job_id=open_job('printf RUNNING; sleep 300')
        job=Job(job_id); jobs.append(job); job.read_until(b'RUNNING')
        if loss=='close': native('session','overlay','close'); outcome('closed')
        else: os.kill(job.pid,signal.SIGHUP)
        assert job.wait()!=0
        if loss=='channel':
            for _ in range(60):
                result=native('session','overlay','result',success=False)
                if result.get('ok'): break
                time.sleep(.1)
            assert result.get('ok'),result
        print('PASS actual job '+loss+' ends its supervisor without touching base workload')
finally:
    for job in jobs: job.close()
    presenter.stop()
