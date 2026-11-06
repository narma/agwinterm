# Restart round-trip: state an agent sets must survive the app it set it in (P3, batch P3 of the
# parity programme; docs/plans/completed/2026-09-05-p3-persistence.md).
#
# Every cell is: sandbox up -> set a context and capture a running command -> the app goes down
# (gracefully, or killed) -> the SAME instance relaunches WITHOUT --no-restore -> `tree --json` must
# carry the context and the captured command. Modelled on agliteterm's test/restore-matrix.ps1
# (Cell / Stop-Lite -Kill / Signature): the killed cell is the one the batch exists for — before P3
# every ordinary save wrote "" into the captured slot, so a crash, a Stop-Process or a missed
# update-quit left exactly the file a restart then read.
#
#   graceful   WM_CLOSE -> WM_DESTROY -> the final save; relaunch; context + capture are back
#   killed     Stop-Process -Force: no final save, only the checkpoints `session context` and
#              `restore capture` wrote; relaunch; same assertions
#   refusal    a refused context (control character, over-length) and a refused capture target
#              before a graceful restart: the OLD value comes back and no slot was touched
#   replay     restore-commands = true in the sandbox's own agwinterm.conf, killed: after the
#              relaunch the captured command is typed back into the pane (`session text` shows it
#              with the `& ` prefix the replay adds, which the original typed line never had)
#   quit-capture  restore-commands = true, a child running, NO `restore capture` for its pane, WM_CLOSE:
#              after the relaunch `capturedCommands` names the child — the slot WM_DESTROY's own
#              capture filled (the one path SaveState(captureCommands: true) is reached from; #234)
#
# P4 (docs/plans/completed/2026-09-06-p4-splits.md, task 3) adds the split axis to the file, and two cells:
#
#   axis-graceful   `session split on --axis horizontal`, WM_CLOSE, relaunch: `tree --json` says
#                   horizontal with the SAME two pane ids in the same order, and `session metrics`
#                   proves the layout — each restored pane has the idle session's columns and at most
#                   half its rows (the axis is provable from the grid without a pixel)
#   axis-killed     the same after Stop-Process -Force: the split's own save wrote "Axis": "horizontal"
#                   at the time of the split (asserted on the file before the kill), so a killed app
#                   comes back stacked too
#
# P4 task 5 adds `session swap` — a swap moves panes, never ids, so after one the pane carrying the
# session id sits in slot 1 — and one cell:
#
#   swap-killed     split, swap (the tree lists [split id, session id]; the swap's own save wrote the
#                   panes in that order), Stop-Process -Force, relaunch: the same two ids come back in
#                   the SAME order with no duplicate — the loader creates pane 0 under its saved id
#                   rather than re-minting it as the session id — both still answer `session text`,
#                   and the file written after the restore lists them in that order again
#
# The long-lived child is `ping -n 300 127.0.0.1`, not a powershell one-liner: powershell, pwsh and
# cmd are on the restore denylist (restore-denylist.conf), so a shell child is the honest null and
# would prove nothing. ping is not denylisted, is quiet enough, and ends on its own.
#
# House rules (qa/product.md): sandbox instances only (--pipe AND --app-id, minted by Start-Sandbox;
# Restart-Sandbox refuses anything else — this is the first script that relaunches without
# --no-restore, which against a real app-id would restore and then overwrite the user's sessions),
# never keybd_event / SendInput, never the real profile. Do not run the .NET or Rust suites beside
# it: the cells are timing-sensitive (a shell must come up and echo before the capture runs).
param(
    [string]$Exe,
    [switch]$Strict,
    [string]$Only = ''          # run a single cell by name: graceful | killed | refusal | replay | quit-capture | axis-graceful | axis-killed | swap-killed
)

$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test RestoreRoundtrip -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
# agwintermctl exits nonzero while a pipe is coming up, and the ping loop in the harness polls it on
# purpose; with the native-command mapping on, that poll would throw on its first iteration.
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot\..\ui\lib.ps1"

$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { "  PASS  $name" }
    else { $script:fail++; "  FAIL  $name$(if ($detail) { " — $detail" })" }
}

"== restore round-trip =="
$ctl = Resolve-Binary $env:AGWINTERMCTL 'agwintermctl.exe' 'Agwinterm.Ctl'
$exe = Resolve-Binary $Exe 'Agwinterm.Win32.exe' 'Agwinterm.Win32'
if (-not $ctl) { "  SKIP  agwintermctl not found (set AGWINTERMCTL)"; exit ($Strict ? 1 : 0) }
if (-not $exe) { "  SKIP  agwinterm build not found (build src\Agwinterm.Win32 or pass -Exe)"; exit ($Strict ? 1 : 0) }
if ($exe -like "$env:LOCALAPPDATA\Programs\*") {
    # Resolve-Binary in lib.ps1 falls back to the installed app; a restore cell must run a build from
    # this tree, or a pre-P3 app "fails" every cell for a reason that has nothing to do with the code.
    "  SKIP  only an installed agwinterm was found; build src\Agwinterm.Win32 or pass -Exe"; exit ($Strict ? 1 : 0)
}
"  using: $exe"
"  ctl:   $ctl"

$ChildLine    = 'ping -n 300 127.0.0.1'
$ChildPattern = '(?i)ping(\.exe)?"?\s+-n 300 127\.0\.0\.1'   # the CIM command line: "C:\WINDOWS\system32\PING.EXE" -n 300 127.0.0.1

