# Called only from the routed survival fixture; shares its isolated control pipe.
$ErrorActionPreference = 'Stop'
function Pane-State([string]$pane) {
    foreach ($window in (Ctl @('window','list')).windows | Where-Object open) {
        foreach ($row in (Ctl @('tree','--window',$window.id)).workspaces.sessions) {
            if ($row.liveSessions -and $row.liveSessions.PSObject.Properties[$pane]) { return $row.liveSessions.$pane }
        }
    }
    return $null
}
function Await-Pane([string]$pane) {
    for ($attempt=0; $attempt -lt 80; $attempt++) {
        $value = Pane-State $pane
        if ($value.recovery -in @('fresh-started','host-adopted','zmx-reattached')) { return $value }
        Start-Sleep -Milliseconds 100
    }
    throw "Pane did not recover: $pane $($value | ConvertTo-Json -Depth 10)"
}
$owned = [Collections.Generic.List[string]]::new()
try {
    $anchor = Ctl @('session','new','--profile','ZMX matrix','--no-select')
    $owned.Add([string]$anchor)
    $null = Await-Pane $anchor
    $null = Ctl @('session','select',$anchor)
    $null = Ctl @('session','scratch','on','--target',$anchor)
    $null = Ctl @('session','type',"printf 'SCRATCH-ORDINARY\n'`n",'--target','active')
    Start-Sleep -Milliseconds 500
    $scratchText = Ctl @('session','text','--target','active')
    if ($scratchText -notlike '*SCRATCH-ORDINARY*' -or $scratchText -like '*[[]WSL live session[]]*') { throw 'Inherited scratch profile did not retain ordinary launch behavior' }
    $null = Ctl @('session','scratch','off','--target',$anchor)
    for ($race=0; $race -lt 3; $race++) {
        $closing = [string](Ctl @('session','new','--profile','ZMX matrix','--no-select'))
        $owned.Add($closing)
        $null = Ctl @('session','close',$closing)
        for ($attempt=0; $attempt -lt 100 -and (Pane-State $closing); $attempt++) { Start-Sleep -Milliseconds 100 }
        if (Pane-State $closing) { throw 'In-flight owned close did not finish' }
        Start-Sleep -Milliseconds 150
        if (Pane-State $closing) { throw 'Late startup callback resurrected closed pane' }
    }
    if (@((Ctl @('zmx','list','--profile','ZMX matrix')).sessions).Count -ne 1) { throw 'In-flight close leaked or duplicated a workload' }
    Write-Host 'PASS three immediate create/close races leave only the anchor workload and no resurrected pane'
    $first = Ctl @('session','new','--profile','ZMX matrix','--no-select')
    $owned.Add([string]$first)
    $firstState = Await-Pane $first
    $second = Ctl @('session','split','on','--target',$first)
    $owned.Add([string]$second)
    $secondState = Await-Pane $second
    if ($firstState.bindingId -eq $secondState.bindingId) { throw 'Split reused workload binding' }
    $null = Ctl @('session','restore','--target',$second,'--command','SHOULD-NOT-RUN-PIN')
    $null = Ctl @('session','swap','--target',$first)
    if ((Pane-State $first).bindingId -ne $firstState.bindingId -or (Pane-State $second).bindingId -ne $secondState.bindingId) { throw 'Swap changed binding identity' }
    $null = Ctl @('session','split','close','--target',$first)
    for ($attempt=0; $attempt -lt 80 -and (Pane-State $first); $attempt++) { Start-Sleep -Milliseconds 100 }
    if (Pane-State $first) { throw 'Owned split close did not finish' }
    if ((Pane-State $second).bindingId -ne $secondState.bindingId) { throw 'Survivor promotion changed binding' }
    # Session identity survives removal of its original carrier pane.
    $null = Ctl @('session','close',$first)
    for ($attempt=0; $attempt -lt 80 -and (Pane-State $second); $attempt++) { Start-Sleep -Milliseconds 100 }
    if (Pane-State $second) { throw 'Owned session close did not finish' }
    $entries = @((Ctl @('recent','list')).entries)
    if (-not $entries.Count) { throw 'Owned close was not saved to Recent' }
    $opened = Ctl @('recent','open',$entries[0].id)
    $reopened = [string]$opened.members[0].paneIds[0]
    $owned.Add($reopened)
    $reopenedState = Await-Pane $reopened
    if ($reopenedState.bindingId -eq $secondState.bindingId -or $reopenedState.daemon.Name -eq $secondState.daemon.Name) { throw 'Recent reused closed workload identity' }
    $screen = Ctl @('session','text','--target',$reopened)
    if ($screen -like '*SHOULD-NOT-RUN-PIN*') { throw 'Recent executed retained pin' }
    $ownerWindow = [string]((Ctl @('window','list')).windows | Where-Object active)[0].id
    $peerWindow = [string](Ctl @('window','new','--name','Live restore peer'))
    Start-Sleep -Milliseconds 500
    foreach ($row in (Ctl @('tree','--window',$peerWindow)).workspaces.sessions) {
        foreach ($pane in $row.liveSessions.PSObject.Properties.Name) {
            $null = Ctl @('zmx','detach','--target',$pane,'--window',$peerWindow)
        }
    }
    $peer = [string](Ctl @('session','new','--profile','ZMX matrix','--window',$peerWindow,'--no-select'))
    $owned.Add($peer)
    $peerState = Await-Pane $peer
    $otherUser = $null
    if ($PeerDistro -and $PeerUser -and $PeerExecutable) {
        $otherUser = [string](Ctl @('session','new','--profile','ZMX peer user','--window',$peerWindow,'--no-select'))
        $owned.Add($otherUser)
        $otherUserState = Await-Pane $otherUser
        if ($otherUserState.endpoint.Distribution -ne $PeerDistro -or $otherUserState.endpoint.User -ne $PeerUser) { throw 'Peer profile resolved to wrong Linux endpoint' }
    }
    $refused = & $ctl zmx retry --target $peer --window $ownerWindow --pipe $id --json
    if ($LASTEXITCODE -ne 1) { throw "Cross-window target did not refuse: $refused" }
    # Both starts durably save their transport tickets. Crash only this test UI,
    # retaining the library's open-window set and both Rust transports.
    $ui.Kill(); $ui.WaitForExit()
    Start-Ui
    $restored = Await-Pane $reopened
    $restoredPeer = Await-Pane $peer
    if ($otherUser -and (Await-Pane $otherUser).bindingId -ne $otherUserState.bindingId) { throw 'Second user endpoint did not restore' }
    if ($restored.bindingId -ne $reopenedState.bindingId -or $restored.daemon.Name -ne $reopenedState.daemon.Name -or
        $restoredPeer.bindingId -ne $peerState.bindingId -or $restoredPeer.daemon.Name -ne $peerState.daemon.Name) { throw 'Multi-window checkpoint changed workload identities' }
    Write-Host 'PASS exact window routing and two-window checkpoint restore preserve independent live bindings'
    $null = Ctl @('window','select',$ownerWindow)
    $profilePath = Join-Path $state 'profiles.json'
    $backupPath = Join-Path $state 'profiles.before-disable.json'
    Copy-Item -LiteralPath $profilePath -Destination $backupPath
    $checkpointBackup = Join-Path $state 'checkpoint-before-disable'
    New-Item -ItemType Directory -Path $checkpointBackup | Out-Null
    Get-ChildItem (Join-Path $state 'windows') -Filter '*.json' | Copy-Item -Destination $checkpointBackup
    $disabled = Get-Content $profilePath -Raw | ConvertFrom-Json
    ($disabled.profiles | Where-Object name -eq 'ZMX matrix').zmx.enabled = $false
    $disabled.default = 'ZMX matrix'
    $disabled.profiles += @{name='ZMX unavailable';command='wsl.exe';args=@('-d',$Distro,'--exec','bash');zmx=@{enabled=$true;executable='/nonexistent/agwinterm-test-zmx'}}
    $disabled | ConvertTo-Json -Depth 10 | Set-Content $profilePath
    $ui.Kill(); $ui.WaitForExit(); Start-Ui
    if ((Await-Pane $reopened).bindingId -ne $reopenedState.bindingId) { throw 'Disabling profile or changing defaults lost saved binding' }
    $ordinary = [string](Ctl @('session','new','--profile','ZMX matrix','--window',$ownerWindow,'--no-select'))
    Start-Sleep -Milliseconds 500
    if (Pane-State $ordinary) { throw 'Disabled profile created a live binding' }
    $unavailable = [string](Ctl @('session','new','--profile','ZMX unavailable','--window',$ownerWindow,'--no-select'))
    $owned.Add($unavailable)
    for ($attempt=0; $attempt -lt 80 -and (Pane-State $unavailable).recovery -ne 'unavailable'; $attempt++) { Start-Sleep -Milliseconds 100 }
    if ((Pane-State $unavailable).recovery -ne 'unavailable') { throw 'Missing dependency did not retain unavailable pane' }
    $refused = & $ctl zmx restart --target $unavailable --window $ownerWindow --source shell --pipe $id --json
    if ($LASTEXITCODE -ne 1) { throw 'Unverified dependency failure incorrectly allowed fresh restart' }
    $null = Ctl @('zmx','detach','--target',$unavailable,'--window',$ownerWindow)
    Copy-Item -LiteralPath $backupPath -Destination $profilePath -Force
    $ui.Kill(); $ui.WaitForExit(); Start-Ui
    $null = Await-Pane $reopened
    if (Pane-State $ordinary) { throw 'Enabling a profile silently migrated its old ordinary checkpoint' }
    $null = Ctl @('session','close',$ordinary,'--window',$ownerWindow)
    Write-Host 'PASS isolated backup/disable/changed-default restore, ordinary new profile launch and missing-dependency refusal'
    $null = Ctl @('recent','clear')
    $null = Ctl @('restore','clear')
    if ((Pane-State $reopened).bindingId -ne $reopenedState.bindingId) { throw 'Clear changed live workload' }
    Write-Host 'PASS split/swap/survivor identity, owned close, fresh Recent reopen and metadata-only clear'
    if ($Distro -eq 'Agwinterm-Zmx-Gate-20261102') {
        # This exact distribution was created solely for this acceptance gate.
        # Never issue global --shutdown or stop a user's distribution.
        & wsl.exe --terminate $Distro
        if ($LASTEXITCODE -ne 0) { throw 'Disposable distribution termination failed' }
        Start-Sleep -Seconds 2
        $lostPanes = @($anchor, $reopened, $peer)
        if ($otherUser -and $PeerDistro -eq $Distro) { $lostPanes += $otherUser }
        foreach ($pane in $lostPanes) {
            foreach ($window in (Ctl @('window','list')).windows | Where-Object open) {
                if (@((Ctl @('tree','--window',$window.id)).workspaces.sessions | Where-Object { $_.liveSessions -and $_.liveSessions.PSObject.Properties[$pane] }).Count) {
                    $null = Ctl @('zmx','retry','--target',$pane,'--window',$window.id)
                }
            }
            for ($attempt=0; $attempt -lt 80 -and (Pane-State $pane).recovery -ne 'missing'; $attempt++) { Start-Sleep -Milliseconds 100 }
            if ((Pane-State $pane).recovery -ne 'missing') { throw 'Distribution shutdown must report missing, never survival' }
        }
        $null = Ctl @('zmx','restart','--target',$peer,'--window',$peerWindow,'--source','shell')
        $afterShutdown = Await-Pane $peer
        if ($afterShutdown.bindingId -eq $peerState.bindingId -or $afterShutdown.daemon.Name -eq $peerState.daemon.Name -or
            $afterShutdown.recovery -ne 'fresh-started') { throw 'Explicit post-shutdown restart did not create a new incarnation' }
        $inventory = Ctl @('zmx','list','--profile','ZMX matrix','--window',$peerWindow)
        if (@($inventory.sessions).Count -ne 1) { throw 'Shutdown retry silently recreated other workloads' }
        if ($otherUser -and $PeerDistro -ne $Distro) {
            $null = Ctl @('session','text','--target',$otherUser,'--window',$peerWindow)
            if ((Pane-State $otherUser).bindingId -ne $otherUserState.bindingId) { throw 'Disposable distro shutdown affected second endpoint' }
            Write-Host 'PASS independent distribution/user endpoint survives checkpoint restore and unrelated distro shutdown'
        }
        elseif ($otherUser) { Write-Host 'PASS independent Linux users restore to their own endpoints and both report missing after disposable shutdown' }
        Write-Host 'PASS disposable distribution shutdown produces missing; one explicit shell source creates one new workload'
    } else { Write-Host 'UNVERIFIED distribution shutdown: requires the dedicated disposable fixture distribution' }
} finally {
    foreach ($pane in $owned) {
        if (Pane-State $pane) {
            foreach ($window in (Ctl @('window','list')).windows | Where-Object open) {
                $found = @((Ctl @('tree','--window',$window.id)).workspaces.sessions | Where-Object { $_.liveSessions -and $_.liveSessions.PSObject.Properties[$pane] })
                if ($found.Count) {
                    $null = Ctl @('zmx','terminate','--target',$pane,'--force','--window',$window.id)
                    $null = Ctl @('zmx','detach','--target',$pane,'--window',$window.id)
                }
            }
        }
    }
}
