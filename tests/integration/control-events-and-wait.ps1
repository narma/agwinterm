# C1 acceptance: private Win32 state/pipe, actual CLI processes; no global input.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe = Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl = Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$id = 'agwinterm-c1-' + [guid]::NewGuid().ToString('N')
$state = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $id
$savedCaller = $env:AGWINTERM_SESSION_ID
$env:AGWINTERM_SESSION_ID = $null
$process = $null
$clients = [Collections.Generic.List[Diagnostics.Process]]::new()
. (Join-Path $PSScriptRoot '../ui/lib.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class C1Window {
    private delegate bool Visit(IntPtr hwnd, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visit visit, IntPtr unused);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    public static void Key(IntPtr hwnd, int key) {
        SendMessageW(hwnd, 0x0100, (IntPtr)key, (IntPtr)1);
        SendMessageW(hwnd, 0x0101, (IntPtr)key, (IntPtr)1);
    }
    public static IntPtr Find(int processId) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) => {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId) return true;
            var name = new StringBuilder(256); GetClassNameW(hwnd, name, name.Capacity);
            if (name.ToString() != "AgwintermWin32") return true;
            if ((GetWindowLongW(hwnd, -16) & 0x00C00000) == 0) return true; // quick is a captionless popup
            found = hwnd; return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@
