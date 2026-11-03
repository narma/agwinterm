param([string]$Distro='Ubuntu', [Parameter(Mandatory)][string]$LinuxUser, [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$AgentSocket, [Parameter(Mandatory)][string]$OriginSocket,
    [Parameter(Mandatory)][string]$Bridge, [string]$TerminalFixture, [string]$FaultBridge, [bool]$PassiveOnly=$false)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslAgtermRemote -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$PSNativeCommandUseErrorActionPreference=$false
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$OutputEncoding=[Text.UTF8Encoding]::new($false)
if ($OriginSocket -notlike '/tmp/aw-stage2-peer-*/control.sock' -and $OriginSocket -notlike '/tmp/aw-stage4-peer-*/control.sock') { throw 'Only an isolated origin fixture socket is accepted' }
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
        if ($LASTEXITCODE -eq 0 -and @((Ctl @('window','list')).windows | Where-Object { $_.open -and $_.active }).Count -eq 1) {
            # Window metadata is visible before Frontmost and the startup UI queue are ready.
            $null=& $ctl recent list --pipe $id --json 2>$null
            if ($LASTEXITCODE -eq 0) { return }
        }
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
function Peer-Ctl([string[]]$Command) {
    $peerCli=$OriginSocket.Replace('/control.sock','/agterm.app/Contents/MacOS/agtermctl')
    $words=@($peerCli)+$Command+@('--socket',$OriginSocket,'--json')
    $escape="'"+[char]34+"'"+[char]34+"'"
    $quoted=($words | ForEach-Object { "'"+$_.Replace("'", $escape)+"'" }) -join ' '
    $raw=& wsl.exe -d $Distro -u $LinuxUser --exec env "SSH_AUTH_SOCK=$AgentSocket" ssh -o BatchMode=yes -o StrictHostKeyChecking=yes $Destination $quoted
    $peerExit=$LASTEXITCODE
    $reply=($raw -join "`n") | ConvertFrom-Json -Depth 100
    $pageResult=$Command[0] -eq 'session' -and $Command[1] -eq 'overlay' -and $Command[2] -eq 'result' -and $reply.ok -and $reply.result.pageOutcome.outcome -in @('pending','dismissed')
    if ($peerExit -ne 0 -and -not $pageResult -and -not ($Command[0] -eq 'ask' -and $Command[1] -eq 'result' -and $reply.result -in @('pending','answered','escaped','cancelled'))) { throw "Peer control failed: $raw" }
    if($reply.PSObject.Properties['result'] -and $reply.PSObject.Properties['ok']) { return $reply.result }
    return $reply
}
function Await-Presentation([scriptblock]$Check) {
    for ($i=0; $i -lt 80; $i++) {
        $row=@((Ctl @('tree','--window',$window)).workspaces.sessions | Where-Object id -eq $import.sessionId)[0]
        if (& $Check $row) { return $row }
        Start-Sleep -Milliseconds 250
    }
    throw "Presentation condition timed out: $($row | ConvertTo-Json -Depth 30 -Compress)"
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
    if ($PassiveOnly) {
        Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class RemoteAskProbe {
    delegate bool Visit(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(Visit callback, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder b, int length);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h,int id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h,uint msg,IntPtr w,string text);
    public static bool SetWindowTextW(IntPtr h,string text) => SendMessageW(h,12,IntPtr.Zero,text)!=IntPtr.Zero;
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h,uint msg,IntPtr w,IntPtr l);
    public static IntPtr Find(uint pid) {
        IntPtr found=IntPtr.Zero;
        EnumWindows((h,p)=>{ uint owner; GetWindowThreadProcessId(h,out owner); var name=new StringBuilder(100); GetClassNameW(h,name,100);
            if(owner==pid && name.ToString()=="Agwinterm.NativeQuestion") {found=h;return false;} return true; },IntPtr.Zero);
        return found;
    }
}
'@
        foreach ($item in $import.panes) { $null=Await-Pane $item.paneId }
        $null=Await-Presentation { param($row) @($row.liveSessions.PSObject.Properties.Value | Where-Object presentationConnected).Count -gt 0 }
        $origin=[string]$candidate.Session.Id
        $peerRow=@((Peer-Ctl @('tree')).tree.workspaces.sessions | Where-Object id -eq $origin)[0]
        if($peerRow.ask.id) { $null=Peer-Ctl @('ask','cancel',[string]$peerRow.ask.id) }
        $geometry=Ctl @('session','metrics','--target',$import.panes[0].paneId) | ConvertTo-Json -Compress
        $null=Peer-Ctl @('session','context','STAGE4_PASSIVE_CONTEXT','--target',$origin)
        $null=Await-Presentation { param($row) $row.context -eq 'STAGE4_PASSIVE_CONTEXT' }
        $null=Peer-Ctl @('session','status','blocked','--target',$origin)
        $null=Await-Presentation { param($row) $row.status -eq 'blocked' }
        $null=Peer-Ctl @('session','hud','open','STAGE4_PASSIVE_HUD','--target',$origin,'--hide-after','3')
        $null=Await-Presentation { param($row) $row.hud.message -eq 'STAGE4_PASSIVE_HUD' }
        $withHud=Ctl @('session','metrics','--target',$import.panes[0].paneId) | ConvertTo-Json -Compress
        if ($geometry -ne $withHud) { throw 'Passive presentation changed terminal geometry' }
        $null=Await-Presentation { param($row) $null -eq $row.hud }
        $null=Peer-Ctl @('session','context','--clear','--target',$origin)
        $null=Peer-Ctl @('session','status','idle','--target',$origin)
        $null=Await-Presentation { param($row) -not $row.context -and $row.status -eq 'idle' }
        Write-Host 'PASS real agterm -> Agwinterm context/status/HUD updates, expiry and clears'
        foreach($item in $import.panes) { $null=Ctl @('zmx','remote','lead','--target',$item.paneId,'--window',$window) }
        $null=Await-Pane $import.panes[0].paneId
        $null=Await-Presentation { param($row) @($row.liveSessions.PSObject.Properties.Value | Where-Object presentationPresenter).Count -gt 0 }
        # Private-desktop LOCALAPPDATA is redirected; use the shared workspace for WSL input.
        $pageConfig=Join-Path $repo "artifacts/$id-page-peer.json"
        $linuxCtl=(& wsl.exe -d $Distro -u $LinuxUser --exec wslpath -u $ctl).Trim()
        @{socket=$OriginSocket;destination=$Destination;agent=$AgentSocket;cli=$linuxCtl;pipe=$id;window=$window;session=$import.sessionId;origin=$origin} | ConvertTo-Json | Set-Content $pageConfig
        $linuxConfig=(& wsl.exe -d $Distro -u $LinuxUser --exec wslpath -u $pageConfig).Trim()
        $pageProbe=(& wsl.exe -d $Distro -u $LinuxUser --exec wslpath -u "$PSScriptRoot/zmx/viewer-page-peer.py").Trim()
        $null=Ctl @('session','select','--target',$import.sessionId,'--window',$window)
        $pageJob=Start-ThreadJob -ArgumentList $Distro,$LinuxUser,$pageProbe,$linuxConfig -ScriptBlock {
            param($distribution,$account,$probe,$config)
            $ErrorActionPreference='Continue'
            & wsl.exe -d $distribution -u $account --exec python3 $probe $config 2>&1 | ForEach-Object { "$_" }
            if($LASTEXITCODE -ne 0) { throw 'Actual Mac-origin page acceptance failed' }
        }
        try {
            while($pageJob.State -in @('NotStarted','Running')) {
                if(Test-Path -LiteralPath ($pageConfig+'.history-click')) {
                    $null=& "$PSScriptRoot/wsl-page-history-ui.ps1" -OwnerProcess $ui.Id -RequestPath ($pageConfig+'.history-click')
                }
                Start-Sleep -Milliseconds 100
            }
            Receive-Job $pageJob -ErrorAction Continue | ForEach-Object { Write-Host $_ }
            if($pageJob.State -ne 'Completed') { throw 'Actual Mac-origin page acceptance failed' }
        } finally { Stop-Job $pageJob; Remove-Job $pageJob }
        $jobCommand='printf "AW_JOB_OS=%s CWD=%s\n" "$(uname -s)" "$PWD"; read answer; printf "AW_JOB_REPLY=%s\n" "$answer"; exit 7'
        $jobReply=Peer-Ctl @('session','overlay','open',$jobCommand,'--cwd','/tmp','--wait','--size-percent','60','--target',$origin)
        $jobRow=Await-Presentation { param($row) @($row.overlayDetails).Count -eq 1 }
        $overlayId=[string]$jobRow.overlayDetails[0].id
        for($i=0;$i -lt 60;$i++) {
            $jobText=[string](Ctl @('session','text','--target',$overlayId))
            if($jobText -match 'AW_JOB_OS=Darwin CWD=/') { break }
            Start-Sleep -Milliseconds 200
        }
        if($jobText -notmatch 'AW_JOB_OS=Darwin CWD=/(private\s*/)?tmp') { throw "Origin job did not run on Mac: $jobText" }
        $null=Ctl @('session','type',"accepted`r",'--target',$overlayId)
        $null=Await-Presentation { param($row) $row.overlayDetails[0].exited -eq $true }
        $jobText=[string](Ctl @('session','text','--target',$overlayId))
        if($jobText -notmatch 'AW_JOB_REPLY=accepted') { throw "Job PTY input failed: $jobText" }
        $jobResult=Peer-Ctl @('session','overlay','result','--target',$origin)
        if($jobResult.exitCode -ne 7) { throw "Incorrect authoritative Mac job result: $($jobResult|ConvertTo-Json -Compress)" }
        $null=Peer-Ctl @('session','overlay','close','--target',$origin)
        $null=Await-Presentation { param($row) @($row.overlayDetails | Where-Object { $_ }).Count -eq 0 }
        Write-Host 'PASS Mac-origin program executes on Darwin, retains cwd, receives PTY input and holds exit before origin close'
        $probe=(& wsl.exe -d $Distro -u $LinuxUser --exec wslpath -u "$PSScriptRoot/zmx/presentation-peer-probe.py").Trim()
        $null=Peer-Ctl @('session','overlay','open','printf MAPPED_PANE_JOB; sleep 2; exit 3','--pane','left','--wait','--target',$origin)
        $null=Await-Presentation { param($row) $row.overlayDetails[0].ownerPaneId -eq $import.panes[0].paneId -and $row.overlayDetails[0].exited }
        if((Peer-Ctl @('session','overlay','result','--pane','left','--target',$origin)).exitCode -ne 3) { throw 'Mapped pane result changed' }
        $null=Peer-Ctl @('session','overlay','close','--pane','left','--target',$origin)
        $null=Await-Presentation { param($row) @($row.overlayDetails | Where-Object { $_ }).Count -eq 0 }
        Write-Host 'PASS actual Mac-origin stable pane mapping and held pane job exit'
        foreach($loss in @('presentation','job-channel','origin-close')) {
            $command='printf "JOB_LOSS_READY\n"; read answer; printf "JOB_LOSS_REPLY=%s\n" "$answer"; exit 9'
            $null=Peer-Ctl @('session','overlay','open',$command,'--wait','--size-percent','50','--target',$origin)
            $jobRow=Await-Presentation { param($row) @($row.overlayDetails | Where-Object { $_ }).Count -eq 1 }
            $jobOverlay=[string]$jobRow.overlayDetails[0].id
            for($i=0;$i -lt 60;$i++) {
                if([string](Ctl @('session','text','--target',$jobOverlay)) -match 'JOB_LOSS_READY') { break }
                Start-Sleep -Milliseconds 200
            }
            if($i -eq 60) { throw 'Job did not reach ready state' }
            $null=Peer-Ctl @('session','overlay','resize','--size-percent','70','--target',$origin)
            $null=Await-Presentation { param($row) $row.overlayDetails[0].geometry.percent -eq 70 }
            if($loss -eq 'origin-close') { $null=Peer-Ctl @('session','overlay','close','--target',$origin) }
            else {
                $fault=if($loss -eq 'presentation') {'disconnect'} else {'job-disconnect'}
                & wsl.exe -d $Distro -u $LinuxUser --exec env "SSH_AUTH_SOCK=$AgentSocket" python3 $probe $fault $Destination $OriginSocket $origin
                if($LASTEXITCODE -ne 0) { throw "Could not isolate $loss" }
                if($loss -eq 'presentation') {
                    Start-Sleep -Milliseconds 1000
                    $null=Ctl @('session','type',"after-loss`r",'--target',$jobOverlay)
                    $null=Await-Presentation { param($row) @($row.overlayDetails | Where-Object { $_ }).Count -eq 0 }
                    if((Peer-Ctl @('session','overlay','result','--target',$origin)).exitCode -ne 9) { throw 'Orphan job did not finish on its original channel' }
                } else {
                    $null=Await-Presentation { param($row) $row.overlayDetails[0].exited -eq $true }
                    $null=Ctl @('session','overlay','close','--target',$import.sessionId)
                }
            }
            $null=Await-Presentation { param($row) @($row.overlayDetails | Where-Object { $_ }).Count -eq 0 }
            $null=Await-Presentation { param($row) @($row.liveSessions.PSObject.Properties.Value | Where-Object presentationPresenter).Count -gt 0 }
            Write-Host "PASS actual Mac-origin job resize and $loss preserve base terminal and release viewer surface"
        }
        & wsl.exe -d $Distro -u $LinuxUser --exec env "SSH_AUTH_SOCK=$AgentSocket" python3 $probe refused $Destination $OriginSocket $origin
        if($LASTEXITCODE -ne 0) { throw 'Second presenter was not refused' }
        $beforeEvents=Pane-State $import.panes[0].paneId
        $null=Peer-Ctl @('notify','STAGE4_EVENT_ONCE','--target',$origin)
        $null=Peer-Ctl @('session','status','active','--sound','beep','--target',$origin)
        $null=Await-Presentation { param($row) $live=$row.liveSessions.PSObject.Properties.Value | Select-Object -First 1; $live.presentationSoundEvents -eq ($beforeEvents.presentationSoundEvents+1) -and $live.presentationNotificationEvents -eq ($beforeEvents.presentationNotificationEvents+1) }
        $null=Peer-Ctl @('session','status','idle','--target',$origin)
        Write-Host 'PASS real agterm-origin live notification and negotiated presenter sound (viewer event counters, not acoustic capture)'
        foreach ($cancel in @($false,$true)) {
            $opened=Peer-Ctl @('ask','open','Stage4 delegated question','--input','line','--value','prefill','--no-block','--target',$origin)
            $questionId=[string]$opened.id
            $question=[IntPtr]::Zero
            for($i=0;$i -lt 60 -and $question -eq [IntPtr]::Zero;$i++) { $question=[RemoteAskProbe]::Find($ui.Id); Start-Sleep -Milliseconds 100 }
            if($question -eq [IntPtr]::Zero) { throw 'No delegated native question in the isolated viewer' }
            if($cancel) { [void][RemoteAskProbe]::SendMessageW($question,0x10,[IntPtr]::Zero,[IntPtr]::Zero) }
            else {
                [void][RemoteAskProbe]::SetWindowTextW([RemoteAskProbe]::GetDlgItem($question,200),'STAGE4_NATIVE_ANSWER')
                [void][RemoteAskProbe]::SendMessageW([RemoteAskProbe]::GetDlgItem($question,1000),0xf5,[IntPtr]::Zero,[IntPtr]::Zero)
            }
            for($i=0;$i -lt 40;$i++) {
                $result=Peer-Ctl @('ask','result',$questionId)
                if(-not $result.PSObject.Properties['ask']) { $result=@{ask=$result} }
                if($result.ask.result -ne 'pending') { break }
                Start-Sleep -Milliseconds 100
            }
            if($cancel) { if($result.ask.result -ne 'escaped') { throw "Origin missed cancellation: $($result|ConvertTo-Json -Depth 10)" } }
            elseif($result.ask.result -ne 'answered' -or $result.ask.text -ne 'STAGE4_NATIVE_ANSWER') { throw "Origin missed native answer: $($result|ConvertTo-Json -Depth 10)" }
            $expected=if($cancel) { 'escaped' } else { 'answered' }
            $null=Await-Presentation { param($row) @($row.liveSessions.PSObject.Properties.Value | Where-Object { $_.presentationQuestionOutcome -eq $expected }).Count -gt 0 }
        }
        Write-Host 'PASS real agterm-origin delegated input answer/cancellation with authoritative origin results'
        foreach($shape in @('single','multi','other','multiline')) {
            $formArgs=if($shape -eq 'multiline') { @('--input','multiline') } else { @('--choice','a=Alpha','--choice','b=Beta') }
            if($shape -eq 'multi') { $formArgs+=@('--multiple','--checked','b') }
            if($shape -eq 'other') { $formArgs+=@('--other','Other') }
            $opened=Peer-Ctl (@('ask','open',('Stage4 form '+$shape),'--no-block','--target',$origin)+$formArgs)
            $questionId=[string]$opened.id
            $question=[IntPtr]::Zero
            for($i=0;$i -lt 60 -and $question -eq [IntPtr]::Zero;$i++) { $question=[RemoteAskProbe]::Find($ui.Id); Start-Sleep -Milliseconds 100 }
            if($question -eq [IntPtr]::Zero) { throw "No native $shape question" }
            if($shape -eq 'other') { [void][RemoteAskProbe]::SetWindowTextW([RemoteAskProbe]::GetDlgItem($question,203),'другое') }
            if($shape -eq 'multiline') { [void][RemoteAskProbe]::SetWindowTextW([RemoteAskProbe]::GetDlgItem($question,200),"строка`r`nnext") }
            [void][RemoteAskProbe]::SendMessageW([RemoteAskProbe]::GetDlgItem($question,1000),0xf5,[IntPtr]::Zero,[IntPtr]::Zero)
            for($i=0;$i -lt 40;$i++) { $answer=Peer-Ctl @('ask','result',$questionId); if($answer.result -ne 'pending') { break }; Start-Sleep -Milliseconds 100 }
            if($answer.result -ne 'answered') { throw "Form answer not retained: $($answer|ConvertTo-Json -Depth 8)" }
            if($shape -eq 'single' -and ($answer.selected -join ',') -ne 'a') { throw 'Missing initial radio selection' }
            if($shape -eq 'multi' -and ($answer.selected -join ',') -ne 'b') { throw 'Lost checked IDs' }
            if($shape -eq 'other' -and ($answer.text -ne 'другое' -or @($answer.selected | Where-Object { $null -ne $_ }).Count -ne 0)) { throw "Other did not exclude radio selection: $($answer|ConvertTo-Json -Depth 8)" }
            if($shape -eq 'multiline' -and $answer.text -ne "строка`nnext") { throw 'Multiline newline/Unicode mismatch' }
        }
        $opened=Peer-Ctl @('ask','open','Stage4 unsupported width','--button','ok=OK','--width','40','--no-block','--target',$origin)
        $questionId=[string]$opened.id
        $null=Await-Presentation { param($row) @($row.liveSessions.PSObject.Properties.Value | Where-Object { $_.presentationError -like 'Remote question refused:*' }).Count -gt 0 }
        if([RemoteAskProbe]::Find($ui.Id) -ne [IntPtr]::Zero) { throw 'Unsupported form opened a changed native dialog' }
        $answer=Peer-Ctl @('ask','result',$questionId)
        if($answer.result -ne 'pending') { throw 'Refusal settled the origin question' }
        $null=Peer-Ctl @('ask','cancel',$questionId)
        Write-Host 'PASS real Mac-origin radio/multi/Other/multiline and unsupported-width handback'
        $opened=Peer-Ctl @('ask','open','Stage4 cancelled before late click','--input','line','--no-block','--target',$origin)
        $questionId=[string]$opened.id
        $question=[IntPtr]::Zero
        for($i=0;$i -lt 60 -and $question -eq [IntPtr]::Zero;$i++) { $question=[RemoteAskProbe]::Find($ui.Id); Start-Sleep -Milliseconds 100 }
        if($question -eq [IntPtr]::Zero) { throw 'No replica for cancellation race' }
        $oldButton=[RemoteAskProbe]::GetDlgItem($question,1000)
        $null=Peer-Ctl @('ask','cancel',$questionId)
        # The click was queued by a user before they observed dismissal; whether the native
        # HWND has already disappeared or the callback runs, the origin cancellation must win.
        [void][RemoteAskProbe]::SendMessageW($oldButton,0xf5,[IntPtr]::Zero,[IntPtr]::Zero)
        $answer=Peer-Ctl @('ask','result',$questionId)
        if($answer.result -ne 'cancelled') { throw "Late native click overwrote cancellation: $($answer|ConvertTo-Json)" }
        for($i=0;$i -lt 60;$i++) { if([RemoteAskProbe]::Find($ui.Id) -eq [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 100 }
        if([RemoteAskProbe]::Find($ui.Id) -ne [IntPtr]::Zero) { throw 'Cancelled replica stayed visible' }
        Write-Host 'PASS actual Mac origin cancellation wins over a late Windows viewer button action'
        $opened=Peer-Ctl @('ask','open','Stage4 connection loss','--input','line','--no-block','--target',$origin)
        $questionId=[string]$opened.id
        for($i=0;$i -lt 60;$i++) { if([RemoteAskProbe]::Find($ui.Id) -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 100 }
        if([RemoteAskProbe]::Find($ui.Id) -eq [IntPtr]::Zero) { throw 'Missing pending replica before loss' }
        & wsl.exe -d $Distro -u $LinuxUser --exec env "SSH_AUTH_SOCK=$AgentSocket" python3 $probe disconnect $Destination $OriginSocket $origin
        if($LASTEXITCODE -ne 0) { throw 'Cannot isolate presentation loss' }
        for($i=0;$i -lt 60;$i++) { if([RemoteAskProbe]::Find($ui.Id) -eq [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 100 }
        if([RemoteAskProbe]::Find($ui.Id) -ne [IntPtr]::Zero) { throw 'Stale question survived presentation loss' }
        $answer=Peer-Ctl @('ask','result',$questionId)
        if($answer.result -ne 'pending') { throw 'Connection loss invented an origin outcome' }
        $null=Await-Presentation { param($row) @($row.liveSessions.PSObject.Properties.Value | Where-Object presentationPresenter).Count -gt 0 }
        if([RemoteAskProbe]::Find($ui.Id) -ne [IntPtr]::Zero) { throw 'Reconnection replayed the question' }
        $null=Peer-Ctl @('ask','cancel',$questionId)
        if($geometry -ne (Ctl @('session','metrics','--target',$import.panes[0].paneId) | ConvertTo-Json -Compress)) { throw 'Presentation loss resized terminal' }
        Write-Host 'PASS real Mac-origin presentation-only loss dismisses replica, preserves pending origin question and never replays it'
        $questionId=$null # The cancelled question belongs to the pre-restart origin registry.
        $beforeRestart=Ctl (@('zmx','remote','list')+$remote)
        $null=Peer-Ctl @('session','context','STAGE4_RESTART_CONTEXT','--target',$origin)
        $null=Await-Presentation { param($row) $row.context -eq 'STAGE4_RESTART_CONTEXT' }
        $beforeEvents=Pane-State $import.panes[0].paneId
        & wsl.exe -d $Distro -u $LinuxUser --exec env "SSH_AUTH_SOCK=$AgentSocket" python3 $probe origin-restart $Destination $OriginSocket $origin
        if($LASTEXITCODE -ne 0) { throw 'Isolated Mac origin restart failed' }
        $peerRow=@((Peer-Ctl @('tree')).tree.workspaces.sessions | Where-Object id -eq $origin)[0]
        if($peerRow.context -ne 'STAGE4_RESTART_CONTEXT') { throw 'Origin restart lost its saved context' }
        $null=Peer-Ctl @('session','context','STAGE4_RESTART_AFTER','--target',$origin)
        $null=Await-Presentation { param($row) $row.context -eq 'STAGE4_RESTART_AFTER' -and @($row.liveSessions.PSObject.Properties.Value | Where-Object presentationPresenter).Count -gt 0 }
        $afterRestart=Ctl (@('zmx','remote','list')+$remote)
        $beforeDaemons=@($beforeRestart.Sessions | Where-Object {$_.Session.Id -eq $origin})[0].Session.Panes | ConvertTo-Json -Depth 10 -Compress
        $afterDaemons=@($afterRestart.Sessions | Where-Object {$_.Session.Id -eq $origin})[0].Session.Panes | ConvertTo-Json -Depth 10 -Compress
        if($beforeDaemons -ne $afterDaemons) { throw 'Origin UI restart changed terminal identities' }
        $afterEvents=Pane-State $import.panes[0].paneId
        if($beforeEvents.presentationSoundEvents -ne $afterEvents.presentationSoundEvents -or $beforeEvents.presentationNotificationEvents -ne $afterEvents.presentationNotificationEvents) { throw 'Origin restart replayed alerts' }
        $null=Peer-Ctl @('session','context','--clear','--target',$origin)
        Write-Host 'PASS isolated Mac origin restart restores passive state without duplicate alerts or new base terminals'
        $ui.Kill(); $ui.WaitForExit()
        $runtime=$env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER
        try {
            $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER=Join-Path $state 'missing-runtime'
            Start-Ui
        } finally { $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER=$runtime }
        foreach ($item in $import.panes) { $null=Await-Pane $item.paneId }
        $null=Await-Presentation { param($row) @($row.liveSessions.PSObject.Properties.Value | Where-Object presentationPresenter).Count -gt 0 }
        $openedPage=Peer-Ctl @('session','overlay','open','--url','https://example.com','--target',$origin)
        for($i=0;$i -lt 50;$i++) {
            $result=Peer-Ctl @('session','overlay','result','--page',$openedPage.pageID,'--target',$origin)
            if($result.pageOutcome.outcome -eq 'dismissed') { break }
            Start-Sleep -Milliseconds 100
        }
        if($result.pageOutcome.outcome -ne 'dismissed') { throw 'Missing viewer runtime did not refuse the remote page' }
        $null=Await-Presentation { param($row) @($row.htmlOverlays | Where-Object { $_ }).Count -eq 0 }
        $null=Ctl @('session','text','--target',$import.panes[0].paneId)
        Write-Host 'PASS viewer UI restart preserves terminal; missing WebView2 refuses page and releases origin slot'
        $raw='{}' | & $ctl ask open --no-block --target ([guid]::NewGuid().ToString()) --window $window --pipe $id
        if ($LASTEXITCODE -eq 0) { throw 'Unknown exact session opened a question' }
        $raw='{}' | & $ctl ask open --no-block --target $import.sessionId --window $window --pipe $id
        if ($LASTEXITCODE -ne 0) { throw "Targeted question failed: $raw" }
        $ask=($raw | ConvertFrom-Json).id
        $null=Ctl @('session','close','--target',$import.sessionId,'--window',$window)
        for($i=0;$i -lt 80;$i++) {
            $raw=& $ctl ask result $ask --window $window --pipe $id
            $outcome=$raw | ConvertFrom-Json
            if($outcome.result -ne 'pending') { break }
            Start-Sleep -Milliseconds 100
        }
        if ($LASTEXITCODE -ne 2 -or $outcome.reason -ne 'owner-closed') { throw "Targeted question survived owner removal: $raw" }
        $raw='{}' | & $ctl ask open --no-block --window $window --pipe $id
        if ($LASTEXITCODE -ne 0) { throw "Legacy window-only question failed: $raw" }
        $legacy=($raw | ConvertFrom-Json).id
        $null=& $ctl ask cancel $legacy --window $window --pipe $id
        Write-Host 'PASS native targeted question owner-close, unknown-session refusal and legacy window-only open'
        return
    }
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
    for ($outage=0;$outage -lt 6;$outage++) {
    $hosts=@(Get-CimInstance Win32_Process -Filter "Name = 'agwinterm-ptyhost.exe'" | Where-Object { $_.CommandLine.Contains($id) })
    $transports=@(Get-CimInstance Win32_Process -Filter "Name = 'wsl.exe'" | Where-Object { $_.ParentProcessId -eq $hosts[0].ProcessId -and $_.CommandLine.Contains($Bridge) })
    if (-not $transports.Count) { throw 'Test-owned SSH transport was not found' }
    Stop-Process -Id $transports[0].ProcessId -Force
    Start-Sleep -Seconds 3
    foreach ($item in $import.panes) { $null=Await-Pane $item.paneId }
    }
    Write-Host 'PASS six independent SSH outages each reconnect without replay'
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
        $closingWindow=[string](Ctl @('window','new','--name','remote-import-race'))
        $start=[Diagnostics.ProcessStartInfo]::new($ctl)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($arg in (@('zmx','remote','attach')+$remote+@('--bridge',$FaultBridge,'--session',$candidate.Session.Id,'--window',$closingWindow,'--pipe',$id,'--json','--timeout','90'))) { $start.ArgumentList.Add($arg) }
        $pending=[Diagnostics.Process]::Start($start)
        $pendingOutput=$pending.StandardOutput.ReadToEndAsync()
        $pendingError=$pending.StandardError.ReadToEndAsync()
        Start-Sleep -Seconds 1
        $null=Ctl @('window','close','--target',$closingWindow)
        if (-not $pending.WaitForExit(30000)) { $pending.Kill(); throw 'Import into closed window did not finish' }
        if ($pending.ExitCode -eq 0) { throw 'Import into closed window succeeded' }
        $pending.Dispose()
        if (@((Ctl @('tree','--window',$window)).workspaces.sessions.liveSessions | Where-Object { $_ -and $_.PSObject.Properties.Count }).Count) { throw 'Closed-window import fell back to another window' }
        Write-Host 'PASS disappeared import window cannot redirect attachment'
        $partial=Ctl (@('zmx','remote','attach')+$remote+@('--bridge',$FaultBridge,'--session',$candidate.Session.Id,'--window',$window))
        if ($partial.panes.Count -ne 2) { throw 'Fault fixture needs an origin split' }
        $left=[string]$partial.panes[0].paneId; $right=[string]$partial.panes[1].paneId
        $prepared=$null
        for ($i=0;$i -lt 100;$i++) {
            foreach ($file in Get-ChildItem (Join-Path $state 'windows') -Filter '*.json') {
                $savedWindow=Get-Content $file.FullName -Raw | ConvertFrom-Json -Depth 100
                $prepared=@($savedWindow.Workspaces.Sessions.Panes | Where-Object { $_.Id -eq $left -and $_.LiveBinding.TransportTicket -and $_.LiveBinding.Recovery -eq 'pending' })[0]
                if ($prepared) { break }
            }
            if ($prepared) { break }
            Start-Sleep -Milliseconds 50
        }
        if (-not $prepared -or (Pane-State $left).role -ne 'unknown') { throw 'No durable ticket before managed handshake' }
        $ui.Kill(); $ui.WaitForExit()
        Start-Ui
        $null=Await-Pane $left
        Write-Host 'PASS UI crash before role acknowledgement recovers the checkpointed transport'
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
    # Keep the origin alive; point only an offline test-owned Recent descriptor at an absent socket.
    $remembered=@((Ctl @('recent','list')).entries)[0]
    if (-not $remembered) { throw 'No remote Recent entry for absent-origin test' }
    $ui.Kill(); $ui.WaitForExit()
    $recentPath=Join-Path $state 'recent.json'
    $savedRecent=Get-Content $recentPath -Raw | ConvertFrom-Json -Depth 100
    $missing='/tmp/aw-stage2-missing-'+[guid]::NewGuid().ToString('N')
    foreach ($saved in $savedRecent.Sessions.State.Panes | Where-Object { $_.LiveBinding.Provider -eq 'agterm-ssh' }) {
        $saved.LiveBinding.Endpoint.SocketDirectory=$missing
        $saved.LiveBinding.Identity.path=$missing+'/'+$saved.LiveBinding.Daemon
        $saved.LiveBinding.Bridge=$Bridge
    }
    $savedRecent | ConvertTo-Json -Depth 100 | Set-Content $recentPath
    Start-Ui
    $absent=Ctl @('recent','open',$remembered.id)
    foreach ($pane in $absent.members[0].paneIds) {
        for ($i=0;$i -lt 100;$i++) {
            $failed=Pane-State $pane
            if ($failed.recovery -eq 'unavailable') { break }
            Start-Sleep -Milliseconds 100
        }
        if ($failed.recovery -ne 'unavailable' -or $failed.remoteAttach) { throw 'Recent recreated an absent origin' }
        Refuse @('session','type','SHOULD-NOT-CREATE','--target',$pane)
    }
    $null=Ctl @('recent','clear')
    $after=Ctl (@('zmx','remote','list')+$remote)
    if (-not ($after.Sessions | Where-Object { $_.Session.Id -eq $candidate.Session.Id })) { throw 'Absent-origin Recent mutated real origin' }
    Write-Host 'PASS absent-origin Recent stays unavailable and preserves original work'
    if ($TerminalFixture) {
        & dotnet (Join-Path $repo 'tests/Agwinterm.RemoteTerminalHarness/bin/Release/net10.0-windows/Agwinterm.RemoteTerminalHarness.dll') $Distro $LinuxUser $Destination $AgentSocket $OriginSocket $Bridge $TerminalFixture (Join-Path (Split-Path $exe) 'agwinterm-ptyhost.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Remote terminal continuity harness failed' }
    }
} finally {
    if ($PassiveOnly -and $questionId) { try { $null=Peer-Ctl @('ask','cancel',$questionId) } catch { Write-Warning $_ } }
    if ($ui -and -not $ui.HasExited) { $ui.Kill(); $ui.WaitForExit() }
    # The enclosing private desktop job reaps all remaining test-owned local transports.
}
