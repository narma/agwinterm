param(
    [string]$Distro = 'Ubuntu',
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('ghostty','rust','managed')][string]$Core = 'ghostty',
    [ValidateSet('bundled','inbox')][string]$Conpty = 'inbox',
    [ValidateRange(5,120)][int]$Seconds = 20
)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslTuiRendering -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$id = 'agwinterm-tui-test-' + [guid]::NewGuid().ToString('N')
$state = Join-Path $env:LOCALAPPDATA $id
$null = New-Item -ItemType Directory -Path $state
$trace = Join-Path $OutputDirectory "$Core-paints.jsonl"
if (Test-Path -LiteralPath $trace) { throw 'Use a new output directory.' }
$env:AGWINTERM_PAINT_TRACE = $trace
$env:AGWINTERM_PERF = Join-Path $OutputDirectory "$Core-perf.log"
$env:AGWINTERM_DUMP = Join-Path $OutputDirectory "$Core-output.vt"
# Separate htop configuration; do not read or modify the user's htop settings.
$htopRc = '/tmp/' + $id + '.htoprc'
@{ default='htop fixture'; profiles=@(@{name='htop fixture';command='wsl.exe';args=@('-d',$Distro,'--exec','env','TERM=xterm-256color',"HTOPRC=$htopRc",'htop','-d','1','-s','PERCENT_CPU')}) } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $state 'profiles.json')
@("emulator-core = $Core",'session-host = in-process',"conpty = $Conpty",'font-family = Consolas','font-size = 12','cursor-blink = false') |
    Set-Content -LiteralPath (Join-Path $state 'agwinterm.conf')
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TuiFixtureWindow {
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr w,uint message,IntPtr key,IntPtr data);
    private delegate bool EnumProc(IntPtr w,IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr w,System.Text.StringBuilder name,int length);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr w,int index);
    public static IntPtr Find(uint pid) {
        IntPtr found=IntPtr.Zero;
        EnumWindows((w,data)=>{GetWindowThreadProcessId(w,out uint owner);var name=new System.Text.StringBuilder(256);GetClassNameW(w,name,256);if(owner==pid && name.ToString()=="AgwintermWin32" && (GetWindowLongPtrW(w,-20).ToInt64() & 0x80)==0){found=w;return false;}return true;},IntPtr.Zero);
        return found;
    }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr w,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr w,int command);
}
'@
$process = $null
try {
    $process = Start-Process -FilePath (Join-Path $AppDirectory 'Agwinterm.Win32.exe') -ArgumentList @('--app-id',$id,'--pipe',$id,'--no-restore','--default-session-host','inprocess') -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $window = [IntPtr]::Zero
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw 'Fixture application exited during startup.' }
        $window = [TuiFixtureWindow]::Find($process.Id)
    } while ($window -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($window -eq [IntPtr]::Zero) { throw 'Fixture window did not start.' }
    # This script only runs on the worker's private non-input Win32 desktop.
    [void][TuiFixtureWindow]::SetWindowPos($window,[IntPtr]::Zero,0,0,1800,1100,0x14)
    [void][TuiFixtureWindow]::ShowWindow($window,4)
    Start-Sleep -Seconds 2
    for ($i=0;$i -lt 3;$i++) {
        [void][TuiFixtureWindow]::PostMessageW($window,0x100,[IntPtr]40,[IntPtr]0x1500001)
        [void][TuiFixtureWindow]::PostMessageW($window,0x101,[IntPtr]40,[IntPtr](-1051721727))
    }
    Start-Sleep -Seconds $Seconds
    if (-not (Test-Path -LiteralPath $trace)) { throw 'No actual paint captures recorded.' }
    $frames = @(Get-Content -LiteralPath $trace | ForEach-Object { $_ | ConvertFrom-Json })
    $htop = @($frames | Where-Object { ($_.lines -join "`n") -match 'PID.{0,4}USER' })
    if ($htop.Count -lt 20) { throw "Too few htop frames: $($htop.Count)" }
    $expectedCore = @{ghostty='GhosttyTerminalCore';rust='RustTerminalCore';managed='TerminalEmulator'}[$Core]
    if (@($htop | Where-Object core -ne $expectedCore).Count) { throw 'Wrong terminal core; comparison would be invalid.' }
    $bad = @($htop | Where-Object {
        $header = -1
        for ($r=0; $r -lt $_.lines.Count; $r++) { if ($_.lines[$r] -match 'PID.{0,4}USER') { $header=$r; break } }
        $header -ge 0 -and $header+1 -lt $_.lines.Count -and [string]::IsNullOrWhiteSpace($_.lines[$header+1])
    })
    $settled = @($htop | Where-Object { $_.tick -ge $frames[0].tick + 5*[Diagnostics.Stopwatch]::Frequency })
    $header = 0
    while ($header -lt $settled[-1].lines.Count -and $settled[-1].lines[$header] -notmatch 'PID.{0,4}USER') { $header++ }
    $selectedRow = $header+4 # three Down presses from the first process
    $color = ($settled | ForEach-Object { $_.backgrounds[$selectedRow] } | Where-Object {$_ -ne 0} | Group-Object | Sort-Object Count -Descending | Select-Object -First 1).Name
    if (-not $color) { throw 'No selected process background found.' }
    $displaced = @($settled | Where-Object {
        $moved = $false
        for ($r=$header+1; $r -lt $_.rows-1; $r++) { if ($r -ne $selectedRow -and $_.backgrounds[$r] -eq [uint32]$color) { $moved=$true; break } }
        $moved
    })
    $summary = @{core=$Core;conpty=$Conpty;frames=$frames.Count;htopFrames=$htop.Count;blankFirstProcessRow=$bad.Count;highlightDisplacements=$displaced.Count;settledFrames=$settled.Count;seconds=$Seconds}
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory "$Core-summary.json")
    $summary | ConvertTo-Json | Write-Host
} finally {
    if ($process -and -not $process.HasExited) { [void]$process.CloseMainWindow(); if (-not $process.WaitForExit(5000)) { $process.Kill() } }
    & wsl.exe -d $Distro --exec rm -f -- $htopRc
}