function Reply($s, [string[]]$a) { ConvertFrom-Json (Send-Ctl $s $a) }
function Tree($s)       { (Reply $s @('tree')).result }
function Sessions($s)   { Tree $s | ForEach-Object workspaces | ForEach-Object sessions }
function Node($s, $id)  { Sessions $s | Where-Object { $_.id -eq $id } }
function Sid($s)        { (Tree $s).workspaces[0].sessions[0].id }
function PaneText($s, $id) { [string](Reply $s @('session', 'text', '--target', $id)).result }
function StateFile($s) {
    Get-ChildItem -LiteralPath (Join-Path $s.AppDir 'windows') -Filter '*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

# A shell that has not drawn its prompt yet drops what is typed at it (the ConPTY early-discard
# class), so wait for SOME text before typing, and type again if the echo never shows.
function Wait-Prompt($s, $id) {
    for ($i = 0; $i -lt 40; $i++) {
        if ((PaneText $s $id).Trim().Length -gt 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Start-Child($s, $id) {
    foreach ($attempt in 1, 2) {
        Send-Ctl $s @('session', 'type', "$ChildLine`r", '--target', $id) | Out-Null
        for ($i = 0; $i -lt 16; $i++) {
            Start-Sleep -Milliseconds 250
            # Ping's banner is localized, and the echoed command alone does not
            # prove a child started. Require our exact command below this app.
            $processes = @(Get-CimInstance Win32_Process)
            $parents = @{}
            foreach ($process in $processes) { $parents[[int]$process.ProcessId] = [int]$process.ParentProcessId }
            foreach ($child in $processes | Where-Object { $_.Name -ieq 'PING.EXE' -and $_.CommandLine -match $ChildPattern }) {
                $ancestor = [int]$child.ParentProcessId
                $seen = @{}
                while ($ancestor -gt 0 -and -not $seen.ContainsKey($ancestor)) {
                    if ($ancestor -eq $s.Proc.Id) { return $true }
                    $seen[$ancestor] = $true
                    if (-not $parents.ContainsKey($ancestor)) { break }
                    $ancestor = $parents[$ancestor]
                }
            }
        }
    }
    return $false
}

# A killed app takes its ConPTYs with it and the children normally go too; if one does not, it must
# not outlive the run. Only OUR child's exact command line, and only when its parent is gone.
function Stop-StrayChildren {
    Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '-n 300 127\.0\.0\.1' } | ForEach-Object {
            $parent = Get-Process -Id $_.ParentProcessId -ErrorAction SilentlyContinue
            if (-not $parent) {
                "        (stopping stray ping pid $($_.ProcessId), parent $($_.ParentProcessId) gone)"
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
        }
}

# One cell: its own sandbox (own pipe, own minted app-id, own dir), torn down in finally whatever
# happened. Setup returns what Assert needs (ids); both run inside the try so a throw is reported
# as the cell's failure rather than ending the suite.
function Get-TestAgentHosts($s) {
    $pipePattern = '--pipe\s+"?' + [regex]::Escape($s.Pipe + '-owner-v1') + '"?(\s|$)'
    Get-CimInstance Win32_Process -Filter "Name='Agwinterm.Win32.exe'" |
        Where-Object { $_.CommandLine -match '--pty-host' -and $_.CommandLine -match $pipePattern }
}
function Cell {
    param([string]$Name, [switch]$Kill, [switch]$KillHostAfterClose, [switch]$WaitDetachedCompletion, [switch]$SkipDetachedAdoption, [string[]]$Conf = @(), [scriptblock]$Setup, [scriptblock]$Assert)
    if ($Only -and $Name -notlike $Only) { return }
    "-- $Name --"
    $s = $null
    try {
        $s = Start-Sandbox -Exe $exe -Ctl $ctl -Pipe "rr-$Name" -Conf $Conf
        if (-not $s.Proc -or $s.Proc.HasExited -or -not ((Send-Ctl $s @('ping')) -match '"ok":true')) { throw "the sandbox for '$Name' never came up" }
        # Setup's Check lines and its return value share one output stream: print the lines, keep the
        # hashtable — otherwise a setup FAIL is counted but never shown.
        $produced = @(& $Setup $s)
        $produced | Where-Object { $_ -is [string] } | ForEach-Object { $_ }
        $ctx = $produced | Where-Object { $_ -is [hashtable] } | Select-Object -Last 1
        if (-not $ctx) { throw 'setup returned no context' }
        $oldPid = $s.Proc.Id
        if ($KillHostAfterClose) {
            # The exact #368 reproduction: the shell is idle, the UI closes,
            # then its private PTY host dies before a fresh UI restores the file.
            [void][AgwUi]::PostMessageW($s.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
            if (-not $s.Proc.WaitForExit(20000)) { throw 'UI did not close gracefully' }
            if ($WaitDetachedCompletion) {
                $saved = Get-SavedSession $s $ctx.Sid
                Check 'agent-detached: UI closed with a live saved binding' ($saved.Panes[0].AgentResume -eq $ctx.Binding)
                $checkpoint = Join-Path $s.AppDir ('agent-completions/' + $saved.Panes[0].PromptToken)
                $generation = [long]$saved.Panes[0].AgentResumeGeneration
                if ($ctx.ExitPid) {
                    Check 'agent-detached: quit captured the live command and its shell checkpoint' `
                        ($saved.Panes[0].Command -match '-n 301' -and $saved.Panes[0].CommandToken -eq $saved.Panes[0].PromptToken)
                    Stop-Process -Id $ctx.ExitPid -Force
                }
                $finished = $false
                for ($i = 0; $i -lt 150; $i++) {
                    if ((Test-Path -LiteralPath $checkpoint) -and [long](Get-Content -LiteralPath $checkpoint -Raw) -gt $generation) { $finished = $true; break }
                    Start-Sleep -Milliseconds 100
                }
                Check 'agent-detached: PTY owner durably records exit without a UI' $finished
                if (-not $SkipDetachedAdoption) {
                if (-not (Start-SandboxProcess $s @('--pipe', $s.Pipe, '--app-id', $s.AppId))) { throw 'Adopting UI did not start' }
                Start-Sleep -Seconds 2
                $saved = Get-SavedSession $s $ctx.Sid
                Check 'agent-detached: adoption reconciles the completed binding' ($null -eq $saved.Panes[0].AgentResume)
                [void][AgwUi]::PostMessageW($s.Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
                if (-not $s.Proc.WaitForExit(20000)) { throw 'Adopting UI did not close' }
                }
            }
            $hosts = @(Get-TestAgentHosts $s)
            Check "$Name`: exactly one private PTY host survived UI close" ($hosts.Count -eq 1)
            foreach ($hostProcess in $hosts) { Stop-Process -Id $hostProcess.ProcessId -Force }
            if (-not (Start-SandboxProcess $s @('--pipe', $s.Pipe, '--app-id', $s.AppId))) { throw 'Fresh UI did not start' }
        } else { Restart-Sandbox $s -Kill:$Kill | Out-Null }
        Check "$Name`: the same instance relaunched (new pid, same pipe, same app-id)" `
            ($s.Proc.Id -ne $oldPid -and -not $s.Proc.HasExited) "old=$oldPid new=$($s.Proc.Id)"
        & $Assert $s $ctx
    } catch {
        Check "$Name ran to completion" $false $_.Exception.Message
    } finally {
        if ($s) {
            Get-TestAgentHosts $s | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
            Stop-Sandbox $s
        }
        Stop-StrayChildren
    }
}

# The shared setup: a context on the first session, an idle second session, the child in the first
# session's pane, a capture, and the state file already carrying both — the world before the restart.
function Setup-ContextAndCapture($s, [string]$contextText, [string]$name) {
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'the first shell never drew a prompt' }
    $set = Reply $s @('session', 'context', $contextText, '--target', $sid)
    Check "$name`: session context replies with the value in effect" ($set.ok -and $set.result.context -eq $contextText) ($set | ConvertTo-Json -Compress)
    $idle = [string](Reply $s @('session', 'new', '--name', 'idle', '--no-select')).result
    for ($i = 0; $i -lt 30 -and -not (Node $s $idle); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Start-Child $s $sid)) { throw 'the ping child never echoed in the first pane' }
    $cap = Reply $s @('restore', 'capture')
    $mine = @($cap.result.panes) | Where-Object { $_.pane -eq $sid }
    Check "$name`: restore capture names the running command in the first pane" `
        ($cap.ok -and $cap.result.captured -ge 1 -and $mine -and ("$($mine.captured)" -match $ChildPattern)) ($cap | ConvertTo-Json -Compress -Depth 5)
    Start-Sleep -Milliseconds 600
    $file = StateFile $s
    $raw = if ($file) { Get-Content -LiteralPath $file.FullName -Raw } else { '' }
    Check "$name`: the state file already carries the context and the capture (what a kill leaves)" `
        (($raw -match ('"Context":\s*"' + [regex]::Escape($contextText) + '"')) -and ($raw -match '(?i)"Command":\s*"[^"]*ping')) "file=$($file.FullName)"
    return @{ Sid = $sid; Idle = $idle; Context = $contextText; ReplayOnRestore = [bool]$cap.result.replayOnRestore }
}

function Assert-ContextAndCapture($s, $ctx, [string]$name) {
    Start-Sleep -Seconds 3   # the restored shells come up; the tree is readable before they do
    $n = Node $s $ctx.Sid
    Check "$name`: the context is back on the same session id" ($n -and $n.context -eq $ctx.Context) "node=$($n | ConvertTo-Json -Compress)"
    Check "$name`: the captured command is back under capturedCommands, readable before any replay" `
        ($n -and $n.PSObject.Properties['capturedCommands'] -and ("$($n.capturedCommands.($ctx.Sid))" -match $ChildPattern)) `
        "capturedCommands=$($n.capturedCommands | ConvertTo-Json -Compress)"
    $idle = Node $s $ctx.Idle
    Check "$name`: the idle session is back without a context or a capture" `
        ($idle -and -not $idle.PSObject.Properties['context'] -and -not ($idle.PSObject.Properties['capturedCommands'] -and $idle.capturedCommands.PSObject.Properties[$ctx.Idle])) `
        "node=$($idle | ConvertTo-Json -Compress)"
}

Cell -Name 'graceful' -Setup {
    param($s)
    Setup-ContextAndCapture $s 'graceful: survives WM_CLOSE' 'graceful'
} -Assert {
    param($s, $ctx)
    Assert-ContextAndCapture $s $ctx 'graceful'
    Start-Sleep -Seconds 4   # past the 2.5 s replay delay: with restore-commands off nothing is typed back
    Check 'graceful: nothing was replayed (restore-commands is off, and the capture reply said so)' `
        ((-not $ctx.ReplayOnRestore) -and ((PaneText $s $ctx.Sid) -notmatch '(?i)& "[^"]*ping')) "text=$(PaneText $s $ctx.Sid)"
}

Cell -Name 'killed' -Kill -Setup {
    param($s)
    Setup-ContextAndCapture $s 'killed: survives Stop-Process' 'killed'
} -Assert {
    param($s, $ctx)
    Assert-ContextAndCapture $s $ctx 'killed'
}

Cell -Name 'refusal' -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'the first shell never drew a prompt' }
    $keep = 'keep me: the refusals below change nothing'
    $set = Reply $s @('session', 'context', $keep, '--target', $sid)
    Check 'refusal: the context to keep is set' ($set.ok -and $set.result.context -eq $keep) ($set | ConvertTo-Json -Compress)
    $ctl1 = Reply $s @('session', 'context', "two`nlines", '--target', $sid)
    $ctl2 = Reply $s @('session', 'context', ('y' * 201), '--target', $sid)
    $cap  = Reply $s @('restore', 'capture', '--target', 'no-such-pane-zz')
    Start-Sleep -Milliseconds 600
    $n = Node $s $sid
    Check 'refusal: a newline is refused' ((-not $ctl1.ok) -and ("$($ctl1.error)" -match 'U\+000A')) "error=$($ctl1.error)"
    Check 'refusal: over the ceiling is refused' ((-not $ctl2.ok) -and ("$($ctl2.error)" -match '\b200\b')) "error=$($ctl2.error)"
    Check 'refusal: an unknown capture target is refused' ((-not $cap.ok) -and ("$($cap.error)" -match 'no-such-pane-zz')) "error=$($cap.error)"
    Check 'refusal: the old context stands and no slot was written' ($n.context -eq $keep -and -not $n.PSObject.Properties['capturedCommands']) "node=$($n | ConvertTo-Json -Compress)"
    $raw = (Get-Content -LiteralPath (StateFile $s).FullName -Raw)
    Check 'refusal: the state file carries the kept value and none of the refused text' `
        (($raw -match ('"Context":\s*"' + [regex]::Escape($keep) + '"')) -and ($raw -notmatch 'two') -and ($raw -notmatch 'yyyyy')) ''
    return @{ Sid = $sid; Context = $keep }
} -Assert {
    param($s, $ctx)
    Start-Sleep -Seconds 3
    $n = Node $s $ctx.Sid
    Check 'refusal: after the restart the kept context is back and no capture appeared' `
        ($n -and $n.context -eq $ctx.Context -and -not $n.PSObject.Properties['capturedCommands']) "node=$($n | ConvertTo-Json -Compress)"
}

# restore-commands is a config key (TerminalConfig.cs: `restore-commands = false` by default), and the
# sandbox has its own agwinterm.conf, so the replay half is checkable here rather than asserted.
Cell -Name 'replay' -Kill -Conf @('restore-commands = true') -Setup {
    param($s)
    $produced = @(Setup-ContextAndCapture $s 'replay: typed back after a kill' 'replay')
    $ctx = $produced | Where-Object { $_ -is [hashtable] } | Select-Object -Last 1
    $produced | Where-Object { $_ -is [string] }
    Check 'replay: the capture reply says the slot WILL be typed back (replayOnRestore true)' ([bool]$ctx.ReplayOnRestore) ''
    $ctx
} -Assert {
    param($s, $ctx)
    Assert-ContextAndCapture $s $ctx 'replay'
    # The replay is typed 2.5 s after the shell starts, with the call operator prefixed; give the
    # shell (profile and all) time to have drawn it.
    $typed = $false; $text = ''
    for ($i = 0; $i -lt 40; $i++) {
        $text = PaneText $s $ctx.Sid
        if ($text -match '(?i)& "[^"]*ping[^"]*"\s+-n 300 127\.0\.0\.1') { $typed = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Check 'replay: the captured command was typed back into the pane (with the `& ` prefix the replay adds)' $typed "text=$text"
}

# The QUIT-TIME capture: SaveState(captureCommands: true) is reached from WM_DESTROY alone and runs
# only with restore-commands on. The graceful cell above runs with it off and the replay cell is
# killed, so the code the P3 round-1 fix rewrote was run by no cell (#234). This one: the toggle on,
# a child running, NO `restore capture` for its pane, a clean close — the checkpoint the relaunch
# reads must be the one WM_DESTROY wrote. CIM is warmed first by a capture aimed at the IDLE pane
# (it writes null into that pane's slot and never touches the first pane's), so the quit path's 4 s
# budget is not what this cell measures on a cold provider.
Cell -Name 'quit-capture' -Conf @('restore-commands = true') -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'the first shell never drew a prompt' }
    $ctxText = 'quit-capture: WM_DESTROY fills the slot'
    $set = Reply $s @('session', 'context', $ctxText, '--target', $sid)
    Check 'quit-capture: session context replies with the value in effect' ($set.ok -and $set.result.context -eq $ctxText) ($set | ConvertTo-Json -Compress)
    $idle = [string](Reply $s @('session', 'new', '--name', 'idle', '--no-select')).result
    for ($i = 0; $i -lt 30 -and -not (Node $s $idle); $i++) { Start-Sleep -Milliseconds 200 }
    # The idle shell must have a pid before the warm-up names it: a pane whose child has not started
    # yet is filtered out of the query, and a capture with no pids never runs powershell at all.
    if (-not (Wait-Prompt $s $idle)) { throw 'the idle shell never drew a prompt' }
    if (-not (Start-Child $s $sid)) { throw 'the ping child never echoed in the first pane' }
    $warm = Reply $s @('restore', 'capture', '--target', $idle)
    $warmMine = @($warm.result.panes) | Where-Object { $_.pane -eq $sid }
    Check 'quit-capture: the warming capture is scoped to the idle pane (the first pane is not in its reply)' `
        ($warm.ok -and -not $warmMine -and @($warm.result.panes).Count -eq 1) ($warm | ConvertTo-Json -Compress -Depth 5)
    Start-Sleep -Milliseconds 600
    $raw = Get-Content -LiteralPath (StateFile $s).FullName -Raw
    Check 'quit-capture: before the close the state file carries the context and NO ping command (nothing captured the first pane yet)' `
        (($raw -match ('"Context":\s*"' + [regex]::Escape($ctxText) + '"')) -and ($raw -notmatch '(?i)"Command":\s*"[^"]*ping')) "file=$((StateFile $s).FullName)"
    return @{ Sid = $sid; Idle = $idle; Context = $ctxText }
} -Assert {
    param($s, $ctx)
    Start-Sleep -Seconds 3   # the restored shells come up; the tree is readable before they do
    $n = Node $s $ctx.Sid
    Check 'quit-capture: the context is back on the same session id' ($n -and $n.context -eq $ctx.Context) "node=$($n | ConvertTo-Json -Compress)"
    Check 'quit-capture: capturedCommands names the child — the slot WM_DESTROY filled, with no restore capture having named that pane' `
        ($n -and $n.PSObject.Properties['capturedCommands'] -and ("$($n.capturedCommands.($ctx.Sid))" -match $ChildPattern)) `
        "capturedCommands=$($n.capturedCommands | ConvertTo-Json -Compress)"
    $idle = Node $s $ctx.Idle
    Check 'quit-capture: the idle session is back with no capture (its shell ran nothing, at the warm-up and at the quit)' `
        ($idle -and -not ($idle.PSObject.Properties['capturedCommands'] -and $idle.capturedCommands.PSObject.Properties[$ctx.Idle])) `
        "node=$($idle | ConvertTo-Json -Compress)"
}

# ---- P4: the split axis survives the restart ----
#
# The idle session is the ruler: one pane, the full content grid. A stacked pane has its columns
# (the full width) and at most half its rows (the height, minus the divider, halved); a side-by-side
# pane would have its rows and about half its columns. `session metrics` answers per pane, so the
# comparison needs no pixel and no PrintWindow.
function Setup-HorizontalSplit($s, [string]$name) {
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'the first shell never drew a prompt' }
    $idle = [string](Reply $s @('session', 'new', '--name', 'idle', '--no-select')).result
    for ($i = 0; $i -lt 30 -and -not (Node $s $idle); $i++) { Start-Sleep -Milliseconds 200 }
    $split = Reply $s @('session', 'split', 'on', '--axis', 'horizontal', '--target', $sid)
    $n = Node $s $sid
    Check "$name`: session split on --axis horizontal answers the new pane id and the tree says horizontal" `
        ($split.ok -and $n -and $n.axis -eq 'horizontal' -and @($n.paneIds).Count -eq 2 -and ([string]$n.paneIds[1] -eq [string]$split.result)) `
        "split=$($split | ConvertTo-Json -Compress) node=$($n | ConvertTo-Json -Compress)"
    Start-Sleep -Milliseconds 600
    $file = StateFile $s
    $raw = if ($file) { Get-Content -LiteralPath $file.FullName -Raw } else { '' }
    Check "$name`: the state file already carries ""Axis"": ""horizontal"" (the split's own save — what a kill leaves)" `
        ($raw -match '"Axis":\s*"horizontal"') "file=$($file.FullName)"
    Check "$name`: the idle (single-pane, vertical) session writes no Axis key" `
        (([regex]::Matches($raw, '"Axis":')).Count -eq 1) "file=$($file.FullName)"
    return @{ Sid = $sid; Idle = $idle; PaneIds = @($n.paneIds | ForEach-Object { [string]$_ }) }
}

function Assert-HorizontalSplit($s, $ctx, [string]$name) {
    Start-Sleep -Seconds 3   # the restored shells come up; the tree and the grids are readable before they do
    $n = Node $s $ctx.Sid
    Check "$name`: the axis is back on the same session, with the same two pane ids in the same order" `
        ($n -and $n.axis -eq 'horizontal' -and ((@($n.paneIds) -join ',') -eq ($ctx.PaneIds -join ','))) "node=$($n | ConvertTo-Json -Compress)"
    $idle   = Reply $s @('session', 'metrics', '--target', $ctx.Idle)
    $top    = Reply $s @('session', 'metrics', '--target', $ctx.PaneIds[0])
    $bottom = Reply $s @('session', 'metrics', '--target', $ctx.PaneIds[1])
    $halfRows = [math]::Ceiling([int]$idle.result.rows / 2)
    Check "$name`: the restored panes are stacked — the idle session's columns, and at most half its rows, on both" `
        ($idle.ok -and $top.ok -and $bottom.ok -and
         [int]$top.result.cols -eq [int]$idle.result.cols -and [int]$bottom.result.cols -eq [int]$idle.result.cols -and
         [int]$top.result.rows -ge 1 -and [int]$top.result.rows -le $halfRows -and
         [int]$bottom.result.rows -ge 1 -and [int]$bottom.result.rows -le $halfRows) `
        "idle=$($idle.result.cols)x$($idle.result.rows) top=$($top.result.cols)x$($top.result.rows) bottom=$($bottom.result.cols)x$($bottom.result.rows)"
    $idleNode = Node $s $ctx.Idle
    Check "$name`: the idle session is back with one pane and no axis" `
        ($idleNode -and -not $idleNode.PSObject.Properties['paneCount'] -and -not $idleNode.PSObject.Properties['axis']) "node=$($idleNode | ConvertTo-Json -Compress)"
    $raw = (Get-Content -LiteralPath (StateFile $s).FullName -Raw)
    Check "$name`: the file written after the restore still carries the key, once" `
        ((([regex]::Matches($raw, '"Axis":\s*"horizontal"')).Count -eq 1) -and (([regex]::Matches($raw, '"Axis":')).Count -eq 1)) ''
}

Cell -Name 'axis-graceful' -Setup {
    param($s)
    Setup-HorizontalSplit $s 'axis-graceful'
} -Assert {
    param($s, $ctx)
    Assert-HorizontalSplit $s $ctx 'axis-graceful'
}

Cell -Name 'axis-killed' -Kill -Setup {
    param($s)
    Setup-HorizontalSplit $s 'axis-killed'
} -Assert {
    param($s, $ctx)
    Assert-HorizontalSplit $s $ctx 'axis-killed'
}

# P4 task 5: a swapped session — the session id on pane 1 — restores with its ids where the swap left
# them. Before durable ids the loader created pane 0 as the session id and appended pane 1 under its
# saved id (also the session id): a duplicate, and the split shell renamed. The file's own shape is
# pinned first (the swap's save lists the panes in the swapped order), then the kill, then the tree,
# the resolver (both ids answer `session text`) and the next save.
function Get-SavedSession($s, [string]$id) {
    $file = StateFile $s
    if (-not $file) { return $null }
    $st = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($w in @($st.Workspaces)) { foreach ($ss in @($w.Sessions)) { if ($ss.Id -eq $id) { return $ss } } }
    return $null
}

Cell -Name 'swap-killed' -Kill -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'the first shell never drew a prompt' }
    $split = Reply $s @('session', 'split', 'on', '--target', $sid)
    $splitId = [string]$split.result
    $swap = Reply $s @('session', 'swap', '--target', $sid)
    $n = Node $s $sid
    Check "swap-killed: session swap answers the session, the pane ids reversed and the axis, and the tree agrees" `
        ($split.ok -and $swap.ok -and $swap.result.session -eq $sid -and
         ((@($swap.result.paneIds) -join ',') -eq "$splitId,$sid") -and $swap.result.axis -eq 'vertical' -and
         $n -and ((@($n.paneIds) -join ',') -eq "$splitId,$sid") -and [int]$n.focusedPane -eq 0) `
        "split=$($split | ConvertTo-Json -Compress) swap=$($swap | ConvertTo-Json -Compress) node=$($n | ConvertTo-Json -Compress)"
    Start-Sleep -Milliseconds 600
    $saved = Get-SavedSession $s $sid
    Check "swap-killed: the swap's own save lists the panes in the swapped order — the session id on pane 1 (what a kill leaves)" `
        ($saved -and @($saved.Panes).Count -eq 2 -and $saved.Panes[0].Id -eq $splitId -and $saved.Panes[1].Id -eq $sid -and $saved.Id -eq $sid) `
        "saved=$($saved | ConvertTo-Json -Compress -Depth 4)"
    return @{ Sid = $sid; SplitId = $splitId; PaneIds = @($n.paneIds | ForEach-Object { [string]$_ }) }
} -Assert {
    param($s, $ctx)
    Start-Sleep -Seconds 3
    $n = Node $s $ctx.Sid
    $ids = @($n.paneIds | ForEach-Object { [string]$_ })
    Check "swap-killed: the same two pane ids are back in the same order — the session id on pane 1 — with no duplicate" `
        ($n -and $ids.Count -eq 2 -and (($ids -join ',') -eq ($ctx.PaneIds -join ',')) -and ($ids | Select-Object -Unique).Count -eq 2 -and $ids[1] -eq $ctx.Sid) `
        "node=$($n | ConvertTo-Json -Compress)"
    $bySplit = Reply $s @('session', 'text', '--target', $ctx.SplitId)
    $bySession = Reply $s @('session', 'text', '--target', $ctx.Sid)
    Check "swap-killed: both ids still answer session text (the session id reaches its own pane, in slot 1)" `
        ($bySplit.ok -and $bySession.ok) "bySplit=$($bySplit.ok) bySession=$($bySession.ok)"
    $saved = Get-SavedSession $s $ctx.Sid
    Check "swap-killed: the file written after the restore lists the panes in the swapped order again" `
        ($saved -and @($saved.Panes).Count -eq 2 -and $saved.Panes[0].Id -eq $ctx.SplitId -and $saved.Panes[1].Id -eq $ctx.Sid) `
        "saved=$($saved | ConvertTo-Json -Compress -Depth 4)"
}

foreach ($agentHost in @('server', 'server-rust')) {
Cell -Name "agent-captured-detached-$agentHost" -KillHostAfterClose -WaitDetachedCompletion -SkipDetachedAdoption -Conf @("session-host = $agentHost", 'restore-commands = true') -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'Shell did not start' }
    Send-Ctl $s @('session', 'type', ('ping -n 301 127.0.0.2' + [char]13), '--target', $sid) | Out-Null
    $child = $null
    for ($i = 0; $i -lt 30; $i++) {
        $child = Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -match '-n 301 127\.0\.0\.2' } | Select-Object -First 1
        if ($child) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $child) { throw 'Capture fixture did not start' }
    $binding = "Write-Host ('UNEXPECTED_' + 'CAPTURED_RESUME')"
    $null = Reply $s @('session', 'bind', $binding, '--target', $sid)
    Start-Sleep -Milliseconds 400
    return @{ Sid = $sid; Binding = $binding; ExitPid = [int]$child.ProcessId }
} -Assert {
    param($s, $ctx)
    Start-Sleep -Seconds 4
    $saved = Get-SavedSession $s $ctx.Sid
    Check 'agent-captured: cold restore clears the finished binding' ($null -eq $saved.Panes[0].AgentResume)
    Check 'agent-captured: cold restore clears the finished captured command' ([string]::IsNullOrEmpty($saved.Panes[0].Command))
    $replayed = @(Get-CimInstance Win32_Process -Filter "Name = 'PING.EXE'" | Where-Object { $_.CommandLine -match '-n 301 127\.0\.0\.2' })
    Check 'agent-captured: restore-commands does not launch the exited process' ($replayed.Count -eq 0)
    Check 'agent-captured: agent binding is not replayed either' (-not (PaneText $s $ctx.Sid).Contains('UNEXPECTED_CAPTURED_RESUME'))
}
Cell -Name "agent-detached-$agentHost" -KillHostAfterClose -WaitDetachedCompletion -Conf @("session-host = $agentHost", 'emulator-core = managed', 'restore-commands = false') -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'Shell did not start' }
    Send-Ctl $s @('session', 'type', ("Write-Host ('DETACHED_' + 'AGENT'); Start-Sleep 10" + [char]13), '--target', $sid) | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        if ((PaneText $s $sid).Contains('DETACHED_AGENT')) { break }
        Start-Sleep -Milliseconds 100
    }
    $binding = "Write-Host ('UNEXPECTED_' + 'DETACHED_RESUME')"
    $null = Reply $s @('session', 'bind', $binding, '--target', $sid)
    Start-Sleep -Milliseconds 400
    return @{ Sid = $sid; Binding = $binding }
} -Assert {
    param($s, $ctx)
    Start-Sleep -Seconds 3
    $saved = Get-SavedSession $s $ctx.Sid
    Check 'agent-detached: later host loss keeps recovery empty' ($null -eq $saved.Panes[0].AgentResume)
    Check 'agent-detached: exited conversation never restarts' (-not (PaneText $s $ctx.Sid).Contains('UNEXPECTED_DETACHED_RESUME'))
}
}

