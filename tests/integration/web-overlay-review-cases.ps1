# Runs in web-overlay-parity.ps1's private instance. No native/global input.
function Wait-PageTitle([string]$Title) {
    for($i=0;$i -lt 100;$i++) {
        if(@((Pages) | Where-Object title -eq $Title).Count){return}
        Start-Sleep -Milliseconds 100
    }
    throw "Missing title $Title : $((Pages) | ConvertTo-Json -Depth 5 -Compress)"
}
$frameChild=Join-Path $directory 'frame-child.html'
@'
<!doctype html><p>Rendered child</p><script src="frame-child.js"></script>
'@ | Set-Content $frameChild
@'
let isolated=false;
try { parent.agterm.request('session.rename',{args:{name:'UNSAFE CHILD'}}); }
catch(e) { isolated=true; }
parent.postMessage({kind:'frame-test',isolated},'*');
'@ | Set-Content (Join-Path $directory 'frame-child.js')
$frameParent=Join-Path $directory 'frame-parent.html'
@'
<!doctype html><title>Waiting for child</title><script>
addEventListener('message',e=>{if(e.data.kind==='frame-test')document.title=e.data.isolated?'child-isolated':'CHILD ESCAPED';});
</script><iframe src="frame-child.html"></iframe>
<iframe srcdoc="<script>parent.agterm.request('session.rename',{args:{name:'UNSAFE SRCDOC'}})</script>"></iframe>
'@ | Set-Content $frameParent
[void](Call @('session','rename','Before frames','--target',$owner))
Require ((Call @('session','overlay','open','--html',$frameParent,'--cwd',$directory,'--js','--target',$owner)).ok) 'general bridge page with hostile same-origin child opens'
Wait-PageTitle 'child-isolated'
Start-Sleep -Milliseconds 300
Require ((Session).name -eq 'Before frames') 'same-origin and srcdoc children cannot call the parent bridge'
[void](Call @('session','overlay','close','--target',$owner))
$sameUrl=Join-Path $directory 'same-url-frame.html'
@'
<!doctype html><script>
if(window===top) {
  addEventListener('message',e=>{if(e.data==='same-url-isolated')document.title=e.data;});
  addEventListener('DOMContentLoaded',()=>{const f=document.createElement('iframe');f.src=location.href;document.body.append(f);});
} else {
  try { parent.agterm.request('session.rename',{args:{name:'UNSAFE SAME URL'}}); }
  catch(e) { parent.postMessage('same-url-isolated','*'); }
}
</script><body></body>
'@ | Set-Content $sameUrl
Require ((Call @('session','overlay','open','--html',$sameUrl,'--cwd',$directory,'--js','--target',$owner)).ok) 'same URL can be embedded as an isolated child'
Wait-PageTitle 'same-url-isolated'
Require ((Session).name -eq 'Before frames') 'frame identity rather than URL determines bridge isolation'
[void](Call @('session','overlay','close','--target',$owner))
Require ((Call @('session','split','on','--target',$owner)).ok) 'review split enables'
$paneScript=Join-Path $directory 'pane-commands.html'
@'
<!doctype html><script>
agterm.request('session.type',{args:{text:'echo RIGHT_PANE_BRIDGE\r'}})
.then(()=>agterm.request('session.status',{args:{status:'active'}}))
.then(()=>document.title='pane-commands-done');
</script>
'@ | Set-Content $paneScript
$page=Call @('session','overlay','open','--html',$paneScript,'--js','--pane','right','--target',$owner)
Require $page.ok 'right-pane command page opens'
Wait-PageTitle 'pane-commands-done'
$rightId=(Session).paneIds[1]
Require ($rightId -and $rightId -ne $owner) 'right page has its own pane identity'
$text=Call @('session','text','--target',$rightId)
$otherText=Call @('session','text','--target',$owner)
Require ($text.result -match 'RIGHT_PANE_BRIDGE' -and $otherText.result -notmatch 'RIGHT_PANE_BRIDGE') 'implicit bridge typing reaches only its owning right pane'
Require ((Call @('session','swap','--target',$owner)).ok) 'review panes swap'
[void](Call @('session','overlay','reload','--pane','left','--target',$owner))
Wait-PageTitle 'pane-commands-done'
Require ((Session).paneIds[0] -eq $rightId -and ((Pages) | Where-Object id -eq $page.result.pageID).pane -eq 'left') 'bridge keeps its owning pane after swap'
$child=Call @('session','overlay','open','--html',"$directory/page.html",'--companion','--target',$rightId)
Require ($child.ok -and $child.result.parent -eq $owner) 'exact non-primary pane target opens a companion for its parent'
[void](Call @('session','close','--target',$child.result.id))
[void](Call @('session','overlay','close','--pane','left','--target',$owner))
$early=Call @('session','overlay','open','--html',"$directory/page.html",'--target',$owner,'--navigation')
Require $early.ok 'early reload fixture opens'
Require ((Call @('session','overlay','reload','--target',$owner)).ok) 'reload during controller initialization is coalesced'
Wait-Pages 1
[void](Call @('session','overlay','close','--target',$owner))
