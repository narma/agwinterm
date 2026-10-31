# Runs inside web-overlay-parity.ps1's private application/pipe.
function Wait-Title([string]$Expected) {
    for ($i=0; $i -lt 100; $i++) {
        $page = (Pages)[0]
        if ($page.title -eq $Expected) { return $page }
        Start-Sleep -Milliseconds 100
    }
    throw "Missing title $Expected : $($page | ConvertTo-Json -Compress -Depth 5)"
}
function Open-State {
    Require ((Call @('session','overlay','open','--url',($url+'state'),'--persistent','--js','--target',$owner)).ok) 'persistent URL opens'
    Wait-Pages 1
    for ($i=0; $i -lt 100; $i++) {
        try { $state = (Pages)[0].title | ConvertFrom-Json; if ($state.bridge) { return $state } } catch { }
        Start-Sleep -Milliseconds 100
    }
    throw 'Storage probe did not finish'
}
$bridgeFile = Join-Path $directory 'bridge.html'
$aboutFile = Join-Path $directory 'about.html'
'<!doctype html><script>addEventListener("load",()=>setTimeout(()=>location.href="about:blank",50))</script>' | Set-Content $aboutFile
Require ((Call @('session','overlay','open','--html',$aboutFile,'--js','--target',$owner)).ok) 'local page can request about navigation'
for ($i=0; $i -lt 100; $i++) {
    $aboutPage = (Pages)[0]
    if ($aboutPage.currentUrl -eq 'about:blank' -and $aboutPage.state -eq 'loaded') { break }
    Start-Sleep -Milliseconds 100
}
Require ($aboutPage.currentUrl -eq 'about:blank' -and $aboutPage.state -eq 'loaded') 'about blank navigation completes without a web failure'
[void](Call @('session','overlay','close','--target',$owner))
@'
<!doctype html><script>
agterm.request('session.rename',{args:{name:'Bridge owner'}})
.then(()=>agterm.request('zmx.reset')).then(()=>document.title='UNSAFE')
.catch(()=>document.title='restricted command denied');
</script>
'@ | Set-Content $bridgeFile
Require ((Call @('session','overlay','open','--html',$bridgeFile,'--js','--target',$owner)).ok) 'trusted general bridge opens'
[void](Wait-Title 'restricted command denied')
Require ((Session).name -eq 'Bridge owner') 'general bridge inherits its owning session'
Require ((Call @('session','overlay','close','--target',$owner)).ok) 'general bridge page closes'
$mainWindow = @((Call @('window','list')).result.windows | Where-Object open)[0].id
$otherWindow = (Call @('window','new','--name','Bridge override test')).result
$otherOwner = (Call @('tree','--window',$otherWindow)).result.workspaces[0].sessions[0].id
$windowOptions = @{window=$otherWindow;args=@{name='Window override'}} | ConvertTo-Json -Compress
$targetOptions = @{target=$otherOwner;args=@{name='Target override'}} | ConvertTo-Json -Compress
@"
<!doctype html><script>
agterm.request('session.rename',$windowOptions)
.then(()=>agterm.request('session.rename',$targetOptions))
.then(()=>agterm.request('session.rename',{args:{name:'Bridge owner'}}))
.then(()=>document.title='Explicit overrides passed')
.catch(e=>document.title='Override error: '+e.message);
</script>
"@ | Set-Content $bridgeFile
Require ((Call @('session','overlay','open','--html',$bridgeFile,'--js','--target',$owner,'--window',$mainWindow)).ok) 'background bridge page opens while another window is active'
[void](Wait-Title 'Explicit overrides passed')
Require ((Call @('tree','--window',$otherWindow)).result.workspaces[0].sessions[0].name -eq 'Target override') 'explicit window and cross-window target override inherited bridge context'
Require ((Session).name -eq 'Bridge owner') 'implicit bridge context remains attached to the background owner'
[void](Call @('session','overlay','close','--target',$owner,'--window',$mainWindow))
Require ((Call @('window','close',$otherWindow)).ok) 'bridge override test window closes'
[void](Call @('window','select',$mainWindow))
$submitFile = Join-Path $directory 'submit.html'
"<!doctype html><script>agterm.request('session.overlay.submit',{args:{value:''}})</script>" | Set-Content $submitFile
$answer = Call @('session','overlay','open','--html',$submitFile,'--js','--block','--timeout','15','--target',$owner)
Require ($ctlExitCode -eq 0 -and $answer.ok -and $answer.result.pageOutcome.outcome -eq 'submitted' -and $answer.result.pageOutcome.value -ceq '') 'block returns successful empty submission with exit 0'
for ($i=0; $i -lt 40 -and (Pages).Count; $i++) { Start-Sleep -Milliseconds 50 }
$cancelFile = Join-Path $directory 'cancel.html'
'<!doctype html><script>window.close()</script>' | Set-Content $cancelFile
$answer = Call @('session','overlay','open','--html',$cancelFile,'--js','--block','--timeout','15','--target',$owner)
Require ($ctlExitCode -eq 2 -and $answer.ok -and $answer.result.pageOutcome.outcome -eq 'dismissed') "window.close dismisses only its page with exit 2 ($($answer | ConvertTo-Json -Compress -Depth 5))"
for ($i=0; $i -lt 40 -and (Pages).Count; $i++) { Start-Sleep -Milliseconds 50 }
$timeoutAnswer = & $Ctl session overlay open --html "$directory/page.html" --block --timeout 1 --target $owner --pipe $identity --json 2>"$directory/block-timeout.txt"
$script:ctlExitCode = $LASTEXITCODE
Require ($ctlExitCode -eq 1 -and -not $timeoutAnswer) 'blocking transport timeout returns failure exit 1'
Wait-Pages 1
$timeoutId = (Pages)[0].id
Require ((Call @('session','overlay','result','--page',$timeoutId)).result.pageOutcome.outcome -eq 'pending') 'blocking timeout leaves the page pending and recoverable'
Require ((Call @('session','overlay','close','--target',$owner)).ok) 'timed-out page can be closed explicitly'
$closingOwner = (Call @('session','new','--name','Blocked page owner','--no-select')).result
$waiter = Start-Process $Ctl -ArgumentList @('session','overlay','open','--html',('"'+$directory+'/page.html"'),'--block','--timeout','15','--target',$closingOwner,'--pipe',$identity,'--json') -WindowStyle Hidden -PassThru -RedirectStandardOutput "$directory/block-owner.json" -RedirectStandardError "$directory/block-owner.err"
try {
    for ($i=0; $i -lt 100; $i++) {
        $closingNode = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $closingOwner)[0]
        if ($closingNode.htmlOverlays) { break }; Start-Sleep -Milliseconds 50
    }
    Require ([bool]$closingNode.htmlOverlays) 'blocking page belongs to temporary owner'
    Require ((Call @('session','close','--target',$closingOwner)).ok) 'owner teardown closes blocking page'
    Require ($waiter.WaitForExit(10000)) 'owner teardown releases blocking CLI'
    Require ($waiter.ExitCode -eq 2 -and (Get-Content "$directory/block-owner.json" -Raw | ConvertFrom-Json).result.pageOutcome.outcome -eq 'dismissed') 'owner teardown returns dismissal with exit 2'
} finally { if (-not $waiter.HasExited) { $waiter.Kill(); $waiter.WaitForExit() } }
$original = Call @('session','overlay','open','--html',"$directory/page.html",'--target',$owner)
$companion = Call @('session','overlay','open','--html',"$directory/page.html",'--companion','--target',$owner)
Require ($original.ok -and $companion.ok -and $companion.result.parent -eq $owner) 'companion has independent parent relationship and slot'
$companionId = $companion.result.id
$node = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $companionId)[0]
Require ($node.content -eq 'page') 'companion readback identifies page content'
Require (-not (Call @('session','type','echo must-not-run','--target',$companionId)).ok) 'companion refuses terminal input'
Require (-not (Call @('session','split','on','--target',$companionId)).ok) 'companion refuses split'
Require (-not (Call @('session','overlay','hide','--target',$companionId)).ok) 'companion cannot hide its only page'
Require ((Call @('session','overlay','close','--target',$companionId)).ok) 'closing companion page closes its row'
Require (@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $companionId).Count -eq 0) 'companion row is removed'
Require ((Pages)[0].id -eq $original.result.pageID) 'parent overlay survives companion close'
Require ((Call @('session','overlay','close','--target',$owner)).ok) 'parent overlay closes'
$parentToClose = (Call @('session','new','--name','Companion parent to close','--no-select')).result
$childToClose = Call @('session','overlay','open','--html',"$directory/page.html",'--companion','--target',$parentToClose)
Require $childToClose.ok 'companion opens under temporary parent'
Require ((Call @('session','close','--undoable','--target',$childToClose.result.id)).result.undoable) 'companion can enter its own undo grace'
Require ((Call @('session','close','--target',$parentToClose)).ok) 'parent teardown succeeds with a companion'
Require (@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $childToClose.result.id).Count -eq 0) 'parent teardown removes its companion row'
Require ((Call @('session','overlay','result','--page',$childToClose.result.pageID)).result.pageOutcome.outcome -eq 'dismissed') 'parent teardown dismisses its companion page'
$imageFile = Join-Path $directory 'image.bin'
[IO.File]::WriteAllBytes($imageFile,[Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a12kAAAAASUVORK5CYII='))
Require ((Call @('session','overlay','open','--image',$imageFile,'--target',$owner)).ok) 'image format is detected from bytes rather than filename'
Wait-Pages 1
Require ((Pages)[0].image -eq $imageFile -and -not (Pages)[0].javascript) 'image readback has no script grant'
Require ((Call @('session','overlay','close','--target',$owner)).ok) 'image closes'
foreach ($format in @('gif','webp','jpeg')) {
    $formatPath = Join-Path $directory ("image-"+$format+".bin")
    if ($format -eq 'jpeg') {
        $bitmap = [Drawing.Bitmap]::new(8,4)
        try { $bitmap.Save($formatPath,[Drawing.Imaging.ImageFormat]::Jpeg) } finally { $bitmap.Dispose() }
    } else {
        $encoded = if ($format -eq 'gif') { 'R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7' } else { 'UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEADsD+JaQAA3AAAAAA' }
        [IO.File]::WriteAllBytes($formatPath,[Convert]::FromBase64String($encoded))
    }
    Require ((Call @('session','overlay','open','--image',$formatPath,'--target',$owner)).ok) "$format bytes open as an image"
    Wait-Pages 1
    Require ((Pages)[0].state -eq 'loaded') "$format image decodes in WebView2"
    Require ((Call @('session','overlay','close','--target',$owner)).ok) "$format image closes"
}
$undoFile = Join-Path $directory 'undo.html'
"<!doctype html><script>document.title=crypto.randomUUID()</script><textarea>draft</textarea>" | Set-Content $undoFile
$undoPage = Call @('session','overlay','open','--html',$undoFile,'--js','--target',$owner)
Wait-Pages 1
$identityBefore = (Pages)[0].title
$softReply = Call @('session','close','--undoable','--target',$owner)
Require ($softReply.ok -and $softReply.result.undoable -and $owner -in $softReply.result.closed) 'undoable close reports retained web owner'
Require (-not (Session)) 'soft-closed owner leaves visible tree'
$recentId = ((Call @('recent','list')).result.entries | Where-Object name -eq 'Bridge owner' | Select-Object -First 1).id
Require ((Call @('recent','open',$recentId)).ok) 'recent open undoes live close within grace interval'
Require ((Pages)[0].id -eq $undoPage.result.pageID -and (Pages)[0].title -eq $identityBefore) 'undo retains browser identity and document without reload'
$batchPeer = (Call @('session','new','--name','Undo peer')).result
$batchReply = Call @('session','close','--targets',"$owner,$batchPeer")
Require ($batchReply.ok -and $batchReply.result.undoable) 'batch close retains one undo group'
$batchEntry = @((Call @('recent','list')).result.entries | Where-Object name -eq 'Undo peer')[0].id
$batchRestore = Call @('recent','open',$batchEntry)
Require ($batchRestore.ok -and @($batchRestore.result.members).Count -eq 2) 'either recent member restores the entire batch'
Require ((Pages)[0].id -eq $undoPage.result.pageID -and (Pages)[0].title -eq $identityBefore) 'batch undo preserves document identity'
$workspaceId = (Call @('workspace','new','--name','Undo workspace')).result
Require ((Call @('session','move',$workspaceId,'--target',$owner)).ok) 'web owner moves into workspace to close'
Require ((Call @('workspace','delete','--target',$workspaceId)).ok) 'workspace containing a web owner soft closes'
$workspaceEntry = @((Call @('recent','list')).result.entries | Where-Object { $_.name -eq 'Undo workspace' -and $_.kind -eq 'workspace' })[0].id
Require ((Call @('recent','open',$workspaceEntry)).ok) 'workspace undo restores the live group'
Require ((Pages)[0].id -eq $undoPage.result.pageID -and (Pages)[0].title -eq $identityBefore) 'workspace undo preserves document identity'
Require ((Call @('session','close','--undoable','--target',$owner)).ok) 'second soft close begins expiry test'
Start-Sleep -Milliseconds 3300
$outcome = (Call @('session','overlay','result','--page',$undoPage.result.pageID)).result.pageOutcome
Require ($outcome.outcome -eq 'dismissed') 'grace expiry releases browser and dismisses pending outcome'
$hardOwner = (Call @('session','new','--name','Explicit immediate close')).result
$hardPage = Call @('session','overlay','open','--html',$undoFile,'--target',$hardOwner)
$hardReply = Call-Raw @{cmd='session.close';target=$hardOwner;args=@{undoable=$false}}
Require ($hardReply.ok -and -not $hardReply.result.undoable) 'explicit undoable false bypasses grace'
Require ((Call @('session','overlay','result','--page',$hardPage.result.pageID)).result.pageOutcome.outcome -eq 'dismissed') 'immediate close releases the page before replying'
$script:owner = (Call @('tree')).result.workspaces[0].sessions[0].id
$server = Start-Process py -ArgumentList @('-3',('"'+[IO.Path]::GetFullPath("$PSScriptRoot/../fixtures/web-parity-server.py")+'"'),('"'+$directory+'/server.json"')) -WindowStyle Hidden -PassThru
for ($i=0; $i -lt 60 -and -not (Test-Path "$directory/server.json"); $i++) { Start-Sleep -Milliseconds 100 }
$url = (Get-Content "$directory/server.json" -Raw | ConvertFrom-Json).url
$state = Open-State
Require ($null -eq $state.prior -and -not $state.cookie -and $null -eq $state.indexed -and $state.bridge -eq 'undefined') 'fresh persistent profile has no data and URL has no bridge'
Require (-not (Call @('browser','clear')).ok) 'storage clear refuses a live persistent page'
$persistentId = (Pages)[0].id
Require ((Call @('session','close','--undoable','--target',$owner)).ok) 'persistent owner enters undo grace'
Require (-not (Call @('browser','clear')).ok) 'storage clear refuses a persistent page retained for Undo'
$persistentRecent = @((Call @('recent','list')).result.entries | Where-Object kind -eq 'session')[0].id
Require ((Call @('recent','open',$persistentRecent)).ok) 'persistent page can be restored during grace'
Require ((Pages)[0].id -eq $persistentId) 'persistent undo restores the same browser instance'
Require ((Call @('session','overlay','close','--target',$owner)).ok) 'persistent page closes'
$state = Open-State
Require ($state.prior -eq 'saved' -and $state.cookie -and $state.indexed -eq 'saved') 'cookies localStorage and IndexedDB survive page close'
Require ((Call @('window','close')).ok) 'private application closes for restart test'
Require ($process.WaitForExit(10000)) 'private application exited before restart'
$process = Start-Process $Exe -ArgumentList @('--app-id',$identity,'--pipe',$identity,'--no-restore') -WindowStyle Hidden -PassThru
for ($i=0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 100; try { if ((Call @('ping')).ok) { break } } catch { } }
$script:owner = (Call @('tree')).result.workspaces[0].sessions[0].id
$script:mainWindow = @((Call @('window','list')).result.windows | Where-Object open)[0].id
$state = Open-State
Require ($state.prior -eq 'saved' -and $state.cookie -and $state.indexed -eq 'saved') 'persistent origin storage survives application restart'
Require ([Math]::Abs((Pages)[0].zoom - 1.1) -lt 0.001) 'shared web zoom survives application restart'
Require ((Call @('tree')).result.linkOpenMode -eq 'overlay75') 'browser-link preference survives application restart'
Require ((Call @('session','overlay','close','--target',$owner)).ok) 'persistent page closes before clear'
Require ((Call @('browser','clear')).ok) 'storage clear waits for removal'
$state = Open-State
Require ($null -eq $state.prior -and -not $state.cookie -and $null -eq $state.indexed) 'clear removes cookies localStorage and IndexedDB'
Require ((Call @('session','overlay','close','--target',$owner)).ok) 'final persistent page closes'
