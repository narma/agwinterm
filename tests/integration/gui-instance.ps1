param([Parameter(Mandatory)][string]$Exe, [string]$CtlExe)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test GuiInstance -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
. "$PSScriptRoot/../ui/lib.ps1"
$ctl = Resolve-Binary $CtlExe 'agwintermctl.exe' 'Agwinterm.Ctl'
$s = Start-Sandbox -Exe $Exe -Ctl $ctl -Pipe ('agwinterm-instance-test-' + [guid]::NewGuid().ToString('N')) -Conf @('session-host = server')
$duplicates = [Collections.Generic.List[Diagnostics.Process]]::new()
$hostPid = $null
function Reply([string[]]$Arguments) { Send-Ctl $s $Arguments | ConvertFrom-Json }
function InputWorks([string]$pane, [string]$marker) {
    Write-Host "Checking shell input: $marker"
    $command = "echo $marker`r"
    $reply = Reply @('session', 'type', $command, '--target', $pane)
    if (-not $reply.ok) { throw "Input rejected at ${marker}: $($reply | ConvertTo-Json -Compress)" }
    for ($i = 0; $i -lt 50; $i++) {
        $text = [string](Reply @('session', 'text', '--target', $pane)).result
        if ($text -match "(?m)^\s*$marker\s*$") { return }
        Start-Sleep -Milliseconds 200
    }
    throw "Shell did not execute input: $marker"
}
try {
    Start-Sleep -Seconds 2
    $pane = (Reply @('tree')).result.workspaces[0].sessions[0].id
    InputWorks $pane 'BEFORE_DUPLICATE'
    $hostProcess = @(Get-CimInstance Win32_Process -Filter "Name='Agwinterm.Host.exe'" | Where-Object { $_.CommandLine.Contains($s.Pipe) })
    if ($hostProcess.Count -ne 1) { throw 'The test requires a running session host; in-process fallback is not a pass' }
    $hostPid = $hostProcess[0].ProcessId
    Write-Host 'Session host confirmed; restarting GUI gracefully'
    Restart-Sandbox $s | Out-Null
    InputWorks $pane 'AFTER_RESTORE'
    $duplicate = Start-Process -FilePath $Exe -ArgumentList @('--app-id', $s.AppId, '--pipe', $s.Pipe) -WindowStyle Hidden -PassThru
    $duplicates.Add($duplicate)
    if (-not $duplicate.WaitForExit(20000) -or $duplicate.ExitCode -ne 0) { throw 'Same-pipe duplicate did not forward and exit' }
    InputWorks $pane 'AFTER_SAME_PIPE_DUPLICATE'
    # Even a different control pipe must not allow the same library to be restored twice.
    $duplicate = Start-Process -FilePath $Exe -ArgumentList @('--app-id', $s.AppId, '--pipe', ($s.Pipe + '-other')) -WindowStyle Hidden -PassThru
    $duplicates.Add($duplicate)
    if (-not $duplicate.WaitForExit(20000) -or $duplicate.ExitCode -ne 0) { throw 'Duplicate did not forward and exit' }
    if ($s.Proc.HasExited) { throw 'Original GUI exited' }
    InputWorks $pane 'AFTER_DUPLICATE'
    $sessions = @((Reply @('tree')).result.workspaces | ForEach-Object sessions)
    if ($sessions.Count -ne 1 -or $sessions[0].id -ne $pane) { throw 'Duplicate changed the restored session tree' }
    $duplicate = Start-Process -FilePath $Exe -ArgumentList @('--app-id', $s.AppId, '--pipe', $s.Pipe, '--dir', 'C:\') -WindowStyle Hidden -PassThru
    $duplicates.Add($duplicate)
    if (-not $duplicate.WaitForExit(20000) -or $duplicate.ExitCode -ne 0) { throw 'Directory launch did not forward' }
    Start-Sleep -Seconds 2
    $sessions = @((Reply @('tree')).result.workspaces | ForEach-Object sessions)
    if ($sessions.Count -ne 2) { throw 'Directory launch did not create exactly one session' }
    InputWorks $pane 'ORIGINAL_STILL_WRITABLE'
    Restart-Sandbox $s -Kill | Out-Null
    InputWorks $pane 'AFTER_CRASH_RECOVERY'
    if (-not (Get-Process -Id $hostPid -ErrorAction SilentlyContinue)) { throw 'The original session host did not survive GUI restarts' }
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$hostPid")
    if (-not $children.Count) { throw 'No hosted children available for the no-restore regression' }
    Stop-Process -Id $s.Proc.Id -Force
    $null = $s.Proc.WaitForExit(10000)
    if (-not (Start-SandboxProcess $s @('--pipe', $s.Pipe, '--app-id', $s.AppId, '--no-restore'))) { throw 'Fresh GUI did not start' }
    # The startup reap runs after 30 seconds. A fresh GUI has no authority to
    # reap shells whose saved pane IDs it deliberately did not restore.
    Start-Sleep -Seconds 35
    foreach ($child in $children) {
        $survivor = Get-CimInstance Win32_Process -Filter "ProcessId=$($child.ProcessId)"
        if (-not $survivor -or $survivor.CreationDate -ne $child.CreationDate) { throw 'No-restore launch killed a surviving hosted child' }
    }
    Write-Host 'PASS: no-restore launch preserves pre-existing hosted children beyond startup cleanup'
    Write-Host 'PASS: restored input survives duplicate launch; explicit directory opens one new session'
}
finally {
    foreach ($process in $duplicates) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force } }
    # A failed UI assertion may leave its message loop blocked. Cleanup must not
    # synchronously send WM_CLOSE to that loop and hide the original failure.
    if ($s.Proc -and -not $s.Proc.HasExited) { Stop-Process -Id $s.Proc.Id -Force; $null = $s.Proc.WaitForExit(10000) }
    # This fixture deliberately keeps its host alive across GUI exit. End that
    # exact host too, so its inherited output handles cannot hold the runner open.
    if ($hostPid) { Stop-Process -Id $hostPid -Force -ErrorAction SilentlyContinue }
    Stop-Sandbox $s
}
