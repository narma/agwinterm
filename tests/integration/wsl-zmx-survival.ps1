param([string]$Distro = 'Ubuntu', [Parameter(Mandatory)][string]$BuildDirectory, [switch]$DiagnosticKeepalive, [switch]$LifetimeGuard, [switch]$ProfileLive, [switch]$LifecycleControls,
    [string]$PeerDistro, [string]$PeerUser, [string]$PeerExecutable, [switch]$UpgradeToGhostty)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslZmxSurvival -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe = Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl = Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
foreach ($file in @($exe,$ctl)) { if (-not (Test-Path -LiteralPath $file)) { throw "Missing test build: $file" } }
$linuxScript = (& wsl.exe -d $Distro --exec wslpath -u (Join-Path $PSScriptRoot 'zmx/workload.py')).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve sentinel path' }
$id = 'agwinterm-zmx-test-' + [guid]::NewGuid().ToString('N')
$state = Join-Path $env:LOCALAPPDATA $id
$linuxHome = (& wsl.exe -d $Distro --exec printenv HOME).Trim()
if ($LASTEXITCODE -ne 0 -or -not $linuxHome.StartsWith('/')) { throw 'Cannot resolve Linux home' }
$socketDir = $linuxHome + '/aw-' + [guid]::NewGuid().ToString('N').Substring(0,16)
$recordDir = "$BuildDirectory/survival-$([guid]::NewGuid().ToString('N'))"
$name = 'agterm-' + [guid]::NewGuid().ToString('N')
$ui = $null
$hostProcess = $null
$keepalive = $null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ZmxTestWindow {
    delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder name, int size);
    public static bool Close(uint pid) {
        bool sent = false;
        EnumWindows((window, state) => {
            GetWindowThreadProcessId(window, out uint owner);
            var name = new System.Text.StringBuilder(256);
            GetClassNameW(window, name, 256);
            if (owner == pid && name.ToString().Contains("Agwinterm", StringComparison.OrdinalIgnoreCase))
                sent |= PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return sent;
    }
}
'@
function Inspect {
    $raw = & wsl.exe -d $Distro --exec python3 $linuxScript inspect $recordDir 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Linux workload or daemon missing/replaced; live mode must remain disabled' }
    return ($raw -join "`n")
}
function Saved-Pane([string]$PaneId) {
    foreach ($file in Get-ChildItem (Join-Path $state 'windows') -Filter '*.json') {
        $saved = Get-Content $file.FullName -Raw | ConvertFrom-Json
        foreach ($workspace in $saved.Workspaces) { foreach ($session in $workspace.Sessions) {
            foreach ($pane in $session.Panes) { if ($pane.Id -eq $PaneId) { return $pane } }
        } }
    }
}
function Ctl([string[]]$Command) {
    $raw = & $ctl @Command --pipe $id --json
    if ($LASTEXITCODE -ne 0) { throw "Control failed: $Command $raw" }
    $reply = ($raw -join "`n") | ConvertFrom-Json -Depth 100
    if (-not $reply.ok) { throw "Control refused: $raw" }
    return $reply.result
}
function Start-Ui {
    $script:ui = Start-Process $exe -ArgumentList @('--app-id',$id,'--pipe',$id,'--default-session-host','server-rust') -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 200
        $reply = & $ctl ping --pipe $id --json 2>$null
        if ($LASTEXITCODE -eq 0) { return }
    }
    throw 'Test UI did not become ready'
}
function Assert-GhosttyLive([string]$PaneId) {
    # The native full case-fold matcher proves the actual live replica is Ghostty,
    # rather than merely checking the preference or whether a DLL was loaded.
    $null=Ctl @('session','write',"`r`nGHOSTTY-LIVE-Straße`r`n",'--target',$PaneId)
    $found=Ctl @('session','search','STRASSE','--target',$PaneId)
    for($i=0;$i -lt 100 -and $found -eq 'searching…';$i++) {
        Start-Sleep -Milliseconds 50
        $found=Ctl @('session','search','--target',$PaneId)
    }
    if ($found -ne '1 of 1') { throw "Live session is not using native Ghostty search: $found" }
    $null=Ctl @('session','search','--close','--target',$PaneId)
    Write-Host 'PASS live WSL replica uses Ghostty native Unicode search'
}
function Set-ReplayFixture([string]$Kind) {
    foreach ($file in Get-ChildItem (Join-Path $state 'windows') -Filter '*.json') {
        $checkpoint = Get-Content $file.FullName -Raw | ConvertFrom-Json -Depth 100
        foreach ($workspace in $checkpoint.Workspaces) {
            foreach ($session in $workspace.Sessions) {
                foreach ($pane in $session.Panes) {
                    $pane | Add-Member AgentResume $(if ($Kind -eq 'agent') {'SHOULD-NOT-RESUME'} else {''}) -Force
                    $pane | Add-Member RestoreCommand $(if ($Kind -eq 'pin') {'SHOULD-NOT-PIN'} else {''}) -Force
                    $pane | Add-Member Command 'SHOULD-NOT-CAPTURE' -Force
                    $pane | Add-Member Buffer @('SHOULD-NOT-SEED-BUFFER') -Force
                }
            }
        }
        $checkpoint | ConvertTo-Json -Depth 100 | Set-Content $file.FullName
    }
}
try {
    New-Item -ItemType Directory -Path $state | Out-Null
    & wsl.exe -d $Distro --exec mkdir -m 700 $socketDir
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create private socket directory' }
    @{default='ZMX survival';profiles=@(@{name='ZMX survival';command='wsl.exe';args=@('-d',$Distro,'--exec','env','-u','ZMX_SESSION','-u','ZMX_SESSION_PREFIX','-u','ZMX_MANAGED_CLAIM',"ZMX_DIR=$socketDir",'ZMX_MANAGED=survival',"$BuildDirectory/zmx/zig-out/bin/zmx",'attach',$name,'bash','--noprofile','--norc','-c','exec python3 "$1" run "$2"','sentinel',$linuxScript,$recordDir)})} |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $state 'profiles.json')
    if ($ProfileLive) {
        @{default='ZMX survival';profiles=@(@{name='ZMX survival';command='wsl.exe';zmx=@{enabled=$true;executable="$BuildDirectory/zmx/zig-out/bin/zmx"};args=@('-d',$Distro,'--exec','bash','--noprofile','--norc','-c','exec python3 "$1" run "$2"','sentinel',$linuxScript,$recordDir)})} |
            ConvertTo-Json -Depth 8 | Set-Content (Join-Path $state 'profiles.json')
        $profiles = Get-Content (Join-Path $state 'profiles.json') -Raw | ConvertFrom-Json
        $profiles.profiles += @{name='ZMX matrix';command='wsl.exe';zmx=@{enabled=$true;executable="$BuildDirectory/zmx/zig-out/bin/zmx"};args=@('-d',$Distro,'--exec','bash','--noprofile','--norc')}
        if ($PeerDistro -and $PeerUser -and $PeerExecutable) {
            $profiles.profiles += @{name='ZMX peer user';command='wsl.exe';zmx=@{enabled=$true;executable=$PeerExecutable};args=@('-d',$PeerDistro,'--user',$PeerUser,'--exec','bash','--noprofile','--norc')}
        }
        $profiles | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $state 'profiles.json')
    }
    'session-host = server-rust' | Set-Content (Join-Path $state 'agwinterm.conf')
    if ($UpgradeToGhostty) { 'emulator-core = rust' | Add-Content (Join-Path $state 'agwinterm.conf') }
    if ($ProfileLive) { @('restore-commands = true','restore-buffer = true') | Add-Content (Join-Path $state 'agwinterm.conf') }
    Start-Ui
    if (!$UpgradeToGhostty -and (Ctl @('config','get','emulator-core')) -ne 'ghostty') { throw 'Expected implicit Ghostty default' }
    $initial = $null
    for ($i=0; $i -lt 40; $i++) {
        try { $initial = Inspect; break } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $initial) { throw 'Sentinel did not start' }
    if ($ProfileLive -and !$UpgradeToGhostty) { Assert-GhosttyLive ((Ctl @('tree')).workspaces[0].sessions[0].id) }
    Write-Host "INITIAL $initial"
    $hosts = @(Get-CimInstance Win32_Process -Filter "Name = 'agwinterm-ptyhost.exe' OR Name = 'Agwinterm.Win32.exe'" | Where-Object { $_.CommandLine.Contains($id) -and ($_.Name -eq 'agwinterm-ptyhost.exe' -or $_.CommandLine -match '--pty-host') })
    if ($hosts.Count -ne 1) { throw "Expected exactly one isolated PTY host, got $($hosts.Count)" }
    $hostProcess = Get-Process -Id $hosts[0].ProcessId
    if (-not [ZmxTestWindow]::Close($ui.Id)) { throw 'Cannot request graceful test UI quit' }
    if (-not $ui.WaitForExit(15000)) { throw 'Test UI quit timed out' }
    if ($UpgradeToGhostty) {
        $conf=Join-Path $state 'agwinterm.conf'
        @(Get-Content $conf | Where-Object { $_ -notmatch '^emulator-core\s*=' }) | Set-Content $conf
    }
    if ((Inspect) -ne $initial) { throw 'Identity changed after UI quit' }
    Write-Host 'PASS same Linux incarnation after UI quit'
    if ($ProfileLive) {
        foreach ($kind in @('agent','pin','captured')) {
            Set-ReplayFixture $kind
            Start-Ui
            if ((Ctl @('config','get','emulator-core')) -ne 'ghostty') { throw 'Restore did not use implicit Ghostty default' }
            Start-Sleep -Seconds 3
            & wsl.exe -d $Distro --exec python3 $linuxScript assert-zero-input $recordDir
            if ($LASTEXITCODE -ne 0) { throw "Unexpected $kind replay during host adoption" }
            if (-not [ZmxTestWindow]::Close($ui.Id) -or -not $ui.WaitForExit(15000)) { throw 'Replay test quit failed' }
        }
    }
    Start-Ui
    if ((Inspect) -ne $initial) { throw 'Identity changed after UI restore' }
    $ui.Kill(); $ui.WaitForExit()
    if ((Inspect) -ne $initial) { throw 'Identity changed after forced UI exit' }
    Write-Host 'PASS same Linux incarnation after forced UI exit'
    if ($DiagnosticKeepalive -or $LifetimeGuard) {
        $start = [Diagnostics.ProcessStartInfo]::new('wsl.exe')
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $command = @('sleep','90')
        if ($LifetimeGuard) {
            $guard = (& wsl.exe -d $Distro --exec wslpath -u (Join-Path $repo 'tests/integration/zmx/reference/zmx_lifetime.py')).Trim()
            $command = @('python3',$guard)
            $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true
        }
        foreach ($arg in (@('-d',$Distro,'--exec') + $command)) { $start.ArgumentList.Add($arg) }
        $keepalive = [Diagnostics.Process]::Start($start)
        if ($LifetimeGuard) {
            $request = @{version=1;workloads=@(($initial | ConvertFrom-Json).workload)} | ConvertTo-Json -Compress -Depth 5
            $keepalive.StandardInput.WriteLine($request); $keepalive.StandardInput.Close()
            $ready = $keepalive.StandardOutput.ReadLineAsync()
            if (-not $ready.Wait(10000) -or -not ($ready.Result | ConvertFrom-Json).ok) { throw 'Lifetime guard did not acquire the exact workload' }
        } else { Start-Sleep -Seconds 1 }
    }
    Write-Host "LOSS_PROBE sockets=$socketDir record=$recordDir diagnosticKeepalive=$DiagnosticKeepalive lifetimeGuard=$LifetimeGuard"
    $hostProcess.Kill(); $hostProcess.WaitForExit()
    # Allow WSL idle shutdown to happen; no keepalive or probe during this interval.
    Start-Sleep -Seconds 60
    if ((Inspect) -ne $initial) { throw 'Identity changed after PTY host loss' }
    Write-Host 'PASS same Linux incarnation after isolated PTY host loss and idle interval'
    if ($ProfileLive) {
        Start-Ui
        $restored = $false
        for ($i=0; $i -lt 60; $i++) {
            Start-Sleep -Milliseconds 250
            $saved = @(Get-ChildItem (Join-Path $state 'windows') -Filter '*.json' | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
            if ($saved.Contains('zmx-reattached')) { $restored=$true; break }
        }
        if (-not $restored) { throw "Live profile did not report daemon reattachment. Checkpoint: $saved" }
        if ((Inspect) -ne $initial) { throw 'Identity changed after actual live-profile recovery' }
        Start-Sleep -Seconds 3
        & wsl.exe -d $Distro --exec python3 $linuxScript assert-zero-input $recordDir
        if ($LASTEXITCODE -ne 0) { throw 'Unexpected replay during daemon reattachment' }
        Write-Host 'PASS actual profile recovery reattaches same incarnation after host loss'
        Assert-GhosttyLive ((Ctl @('tree')).workspaces[0].sessions[0].id)
        if ($LifecycleControls) {
            $row = (Ctl @('tree')).workspaces[0].sessions[0]
            $paneId = @($row.liveSessions.PSObject.Properties.Name)[0]
            $before = $row.liveSessions.$paneId
            $promptToken = (Saved-Pane $paneId).PromptToken
            $retryHosts = @(Get-CimInstance Win32_Process -Filter "Name = 'agwinterm-ptyhost.exe' OR Name = 'Agwinterm.Win32.exe'" | Where-Object { $_.CommandLine.Contains($id) -and ($_.Name -eq 'agwinterm-ptyhost.exe' -or $_.CommandLine -match '--pty-host') })
            if ($retryHosts.Count -ne 1) { throw 'Expected one fixture host before live-UI retry' }
            Stop-Process -Id $retryHosts[0].ProcessId -Force
            for ($i=0; $i -lt 100; $i++) {
                $lost = (Ctl @('tree')).workspaces[0].sessions[0].liveSessions.$paneId
                if ($lost.recovery -eq 'unavailable') { break }
                Start-Sleep -Milliseconds 100
            }
            $null = Ctl @('zmx','retry','--target',$paneId)
            for ($i=0; $i -lt 100; $i++) {
                $retried = (Ctl @('tree')).workspaces[0].sessions[0].liveSessions.$paneId
                if ($retried.recovery -eq 'zmx-reattached') { break }
                Start-Sleep -Milliseconds 100
            }
            if ($retried.recovery -ne 'zmx-reattached' -or (Inspect) -ne $initial) { throw 'Local retry after host loss did not preserve workload' }
            Write-Host 'PASS local existing-only retry after PTY-host loss while UI remains open'
            if ((Saved-Pane $paneId).PromptToken -ne $promptToken) { throw 'Retry changed the live shell completion capability' }
            Write-Host 'PASS retry preserves the workload completion capability'
            $listed = Ctl @('zmx','list','--profile','ZMX survival')
            $found = @($listed.sessions)[0]
            if ($found.ownership -ne 'owned' -or $found.daemon -ne $before.daemon.Name) { throw 'Inventory lost ownership or daemon identity' }
            $viewer = Ctl @('zmx','attach','--profile','ZMX survival','--daemon',$found.daemon,'--incarnation',$found.incarnation)
            Start-Sleep -Seconds 1
            if ((Inspect) -ne $initial) { throw 'Existing-only attach changed workload' }
            $text = Ctl @('session','text','--target',$viewer.paneId)
            if ($text -notlike '*AGWINTERM_ZMX_SENTINEL_READY*') { throw "Follower authoritative screen missing sentinel: $text" }
            $null = Ctl @('surface','cursor','--target',$viewer.paneId)
            $null = Ctl @('session','readonly','on','--target',$viewer.paneId)
            $denied = & $ctl session type CONTROL-PROBE --target $viewer.paneId --pipe $id --json
            if ($LASTEXITCODE -ne 1) { throw "Readonly input must refuse: $denied" }
            $null = Ctl @('session','readonly','off','--target',$viewer.paneId)
            $accepted = Ctl @('session','type','CONTROL-PROBE','--target',$viewer.paneId)
            if (-not $accepted.accepted -or $accepted.executionComplete) { throw 'Input result must mean queue acceptance only' }
            Start-Sleep -Milliseconds 250
            & wsl.exe -d $Distro --exec python3 $linuxScript assert-control-input $recordDir
            if ($LASTEXITCODE -ne 0) { throw 'Follower input was lost or duplicated' }
            $viewerToken = (Saved-Pane $viewer.paneId).PromptToken
            $null = Ctl @('zmx','lead','--target',$viewer.paneId)
            $leading = $false
            for ($i=0; $i -lt 40; $i++) {
                Start-Sleep -Milliseconds 250
                $viewerRow = (Ctl @('tree')).workspaces.sessions | Where-Object { $_.id -eq $viewer.paneId }
                if ($viewerRow.liveSessions.($viewer.paneId).role -eq 'leader') { $leading=$true; break }
            }
            if (-not $leading) { throw 'Explicit leadership was not observed' }
            if ((Saved-Pane $viewer.paneId).PromptToken -ne $viewerToken) { throw 'Leadership changed the existing workload completion capability' }
            $null = Ctl @('zmx','detach','--target',$viewer.paneId)
            if ((Inspect) -ne $initial) { throw 'Viewer detach killed workload' }
            $row = (Ctl @('tree')).workspaces.sessions | Where-Object { $_.id -eq $paneId }
            if ($row.name -like '*zmx-role*') { throw 'Managed-role notice leaked into the title' }
            $null = Ctl @('zmx','retry','--target',$paneId)
            if ((Inspect) -ne $initial) { throw 'Retry changed live workload' }
            $bad = & $ctl zmx restart --target $paneId --source shell --pipe $id --json
            if ($LASTEXITCODE -ne 1) { throw "Live restart should refuse: $bad" }
            $freshRecord = "$BuildDirectory/restart-$([guid]::NewGuid().ToString('N'))"
            $replacementBinding = 'replacement-agent-recovery'
            $pin = "exec python3 '$linuxScript' run '$freshRecord'"
            $null = Ctl @('session','restore','--target',$paneId,'--command',$pin)
            $null = Ctl @('zmx','terminate','--target',$paneId,'--force')
            # Let cancelled-reader/child-exit callbacks run before checking the
            # durable result: a late EOF must not overwrite confirmed termination.
            Start-Sleep -Seconds 3
            $missing = (Ctl @('tree')).workspaces[0].sessions[0].liveSessions.$paneId
            if ($missing.recovery -ne 'missing') { throw 'Terminate did not report missing' }
            $bad = & $ctl zmx restart --target $paneId --source agent --pipe $id --json
            if ($LASTEXITCODE -ne 1) { throw "Missing agent source should refuse: $bad" }
            $null = Ctl @('zmx','retry','--target',$paneId)
            Start-Sleep -Seconds 1
            # Hold this fixture's ledger lock: reservation can be saved in Windows,
            # but the Linux restart cannot complete until the new binding has landed.
            $linuxReady = "$freshRecord-gate/ready"
            $linuxRelease = "$freshRecord-gate/release"
            $gateReady = (& wsl.exe -d $Distro --exec wslpath -w $linuxReady).Trim()
            $gateRelease = (& wsl.exe -d $Distro --exec wslpath -w $linuxRelease).Trim()
            $linuxGate = (& wsl.exe -d $Distro --exec wslpath -u "$PSScriptRoot/zmx/restart-gate.py").Trim()
            $ledger = (Saved-Pane $paneId).LiveBinding.Endpoint.LedgerDirectory
            $gateError = Join-Path $state 'restart-gate-error.log'
            $gateClient = Start-Process wsl.exe -ArgumentList @('-d',$Distro,'--exec','python3',$linuxGate,$ledger,$linuxReady,$linuxRelease) `
                -WindowStyle Hidden -RedirectStandardError $gateError -PassThru
            $restartReply = Join-Path $state 'restart-reply.json'
            $restartClient = $null
            try {
                for ($i=0; $i -lt 100 -and -not (Test-Path $gateReady); $i++) { Start-Sleep -Milliseconds 50 }
                if (-not (Test-Path $gateReady)) { throw "Ledger gate did not become ready: $(Get-Content $gateError -Raw)" }
                $restartClient = Start-Process $ctl -ArgumentList @('zmx','restart','--target',$paneId,'--source','pin','--pipe',$id,'--json') `
                    -WindowStyle Hidden -RedirectStandardOutput $restartReply -RedirectStandardError (Join-Path $state 'restart-error.log') -PassThru
                for ($i=0; $i -lt 200 -and (Saved-Pane $paneId).PromptToken -eq $promptToken; $i++) { Start-Sleep -Milliseconds 25 }
                if ((Saved-Pane $paneId).PromptToken -eq $promptToken) { throw 'Restart never reserved a replacement shell' }
                $null = Ctl @('session','bind',$replacementBinding,'--target',$paneId)
                for ($i=0; $i -lt 40 -and (Saved-Pane $paneId).AgentResume -ne $replacementBinding; $i++) { Start-Sleep -Milliseconds 25 }
                $duringRestart = Saved-Pane $paneId
                if ($duringRestart.AgentResume -ne $replacementBinding -or -not $duringRestart.LiveBinding.Pending -or $restartClient.HasExited) {
                    throw 'The replacement binding must land while restart completion is still gated'
                }
                New-Item -ItemType File -Path $gateRelease | Out-Null
                if (-not $gateClient.WaitForExit(5000) -or $gateClient.ExitCode -ne 0) { throw 'Ledger gate did not release cleanly' }
                if (-not $restartClient.WaitForExit(20000) -or $restartClient.ExitCode -ne 0) { throw 'Restart control client failed' }
                if (-not (Get-Content $restartReply -Raw | ConvertFrom-Json).ok) { throw 'Restart was refused' }
            } finally {
                if (-not (Test-Path $gateRelease)) { New-Item -ItemType File -Path $gateRelease | Out-Null }
                if (-not $gateClient.WaitForExit(5000)) { $gateClient.Kill(); $gateClient.WaitForExit() }
                $gateClient.Dispose()
                if ($restartClient) {
                    if (-not $restartClient.HasExited) { $restartClient.Kill(); $restartClient.WaitForExit() }
                    $restartClient.Dispose()
                }
            }
            $script:recordDir = $freshRecord
            $newIdentity = $null
            for ($i=0; $i -lt 60; $i++) {
                Start-Sleep -Milliseconds 250
                $readback = (Ctl @('tree')).workspaces[0].sessions[0].liveSessions.$paneId
                if ($readback.recovery -eq 'fresh-started') {
                    # Transport readiness precedes the pinned command's own startup work.
                    & wsl.exe -d $Distro --exec test -f "$recordDir/identity.json"
                    if ($LASTEXITCODE -eq 0) { $newIdentity=Inspect; break }
                }
            }
            if (-not $newIdentity -or $newIdentity -eq $initial -or $readback.bindingId -eq $before.bindingId) { throw 'Fresh start did not create a new incarnation' }
            $freshToken = (Saved-Pane $paneId).PromptToken
            $linuxToken = (& wsl.exe -d $Distro --exec cat "$recordDir/prompt-token").Trim()
            if (-not $freshToken -or $freshToken -eq $promptToken -or $linuxToken -ne $freshToken) { throw 'Fresh workload and transport have different completion capabilities' }
            Write-Host 'PASS explicit fresh start rotates and checkpoints the Linux shell capability'
            for ($i=0; $i -lt 40 -and (Saved-Pane $paneId).AgentResume -ne $replacementBinding; $i++) { Start-Sleep -Milliseconds 100 }
            if ((Saved-Pane $paneId).AgentResume -ne $replacementBinding) { throw 'Restart result erased the replacement workload binding' }
            Write-Host 'PASS replacement binding survives restart completion and attachment replacement'
            & wsl.exe -d $Distro --exec python3 $linuxScript assert-zero-input $recordDir
            if ($LASTEXITCODE -ne 0) { throw 'Fresh source received additional replay commands' }
            $null = Ctl @('zmx','detach','--target',$paneId)
            if ((Inspect) -ne $newIdentity) { throw 'Detach killed workload' }
            $remaining = @((Ctl @('tree')).workspaces.sessions | Where-Object { $_ })
            if ($remaining.Count -ne 0) { throw 'Detach recreated a local session' }
            Write-Host 'PASS real CLI retry, live-restart refusal, exact terminate, selected pin fresh start and attachment-only detach'
            & "$PSScriptRoot/zmx/layout-controls.ps1"
        }
        $ui.Kill(); $ui.WaitForExit()
        $hosts = @(Get-CimInstance Win32_Process -Filter "Name = 'agwinterm-ptyhost.exe' OR Name = 'Agwinterm.Win32.exe'" | Where-Object { $_.CommandLine.Contains($id) -and ($_.Name -eq 'agwinterm-ptyhost.exe' -or $_.CommandLine -match '--pty-host') })
        foreach ($entry in $hosts) { Stop-Process -Id $entry.ProcessId }
    }
} finally {
    & wsl.exe -d $Distro --exec python3 $linuxScript diagnose $recordDir
    if ($ui -and -not $ui.HasExited) { $ui.Kill(); $ui.WaitForExit() }
    if ($hostProcess -and -not $hostProcess.HasExited) { $hostProcess.Kill(); $hostProcess.WaitForExit() }
    & wsl.exe -d $Distro --exec python3 $linuxScript cleanup $recordDir
    if ($LASTEXITCODE -ne 0) { Write-Warning "Exact sentinel cleanup refused or workload absent; retained evidence at $recordDir" }
    if ($LifetimeGuard -and $keepalive -and -not $keepalive.WaitForExit(10000)) { throw 'Lifetime guard leaked after workload exit' }
    if ($keepalive -and -not $keepalive.HasExited) { $keepalive.Kill(); $keepalive.WaitForExit() }
}