foreach ($agentCore in @('managed', 'rust')) {
Cell -Name "agent-running-$agentCore" -KillHostAfterClose -Conf @('session-host = server-rust', "emulator-core = $agentCore", 'restore-commands = false') -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'Shell did not start' }
    $command = 'Write-Host (''LIVE_'' + ''AGENT''); Start-Sleep 3; $e=[char]27; $b=[char]7; [Console]::Write("$e]777;agwinterm;command-finished$b"); [Console]::Write("$e]777;agwinterm;command-finished;00000000000000000000000000000000;999$b"); [Console]::Write("$e]777;agwinterm;command-finished;$env:AGWINTERM_PROMPT_TOKEN;1$b"); Write-Host (''SPOOF_'' + ''DONE''); Start-Sleep 120'
    Send-Ctl $s @('session', 'type', ($command + [char]13), '--target', $sid) | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        if ((PaneText $s $sid).Contains('LIVE_AGENT')) { break }
        Start-Sleep -Milliseconds 100
    }
    $binding = "Write-Host ('EXPECTED_' + 'AGENT_RESUME'); Start-Sleep 120"
    $null = Reply $s @('session', 'bind', $binding, '--target', $sid)
    Start-Sleep -Milliseconds 400
    for ($i = 0; $i -lt 60; $i++) {
        if ((PaneText $s $sid).Contains('SPOOF_DONE')) { break }
        Start-Sleep -Milliseconds 100
    }
    $saved = Get-SavedSession $s $sid
    Check 'agent-running: bare, foreign and replayed output cannot clear a running binding' ($saved.Panes[0].AgentResume -eq $binding)
    return @{ Sid = $sid; Binding = $binding }
} -Assert {
    param($s, $ctx)
    for ($i = 0; $i -lt 60; $i++) {
        if ((PaneText $s $ctx.Sid).Contains('EXPECTED_AGENT_RESUME')) { break }
        Start-Sleep -Milliseconds 200
    }
    Check 'agent-running: loss of PTY resumes the live agent' ((PaneText $s $ctx.Sid).Contains('EXPECTED_AGENT_RESUME'))
    $saved = Get-SavedSession $s $ctx.Sid
    Check 'agent-running: the initial restored prompt preserves its binding' ($saved.Panes[0].AgentResume -eq $ctx.Binding)
}
Cell -Name "agent-exit-$agentCore" -KillHostAfterClose -Conf @('session-host = server-rust', "emulator-core = $agentCore", 'restore-commands = false') -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'Shell did not start' }
    $command = "Write-Host ('AGENT_' + 'STARTED'); Start-Sleep 5; Write-Host ('AGENT_' + 'DONE')"
    Send-Ctl $s @('session', 'type', ($command + [char]13), '--target', $sid) | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        if ((PaneText $s $sid).Contains('AGENT_STARTED')) { break }
        Start-Sleep -Milliseconds 100
    }
    $binding = "Write-Host ('UNEXPECTED_' + 'AGENT_RESUME')"
    $bound = Reply $s @('session', 'bind', $binding, '--target', $sid)
    Check 'agent-exit: running command can bind independently of restore-commands' $bound.ok
    Start-Sleep -Milliseconds 400
    $saved = Get-SavedSession $s $sid
    Check 'agent-exit: live binding is persisted' ($saved.Panes[0].AgentResume -eq $binding)
    for ($i = 0; $i -lt 80; $i++) {
        $saved = Get-SavedSession $s $sid
        if ((PaneText $s $sid).Contains('AGENT_DONE') -and $null -eq $saved.Panes[0].AgentResume) { break }
        Start-Sleep -Milliseconds 100
    }
    Check 'agent-exit: returning to the prompt clears the saved binding' ($null -eq $saved.Panes[0].AgentResume)
    return @{ Sid = $sid }
} -Assert {
    param($s, $ctx)
    Start-Sleep -Seconds 3
    $saved = Get-SavedSession $s $ctx.Sid
    Check 'agent-exit: lost host restore keeps the binding empty' ($null -eq $saved.Panes[0].AgentResume)
    Check 'agent-exit: lost host restore does not replay the exited agent' (-not (PaneText $s $ctx.Sid).Contains('UNEXPECTED_AGENT_RESUME'))
}
}

