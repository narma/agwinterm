# Without VT input, ConPTY exposes CSI I/O as FOCUS_EVENT records.
$focusScript=Join-Path $artifact 'ghostty-focus-sink.ps1'
$focusLog=Join-Path $artifact 'ghostty-focus-events.txt'
@'
param([string]$Log)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FocusSink {
    [StructLayout(LayoutKind.Explicit,Size=20)] public struct Input {
        [FieldOffset(0)] public ushort Kind;
        [FieldOffset(4)] public int Focused;
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h,uint mode);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool ReadConsoleInputW(IntPtr h,out Input input,uint count,out uint read);
    public static void Run(string path) {
        if(!SetConsoleMode(GetStdHandle(-10),8)||!SetConsoleMode(GetStdHandle(-11),7)) throw new Exception("console mode");
        Console.Write("\x1b[?9001l\x1b[?1004hFOCUS-SINK-READY\r\n");
        for(;;) {
            if(!ReadConsoleInputW(GetStdHandle(-10),out var input,1,out var read)) throw new Exception("ReadConsoleInputW");
            if(read==1 && input.Kind==0x10) System.IO.File.AppendAllText(path,input.Focused!=0 ? "I" : "O");
        }
    }
}
"@
[FocusSink]::Run($Log)
'@ | Set-Content -LiteralPath $focusScript
$focusCommand='"'+(Join-Path $PSHOME 'pwsh.exe')+'" -NoProfile -NonInteractive -File "'+$focusScript+'" -Log "'+$focusLog+'"'
$focusSession=[string](Rpc 'session.new' @{name='ghostty-focus';command=$focusCommand;'command-mode'='direct'})
function FocusCheck([string]$name,[string]$expected,[scriptblock]$action) {
    $before=InputBytes $focusLog
    & $action
    for($i=0;$i -lt 50;$i++){if((InputBytes $focusLog).Length -ge ($before.Length+$expected.Length)){break};Start-Sleep -Milliseconds 50}
    Start-Sleep -Milliseconds 150
    $actual=(InputBytes $focusLog).Substring($before.Length)
    Check $name ($actual -ceq $expected) "expected=$expected actual=$actual"
}
try {
    $ready=$false
    for($i=0;$i -lt 100;$i++){if(([string](Rpc 'session.text' @{} $focusSession)).Contains('FOCUS-SINK-READY')){$ready=$true;break};Start-Sleep -Milliseconds 100}
    if(-not $ready){throw 'Focus sink did not start'}
    # ConPTY consumes the child's mode request; explicitly configure the outer terminal too.
    $null=Rpc 'session.write' @{text=($esc+'[?1004h')} $focusSession
    [void][GhosttyInputFixture]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 250
    if([GhosttyInputFixture]::GetForegroundWindow()-ne $hwnd){throw 'Focus fixture does not own foreground'}
    FocusCheck 'Session switches report focus loss and gain through the owner' 'OI' {
        $null=Rpc 'session.select' @{} $inputSession
        $null=Rpc 'session.select' @{} $focusSession
    }
    FocusCheck 'Selecting the same session sends no duplicate focus report' '' {
        $null=Rpc 'session.select' @{} $focusSession
    }
    FocusCheck 'Window deactivation reports focus loss' 'O' {
        [void][GhosttyInputFixture]::SendMessageW($hwnd,6,[IntPtr]::Zero,[IntPtr]::Zero)
    }
    FocusCheck 'Inactive window switches do not report premature focus' '' {
        $null=Rpc 'session.select' @{} $inputSession
        $null=Rpc 'session.select' @{} $focusSession
    }
    FocusCheck 'Window activation reports focus to the current surface' 'I' {
        [void][GhosttyInputFixture]::SendMessageW($hwnd,6,[IntPtr]1,[IntPtr]::Zero)
    }
    $null=Rpc 'session.write' @{text=($esc+'[?1004l')} $focusSession
    FocusCheck 'Disabled focus reporting stays silent on session switches' '' {
        $null=Rpc 'session.select' @{} $inputSession
        $null=Rpc 'session.select' @{} $focusSession
    }
} finally {
    [void][GhosttyInputFixture]::SendMessageW($hwnd,6,[IntPtr]1,[IntPtr]::Zero)
    $null=Rpc 'session.close' @{} $focusSession
    $null=Rpc 'session.select' @{} $inputSession
}
