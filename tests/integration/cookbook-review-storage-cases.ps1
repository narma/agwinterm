# Runs inside the existing disposable cookbook fixture.
foreach ($completion in @('submit','cancel')) {
    $waiter = Start-Process py -ArgumentList '-3',$reviewScript,'review',$document,'--fit','--output',$backup,'--timeout','20' -WindowStyle Hidden -PassThru -RedirectStandardOutput "$directory/review-$completion-out.txt" -RedirectStandardError "$directory/review-$completion-err.txt"
    try {
        $waitingPage = $null
        for ($i=0; $i -lt 100 -and -not $waitingPage; $i++) {
            $waitingPage = (Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner | ForEach-Object htmlOverlays | Select-Object -First 1
            if (-not $waitingPage) { Start-Sleep -Milliseconds 100 }
        }
        Require ($null -ne $waitingPage) "actual $completion review opens with active adapter waiter"
        Add-Content -Encoding utf8 $waitingPage.file "<script>setTimeout(()=>document.getElementById('$completion-btn').click(),200)</script>"
        for ($i=0; $i -lt 100 -and $waitingPage.state -eq 'loading'; $i++) {
            Start-Sleep -Milliseconds 50
            $waitingPage = (Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner | ForEach-Object htmlOverlays | Select-Object -First 1
        }
        if ($waitingPage) { [void](Call @('session','overlay','reload','--target',$owner)) }
        Require ($waiter.WaitForExit(15000)) "actual $completion review completes adapter waiter"
        $expected = if ($completion -eq 'submit') {0} else {2}
        Require ($waiter.ExitCode -eq $expected) "actual $completion template produces expected adapter exit code"
        $savedResult = [IO.File]::ReadAllText($backup)
        Require (($completion -eq 'submit' -and $savedResult -ceq '') -or ($completion -eq 'cancel' -and $savedResult.Trim() -eq 'dismissed')) "actual $completion writes final backup outcome"
    } finally { if (-not $waiter.HasExited) { $waiter.Kill($true); $waiter.WaitForExit() } }
}

$storagePeer = (Call @('session','new','--name','storage-peer','--no-select')).result
$storageOwners = @($owner,$storagePeer)
for ($index=0; $index -lt 2; $index++) {
    $storageFile = Join-Path $directory "storage-$index.html"
    & py -3 $reviewScript render $document $storageFile
    Require ($LASTEXITCODE -eq 0) 'review storage fixture renders'
    $probeScript = @'
<script>
const marker='MARKER', prior=localStorage.getItem('owner');
localStorage.setItem('owner',marker);
const loads=Number(localStorage.getItem('loads')||0)+1;localStorage.setItem('loads',loads);
if(loads===1){document.getElementById('file-btn').click();document.querySelector('.editor textarea').value='Saved '+marker;document.querySelector('[data-act="save"]').click();}
document.getElementById('file-btn').click();document.querySelector('.editor textarea').value='Draft '+marker;
const probe=document.createElement('input');probe.readOnly=true;probe.setAttribute('aria-label','Review storage probe');document.body.append(probe);
setInterval(()=>probe.value=JSON.stringify({prior,loads,draft:document.querySelector('.editor textarea').value,saved:Object.keys(localStorage).filter(k=>k.startsWith('webrev:')).map(k=>localStorage.getItem(k)).join(''),background:getComputedStyle(document.documentElement).getPropertyValue('--agterm-background')}),50);
</script>
'@
    Add-Content -Encoding utf8 $storageFile ($probeScript.Replace('MARKER',"review-$index"))
    Require ((Call @('session','overlay','open','--html',$storageFile,'--cwd',$directory,'--js','--bridge','review','--target',$storageOwners[$index])).ok) 'independent review storage page opens'
}
$otherTheme = @(((Call @('theme','list')).result -split "`n") | Where-Object { $_ -ne 'Tokyo Night' -and $_ })[0]
Require ((Call @('theme','set',$otherTheme)).ok) 'theme changes with two review pages alive'
for ($index=0; $index -lt 2; $index++) {
    [void](Call @('session','select','--target',$storageOwners[$index]))
    $probeValue = (Web-Element 'Review storage probe').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    for ($i=0; $i -lt 30 -and -not $probeValue.Current.Value; $i++) { Start-Sleep -Milliseconds 50 }
    $probe = $probeValue.Current.Value | ConvertFrom-Json
    Require ($null -eq $probe.prior -and $probe.loads -eq 1) 'two reviews do not read each other storage and theme does not reload them'
    Require ($probe.draft -eq "Draft review-$index" -and $probe.saved.Contains("Saved review-$index")) 'saved note and unsaved review draft survive theme and session switching'
    [void](Call @('session','overlay','reload','--target',$storageOwners[$index]))
    Start-Sleep -Milliseconds 300
    $probeValue = (Web-Element 'Review storage probe').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    for ($i=0; $i -lt 30 -and -not $probeValue.Current.Value; $i++) { Start-Sleep -Milliseconds 50 }
    $probe = $probeValue.Current.Value | ConvertFrom-Json
    Require ($probe.prior -eq "review-$index" -and $probe.loads -eq 2 -and $probe.saved.Contains("Saved review-$index")) 'explicit review reload preserves only its own saved notes'
    [void](Call @('session','overlay','close','--target',$storageOwners[$index]))
}
[void](Call @('session','select','--target',$owner))
[void](Call @('session','close','--target',$storagePeer))
