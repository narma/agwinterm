"""Real Mac-origin resources rendered by the isolated Windows WebView2 viewer."""
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import time

config=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
control=config['socket']; root=Path(control).parent
assert str(root).startswith('/tmp/aw-stage4-peer-') and root.parent==Path('/tmp')
ssh=['ssh','-o','BatchMode=yes','-o','ConnectTimeout=10',config['destination']]
env={**os.environ,'SSH_AUTH_SOCK':config['agent']}
def remote(words, data=None):
    p=subprocess.run(ssh+[shlex.join(words)],input=data,text=True,capture_output=True,env=env,timeout=30)
    if p.returncode:
        # Pinned agtermctl uses a nonzero status for pending/dismissed selector results.
        reply=json.loads(p.stdout) if 'overlay' in words and 'result' in words else {}
        assert reply.get('ok') and reply.get('result',{}).get('pageOutcome',{}).get('outcome') in ['pending','dismissed'],(words,p.stdout,p.stderr)
    return p.stdout
def peer(*args):
    reply=json.loads(remote([str(root/'agterm.app/Contents/MacOS/agtermctl'),*args,'--socket',control,'--json']))
    assert reply['ok'],reply
    return reply['result']
def native(*args):
    p=subprocess.run([config['cli'],*args,'--pipe',config['pipe'],'--window',config['window'],'--json'],capture_output=True,text=True,timeout=30)
    assert p.returncode==0,(args,p.stdout,p.stderr)
    return json.loads(p.stdout)['result']
def state(predicate, timeout=40):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        rows=[p for w in native('tree')['workspaces'] for s in w['sessions'] if s['id']==config['session'] for p in s.get('htmlOverlays',[]) or []]
        if predicate(rows): return rows
        time.sleep(.2)
    diagnostic=[s for w in native('tree')['workspaces'] for s in w['sessions'] if s['id']==config['session']]
    raise AssertionError(('viewer page state did not converge',rows,diagnostic,peer('tree')))
def outcome(page, expected):
    deadline=time.monotonic()+30
    while time.monotonic()<deadline:
        result=peer('session','overlay','result','--page',page,'--target',config['origin'])
        value=result.get('pageOutcome',result)
        if value.get('outcome')==expected: return value
        time.sleep(.2)
    raise AssertionError(('Mac page outcome did not settle',result))
directory=root/('pages-'+config['pipe'])
prefix='<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="стиль %.css"><p id="probe">Mac asset</p>'
hold=prefix+'''<script>addEventListener('load',()=>{document.title=getComputedStyle(document.querySelector('#probe')).color==='rgb(12, 34, 56)'?'AW_VIEWER_ASSET_OK':'AW_VIEWER_ASSET_FAILED'});</script>'''
submit=prefix+'''<iframe srcdoc="&lt;script&gt;parent.agterm.request('session.overlay.submit',{args:{value:'SUBFRAME_ATTACK'}})&lt;/script&gt;"></iframe><script>addEventListener('load',async()=>{
 const denied=async(cmd,options)=>{try{await agterm.request(cmd,options);return false}catch{return true}};
 const result={asset:getComputedStyle(document.querySelector('#probe')).color==='rgb(12, 34, 56)',
 global:await denied('session.new'),target:await denied('session.type',{target:'active',args:{text:'SHOULD_NOT_RUN'}})};
 await agterm.request('session.status',{args:{status:'blocked'}});result.status=true;
 await agterm.request('session.overlay.submit',{args:{value:JSON.stringify(result)}});
});</script>'''
navigate=prefix+'''<script>addEventListener('load',()=>{document.title='AW_NAV_START';if(!sessionStorage.visited){sessionStorage.visited='1';setTimeout(()=>location.href='hold.html',300)}});</script>'''
remote(['python3','-c',"import json,pathlib,sys; root=pathlib.Path(sys.argv[1]); root.mkdir(); [(root/name).write_text(text) for name,text in json.load(sys.stdin).items()]",str(directory)],json.dumps({'hold.html':hold,'navigate.html':navigate,'submit.html':submit,'стиль %.css':'#probe { color: rgb(12, 34, 56); }'}))
def opened(name):
    return peer('session','overlay','open','--html',str(directory/name),'--cwd',str(directory),'--js','--navigation','--target',config['origin'])
try:
    opened('hold.html')
    state(lambda rows:len(rows)==1 and rows[0]['state']=='loaded' and rows[0].get('title')=='AW_VIEWER_ASSET_OK')
    for action,hidden in [('hide',True),('show',False)]:
        peer('session','overlay',action,'--target',config['origin']); state(lambda rows:len(rows)==1 and rows[0]['hidden']==hidden)
    peer('session','overlay','resize','--size-percent','65','--target',config['origin'])
    for _ in range(3): peer('session','overlay','reload','--target',config['origin'])
    state(lambda rows:len(rows)==1 and rows[0]['state']=='loaded' and rows[0].get('title')=='AW_VIEWER_ASSET_OK')
    peer('session','overlay','close','--target',config['origin']); state(lambda rows:not rows)
    print('PASS actual Mac-origin HTML/CSS/Unicode, hide/show/resize/reload and viewer cleanup')
    opened('navigate.html')
    state(lambda rows:len(rows)==1 and rows[0].get('title')=='AW_VIEWER_ASSET_OK' and rows[0].get('canGoBack'))
    peer('session','overlay','navigate','back','--target',config['origin']); state(lambda rows:len(rows)==1 and rows[0].get('title')=='AW_NAV_START')
    peer('session','overlay','navigate','forward','--target',config['origin']); state(lambda rows:len(rows)==1 and rows[0].get('title')=='AW_VIEWER_ASSET_OK')
    peer('session','overlay','close','--target',config['origin']); state(lambda rows:not rows)
    print('PASS actual WebView2 back/forward preserves the origin page')
    opened_page=opened('submit.html')
    assert 'pageID' in opened_page,opened_page
    result=json.loads(outcome(opened_page['pageID'],'submitted')['value'])
    assert result=={'asset':True,'global':True,'target':True,'status':True},result
    state(lambda rows:not rows)
    print('PASS actual WebView2 bridge confines commands to origin page/session and retains Mac submission')
    url_page=peer('session','overlay','open','--url','https://example.com','--navigation','--target',config['origin'])
    state(lambda rows:len(rows)==1 and rows[0]['state']=='loaded')
    native('session','overlay','close','--target',config['session']); state(lambda rows:not rows)
    outcome(url_page['pageID'],'dismissed')
    print('PASS actual Mac-origin URL page and viewer close settles origin without a resource channel')
    for loss in ['page-disconnect','disconnect']:
        opened_page=opened('hold.html')
        state(lambda rows:len(rows)==1 and rows[0]['state']=='loaded')
        subprocess.run(['python3',str(Path(__file__).with_name('presentation-peer-probe.py')),loss,config['destination'],control,config['origin']],env=env,check=True,timeout=30)
        state(lambda rows:not rows)
        outcome(opened_page['pageID'],'dismissed')
        time.sleep(2)
        state(lambda rows:not rows)
        print('PASS actual Mac-origin '+loss+' settles page, releases viewer and never reopens it')
finally:
    try: peer('session','overlay','close','--target',config['origin'])
    except Exception: pass
