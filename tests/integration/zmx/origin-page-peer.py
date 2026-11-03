"""Actual Mac browser consumes Windows/WSL-origin pages; no desktop input injection."""
import json
from pathlib import Path
import subprocess
import sys
import time

run=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
root=Path(run['root'])
assert root.parent==Path('/tmp') and root.name.startswith('aw-stage3-origin-')
cli=run['config']['cli']
selectors=['--pipe',run['config']['pipe'],'--window',run['window'],'--target',run['pane'],'--json']
def native(*args):
    routing=selectors[:4]+selectors[6:] if args[0]=='tree' else selectors
    result=subprocess.run([cli,*args,*routing],capture_output=True,text=True,timeout=25)
    assert result.returncode==0,(args,result.stdout,result.stderr)
    return json.loads(result.stdout)['result']
def state(page, predicate, timeout=30):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        rows=[p for w in native('tree')['workspaces'] for s in w['sessions'] for p in s.get('htmlOverlays',[]) if p['id']==page]
        if rows and predicate(rows[0]): return rows[0]
        time.sleep(.15)
    raise AssertionError(('remote page state did not converge',rows))
def outcome(page, expected):
    deadline=time.monotonic()+30
    while time.monotonic()<deadline:
        result=native('session','overlay','result','--page',page)['pageOutcome']
        if result['outcome']==expected: return result
        time.sleep(.15)
    raise AssertionError(('remote page outcome did not settle',result))

directory=root/'peer-page'; directory.mkdir()
(directory/'стиль %.css').write_text('#probe { color: rgb(12, 34, 56); }')
prefix='<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="стиль %.css"><p id="probe">origin asset</p>'
(directory/'hold.html').write_text(prefix+'''<script>
addEventListener('load',()=>{document.title=getComputedStyle(document.querySelector('#probe')).color==='rgb(12, 34, 56)'?'AW_PAGE_ASSET_OK':'AW_PAGE_ASSET_FAILED'});
</script>''')
(directory/'navigate.html').write_text(prefix+'''<script>
addEventListener('pageshow',async()=>{try{await agterm.request('session.status',{args:{status:'idle'}});document.title='AW_NAV_START';if(!sessionStorage.visited){sessionStorage.visited='1';setTimeout(()=>location.href='hold.html',300)}}catch{document.title='AW_NAV_BRIDGE_FAILED'}});
</script>''')
(directory/'history.html').write_text(prefix+'''<script>
addEventListener('load',()=>{history.pushState({},'', '#one');history.pushState({},'', '#two');document.title='AW_HISTORY_READY'});
addEventListener('popstate',()=>setTimeout(async()=>{try{await agterm.request('session.status',{args:{status:'idle'}});document.title='AW_HISTORY_'+location.hash.slice(1)}catch(e){document.title='AW_HISTORY_FAILED: '+e.message}},150));
</script>''')
def open_file(file, review=False):
    return native('session','overlay','open','--html',str(directory/file),'--cwd',str(directory),'--js','--navigation',
                  *(['--bridge','review'] if review else []))['pageID']
page=None
try:
    page=open_file('hold.html')
    state(page,lambda row:row['state']=='loaded' and row['title']=='AW_PAGE_ASSET_OK')
    # No resource traffic or heartbeat is required on a healthy claimed page channel.
    time.sleep(36)
    native('session','overlay','hide'); state(page,lambda row:row['hidden'])
    native('session','overlay','show'); state(page,lambda row:not row['hidden'])
    native('session','overlay','resize','--size-percent','65')
    for _ in range(3): native('session','overlay','reload')
    state(page,lambda row:row['state']=='loaded' and row['title']=='AW_PAGE_ASSET_OK')
    native('session','overlay','close'); outcome(page,'dismissed'); page=None
    print('PASS actual Mac file/CSS/Unicode rendering, idle resource channel, hide/show/resize/reload and origin dismissal')
    page=open_file('navigate.html')
    state(page,lambda row:row.get('title')=='AW_PAGE_ASSET_OK' and row.get('canGoBack'))
    native('session','overlay','navigate','back'); state(page,lambda row:row.get('title')=='AW_NAV_START')
    native('session','overlay','navigate','forward'); state(page,lambda row:row.get('title')=='AW_PAGE_ASSET_OK')
    native('session','overlay','close'); outcome(page,'dismissed'); page=None
    print('PASS actual Mac viewer back/forward preserves origin page identity')
    page=open_file('history.html'); state(page,lambda row:row.get('title')=='AW_HISTORY_READY')
    native('session','overlay','navigate','back'); state(page,lambda row:row.get('title')=='AW_HISTORY_one')
    native('session','overlay','navigate','forward'); state(page,lambda row:row.get('title')=='AW_HISTORY_two')
    native('session','overlay','close'); outcome(page,'dismissed'); page=None
    print('PASS actual Mac bridge remains usable after same-document back/forward')

    for review in [False,True]:
        script='''<script>
addEventListener('load',async()=>{
 const denied=async(cmd,options)=>{try{await agterm.request(cmd,options);return false}catch{return true}};
 const result={asset:getComputedStyle(document.querySelector('#probe')).color==='rgb(12, 34, 56)',
   global:await denied('session.new'), target:await denied('session.type',{target:'active',args:{text:'SHOULD_NOT_RUN'}})};
 try {await agterm.request('session.status',{args:{status:'blocked'}});result.status=true}catch{result.status=false}
 await agterm.request('session.overlay.submit',{args:{value:JSON.stringify(result)}});
});
</script>'''
        (directory/'submit.html').write_text(prefix+script)
        page=open_file('submit.html',review)
        result=json.loads(outcome(page,'submitted')['value']); page=None
        assert result=={'asset':True,'global':True,'target':True,'status':not review},result
    print('PASS actual Mac general/review bridge boundaries, target/global refusal and authoritative submission')

    page=native('session','overlay','open','--url','https://example.com','--navigation')['pageID']
    state(page,lambda row:row['state']=='loaded',timeout=45)
    native('session','overlay','close'); outcome(page,'dismissed'); page=None
    print('PASS actual Mac URL page and origin dismissal without a file grant')
    for loss in ['page-channel-loss','presentation-loss']:
        page=open_file('hold.html')
        state(page,lambda row:row['state']=='loaded')
        subprocess.run(['python3',str(Path(__file__).with_name('origin-acceptance.py')),'peer-'+loss,sys.argv[1]],check=True,timeout=30)
        outcome(page,'dismissed'); page=None
        time.sleep(2)
        if loss=='presentation-loss':
            subprocess.run(['python3',str(Path(__file__).with_name('origin-acceptance.py')),'peer-page-ready',sys.argv[1]],check=True,timeout=50)
        assert not [p for w in native('tree')['workspaces'] for s in w['sessions'] for p in s.get('htmlOverlays',[]) or []]
        print('PASS actual Mac viewer '+loss+' settles origin page and never reopens it')
finally:
    if page is not None:
        try: native('session','overlay','close')
        except Exception: pass
