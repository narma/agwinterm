param([string]$Distro='Ubuntu', [string]$LinuxUser='alzo', [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$AgentSocket, [Parameter(Mandatory)][string]$OriginSocket,
    [Parameter(Mandatory)][string]$Bridge, [string]$TerminalFixture, [string]$FaultBridge)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslAgtermRemote -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$PSNativeCommandUseErrorActionPreference=$false
if ($OriginSocket -notlike '/tmp/aw-stage2-peer-*/control.sock') { throw 'Only an isolated origin fixture socket is accepted' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe=Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl=Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$id='agwinterm-remote-test-'+[guid]::NewGuid().ToString('N')
$state=Join-Path $env:LOCALAPPDATA $id
$ui=$null
function Ctl([string[]]$Command) {
    $raw=& $ctl @Command --pipe $id --json --timeout 90
    if ($LASTEXITCODE -ne 0) { throw "Control failed: $Command $raw" }
    $reply=($raw -join "`n") | ConvertFrom-Json -Depth 100
    if (-not $reply.ok) { throw "Control refused: $raw" }
    return $reply.result
}
function Start-Ui {
    $script:ui=Start-Process $exe -ArgumentList @('--app-id',$id,'--pipe',$id,'--default-session-host','server-rust') -WindowStyle Hidden -PassThru
    for ($i=0;$i -lt 60;$i++) {
        Start-Sleep -Milliseconds 200
        $null=& $ctl ping --pipe $id --json 2>$null
        if ($LASTEXITCODE -eq 0) { return }
    }
    throw 'Test UI did not become ready'
}
function Refuse([string[]]$Command) {
    $raw=& $ctl @Command --pipe $id --json --timeout 30
    if ($LASTEXITCODE -eq 0) { throw "Unexpected success: $Command $raw" }
}
function Pane-State([string]$pane) {
    foreach ($window in (Ctl @('window','list')).windows | Where-Object open) {
        foreach ($row in (Ctl @('tree','--window',$window.id)).workspaces.sessions) {
            if ($row.liveSessions -and $row.liveSessions.PSObject.Properties[$pane]) { return $row.liveSessions.$pane }
        }
    }
    return $null
}
function Await-Pane([string]$pane) {
    for ($i=0;$i -lt 120;$i++) {
        $value=Pane-State $pane
        if ($value.recovery -in @('host-adopted','remote-reattached')) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw "Remote attachment failed: $($value | ConvertTo-Json -Depth 12)"
}
try {
    $null=New-Item -ItemType Directory -Path $state
    @('session-host = server-rust','restore-commands = true','restore-buffer = true') | Set-Content (Join-Path $state 'agwinterm.conf')
    Start-Ui
    $window=[string]((Ctl @('window','list')).windows | Where-Object active)[0].id
    $remote=@('--distribution',$Distro,'--user',$LinuxUser,'--host',$Destination,'--agent-socket',$AgentSocket,'--origin-socket',$OriginSocket)
    $inventory=Ctl (@('zmx','remote','list')+$remote)
    $candidate=@($inventory.Sessions | Where-Object CanImportTerminal)[0]
    if (-not $candidate) { throw 'No isolated origin terminal available' }
    $import=Ctl (@('zmx','remote','attach')+$remote+@('--bridge',$Bridge,'--session',$candidate.Session.Id,'--window',$window))
    foreach ($item in $import.panes) {
        $pane=[string]$item.paneId
        $live=Await-Pane $pane
        if ($live.ownership -ne 'borrowed' -or $live.terminate -or $live.restart) { throw 'Incorrect borrowed capabilities' }
        $marker='AW_REMOTE_'+[guid]::NewGuid().ToString('N')
        $null=Ctl @('session','type',"printf '$marker\n'`n",'--target',$pane)
        Start-Sleep -Milliseconds 500
        $screen=Ctl @('session','text','--target',$pane)
        if ($screen -notlike "*$marker*") { throw 'Authoritative origin read missed acknowledged input' }
        $null=Ctl @('session','readonly','on','--target',$pane)
        Refuse @('session','type','SHOULD-NOT-TYPE','--target',$pane)
        $null=Ctl @('session','readonly','off','--target',$pane)
        Refuse @('session','text','--styles','--target',$pane)
        Refuse @('zmx','remote','terminate','--target',$pane)
        Write-Host "PASS borrowed import/input/read $pane role=$($live.role)"
    }
    $ui.Kill(); $ui.WaitForExit()
    foreach ($file in Get-ChildItem (Join-Path $state 'windows') -Filter '*.json') {
        $checkpoint=Get-Content $file.FullName -Raw | ConvertFrom-Json -Depth 100
        foreach ($saved in $checkpoint.Workspaces.Sessions.Panes | Where-Object { $_.LiveBinding.Provider -eq 'agterm-ssh' }) {
            foreach ($field in @('AgentResume','RestoreCommand','Command')) { $saved | Add-Member $field 'SHOULD-NOT-REPLAY-REMOTE' -Force }
            $saved | Add-Member Buffer @('SHOULD-NOT-SEED-REMOTE') -Force
        }
        $checkpoint | ConvertTo-Json -Depth 100 | Set-Content $file.FullName
    }
    Start-Ui
    foreach ($item in $import.panes) {
        $live=Await-Pane $item.paneId
        if ($live.recovery -ne 'host-adopted') { throw 'UI restart did not adopt the exact Rust transport' }
        if ((Ctl @('session','text','--target',$item.paneId)) -like '*SHOULD-NOT-*') { throw 'Remote restore injected saved commands/buffer' }
    }
    Write-Host 'PASS exact-ticket UI restart'
    $hosts=@(Get-CimInstance Win32_Process -Filter "Name = 'agwinterm-ptyhost.exe'" | Where-Object { $_.CommandLine.Contains($id) })
    if ($hosts.Count -ne 1) { throw 'Expected exactly one test-owned Rust host' }
    Stop-Process -Id $hosts[0].ProcessId -Force
    Start-Sleep -Seconds 3
    foreach ($item in $import.panes) {
        $live=Await-Pane $item.paneId
        if ($live.recovery -ne 'remote-reattached' -or $live.role -ne 'follower') { throw 'Rust-host recovery must reattach as follower' }
    }
    Write-Host 'PASS Rust-host loss reconnects the same workloads as follower'
    $hosts=@(Get-CimInstance Win32_Process -Filter "Name = 'agwinterm-ptyhost.exe'" | Where-Object { $_.CommandLine.Contains($id) })
    $transports=@(Get-CimInstance Win32_Process -Filter "Name = 'wsl.exe'" | Where-Object { $_.ParentProcessId -eq $hosts[0].ProcessId -and $_.CommandLine.Contains($Bridge) })
    if (-not $transports.Count) { throw 'Test-owned SSH transport was not found' }
    Stop-Process -Id $transports[0].ProcessId -Force
    Start-Sleep -Seconds 3
    foreach ($item in $import.panes) { $null=Await-Pane $item.paneId }
    Write-Host 'PASS SSH transport loss reconnects without replay'
    $null=Ctl @('session','close',$import.sessionId)
    Start-Sleep -Milliseconds 300
    $entry=@((Ctl @('recent','list')).entries)[0]
    $opened=Ctl @('recent','open',$entry.id)
    $reopened=@($opened.members[0].paneIds)
    if ($reopened.Count -ne $import.panes.Count) { throw 'Recent lost the remote split topology' }
    foreach ($pane in $reopened) { $null=Await-Pane $pane }
    Write-Host 'PASS borrowed close/Recent existing-only reopen'
    $null=Ctl @('recent','clear')
    $null=Ctl @('restore','clear')
    foreach ($pane in $reopened) { $null=Ctl @('session','text','--target',$pane) }
    Write-Host 'PASS metadata clear preserves borrowed work'
    $null=Ctl @('zmx','remote','lead','--target',$reopened[0])
    $leader=Await-Pane $reopened[0]
    if ($leader.role -ne 'leader') { throw 'Explicit leadership did not claim origin geometry' }
    foreach ($pane in $reopened) { $null=Ctl @('zmx','remote','detach','--target',$pane) }
    $after=Ctl (@('zmx','remote','list')+$remote)
    if (-not ($after.Sessions | Where-Object { $_.Session.Id -eq $candidate.Session.Id })) { throw 'Origin session lost after detach' }
    Write-Host 'PASS explicit leadership and borrowed detach'
    for ($race=0;$race -lt 3;$race++) {
        $closing=Ctl (@('zmx','remote','attach')+$remote+@('--bridge',$Bridge,'--session',$candidate.Session.Id,'--window',$window))
        $null=Ctl @('session','close',$closing.sessionId)
        for ($wait=0;$wait -lt 80 -and (Pane-State $closing.panes[0].paneId);$wait++) { Start-Sleep -Milliseconds 100 }
        Start-Sleep -Milliseconds 250
        if (Pane-State $closing.panes[0].paneId) { throw 'Late remote start resurrected a closed pane' }
    }
    Write-Host 'PASS immediate import/close races do not resurrect panes'
    if ($FaultBridge) {
        $partial=Ctl (@('zmx','remote','attach')+$remote+@('--bridge',$FaultBridge,'--session',$candidate.Session.Id,'--window',$window))
        if ($partial.panes.Count -ne 2) { throw 'Fault fixture needs an origin split' }
        $left=[string]$partial.panes[0].paneId; $right=[string]$partial.panes[1].paneId
        $null=Await-Pane $left
        for ($i=0;$i -lt 60;$i++) {
            $failed=Pane-State $right
            if ($failed.recovery -eq 'unavailable') { break }
            Start-Sleep -Milliseconds 100
        }
        if ($failed.recovery -ne 'unavailable' -or $failed.remoteAttach) { throw 'Partial split reported false success' }
        $suffix=[guid]::NewGuid().ToString('N')
        Refuse @('session','type',"printf '%s%s\n' 'LOST-ACK-' '$suffix'`n",'--target',$left)
        Start-Sleep -Milliseconds 300
        $screen=[string](Ctl @('session','text','--target',$left))
        if (($screen.Split('LOST-ACK-'+$suffix)).Count -ne 2) { throw 'Input was missing or replayed after lost acknowledgement' }
        $null=Ctl @('session','close',$partial.sessionId)
        Write-Host 'PASS partial split failure and single delivery after lost input reply'
    }
    if ($TerminalFixture) {
        & dotnet (Join-Path $repo 'tests/Agwinterm.RemoteTerminalHarness/bin/Release/net10.0-windows/Agwinterm.RemoteTerminalHarness.dll') $Distro $LinuxUser $Destination $AgentSocket $OriginSocket $Bridge $TerminalFixture (Join-Path (Split-Path $exe) 'agwinterm-ptyhost.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Remote terminal continuity harness failed' }
    }
} finally {
    if ($ui -and -not $ui.HasExited) { $ui.Kill(); $ui.WaitForExit() }
    # The enclosing private desktop job reaps all remaining test-owned local transports.
}
