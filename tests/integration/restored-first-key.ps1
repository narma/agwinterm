param([Parameter(Mandatory)][string]$Exe, [string]$CtlExe, [ValidateRange(1,128)][int]$SessionCount = 84,
    [ValidateRange(1,10)][int]$Runs = 3, [switch]$ForwardedProfile)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test RestoredFirstKey -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
. "$PSScriptRoot/../ui/lib.ps1"
$ctl = Resolve-Binary $CtlExe 'agwintermctl.exe' 'Agwinterm.Ctl'
$env:AGWINTERM_LATENCY_TRACE = Join-Path $env:TEMP ('first-key-' + [guid]::NewGuid().ToString('N'))
$s = Start-Sandbox -Exe $Exe -Ctl $ctl -Pipe ('agwinterm-first-key-' + [guid]::NewGuid().ToString('N')) -Conf @('session-host = server')
function Reply([string[]]$Arguments) {
    $reply = Send-Ctl $s $Arguments | ConvertFrom-Json
    if (-not $reply.ok) { throw "Control request failed: $($reply.error)" }
    $reply.result
}
try {
    for ($i = 1; $i -lt $SessionCount; $i++) {
        $null = Reply @('session','new','--profile','Command Prompt','--name',"background-$i")
    }
    $savedOrder = @((Reply @('tree')).workspaces[0].sessions.id)
    $active = $savedOrder[-1]
    $null = Reply @('session','select','--target',$active)
    $journal = Get-ChildItem (Join-Path $s.AppDir 'logs') -Filter "startup-gui-*-$($s.Proc.Id).jsonl"
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        $events = Get-Content $journal.FullName | ForEach-Object { $_ | ConvertFrom-Json }
        $ready = @($events | Where-Object stage -eq 'pane.first-output' | Select-Object -ExpandProperty id -Unique).Count
        if ($ready -eq $SessionCount) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Only $ready of $SessionCount shells produced output" }
        Start-Sleep -Milliseconds 500
    } while ($true)
    if ($ForwardedProfile) {
        [void][AgwUi]::PostMessageW($s.Hwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
        if (!$s.Proc.WaitForExit(60000)) { throw "GUI did not exit cleanly" }
        # An incompatible command makes the ZMX path observable without a WSL distribution.
        # If restore misclassifies the forwarded profile as a saved pane, cmd.exe opens instead.
        $profilesPath = Join-Path $s.AppDir 'profiles.json'
        $profiles = Get-Content $profilesPath -Raw | ConvertFrom-Json
        $profiles.profiles += @{name='ForwardedZmx';command='cmd.exe';zmx=@{enabled=$true;executable='/usr/bin/zmx'}}
        $profiles | ConvertTo-Json -Depth 12 | Set-Content $profilesPath
        # Start the second process once the first owns this library, before its
        # window is visible. Starting it at first paint can miss the restore.
        $s.Proc = Start-Process $Exe -ArgumentList @('--pipe',$s.Pipe,'--app-id',$s.AppId) -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $log = Get-ChildItem (Join-Path $s.AppDir 'logs') -Filter "startup-gui-*-$($s.Proc.Id).jsonl"
            if ($log -and (Select-String -LiteralPath $log.FullName -SimpleMatch '"config.loaded"' -Quiet)) { break }
            if ($s.Proc.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Library owner did not start' }
            Start-Sleep -Milliseconds 5
        } while ($true)
        $duplicate = Start-Process $Exe -ArgumentList @('--pipe',$s.Pipe,'--app-id',$s.AppId,'--profile','ForwardedZmx') -WindowStyle Hidden -PassThru
        if (!$duplicate.WaitForExit(30000) -or $duplicate.ExitCode -ne 0) { throw 'Forwarded profile launch was not acknowledged' }
        $rows = @((Reply @('tree')).workspaces[0].sessions)
        $newPane = $rows[-1].id
        if ($newPane -in $savedOrder -or ($rows.id -join ',') -ne (($savedOrder + $newPane) -join ',')) {
            throw 'Expected one new session after the restored sessions in their saved order'
        }
        $live = $rows[-1].liveSessions.$newPane
        if ($live.provider -ne 'wsl-zmx' -or $live.error -ne 'ZMX requires a WSL profile') {
            throw 'Forwarded launch bypassed ZMX profile validation'
        }
        $events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }
        if (!($events | Where-Object { $_.stage -eq 'window.restore.drain' -and $_.id -eq 'CreateLaunchSession' })) {
            throw 'Forwarded profile did not exercise an in-progress restore'
        }
        Write-Host 'Forwarded profile followed the ZMX launch path and preserved all saved sessions.'
        return
    }
    # The child stays alive across GUI restarts. Only reading a literal 'a' produces a
    # marker on row 1, together with the echoed key, in one console write.
    $probe = @'
