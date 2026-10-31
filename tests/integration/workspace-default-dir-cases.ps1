# Dot-sourced from win32-control.ps1 — workspace default directory (Win32 host + real spawn).
# Expects: Invoke-Ctl, Check, Wait-StateFile, $testAppDir

$wsDefaultRoot = Join-Path $testAppDir 'workspace-default-dir-root'
New-Item -ItemType Directory -Path $wsDefaultRoot -Force | Out-Null
$wsDefaultRootFull = [IO.Path]::GetFullPath($wsDefaultRoot)

function WsDefault-TreeWorkspace([string]$id) {
    $tree = Invoke-Ctl @('tree')
    if (-not $tree.ok) { return $null }
    foreach ($w in @($tree.result.workspaces)) {
        if ([string]$w.id -eq $id) { return $w }
    }
    return $null
}

function WsDefault-WaitText([string]$sessionId, [string]$needle, [int]$tries = 60) {
    for ($i = 0; $i -lt $tries; $i++) {
        $t = Invoke-Ctl @('session', 'text', '--target', $sessionId)
        if ($t.ok -and [string]$t.result -like "*$needle*") { return [string]$t.result }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function WsDefault-TextReportsCwd([string]$text, [string]$expectedDir) {
    if ([string]::IsNullOrEmpty($text) -or $text -notmatch 'CWD=') { return $false }
    try {
        # Terminal line-wrap can break a long path across rows; compare after removing newlines.
        $flat = ($text -replace '[\r\n]', '')
        $idx = $flat.IndexOf('CWD=', [StringComparison]::Ordinal)
        if ($idx -lt 0) { return $false }
        $reported = [IO.Path]::GetFullPath($flat.Substring($idx + 4).Trim())
        $want = [IO.Path]::GetFullPath($expectedDir)
        return $reported.Equals($want, [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}

function WsDefault-DeleteWorkspace([string]$wsId) {
    if ([string]::IsNullOrEmpty($wsId)) { return }
    try { Invoke-Ctl @('workspace', 'delete', '--target', $wsId) | Out-Null } catch { }
}

# Pause only this suite instance's UI thread to make queued creation deterministic.
# Always resume it before any readback or cleanup.
if (-not ('WsDefaultQueuedCreation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class WsDefaultQueuedCreation {
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static IntPtr Pause(IntPtr window, uint expectedPid) {
        uint pid; uint id = GetWindowThreadProcessId(window, out pid);
        if (id == 0 || pid != expectedPid) throw new InvalidOperationException("not the suite window");
        IntPtr thread = OpenThread(2, false, id);
        if (thread == IntPtr.Zero) throw new Win32Exception();
        if (SuspendThread(thread) == uint.MaxValue) { CloseHandle(thread); throw new Win32Exception(); }
        return thread;
    }
    public static void Resume(IntPtr thread) { ResumeThread(thread); CloseHandle(thread); }
}
'@
}

$wsRace = $null
$sesRace = $null
$wsNew = $null
$wsSet = $null
$sesDefault = $null
$sesExplicit = $null
try {
    $created = Invoke-Ctl @('workspace', 'new', 'ws-default-conform', '--default-dir', $wsDefaultRootFull)
    Check 'workspace new --default-dir succeeds' $created.ok ([string]$created.error)
    $wsNew = [string]$created.result
    $node = WsDefault-TreeWorkspace $wsNew
    Check 'tree carries defaultDir after workspace new' (
        $null -ne $node -and [string]$node.defaultDir -eq $wsDefaultRootFull)

    $setDir = Join-Path $testAppDir 'workspace-default-dir-set'
    New-Item -ItemType Directory -Path $setDir -Force | Out-Null
    $setDirFull = [IO.Path]::GetFullPath($setDir)
    $wsSetReply = Invoke-Ctl @('workspace', 'new', 'ws-default-set', '--collapsed', '--default-dir', $wsDefaultRootFull)
    Check 'workspace new for set target succeeds' $wsSetReply.ok
    $wsSet = [string]$wsSetReply.result
    $collapsedNode = WsDefault-TreeWorkspace $wsSet
    Check 'collapsed workspace creation preserves defaultDir' ($collapsedNode.collapsed -and [string]$collapsedNode.defaultDir -eq $wsDefaultRootFull)
    $set = Invoke-Ctl @('workspace', 'default-dir', 'set', $setDirFull, '--target', $wsSet)
    Check 'workspace default-dir set succeeds' ($set.ok -and [string]$set.result.defaultDir -eq $setDirFull)
    $nodeSet = WsDefault-TreeWorkspace $wsSet
    Check 'tree carries defaultDir after set' ($null -ne $nodeSet -and [string]$nodeSet.defaultDir -eq $setDirFull)

    $bad = Invoke-Ctl @('workspace', 'default-dir', 'set', 'Z:\agwinterm-no-such-default-dir', '--target', $wsSet)
    Check 'workspace default-dir set refuses missing directory' (-not $bad.ok)
    $nodeAfterBad = WsDefault-TreeWorkspace $wsSet
    Check 'tree defaultDir unchanged after refused set' ($null -ne $nodeAfterBad -and [string]$nodeAfterBad.defaultDir -eq $setDirFull)

    $stateFile = Wait-StateFile {
        param($json)
        $json -match '"DefaultDir"\s*:\s*"'
    }
    Check 'persisted state contains workspace DefaultDir' ($null -ne $stateFile)

    $clear = Invoke-Ctl @('workspace', 'default-dir', 'clear', '--target', $wsSet)
    Check 'workspace default-dir clear succeeds' ($clear.ok -and $null -eq $clear.result.defaultDir)
    $nodeCleared = WsDefault-TreeWorkspace $wsSet
    $hasDefaultKey = $null -ne $nodeCleared -and $nodeCleared.PSObject.Properties.Name -contains 'defaultDir'
    Check 'tree omits defaultDir after clear' ($null -ne $nodeCleared -and -not $hasDefaultKey)

    $deleted = Invoke-Ctl @('workspace', 'delete', '--target', $wsNew)
    Check 'workspace with default directory can be deleted' $deleted.ok
    $history = Invoke-Ctl @('recent', 'list')
    $entry = @($history.result.entries | Where-Object { $_.kind -eq 'workspace' -and $_.name -eq 'ws-default-conform' })[0]
    $reopened = Invoke-Ctl @('recent', 'open', [string]$entry.id)
    Check 'recent reopen restores workspace default directory' ($reopened.ok -and [string](WsDefault-TreeWorkspace $wsNew).defaultDir -eq $wsDefaultRootFull)

    $raceDir = Join-Path $testAppDir 'workspace-default-queued'
    New-Item -ItemType Directory -Path $raceDir -Force | Out-Null
    $process.Refresh()
    $pausedUi = [WsDefaultQueuedCreation]::Pause($process.MainWindowHandle, [uint32]$process.Id)
    try {
        $queued = Invoke-Ctl @('workspace', 'new', 'ws-default-queued', '--default-dir', $raceDir)
        $wsRace = [string]$queued.result
        Check 'workspace creation acknowledges while UI action is queued' $queued.ok
        Remove-Item -LiteralPath $raceDir -Force
    } finally { [WsDefaultQueuedCreation]::Resume($pausedUi) }
    $raceNode = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $raceNode = WsDefault-TreeWorkspace $wsRace
        if ($null -ne $raceNode) { break }
        Start-Sleep -Milliseconds 100
    }
    Check 'queued creation retains the validated default after directory disappears' ($null -ne $raceNode -and [string]$raceNode.defaultDir -eq $raceDir)
    New-Item -ItemType Directory -Path $raceDir -Force | Out-Null

    $cmd = 'cmd.exe /d /c cd && echo CWD=%CD%'
    $raceSession = Invoke-Ctl @('session', 'new', '--workspace', $wsRace, '--no-select', '--command-mode', 'direct', '--command', $cmd)
    $sesRace = [string]$raceSession.result
    $raceText = WsDefault-WaitText $sesRace 'CWD='
    Check 'reappeared directory is used by sessions in the queued workspace' ($raceSession.ok -and (WsDefault-TextReportsCwd $raceText $raceDir)) $raceText
    $newSes = Invoke-Ctl @('session', 'new', '--workspace', $wsNew, '--no-select', '--command-mode', 'direct', '--command', $cmd)
    Check 'session new in workspace with default-dir succeeds' $newSes.ok
    $sesDefault = [string]$newSes.result
    $text = WsDefault-WaitText $sesDefault 'CWD='
    Check 'new session spawns in workspace default directory' (WsDefault-TextReportsCwd $text $wsDefaultRootFull) $text

    $otherDir = Join-Path $testAppDir 'workspace-default-dir-explicit'
    New-Item -ItemType Directory -Path $otherDir -Force | Out-Null
    $otherFull = [IO.Path]::GetFullPath($otherDir)
    $newSes2 = Invoke-Ctl @(
        'session', 'new', '--workspace', $wsNew, '--no-select', '--cwd', $otherFull,
        '--command-mode', 'direct', '--command', $cmd)
    Check 'session new with explicit cwd succeeds' $newSes2.ok
    $sesExplicit = [string]$newSes2.result
    $text2 = WsDefault-WaitText $sesExplicit 'CWD='
    Check 'explicit cwd overrides workspace default' (WsDefault-TextReportsCwd $text2 $otherFull) $text2
}
finally {
    foreach ($sid in @($sesDefault, $sesExplicit, $sesRace)) {
        if ($sid) { try { Invoke-Ctl @('session', 'close', '--target', $sid) | Out-Null } catch { } }
    }
    WsDefault-DeleteWorkspace $wsRace
    WsDefault-DeleteWorkspace $wsNew
    WsDefault-DeleteWorkspace $wsSet
}
