# C2: disposable app state, actual CLI, stable IDs and native geometry readback.
param([int]$Dpi = 96)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe = Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl = Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$id = 'agwinterm-c2-' + [guid]::NewGuid().ToString('N')
$state = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $id
$savedCaller = $env:AGWINTERM_SESSION_ID
$savedDpi = $env:AGWINTERM_DPI_OVERRIDE
$env:AGWINTERM_DPI_OVERRIDE = "$Dpi"
$env:AGWINTERM_SESSION_ID = $null
$process = $null
$blocking = $null
. (Join-Path $PSScriptRoot '../ui/lib.ps1')
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class C2Window {
    private delegate bool Visit(IntPtr hwnd, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visit visit, IntPtr unused);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static void Reveal(IntPtr hwnd) { ShowWindow(hwnd, 4); }
    public static IntPtr Find(int processId) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) => {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId) return true;
            var name = new StringBuilder(256); GetClassNameW(hwnd, name, name.Capacity);
            if (name.ToString() != "AgwintermWin32" || (GetWindowLongW(hwnd, -16) & 0x00C00000) == 0) return true;
            found = hwnd; return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@
function Reply([string[]]$argv) { return ((& $ctl @argv --pipe $id --json) -join "`n") | ConvertFrom-Json }
function Ctl([string[]]$argv) {
    $reply = Reply $argv
    if (-not $reply.ok) { throw "$($argv -join ' '): $($reply | ConvertTo-Json -Compress)" }
    return $reply.result
}
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message }; "PASS $message" }
function Node($target) { return (Ctl @('tree')).workspaces.sessions | Where-Object id -eq $target }
function Detail($target, $overlay) { return (Node $target).overlayDetails | Where-Object id -eq $overlay }
try {
    New-Item -ItemType Directory -Path $state | Out-Null
    @{default='C2 cmd';profiles=@(@{name='C2 cmd';command='cmd.exe';args=@('/d');cwd=$state})} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $state 'profiles.json')
    $process = Start-Process $exe -ArgumentList @('--app-id',$id,'--pipe',$id,'--no-restore','--default-session-host','inprocess') -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 40; $i++) { try { $a = (Ctl @('tree')).workspaces[0].sessions[0].id; if ($a) { break } } catch { if ($i -eq 39) { throw } }; Start-Sleep -Milliseconds 200 }
    $b = Ctl @('session','new','--name','C2 background','--no-select')
    $cwd = Join-Path $state 'path with spaces'; New-Item -ItemType Directory -Path $cwd | Out-Null
    $overlay = Ctl @('session','overlay','open','cmd /d','--target',$b,'--cwd',$cwd,'--width','400','--size-percent','60','--background-color','#123456')
    $detail = Detail $b $overlay
    Check (-not $detail.visible -and -not $detail.hidden -and $detail.geometry.widthDip -eq 400 -and $detail.geometry.percent -eq 60) 'Background overlay keeps requested mixed geometry without selection'
    $null = Ctl @('session','overlay','hide','--target',$overlay)
    $null = Ctl @('session','select',$b)
    Check ((Detail $b $overlay).hidden -and -not (Detail $b $overlay).visible) 'Selecting a session does not reveal a hidden overlay'
    $null = Ctl @('session','type',('echo UNDER_C2' + [char]13),'--allow-control','--target','active')
    $null = Ctl @('session','type',('echo OVER_C2 & cd' + [char]13),'--allow-control','--target',$overlay)
    Start-Sleep -Milliseconds 700
    Check ((Ctl @('session','text','--target',$b)) -match 'UNDER_C2') 'Ordinary active input reaches the underlying shell'
    $text = Ctl @('session','overlay','text','--target',$overlay)
    Check ($text -match 'OVER_C2' -and ($text -replace '\s','').Contains(($cwd -replace '\s',''))) 'Explicit hidden overlay ID retains input, buffer and Windows cwd'
    $null = Ctl @('session','overlay','show','--target',$overlay)
    $before = Detail $b $overlay
    Check ($before.visible -and $before.id -eq $overlay) 'Show restores the same terminal'
    $capturePath = Join-Path $repo "artifacts/c2-overlay-$Dpi.png"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $capturePath) | Out-Null
    $captureHwnd = [C2Window]::Find($process.Id)
    [C2Window]::Reveal($captureHwnd)
    Start-Sleep -Milliseconds 400
    $oldDpiContext = [C2Window]::SetThreadDpiAwarenessContext([IntPtr](-4))
    try { $null = Save-SandboxCapture -S @{Hwnd=$captureHwnd} -Path $capturePath }
    finally { $null = [C2Window]::SetThreadDpiAwarenessContext($oldDpiContext) }
    $null = Ctl @('session','overlay','resize','--height','200','--target',$overlay)
    $after = Detail $b $overlay
    Check ($after.geometry.widthDip -eq 400 -and $after.geometry.heightDip -eq 200) 'Partial resize retains the other DIP axis'
    $null = Ctl @('session','overlay','resize','--full','--target',$overlay)
    $full = Detail $b $overlay
    Check ($full.geometry.percent -eq 0 -and $null -eq $full.geometry.widthDip -and $null -eq $full.geometry.heightDip -and $full.geometry.width -ge $before.geometry.width) 'Full reset clears requested dimensions and expands effective geometry'
    Check ([Math]::Abs($before.geometry.width - [Math]::Min(400*$Dpi/96,$full.geometry.width)) -lt 1 -and [Math]::Abs($before.geometry.height-$full.geometry.height*0.6) -lt 1) "Native geometry converts mixed DIP/percent axes at DPI $Dpi"
    $null = Ctl @('session','overlay','resize','--width','16384','--height','16384','--target',$overlay)
    $null = Ctl @('window','resize','active','640','480')
    Start-Sleep -Milliseconds 200
    $small = Detail $b $overlay
    Check ($small.geometry.widthDip -eq 16384 -and $small.geometry.width -le 640 -and $small.geometry.height -le 480) 'Tiny-window layout clamps effective size without overwriting requested DIP dimensions'
    $null = Ctl @('window','resize','active','1100','800')
    Start-Sleep -Milliseconds 200
    Check ((Detail $b $overlay).geometry.width -gt $small.geometry.width) 'Growing the window recalculates effective overlay geometry'
    $null = Ctl @('session','overlay','resize','--full','--target',$overlay)
    $baseline = Ctl @('events')
    $null = Ctl @('session','overlay','show','--target',$overlay)
    $null = Ctl @('session','overlay','hide','--target',$overlay)
    $null = Ctl @('session','overlay','hide','--target',$overlay)
    $events = (Ctl @('events','--since',"$($baseline.cursor)")).events | Where-Object { $_.type -eq 'overlay' -and $_.action -in @('hidden','shown') }
    Check (@($events).Count -eq 1 -and $events[0].overlayId -eq $overlay) 'Visibility events describe only committed changes'
    $invalid = Reply @('session','overlay','open','exit 0','--target',$b,'--cwd',(Join-Path $state 'missing'))
    Check (-not $invalid.ok -and (Detail $b $overlay).id -eq $overlay) 'Invalid cwd cannot replace the running overlay'
    $null = Ctl @('session','split','on','--target',$b)
    $right = (Node $b).paneIds[1]
    $paneOverlay = Ctl @('session','overlay','open','cmd /d','--pane','right','--target',$b)
    $null = Ctl @('session','overlay','hide','--target',$paneOverlay)
    $null = Ctl @('session','swap','--target',$b)
    $paneDetail = Detail $b $paneOverlay
    Check ($paneDetail.hidden -and $paneDetail.ownerPaneId -eq $right) 'Hidden pane overlay follows stable owner through swap'
    $null = Ctl @('session','overlay','show','--target',$paneOverlay)
    Check ((Detail $b $paneOverlay).visible) 'Pane overlay reappears on its current side'
    $null = Ctl @('session','overlay','show','--target',$overlay)
    Check (-not (Detail $b $paneOverlay).visible) 'Session overlay occludes occupied pane slots in readback'
    $null = Ctl @('session','overlay','hide','--target',$paneOverlay)
    $null = Ctl @('session','split','close','--target',$right)
    Check ($null -eq (Detail $b $paneOverlay)) 'Owner removal disposes a hidden pane overlay'
    $gone = Reply @('session','overlay','show','--target',$paneOverlay)
    Check (-not $gone.ok) 'Removed overlay ID never retargets a replacement'
    $null = Ctl @('session','overlay','close','--target',$overlay)
    $exited = Ctl @('session','overlay','open','exit /b 7','--wait','--target',$b)
    $null = Ctl @('session','overlay','hide','--target',$exited)
    for ($i=0; $i -lt 30; $i++) { if ((Detail $b $exited).exited) { break }; Start-Sleep -Milliseconds 100 }
    Check ((Detail $b $exited).hidden -and (Detail $b $exited).exited) 'Exited wait overlay stays retained while hidden'
    Check ((Ctl @('session','overlay','result')) -eq 'exit 7') 'Session result retains its window-wide outcome'
    $null = Ctl @('session','overlay','close','--target',$exited)
    $start = [Diagnostics.ProcessStartInfo]::new($ctl)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($arg in @('session','overlay','open','ping -n 4 127.0.0.1 >nul & exit /b 9','--block','--target',$b,'--pipe',$id,'--json')) { $start.ArgumentList.Add($arg) }
    $blocking = [Diagnostics.Process]::Start($start)
    for ($i=0; $i -lt 30; $i++) { $blockId = ((Node $b).overlayDetails | Select-Object -First 1).id; if ($blockId) { break }; Start-Sleep -Milliseconds 100 }
    $null = Ctl @('session','overlay','hide','--target',$blockId)
    Check (-not $blocking.HasExited) 'Hiding a running overlay does not complete its block waiter'
    Check ($blocking.WaitForExit(7000)) 'Hidden overlay block waiter finishes on process exit'
    $blockReply = $blocking.StandardOutput.ReadToEnd() | ConvertFrom-Json
    Check ($blocking.ExitCode -eq 0 -and $blockReply.result -eq 'exit 9') 'Hidden block preserves its own process outcome'
    $null = Ctl @('session','select',$a)
    $follow = Ctl @('session','overlay','open','cmd /d','--target',$b,'--follow')
    Check ((Node $b).active -and (Detail $b $follow).visible) 'Explicit follow selects the owner after successful open'
    $null = Ctl @('session','close',$b)
    Check (-not (Reply @('session','overlay','show','--target',$follow)).ok) 'Session teardown removes its overlay identity'
    'C2 Win32 acceptance passed'
} finally {
    if ($blocking) { if (-not $blocking.HasExited) { $blocking.Kill(); $blocking.WaitForExit() }; $blocking.Dispose() }
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
    $parent = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData'))
    $full = [IO.Path]::GetFullPath($state)
    if ([IO.Path]::GetDirectoryName($full) -eq $parent -and [IO.Path]::GetFileName($full) -match '^agwinterm-c2-[a-f0-9]{32}$') {
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
    $env:AGWINTERM_SESSION_ID = $savedCaller
    $env:AGWINTERM_DPI_OVERRIDE = $savedDpi
}
# The last assertion deliberately invokes a refused CLI request. Report suite success
# explicitly so PowerShell command wrappers do not propagate that expected exit code.
exit 0
