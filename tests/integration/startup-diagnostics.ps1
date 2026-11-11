param([Parameter(Mandatory)][string]$Exe, [string]$CtlExe, [ValidateSet('server','inprocess')][string]$SessionHost = 'server', [switch]$ColdRestore)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test StartupDiagnostics -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
. "$PSScriptRoot/../ui/lib.ps1"
$ctl = Resolve-Binary $CtlExe 'agwintermctl.exe' 'Agwinterm.Ctl'
$s = Start-Sandbox -Exe $Exe -Ctl $ctl -Pipe ('agwinterm-startup-' + [guid]::NewGuid().ToString('N')) -Conf @("session-host = $SessionHost")
function Reply([string[]]$Arguments) {
    $reply = Send-Ctl $s $Arguments | ConvertFrom-Json
    if (-not $reply.ok) { throw "Control request failed: $($Arguments -join ' '): $($reply | ConvertTo-Json -Compress -Depth 8)" }
    return $reply
}
try {
    Write-Host 'Initial window started; creating saved sessions'
    1..3 | ForEach-Object { $null = Reply @('session','new','--name',"restore-$_") }
    Start-Sleep -Seconds 1
    $before = @((Reply @('tree')).result.workspaces.sessions | ForEach-Object id | Sort-Object)
    if ($before.Count -ne 4) { throw "Expected four sessions before restart, found $($before.Count)" }
    # The UI now becomes usable before queued shells have all spawned. A warm
    # adoption test must wait for four real attachments, not just four tree nodes.
    $initialLog = Get-ChildItem (Join-Path $s.AppDir 'logs') -Filter "startup-gui-*-$($s.Proc.Id).jsonl" | Select-Object -First 1
    $attached = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $initialRecords = @(Get-Content $initialLog.FullName | ForEach-Object { $_ | ConvertFrom-Json })
        if (@($initialRecords | Where-Object { $_.stage -eq 'pane.first-output' -and $_.id -in $before }).Count -eq 4) { $attached = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $attached) { throw 'Initial four hosted panes did not attach before the restart test' }
    Write-Host 'Four sessions confirmed; maximizing and restarting'
    # Exercise the WM_SIZE save path before restoration of a maximized window.
    [void][AgwUi]::PostMessageW($s.Hwnd, 0x0112, [IntPtr]0xF030, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    if ($ColdRestore) {
        if ($SessionHost -ne 'server') { throw 'ColdRestore requires server sessions' }
        [void][AgwUi]::PostMessageW($s.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $s.Proc.WaitForExit(20000)) { throw 'GUI did not save and exit before cold restore' }
        $hosts = @(Get-CimInstance Win32_Process -Filter "Name='Agwinterm.Host.exe'" |
            Where-Object { $_.CommandLine -and $_.CommandLine.Contains($s.Pipe) })
        if ($hosts.Count -ne 1) { throw 'Expected one test-owned host before cold restore' }
        foreach ($hostProcess in $hosts) { Stop-Process -Id $hostProcess.ProcessId -Force }
        if (-not (Start-SandboxProcess $s @('--pipe', $s.Pipe, '--app-id', $s.AppId))) { throw 'Cold restore did not answer ping' }
    } else { Restart-Sandbox $s | Out-Null }
    Write-Host 'Restart complete; checking restored sessions and timings'
    $after = @((Reply @('tree')).result.workspaces.sessions | ForEach-Object id | Sort-Object)
    if (Compare-Object $before $after) { throw 'Early window display changed the saved session tree' }
    $path = Get-ChildItem (Join-Path $s.AppDir 'logs') -Filter "startup-gui-*-$($s.Proc.Id).jsonl" | Select-Object -First 1
    if (-not $path) { throw 'GUI startup journal missing' }
    $records = @(Get-Content $path.FullName | ForEach-Object { $_ | ConvertFrom-Json })
    $stages = @($records.stage)
    $paint = [Array]::IndexOf($stages, 'window.first-paint')
    $restore = [Array]::IndexOf($stages, 'window.restore.begin')
    if ($paint -lt 0 -or $restore -le $paint) { throw 'Window was not painted before restoration' }
    if (@($records | Where-Object stage -eq 'session.restore.end').Count -ne 4) { throw 'Per-session timings missing' }
    if ($stages -notcontains 'message-loop.ready') { throw 'Startup did not complete' }
    if ($SessionHost -eq 'server') {
        if (@($records | Where-Object stage -eq 'host.restore-inventory.end').Count -ne 1) { throw 'Restore did not take exactly one host inventory' }
        $expectedAdoptions = if ($ColdRestore) { 0 } else { 4 }
        if (@($records | Where-Object stage -eq 'pane.adopt.begin').Count -ne $expectedAdoptions) { throw 'Unexpected synchronous adoption attempts' }
    }
    $null = Reply @('session','type',"echo STARTUP_RESTORE_OK`r",'--target',$before[0])
    $inputWorked = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        $terminalText = [string](Reply @('session','text','--target',$before[0])).result
        if ($terminalText -match '(?m)^\s*STARTUP_RESTORE_OK\s*$') { $inputWorked = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $inputWorked) { throw "Restored terminal did not execute input: $terminalText" }
    $records | Select-Object stage,elapsedMs,durationMs | Format-Table | Out-String | Write-Host
    Write-Host 'PASS: first paint precedes restore; maximized restart preserves all four sessions; timings recorded'
} finally {
    Get-ChildItem (Join-Path $s.AppDir 'logs') -Filter '*.jsonl' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host $_.Name
        Get-Content $_.FullName | Write-Host
    }
    # A hosted session deliberately survives GUI close. The guest runner waits
    # for descendants too, so stop only this test's host before returning.
    $ownedHosts = @(Get-CimInstance Win32_Process -Filter "Name='Agwinterm.Host.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($s.Pipe) })
    if ($s.Proc -and -not $s.Proc.HasExited) {
        [void][AgwUi]::PostMessageW($s.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $s.Proc.WaitForExit(10000)) { Stop-Process -Id $s.Proc.Id -Force }
    }
    foreach ($hostProcess in $ownedHosts) { Stop-Process -Id $hostProcess.ProcessId -Force -ErrorAction SilentlyContinue }
    Stop-Sandbox $s
}