Cell -Name 'agent-startup' -KillHostAfterClose -Conf @('session-host = server-rust', 'restore-commands = false') -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'Shell did not start' }
    $null = Reply $s @('install', 'shell', '--target', $sid)
    $binding = "Write-Host ('UNEXPECTED_' + 'STARTUP_RESUME')"
    $gate = Join-Path $s.AppDir 'startup-release.flag'
    $report = Join-Path $s.AppDir 'startup-bind.json'
    $quotedBinding = $binding.Replace("'", "''")
    $command = "`$env:DOTNET_ROOT='$env:DOTNET_ROOT'; `$env:DOTNET_ROOT_X64='$env:DOTNET_ROOT_X64'; & '$ctl' session bind '$quotedBinding' --json | Set-Content -LiteralPath '$report'; while (-not (Test-Path -LiteralPath '$gate')) { Start-Sleep -Milliseconds 100 }; Write-Host ('STARTUP_' + 'DONE')"
    $scriptPath = Join-Path $s.AppDir 'startup-agent.ps1'
    [IO.File]::WriteAllText($scriptPath, $command)
    # Per-process override only: the guest's default execution policy stays intact.
    $startupCommand = "powershell.exe -NoLogo -NoExit -ExecutionPolicy Bypass -File `"$scriptPath`""
    $created = Reply $s @('session', 'new', '--command-mode', 'direct', '--command', $startupCommand)
    $startup = [string]$created.result
    Check 'agent-startup: startup-command pane created' $created.ok
    $sawBound = $false
    for ($i = 0; $i -lt 100; $i++) {
        $saved = Get-SavedSession $s $startup
        if ($saved -and $saved.Panes[0].AgentResume -eq $binding) { $sawBound = $true; break }
        Start-Sleep -Milliseconds 100
    }
    $bindReply = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw } else { 'no startup CLI reply' }
    Check 'agent-startup: startup command really established a live binding' $sawBound ($bindReply + " SCREEN: " + (PaneText $s $startup))
    [IO.File]::WriteAllText($gate, '')
    for ($i = 0; $i -lt 100; $i++) {
        $saved = Get-SavedSession $s $startup
        if ($saved -and (PaneText $s $startup).Contains('STARTUP_DONE') -and $null -eq $saved.Panes[0].AgentResume) { break }
        Start-Sleep -Milliseconds 100
    }
    Check 'agent-startup: first interactive prompt clears the completed startup binding' ($saved -and $null -eq $saved.Panes[0].AgentResume)
    return @{ Sid = $startup }
} -Assert {
    param($s, $ctx)
    Start-Sleep -Seconds 3
    Check 'agent-startup: host loss does not restart the startup agent' (-not (PaneText $s $ctx.Sid).Contains('UNEXPECTED_STARTUP_RESUME'))
}

Cell -Name 'agent-slow-prompt' -KillHostAfterClose -Conf @('session-host = server-rust', 'restore-commands = false') -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'Shell did not start' }
    $null = Reply $s @('install', 'shell', '--target', $sid)
    # This script is VM-routed before any UI work: this is the disposable guest
    # profile, restored below; the user's host profile is never accessed.
    $profilePath = (& powershell.exe -NoProfile -Command '$PROFILE.CurrentUserCurrentHost').Trim()
    $profileText = [IO.File]::ReadAllText($profilePath)
    $readyFile = Join-Path $s.AppDir 'slow-profile-ready.flag'
    [IO.File]::AppendAllText($profilePath, "`nStart-Sleep -Seconds 15; [IO.File]::WriteAllText('$readyFile', '')`n")
    $profilesPath = Join-Path $s.AppDir 'profiles.json'
    $profiles = Get-Content -LiteralPath $profilesPath -Raw | ConvertFrom-Json
    $profiles.profiles += [pscustomobject]@{ name = 'Slow prompt fixture'; command = 'powershell.exe'; args = @('-NoLogo', '-NoExit', '-ExecutionPolicy', 'Bypass') }
    $profiles | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $profilesPath -Encoding utf8
    $null = Reply $s @('profiles', 'reload')
    $sid = [string](Reply $s @('session', 'new', '--profile', 'Slow prompt fixture')).result
    if (-not (Wait-Prompt $s $sid)) { throw 'Slow fixture shell did not start' }
    Send-Ctl $s @('session', 'type', ("Write-Host ('SLOW_' + 'LIVE'); Start-Sleep 120" + [char]13), '--target', $sid) | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        if ((PaneText $s $sid).Contains('SLOW_LIVE')) { break }
        Start-Sleep -Milliseconds 100
    }
    $binding = "Write-Host ('SLOW_' + 'RESUMED'); Start-Sleep 120"
    $null = Reply $s @('session', 'bind', $binding, '--target', $sid)
    $cancelSid = [string](Reply $s @('session', 'new', '--profile', 'Slow prompt fixture')).result
    if (-not (Wait-Prompt $s $cancelSid)) { throw 'Cancellation fixture shell did not start' }
    Send-Ctl $s @('session', 'type', ("Write-Host ('CANCEL_' + 'LIVE'); Start-Sleep 120" + [char]13), '--target', $cancelSid) | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        if ((PaneText $s $cancelSid).Contains('CANCEL_LIVE')) { break }
        Start-Sleep -Milliseconds 100
    }
    $null = Reply $s @('session', 'bind', "Write-Host ('CANCEL_' + 'SHOULD_NOT_RUN'); Start-Sleep 120", '--target', $cancelSid)
    Remove-Item -LiteralPath $readyFile -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    return @{ Sid = $sid; CancelSid = $cancelSid; Binding = $binding; ProfilePath = $profilePath; ProfileText = $profileText; ReadyFile = $readyFile }
} -Assert {
    param($s, $ctx)
    try {
        $cleared = Reply $s @('session', 'bind', 'none', '--target', $ctx.CancelSid)
        Check 'agent-slow-prompt: user can cancel recovery while the initial prompt is pending' $cleared.ok
        for ($i = 0; $i -lt 100; $i++) {
            if ((PaneText $s $ctx.Sid).Contains('SLOW_RESUMED')) { break }
            Start-Sleep -Milliseconds 200
        }
        $saved = Get-SavedSession $s $ctx.Sid
        Check 'agent-slow-prompt: the delayed profile actually ran' (Test-Path -LiteralPath $ctx.ReadyFile)
        Check 'agent-slow-prompt: queued recovery runs after the fifteen-second profile' ((PaneText $s $ctx.Sid).Contains('SLOW_RESUMED'))
        Check 'agent-slow-prompt: the late initial prompt preserves recovery' ($saved.Panes[0].AgentResume -eq $ctx.Binding)
        $cancelled = Get-SavedSession $s $ctx.CancelSid
        Check 'agent-slow-prompt: delayed replay does not undo bind none' ($null -eq $cancelled.Panes[0].AgentResume)
        Check 'agent-slow-prompt: cancelled command was never sent' (-not (PaneText $s $ctx.CancelSid).Contains('CANCEL_SHOULD_NOT_RUN'))
    } finally { [IO.File]::WriteAllText($ctx.ProfilePath, $ctx.ProfileText) }
}

foreach ($reflowHost in 'in-process','server') {
$reflowName = if ($reflowHost -eq 'server') { 'buffer-reflow-ghostty-host' } else { 'buffer-reflow-ghostty' }
Cell -Name $reflowName -Conf @('restore-buffer = true', "session-host = $reflowHost", 'emulator-core = ghostty') -Setup {
    param($s)
    $sid = Sid $s
    if (-not (Wait-Prompt $s $sid)) { throw 'shell did not start' }
    # Hosted Windows runners can cap the requested window width. Use a marker that
    # fits their wide grid, and prove it exceeds the actual narrow grid first.
    [void][AgwUi]::SetWindowPos($s.Hwnd, [IntPtr]::Zero, 0, 0, 800, 800, 0x0004)
    Start-Sleep -Seconds 2
    $marker = 'REFLOW_BEGIN_' + ('0123456789' * 4) + '_REFLOW_END'
    $metrics = Reply $s @('session', 'metrics', '--target', $sid)
    Check 'buffer-reflow: source grid forces marker wrapping' ([int]$metrics.result.cols -lt $marker.Length) ($metrics | ConvertTo-Json -Compress)
    # The command itself does not contain the complete marker, so echo cannot satisfy the assertion.
    $command = "Write-Host ('REFLOW_BEGIN_' + ('0123456789' * 4) + '_REFLOW_END'); 1..60 | ForEach-Object { Write-Host ('after_' + `$_) }"
    Send-Ctl $s @('session', 'type', ($command + [char]13), '--target', $sid) | Out-Null
    Start-Sleep -Seconds 2
    $text = [string](Reply $s @('session', 'text', '--all', '--target', $sid)).result
    Check 'buffer-reflow: long output arrived before saving' ($text.Replace([string][char]13,'').Replace([string][char]10,'') -like '*REFLOW_BEGIN_0123456789*')
    $ownerPid = $s.Proc.Id
    if ($reflowHost -eq 'server') {
        $hosts = @(Get-TestAgentHosts $s)
        if ($hosts.Count -ne 1) { throw 'Expected exactly one isolated owner host' }
        $ownerPid = $hosts[0].ProcessId
    }
    $loaded = @((Get-Process -Id $ownerPid).Modules | Where-Object ModuleName -eq 'agwinterm_ghostty.dll').Count -eq 1
    Check 'buffer-reflow: Ghostty is loaded by the terminal owner' $loaded
    [pscustomobject]@{ Sid=$sid; HostPid=$ownerPid }
} -Assert {
    param($s,$ctx)
    $marker = 'REFLOW_BEGIN_' + ('0123456789' * 4) + '_REFLOW_END'
    if ($reflowHost -eq 'server') {
        $hosts = @(Get-TestAgentHosts $s)
        Check 'buffer-reflow: the same owner survives UI restart' ($hosts.Count -eq 1 -and $hosts[0].ProcessId -eq $ctx.HostPid)
    }
    [void][AgwUi]::SetWindowPos($s.Hwnd, [IntPtr]::Zero, 0, 0, 2200, 800, 0x0004)
    Start-Sleep -Seconds 2
    $text = [string](Reply $s @('session', 'text', '--all', '--target', $ctx.Sid)).result
    $marker = 'REFLOW_BEGIN_' + ('0123456789' * 4) + '_REFLOW_END'
    $metrics = Reply $s @('session', 'metrics', '--target', $ctx.Sid)
    Check 'buffer-reflow: widened grid fits the complete marker' ([int]$metrics.result.cols -ge $marker.Length) ($metrics | ConvertTo-Json -Compress)
    Check 'buffer-reflow: restored output joins into one row after widening' ($text.Contains($marker)) $text
    [void][AgwUi]::SetWindowPos($s.Hwnd, [IntPtr]::Zero, 0, 0, 800, 800, 0x0004)
    Start-Sleep -Milliseconds 500
    Restart-Sandbox $s | Out-Null
    [void][AgwUi]::SetWindowPos($s.Hwnd, [IntPtr]::Zero, 0, 0, 2200, 800, 0x0004)
    Start-Sleep -Seconds 2
    $text = [string](Reply $s @('session', 'text', '--all', '--target', $ctx.Sid)).result
    Check 'buffer-reflow: narrow save and second restore retain logical line' ($text.Contains($marker)) $text
}
}

. "$PSScriptRoot/ghostty-remote-restore-cases.ps1"

if ($fail) { "restore round-trip: $fail FAILED"; exit 1 }
"restore round-trip: all passed"
exit 0
