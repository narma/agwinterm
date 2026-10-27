# Real Win32 split toggle regression: private state and pipe; no global input.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe = Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl = Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$id = 'agwinterm-split-retention-' + [guid]::NewGuid().ToString('N')
$parent = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData'))
$state = Join-Path $parent $id
$process = $null
function Ctl([string[]]$a) {
    $raw = (& $ctl @a --pipe $id --json) -join "`n"
    $r = $raw | ConvertFrom-Json
    if (-not $r.ok) { throw "$a : $($r.error)" }
    $r.result
}
function Check([bool]$ok, [string]$message) {
    if (-not $ok) { throw $message }
    "PASS $message"
}
function RefusesHidden([string[]]$a) {
    $r = ((& $ctl @a --pipe $id --json) -join "`n") | ConvertFrom-Json
    Check (-not $r.ok -and $r.error -match 'hidden') "Hidden target refused: $a"
}
function AwaitText([string]$pane, [string]$marker) {
    for ($i = 0; $i -lt 60; $i++) {
        if ((Ctl @('session','text','--all','--target',$pane)).Contains($marker)) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Missing output: $marker"
}
try {
    New-Item -ItemType Directory -Path $state | Out-Null
    @{default='Retention cmd'; profiles=@(@{name='Retention cmd';command='cmd.exe';args=@('/d');cwd=$state})} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $state 'profiles.json')
    $process = Start-Process $exe -ArgumentList @('--app-id',$id,'--pipe',$id,'--no-restore','--default-session-host','inprocess') -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        try { $null = Ctl @('ping'); break } catch { if ($i -eq 39) { throw }; Start-Sleep -Milliseconds 250 }
    }
    $session = Ctl @('session','new','--profile','Retention cmd')
    $pane = Ctl @('session','split','toggle','--target',$session)
    $null = Ctl @('command','run','set RETAINED_VALUE=still-here','--mode','send','--target',$pane)
    $null = Ctl @('command','run','echo BEFORE-HIDE','--mode','send','--target',$pane)
    AwaitText $pane 'BEFORE-HIDE'
    $null = Ctl @('command','run','for /L %i in (1,1,300) do @echo HISTORY-FILL-%i','--mode','send','--target',$pane)
    AwaitText $pane 'HISTORY-FILL-300'
    Check (-not (Ctl @('session','text','--target',$pane)).Contains('BEFORE-HIDE')) 'History marker has left the visible screen before hiding'
    AwaitText $pane 'BEFORE-HIDE'
    $null = Ctl @('session','resize','--split-ratio','0.3','--target',$session)
    $null = Ctl @('session','appearance','set','--color','#123456','--target',$pane)
    $appearanceBefore = Ctl @('session','appearance','get','--target',$pane)
    $null = Ctl @('session','restore','echo original-pin','--target',$pane)
    $overlay = Ctl @('session','overlay','open','--pane','right','--command','echo PANE-OVERLAY-ALIVE & cmd.exe /d /k','--target',$session)
    $done = Join-Path $state 'hidden-output.txt'
    $null = Ctl @('command','run',"ping -n 3 127.0.0.1 >nul & echo HIDDEN-DONE & echo done> `"$done`"",'--mode','send','--target',$pane)
    Check ((Ctl @('session','split','toggle','--target',$session)) -eq $session) 'Toggle hides secondary and focuses primary'
    $sessionOverlay = Ctl @('session','overlay','open','--command','echo SESSION-OVERLAY-ALIVE & cmd.exe /d /k','--target',$session)
    foreach ($hiddenTarget in @($pane, $overlay)) {
        foreach ($action in @('close','text','result')) {
            RefusesHidden @('session','overlay',$action,'--target',$hiddenTarget)
        }
    }
    AwaitText $sessionOverlay 'SESSION-OVERLAY-ALIVE'
    AwaitText $overlay 'PANE-OVERLAY-ALIVE'
    'PASS Refused hidden overlay operations preserve both overlay processes'
    $null = Ctl @('session','overlay','close','--target',$session)
    RefusesHidden @('session','appearance','get','--target',$pane)
    RefusesHidden @('session','appearance','set','--color','#abcdef','--target',$pane)
    RefusesHidden @('session','appearance','clear','--target',$pane)
    RefusesHidden @('session','restore','echo lost-pin','--target',$pane)
    RefusesHidden @('session','restore','none','--target',$pane)
    $null = Ctl @('session','status','blocked','--target',$pane)
    $null = Ctl @('notify','Hidden split notification','--target',$pane)
    $null = Ctl @('notify','Hidden overlay notification','--target',$overlay)
    $hiddenNode = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $session
    Check ($hiddenNode.paneCount -le 1 -and $pane -notin $hiddenNode.paneIds) 'Hidden pane stays out of visible tree'
    Check ($hiddenNode.notifications -eq 2) 'Hidden shell and overlay notifications are counted against their owning session'
    $null = Ctl @('session','select',$session)
    $hiddenNode = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $session
    Check (-not $hiddenNode.notifications) 'Visiting owner clears hidden notifications'
    for ($i = 0; $i -lt 60 -and -not (Test-Path -LiteralPath $done); $i++) { Start-Sleep -Milliseconds 100 }
    Check (Test-Path -LiteralPath $done) 'Command keeps running while hidden'
    Check ((Ctl @('session','split','toggle','--target',$session)) -eq $pane) 'Toggle reuses the exact pane'
    $node = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $session
    Check ($node.status -eq 'blocked') 'Agent status written while hidden survives reopening'
    Check ($node.restoreCommands.$pane -eq 'echo original-pin') 'Refused hidden restore commands leave the existing pin unchanged'
    $appearanceAfter = Ctl @('session','appearance','get','--target',$pane)
    Check (($appearanceAfter | ConvertTo-Json -Depth 10 -Compress) -eq ($appearanceBefore | ConvertTo-Json -Depth 10 -Compress)) 'Refused appearance commands do not mutate the pane'
    AwaitText $pane 'HIDDEN-DONE'
    $null = Ctl @('command','run','echo VALUE=%RETAINED_VALUE%','--mode','send','--target',$pane)
    AwaitText $pane 'VALUE=still-here'
    AwaitText $pane 'BEFORE-HIDE'
    'PASS Shell variables and scrollback survive'
    $node = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $session
    Check ([Math]::Abs($node.splitRatios[0] - 0.3) -lt 0.01) 'Divider ratio survives'
    $null = Ctl @('session','split','off','--target',$session)
    Check ((Ctl @('session','split','on','--target',$session)) -eq $pane) 'Explicit off/on also restores the pane'
    $null = Ctl @('session','swap','--target',$session)
    $null = Ctl @('session','split','off','--target',$session)
    Check ((Ctl @('session','split','on','--target',$session)) -eq $session) 'Swap then hide restores the original primary in the secondary slot'
    $null = Ctl @('session','split','close','--target',$pane)
    $fresh = Ctl @('session','split','on','--target',$session)
    Check ($fresh -ne $pane) 'Explicit close discards the terminal'
    $null = Ctl @('session','split','off','--target',$session)
    $null = Ctl @('session','close',$session)
    'PASS Session closes with a hidden split'
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    $full = [IO.Path]::GetFullPath($state)
    if ([IO.Path]::GetDirectoryName($full) -eq $parent -and [IO.Path]::GetFileName($full) -match '^agwinterm-split-retention-[a-f0-9]{32}$') {
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
}
