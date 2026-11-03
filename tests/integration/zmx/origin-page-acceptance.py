"""Real scoped Rust/CLI/origin page claims; invoked only by the private-desktop runner."""
import base64
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

run = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
root = Path(run['root'])
assert root.parent == Path('/tmp') and root.name.startswith('aw-stage3-origin-')
adapter = str(root/'endpoint/agtermctl')
cli = run['config']['cli']
selectors = ['--pipe', run['config']['pipe'], '--window', run['window'], '--target', run['pane'], '--json']
kinds = ['page.request','page.rejected','page.close','page.closed','page.command','page.state','page.bridge-revision']

def native(*args):
    routing=selectors[:4]+selectors[6:] if args[0]=='tree' else selectors
    result = subprocess.run([cli,*args,*routing], text=True, capture_output=True, timeout=25)
    assert result.returncode == 0, (args,result.stdout)
    return json.loads(result.stdout)['result']

class Channel:
    def __init__(self, *args):
        self.process = subprocess.Popen([adapter,*args],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        self.frames = queue.Queue()
        def read():
            for line in self.process.stdout: self.frames.put(json.loads(line))
            self.frames.put(None)
        threading.Thread(target=read,daemon=True).start()
    def send(self, frame):
        self.process.stdin.write(json.dumps(frame)+'\n'); self.process.stdin.flush()
    def take(self):
        value=self.frames.get(timeout=20)
        assert value is not None,'page channel ended unexpectedly'
        return value
    def stop(self):
        self.process.terminate(); self.process.wait(timeout=10)

presenter=Channel('zmx','present',run['pane'])
presenter.send({'kind':'hello','gen':0,'rev':0,'hello':{'version':1,'mode':'presenter','kinds':kinds}})
hello=presenter.take(); generation=hello['gen']
assert all(k in hello['hello']['kinds'] for k in kinds)
assert presenter.take()['kind']=='snapshot'
def send(kind, body=None):
    frame={'kind':kind,'gen':generation,'rev':0}
    if body is not None: frame['page']=body
    presenter.send(frame)
def take(kind):
    deadline=time.monotonic()+20
    while time.monotonic()<deadline:
        frame=presenter.frames.get(timeout=max(.01,deadline-time.monotonic()))
        assert frame is not None,'presentation channel ended'
        if frame['kind']=='ping': send('ack')
        elif frame['kind']==kind: return frame.get('page',{})
    raise AssertionError('missing '+kind)
send('presenter.acquire'); take('presenter.granted')
channels=[]
directory=root/'page-grant'; directory.mkdir()
(directory/'index.html').write_text('<!doctype html><link rel="stylesheet" href="стиль %.css"><p>origin document</p>')
(directory/'стиль %.css').write_text('body { color: green; }')
(root/'page-secret').write_text('not granted')
(directory/'escape').symlink_to(root/'page-secret')

def opened(review=False):
    result=native('session','overlay','open','--html',str(directory/'index.html'),'--cwd',str(directory),
                  *(['--js','--bridge','review'] if review else []))
    request=take('page.request')
    assert 'token' not in json.dumps(result)
    return result['pageID'],request
def outcome(page, expected):
    for _ in range(80):
        result=native('session','overlay','result','--page',page)['pageOutcome']
        if result['outcome']==expected: return result
        time.sleep(.1)
    raise AssertionError(('missing origin page outcome',expected))
def claim(request):
    channel=Channel('session','overlay','page-serve',request['token']); channels.append(channel); return channel
def fetch(channel, path, ident=1):
    channel.send({'kind':'resource','id':ident,'path':path})
    result=bytearray(); mime=None
    while True:
        frame=channel.take(); assert frame['id']==ident
        if frame['kind']=='head': mime=frame['mimeType']
        elif frame['kind']=='chunk': result.extend(base64.b64decode(frame['data']))
        elif frame['kind']=='failure': return None
        elif frame['kind']=='end': assert mime; return bytes(result)
        else: raise AssertionError('unexpected page resource frame')
def bridge(channel, command, args=None, ident=20, **routing):
    request={'cmd':command,**routing}
    if args is not None: request['args']=args
    channel.send({'kind':'bridge','id':ident,'request':json.dumps({'document':channel.document,'appliedSeq':channel.applied,'request':request})})
    frame=channel.take(); assert frame['id']==ident
    return json.loads(frame['response']) if frame['kind']=='reply' else None

def activate(channel, document=1, applied=0, expected=True):
    channel.send({'kind':'bridge','id':70,'request':json.dumps({'document':document,'appliedSeq':applied})})
    frame=channel.take(); assert frame['id']==70
    assert (frame['kind']=='reply') == expected, frame
    if expected: channel.document,channel.applied=document,applied

try:
    page,request=opened(); channel=claim(request)
    activate(channel)
    path=request['source']['file']['path']; prefix=path.rsplit('/',1)[0]
    assert b'origin document' in fetch(channel,path)
    assert fetch(channel,prefix+'/стиль %.css',2)==b'body { color: green; }'
    assert fetch(channel,prefix+'/../page-secret',3) is None
    assert fetch(channel,prefix+'/escape',4) is None
    assert fetch(channel,str(directory/'index.html'),5) is None
    duplicate=claim(request); duplicate.send({'kind':'resource','id':1,'path':path})
    assert duplicate.frames.get(timeout=10) is None
    for i,(command,extra) in enumerate([('session.new',{}),('batch',{}),('session.type',{'target':'active'})]):
        assert bridge(channel,command,ident=30+i,**extra) is None
    assert bridge(channel,'session.status',{'status':'blocked'},40)['ok']
    native('session','overlay','hide'); command=take('page.command'); assert command['action']=='hide' and command['seq']==1
    # A delayed state from before hide must not overwrite its newer authoritative command.
    send('page.state',{'page':request['page'],'appliedSeq':0,'hidden':False,'load':'loaded','title':'STALE',
                       'address':path,'canGoBack':False,'canGoForward':False})
    time.sleep(.2)
    row=next(p for w in native('tree')['workspaces'] for s in w['sessions'] for p in s.get('htmlOverlays',[]) or [] if p['id']==page)
    assert row['hidden'] and row.get('title')!='STALE',row
    native('session','overlay','show'); command=take('page.command'); assert command['action']=='show' and command['seq']==2
    native('session','overlay','reload'); command=take('page.command'); assert command['action']=='reloadOriginal' and command['seq']==3
    assert bridge(channel,'session.overlay.submit',{'value':'STALE_AFTER_RELOAD'}) is None
    activate(channel,99,2,False)
    activate(channel,2,3)
    activate(channel,3,3)  # HTML-initiated navigation, without an origin command.
    channel.document=2
    assert bridge(channel,'session.overlay.submit',{'value':'STALE_AFTER_SELF_NAVIGATION'}) is None
    channel.document=3
    send('page.state',{'page':request['page'],'appliedSeq':3,'hidden':False,'load':'loaded','title':'CURRENT',
                       'address':path,'canGoBack':False,'canGoForward':False})
    # Submission remains authoritative even when its bridge reply loses a race with page.close.
    channel.send({'kind':'bridge','id':50,'request':json.dumps({'document':3,'appliedSeq':3,'request':{'cmd':'session.overlay.submit','args':{'value':'origin accepted'}}})})
    assert outcome(page,'submitted')['value']=='origin accepted'
    take('page.close')
    print('PASS origin page assets/Unicode, traversal/symlink/cross-grant refusal, single claim, bridge scope, commands and retained submit')

    page,request=opened(True); channel=claim(request)
    activate(channel)
    assert bridge(channel,'session.status',{'status':'idle'}) is None
    channel.send({'kind':'bridge','id':21,'request':json.dumps({'document':1,'appliedSeq':0,'request':{'cmd':'session.overlay.close'}})})
    outcome(page,'dismissed'); take('page.close')
    print('PASS review page refuses general bridge commands and retains close-only authority')

    page,request=opened(); channel=claim(request)
    assert fetch(channel,request['source']['file']['path']) is not None  # Claim must complete before disconnect.
    channel.stop(); outcome(page,'dismissed'); take('page.close')
    page,request=opened(); send('page.rejected',{'page':request['page'],'reason':'fixture refusal'}); outcome(page,'dismissed'); take('page.close')
    page,request=opened(); presenter.stop(); outcome(page,'dismissed')
    print('PASS page channel loss, viewer rejection and presentation loss release origin slots without reopening')
    presenter=Channel('zmx','present',run['pane'])
    presenter.send({'kind':'hello','gen':0,'rev':0,'hello':{'version':1,'mode':'presenter','kinds':kinds[:-1]}})
    generation=presenter.take()['gen']; assert presenter.take()['kind']=='snapshot'
    send('presenter.acquire'); take('presenter.granted')
    refused=subprocess.run([cli,'session','overlay','open','--html',str(directory/'index.html'),*selectors],text=True,capture_output=True,timeout=25)
    assert refused.returncode!=0 and 'page.bridge-revision' in refused.stdout,refused.stdout
    page=native('session','overlay','open','--url','https://example.com')['pageID']
    request=take('page.request'); assert 'url' in request['source']
    native('session','overlay','reload'); assert take('page.command')['action']=='reloadOriginal'
    native('session','overlay','close'); take('page.close'); outcome(page,'dismissed')
    print('PASS legacy presenter explicitly refuses file bridge while URL reload and terminal remain available')
finally:
    for channel in channels:
        if channel.process.poll() is None: channel.stop()
    if presenter.process.poll() is None: presenter.stop()