foreach ($sample in 1..10) {
    [IO.File]::WriteAllText((Join-Path $env:TEMP "first-key-child-$env:AGWINTERM_PANE_ID"),[string]$PID)
    [Console]::Clear()
    [Console]::WriteLine('WAITING_' + $sample)
    do { $key = [Console]::ReadKey($true).KeyChar } until ($key -ceq 'a')
    $received = [Diagnostics.Stopwatch]::GetTimestamp()
    [Console]::Write(('~AGL{0:X8}~{1}' -f $sample,$key))
    $written = [Diagnostics.Stopwatch]::GetTimestamp()
    [IO.File]::WriteAllText((Join-Path $env:TEMP "first-key-write-$env:AGWINTERM_PANE_ID-$sample"),[string]$written)
    [IO.File]::WriteAllText((Join-Path $env:TEMP "first-key-$env:AGWINTERM_PANE_ID-$sample"),[string]$received)
    [void][Console]::ReadKey($true)
}
'@
    $probePath = Join-Path $env:TEMP "first-key-$active.ps1"
    Set-Content -LiteralPath $probePath -Value $probe
    $null = Reply @('session','type',('powershell -NoProfile -ExecutionPolicy Bypass -File "' + $probePath + '"' + [char]13),'--target',$active)
    foreach ($run in 1..$Runs) {
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        while ([string](Reply @('session','text','--target',$active)) -notmatch "WAITING_$run") {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Echo child did not become ready' }
            Start-Sleep -Milliseconds 100
        }
        [void][AgwUi]::PostMessageW($s.Hwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
        if (-not $s.Proc.WaitForExit(60000)) { throw 'GUI did not exit cleanly' }
        $launch = [Diagnostics.Stopwatch]::GetTimestamp()
        $s.Proc = Start-Process $Exe -ArgumentList @('--pipe',$s.Pipe,'--app-id',$s.AppId) -PassThru
        do {
            $s.Proc.Refresh()
            $s.Hwnd = $s.Proc.MainWindowHandle
            if ($s.Proc.HasExited -or [Diagnostics.Stopwatch]::GetElapsedTime($launch).TotalSeconds -gt 60) { throw 'No restored window' }
            if (!$s.Hwnd.ToInt64()) { Start-Sleep -Milliseconds 5 }
        } until ($s.Hwnd.ToInt64())
        $start = [Diagnostics.Stopwatch]::GetTimestamp()
        if (-not [AgwUi]::PostMessageW($s.Hwnd,0x100,[IntPtr]65,[IntPtr]0x001e0001)) { throw 'Key down failed' }
        [void][AgwUi]::PostMessageW($s.Hwnd,0x101,[IntPtr]65,[IntPtr]0xc01e0001L)
        $trace = Join-Path $env:AGWINTERM_LATENCY_TRACE "trace-$($s.Proc.Id).csv"
        $receivedFile = Join-Path $env:TEMP "first-key-$active-$run"
        do {
            $end = $null
            if (Test-Path $trace) { $end = Get-Content $trace | Select-String "^8,$run,(\d+)," | Select-Object -First 1 }
            if ($end) { break }
            if ([Diagnostics.Stopwatch]::GetElapsedTime($start).TotalSeconds -gt 30) {
                $probePid = Get-Content (Join-Path $env:TEMP "first-key-child-$active")
                $alive = [bool](Get-Process -Id $probePid -ErrorAction SilentlyContinue)
                throw "No presented letter within 30 seconds. Child PID $probePid alive=$alive, receipt=$(Test-Path $receivedFile). Pane text: $(Reply @('session','text','--target',$active))"
            }
            Start-Sleep -Milliseconds 50
        } while ($true)
        if ($run -eq $Runs) {
            [void][AgwUi]::PostMessageW($s.Hwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
            if (-not $s.Proc.WaitForExit(60000)) { throw 'Closing during background restore did not finish' }
        }
        $journal = Get-ChildItem (Join-Path $s.AppDir 'logs') -Filter "startup-gui-*-$($s.Proc.Id).jsonl"
        $deadline = [DateTime]::UtcNow.AddMinutes(2)
        while (-not (Select-String -Path $journal.FullName -SimpleMatch '"window.restore.end"' -Quiet)) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Background restore did not finish' }
            Start-Sleep -Milliseconds 100
        }
        $events = Get-Content $journal.FullName | ForEach-Object { $_ | ConvertFrom-Json }
        $adoptions = @($events | Where-Object stage -eq 'pane.adopt.end').Count
        if ($adoptions -ne $SessionCount -or 'pane.adopt.missing' -in $events.stage) { throw "Expected $SessionCount surviving attachments" }
        $ticks = [long]$end.Matches[0].Groups[1].Value
        $attachedAtEcho = @($events | Where-Object { $_.stage -eq 'pane.adopt.end' -and [long]$_.ticks -le $ticks }).Count
        Write-Host "Attached when the echo was presented: $attachedAtEcho/$SessionCount"
        foreach ($event in $events | Where-Object { $_.stage -in 'window.first-paint','host.restore-inventory.end','session.restore.begin','message-loop.ready','window.restore.end','window.restore.drain','window.first-key' } | Where-Object { $_.stage -ne 'session.restore.begin' -or $_.id -eq $active }) {
            Write-Host ('{0} {2} {1:F1} ms from key' -f $event.stage,[Diagnostics.Stopwatch]::GetElapsedTime($start,[long]$event.ticks).TotalMilliseconds,$event.id)
        }
        if ($run -eq $Runs) {
            $saved = Get-ChildItem (Join-Path $s.AppDir 'windows') -Filter '*.json' | Get-Content -Raw | ConvertFrom-Json
            if ($saved.activeId -ne $active -or ($saved.workspaces[0].sessions.id -join ',') -ne ($savedOrder -join ',')) {
                throw 'Closing during restore lost the active session or changed the saved tree'
            }
        } elseif (((Reply @('tree')).workspaces[0].sessions.id -join ',') -ne ($savedOrder -join ',')) { throw 'Saved session order changed' }
        Write-Host ('RESTORED_FIRST_KEY run={0} panes={1} launchToKeyMs={2:F1} keyToEndDrawMs={3:F1}' -f $run,$adoptions,
            [Diagnostics.Stopwatch]::GetElapsedTime($launch,$start).TotalMilliseconds,
            [Diagnostics.Stopwatch]::GetElapsedTime($start,$ticks).TotalMilliseconds)
        # A fast EndDraw can precede the child's diagnostic file write.
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $received = 0L
            if (Test-Path $receivedFile) { [void][long]::TryParse([string](Get-Content $receivedFile -Raw),[ref]$received) }
            if ($received) { break }
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Missing child timestamp after the echo was presented' }
            Start-Sleep -Milliseconds 20
        } while ($true)
        $stages = Get-Content $trace | Where-Object { $_ -match "^[678],$run," } | ForEach-Object { [long]($_.Split(',')[2]) }
        Write-Host ('childToCaptureMs={0:F1} captureToApplyMs={1:F1} applyToEndDrawMs={2:F1}' -f [Diagnostics.Stopwatch]::GetElapsedTime($received,$stages[0]).TotalMilliseconds,
            [Diagnostics.Stopwatch]::GetElapsedTime($stages[0],$stages[1]).TotalMilliseconds,
            [Diagnostics.Stopwatch]::GetElapsedTime($stages[1],$ticks).TotalMilliseconds)
        $echoLatency = [Diagnostics.Stopwatch]::GetElapsedTime($received,$ticks)
        $written = [long](Get-Content (Join-Path $env:TEMP "first-key-write-$active-$run") -Raw)
        Write-Host ('childReadToWriteMs={0:F1}' -f [Diagnostics.Stopwatch]::GetElapsedTime($received,$written).TotalMilliseconds)
        Write-Host ('keyToChildMs={0:F1} childToEndDrawMs={1:F1}' -f [Diagnostics.Stopwatch]::GetElapsedTime($start,$received).TotalMilliseconds,
            $echoLatency.TotalMilliseconds)
        if ([Diagnostics.Stopwatch]::GetElapsedTime($start,$ticks).TotalSeconds -gt 2) { throw 'First key took over two seconds to reach EndDraw' }
        if ($SessionCount -ge 32 -and $attachedAtEcho -ge $SessionCount) { throw 'First echo waited for every background pane' }
        if ($run -lt $Runs) { [AgwUi]::Key($s.Hwnd,66,1) }
    }
} finally {
    foreach ($log in Get-ChildItem (Join-Path $s.AppDir 'logs') -Filter 'startup-gui-*.jsonl') {
        $events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }
        Write-Host ($log.Name + ' ' + (($events | Group-Object stage | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' '))
    }
    $hosts = @(Get-CimInstance Win32_Process -Filter "Name='Agwinterm.Host.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($s.Pipe) })
    Stop-Sandbox $s
    foreach ($owned in $hosts) { Stop-Process -Id $owned.ProcessId -Force -ErrorAction SilentlyContinue }
}