function Raw($request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $id, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000)
        $bytes = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Depth 12 -Compress) + "`n")
        $pipe.Write($bytes,0,$bytes.Length)
        $reader = [IO.StreamReader]::new($pipe)
        return $reader.ReadLine() | ConvertFrom-Json
    } finally { $pipe.Dispose() }
}
function Reply([string[]]$argv) {
    $text = (& $ctl @argv --pipe $id --json) -join "`n"
    if (-not $text) { throw "No reply: $argv" }
    return $text | ConvertFrom-Json
}
function Ctl([string[]]$argv) {
    $reply = Reply $argv
    if (-not $reply.ok) { throw "$($argv -join ' '): $($reply | ConvertTo-Json -Compress)" }
    return $reply.result
}
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message }; "PASS $message" }
function BeginCtl([string[]]$argv) {
    $start = [Diagnostics.ProcessStartInfo]::new($ctl)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($arg in ($argv + @('--pipe',$id,'--json'))) { $start.ArgumentList.Add($arg) }
    $child = [Diagnostics.Process]::Start($start); $clients.Add($child)
    return $child
}
function EndCtl($child, [int]$expected) {
    $out = $child.StandardOutput.ReadToEndAsync(); $err = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(7000)) { throw 'CLI failed to terminate' }
    if ($child.ExitCode -ne $expected) { throw "Expected $expected got $($child.ExitCode): $($err.Result) $($out.Result)" }
    return $out.Result | ConvertFrom-Json
}
function Baseline { return (Raw @{cmd='events'; args=@{tail=$true}}).result }
function Events($baseline) { return (Ctl @('events','--since',"$($baseline.cursor)",'--run',$baseline.run,'--window',$baseline.window)).events }
try {
    New-Item -ItemType Directory -Path $state | Out-Null
    @('map f8 = split_pane', 'map f9 = toggle_scratch') | Set-Content -LiteralPath (Join-Path $state 'keymap.conf')
    @{default='C1 cmd';profiles=@(@{name='C1 cmd';command='cmd.exe';args=@('/d');cwd=$state})} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $state 'profiles.json')
    $process = Start-Process $exe -ArgumentList @('--app-id',$id,'--pipe',$id,'--no-restore','--default-session-host','inprocess') -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 40; $i++) { try { $null = Ctl @('ping'); break } catch { if ($i -eq 39) { throw }; Start-Sleep -Milliseconds 200 } }
    $a = Ctl @('session','new','--name','C1 observed')
    $null = Ctl @('session','split','on','--target',$a)
    $node = (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $a
    $left = $node.paneIds[0]; $right = $node.paneIds[1]
    $b = Ctl @('session','new','--name','C1 visible')
    $null = Ctl @('session','focus','left','--target',$a)
    $base = Baseline
    $null = Ctl @('session','focus','right','--target',$a)
    $null = Ctl @('session','resize','--split-ratio','0.3','--target',$a)
    $events = @(Events $base)
    Check ([bool]($events | Where-Object { $_.type -eq 'focus' -and $_.paneId -eq $right -and $_.sessionId -eq $a })) 'Background focus carries exact pane/session owners'
    Check (-not ($events | Where-Object type -eq 'selection')) 'Background focus does not emit selection'
    Check ([bool]($events | Where-Object { $_.type -eq 'split' -and $_.action -eq 'resized' })) 'Resize publishes committed layout'
    $page = Ctl @('events','--since',"$($base.cursor)",'--limit','1')
    Check ($page.cursor -lt $page.head -and $page.more) 'Limited page preserves continuation'
    $base = Baseline
    $null = Ctl @('session','focus','right','--target',$a)
    $null = Ctl @('session','resize','--split-ratio','0.3','--target',$a)
    $refused = Reply @('session','resize','--grow-top','1','--target',$a)
    Check (-not $refused.ok -and -not (@(Events $base) | Where-Object { $_.type -in @('focus','split') })) 'No-op and refused layout changes emit no false events'
    $base = Baseline
    $null = Ctl @('session','swap','--target',$a)
    $swaps = @(Events $base) | Where-Object { $_.type -eq 'split' -and $_.action -eq 'swapped' }
    Check ($swaps.Count -eq 1 -and $swaps[0].paneIds[0] -eq $right) 'Swap event follows stable pane IDs'
    $null = Ctl @('session','swap','--target',$a)
    $base = Baseline
    $null = Ctl @('session','split','on','--axis','horizontal','--target',$a)
    Check ([bool](@(Events $base) | Where-Object { $_.type -eq 'split' -and $_.action -eq 'axis-changed' -and $_.axis -eq 'horizontal' })) 'Axis change has an explicit committed event'
    $null = Ctl @('session','split','on','--axis','vertical','--target',$a)
    $null = Ctl @('session','status','blocked','--target',$left)
    $null = Ctl @('session','status','completed','--target',$right)
    $matched = EndCtl (BeginCtl @('session','wait','completed','--scope','pane','--target',$right)) 0
    Check ($matched.result.source -eq 'snapshot' -and $matched.result.paneId -eq $right) 'Pane snapshot matches independently of aggregate'
    $timed = EndCtl (BeginCtl @('session','wait','completed','--target',$a,'--wait-timeout','1')) 3
    Check ($timed.result.outcome -eq 'timeout') 'Blocked wins aggregate and timeout exits 3'
    $waiting = BeginCtl @('session','wait','completed','--target',$a)
    Start-Sleep -Milliseconds 200
    $null = Ctl @('session','split','close','--target',$left)
    $matched = EndCtl $waiting 0
    Check ($matched.result.source -eq 'transition' -and $matched.result.paneId -eq $right) 'Split removal recomputes session wait'
    $base = Baseline
    $null = Ctl @('session','scratch','on','--target',$b)
    $null = Ctl @('session','scratch','off','--target',$b)
    $events = @(Events $base)
    foreach ($action in 'opened','shown','hidden') { Check ([bool]($events | Where-Object { $_.type -eq 'scratch' -and $_.action -eq $action })) "Scratch $action published" }
    $scratchId = ($events | Where-Object { $_.type -eq 'scratch' -and $_.action -eq 'opened' }).paneId
    $auxiliary = EndCtl (BeginCtl @('session','wait','idle','--target',$scratchId)) 1
    Check ($auxiliary.code -eq 'invalid-target') 'Session wait refuses an auxiliary surface'
    $auxiliaryPane = EndCtl (BeginCtl @('session','wait','idle','--scope','pane','--target',$scratchId)) 0
    Check ($auxiliaryPane.result.paneId -eq $scratchId) 'Pane wait can observe a hidden scratch surface'
    $base = Baseline
    $overlay = Ctl @('session','overlay','open','--command','exit /b 7','--wait','--target',$b)
    for ($i=0; $i -lt 30; $i++) { $events = @(Events $base); if ($events | Where-Object { $_.type -eq 'overlay' -and $_.action -eq 'exited' }) { break }; Start-Sleep -Milliseconds 100 }
    Check ([bool]($events | Where-Object { $_.type -eq 'overlay' -and $_.action -eq 'opened' -and $_.overlayId -eq $overlay })) 'Overlay open has stable ID'
    Check ([bool]($events | Where-Object { $_.type -eq 'overlay' -and $_.action -eq 'exited' -and $_.exitCode -eq 7 })) 'Overlay exit reports actual process code'
    $null = Ctl @('session','overlay','close','--target',$b)
    $base = Baseline
    $oldOverlay = Ctl @('session','overlay','open','--command','ping -n 8 127.0.0.1 >nul','--target',$b)
    $newOverlay = Ctl @('session','overlay','open','--command','ping -n 8 127.0.0.1 >nul','--target',$b)
    $events = @(Events $base)
    Check ([bool]($events | Where-Object { $_.type -eq 'overlay' -and $_.action -eq 'closed' -and $_.overlayId -eq $oldOverlay -and $_.reason -eq 'replaced' })) 'Replacement closes the old overlay identity'
    Check (-not ($events | Where-Object { $_.type -eq 'overlay' -and $_.action -eq 'exited' -and $_.overlayId -eq $newOverlay })) 'Late replaced-process exit does not terminate the new overlay'
    $null = Ctl @('session','overlay','close','--target',$b)
    $base = Baseline
    $null = Ctl @('session','hud','open','C1 secret message','--target',$b)
    $null = Ctl @('session','hud','update','C1 replacement','--target',$b)
    $null = Ctl @('session','hud','close','--target',$b)
    $events = @(Events $base)
    Check (@($events | Where-Object type -eq 'hud').Count -eq 3) 'HUD open/update/close published'
    Check (($events | ConvertTo-Json -Depth 10) -notmatch 'secret message|replacement') 'HUD event omits message text'
    $null = Ctl @('session','status','idle','--target',$right)
    $gone = BeginCtl @('session','wait','completed','--target',$a)
    Start-Sleep -Milliseconds 200
    $null = Ctl @('session','close',$a)
    $removed = EndCtl $gone 1
    Check ($removed.code -eq 'target-gone') 'Removal terminates pinned waiter'
    $exited = Ctl @('session','new','--name','C1 exited retained','--command','cmd.exe /d /c exit 0','--command-mode','direct','--no-select')
    Start-Sleep -Milliseconds 500
    $null = Ctl @('session','status','completed','--target',$exited)
    $retained = EndCtl (BeginCtl @('session','wait','completed','--target',$exited)) 0
    Check ($retained.result.source -eq 'snapshot') 'Retained exited pane remains status-observable'
    $null = Ctl @('session','new','--name','C1 ambiguous','--no-select')
    $null = Ctl @('session','new','--name','C1 ambiguous','--no-select')
    $ambiguous = EndCtl (BeginCtl @('session','wait','idle','--target','C1 ambiguous')) 1
    Check ($ambiguous.code -eq 'invalid-target') 'Ambiguous wait target refuses without fallback'
    $watch = BeginCtl @('events','--watch','--kind','status')
    $first = $watch.StandardOutput.ReadLineAsync()
    Check ($first.Wait(3000)) 'Watch flushes baseline before events'
    $baseline = $first.Result | ConvertFrom-Json
    Check ($baseline.result.events.Count -eq 0 -and $baseline.result.schema -eq 1) 'Watch baseline is a versioned empty page'
    $null = Ctl @('session','status','completed','--target',$b)
    $next = $watch.StandardOutput.ReadLineAsync()
    Check ($next.Wait(3000)) 'Watch wakes for matching event'
    Check ((($next.Result | ConvertFrom-Json).result.events[0].sessionId) -eq $b) 'Watch event retains owner'
    $watch.Kill(); $watch.WaitForExit()
    $resume = BeginCtl @('events','--watch','--since',"$($baseline.result.cursor)",'--run',$baseline.result.run,'--window',$baseline.result.window)
    $resumedLine = $resume.StandardOutput.ReadLineAsync()
    Check ($resumedLine.Wait(3000) -and (($resumedLine.Result | ConvertFrom-Json).result.run -eq $baseline.result.run)) 'Explicit run/window/cursor resume works against the real host'
    $resume.Kill(); $resume.WaitForExit()
    $process.Refresh()
    $hwnd = [C1Window]::Find($process.Id)
    Check ($hwnd -ne [IntPtr]::Zero) 'Private UI window handle is available'
    $base = Baseline
    [C1Window]::Key($hwnd, 0x77) # fixture F8 -> split_pane, no global modifier state
    $events = @(Events $base)
    if (-not ($events | Where-Object { $_.type -eq 'split' -and $_.action -eq 'opened' })) { Write-Host ($events | ConvertTo-Json -Depth 8); Write-Host ((Ctl @('tree')) | ConvertTo-Json -Depth 8) }
    Check ([bool]($events | Where-Object { $_.type -eq 'split' -and $_.action -eq 'opened' -and $_.sessionId -eq $b })) 'Keyboard split emits the same owned lifecycle as control split'
    $base = Baseline
    [C1Window]::Key($hwnd, 0x77)
    Check ([bool](@(Events $base) | Where-Object { $_.type -eq 'split' -and $_.action -eq 'closed' })) 'Keyboard split close emits committed removal'
    $base = Baseline
    [C1Window]::Key($hwnd, 0x78) # fixture F9 -> toggle_scratch
    [C1Window]::Key($hwnd, 0x78)
    $events = @(Events $base)
    Check (@($events | Where-Object { $_.type -eq 'scratch' -and $_.action -in @('shown','hidden') }).Count -eq 2) 'Keyboard scratch visibility shares control event contract'
    $secondary = Ctl @('window','new','--name','C1 secondary')
    $other = Ctl @('session','new','--name','C1 secondary observed','--window',$secondary)
    $outside = EndCtl (BeginCtl @('session','wait','idle','--target',$b,'--window',$secondary)) 1
    Check ($outside.code -eq 'invalid-target') 'Explicit window bounds status-wait resolution'
    $beforeClose = Ctl @('events','--window',$secondary)
    $waitingWindow = BeginCtl @('session','wait','blocked','--target',$other,'--window',$secondary)
    Start-Sleep -Milliseconds 200
    $null = Ctl @('window','close','--target',$secondary)
    $windowGone = EndCtl $waitingWindow 1
    Check ($windowGone.code -eq 'window-gone') 'Window closure terminates its observer'
    $closedPage = Reply @('events','--window',$secondary,'--run',$beforeClose.run,'--since',"$($beforeClose.cursor)")
    Check ($closedPage.code -eq 'window-gone') 'Closed-window polling refuses instead of retargeting the frontmost window'
    $null = Ctl @('ping')
    'C1 Win32 acceptance passed'
} finally {
    foreach ($child in $clients) { if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit() }; $child.Dispose() }
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    $parent = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData'))
    $full = [IO.Path]::GetFullPath($state)
    if ([IO.Path]::GetDirectoryName($full) -eq $parent -and [IO.Path]::GetFileName($full) -match '^agwinterm-c1-[a-f0-9]{32}$') {
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
    $env:AGWINTERM_SESSION_ID = $savedCaller
}
