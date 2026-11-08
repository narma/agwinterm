# Real keyboard input is permitted only on the owned guest desktop.
if (-not (($env:AGWINTERM_SANDBOX_UI -eq '1' -and $env:USERNAME -eq 'WDAGUtilityAccount') -or
    ($env:AGWINTERM_HYPERV_UI -eq '1' -and (& "$PSScriptRoot/hyperv-ui-context.ps1")))) {
    throw 'Ghostty input acceptance requires a verified isolated Windows guest.'
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GhosttyInputFixture {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent,IntPtr after,string name,string text);
    public static IntPtr FindRename(IntPtr parent) => FindWindowExW(parent,IntPtr.Zero,"EDIT",null);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr LoadKeyboardLayoutW(string name,uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
    public static void Key(byte key,bool up) { keybd_event(key,0,(up?2u:0u)|(key==0xA5?1u:0u),UIntPtr.Zero); }
    public static void Tap(byte key) { Key(key,false); System.Threading.Thread.Sleep(30); Key(key,true); }
}
'@
$inputScript=Join-Path $artifact 'ghostty-input-sink.ps1'
$inputLog=Join-Path $artifact 'ghostty-input-bytes.txt'
@'
param([string]$Log)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeInputSink {
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h,uint mode);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool ReadConsoleW(IntPtr h,[Out] char[] text,uint count,out uint read,IntPtr control);
    public static void Run(string log) {
        System.IO.File.WriteAllText(log+".environment", Environment.GetEnvironmentVariable("TERM")+"|"+Environment.GetEnvironmentVariable("TERM_PROGRAM")+"|"+Environment.GetEnvironmentVariable("COLORTERM"));
        Console.InputEncoding=System.Text.Encoding.UTF8;
        Console.OutputEncoding=new System.Text.UTF8Encoding(false);
        if(!SetConsoleMode(GetStdHandle(-10),0x200)||!SetConsoleMode(GetStdHandle(-11),7)) throw new Exception("VT console mode");
        Console.Write("\x1b[?9001lINPUT-SINK-READY\r\n");
        // Console input is UTF-16. Keep an encoder across reads: ConPTY may deliver
        // the two KEY_EVENT records of an astral character in separate reads.
        var buffer=new char[4096];
        var bytes=new byte[16384];
        var encoder=System.Text.Encoding.UTF8.GetEncoder();
        for(;;) {
            if(!ReadConsoleW(GetStdHandle(-10),buffer,(uint)buffer.Length,out uint read,IntPtr.Zero)) throw new Exception("ReadConsoleW");
            int count=encoder.GetBytes(buffer,0,(int)read,bytes,0,false);
            System.IO.File.AppendAllText(log,Convert.ToHexString(bytes,0,count));
        }
    }
}
"@
[NativeInputSink]::Run($Log)
'@ | Set-Content -LiteralPath $inputScript
function InputBytes([string]$path=$inputLog) {
    if(-not(Test-Path -LiteralPath $path)){return ''}
    $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $reader=[IO.StreamReader]::new($stream)
    try{return $reader.ReadToEnd()}finally{$reader.Dispose()}
}
function InputLayout([string]$name) {
    $layout=[GhosttyInputFixture]::LoadKeyboardLayoutW($name,0)
    if($layout-eq [IntPtr]::Zero){throw "Keyboard layout unavailable: $name"}
    [void][GhosttyInputFixture]::SendMessageW($hwnd,0x50,[IntPtr]::Zero,$layout)
}
function InputCheck([string]$name,[string]$expected,[scriptblock]$action) {
    $before=InputBytes
    & $action
    $hex=[Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes($expected))
    for($i=0;$i-lt 50;$i++) { if((InputBytes).Length-ge ($before.Length+$hex.Length)){break};Start-Sleep -Milliseconds 50 }
    Start-Sleep -Milliseconds 100
    $actual=(InputBytes).Substring($before.Length)
    Check $name ($actual-ceq $hex) "expected=$hex actual=$actual"
}
$inputCommand='"'+(Join-Path $PSHOME 'pwsh.exe')+'" -NoProfile -NonInteractive -File "'+$inputScript+'" -Log "'+$inputLog+'"'
$inputSession=[string](Rpc 'session.new' @{name='ghostty-input';command=$inputCommand;'command-mode'='direct'})
try {
    $ready=$false
    for($i=0;$i-lt 100;$i++) {if(([string](Rpc 'session.text' @{} $inputSession)).Contains('INPUT-SINK-READY')){$ready=$true;break};Start-Sleep -Milliseconds 100}
    if(-not $ready){throw 'Input byte sink did not start'}
    Check 'Child receives portable TERM and Agwinterm truecolor identity' ((Get-Content -LiteralPath ($inputLog+'.environment') -Raw) -ceq 'xterm-256color|agwinterm|truecolor')
    . "$PSScriptRoot/ghostty-focus-ui-cases.ps1"
    InputCheck 'DA capabilities reach the child through the session owner' ($esc+'[?62;22c'+$esc+'[>1;0;0c') {
        $null=Rpc 'session.write' @{text=($esc+'[c'+$esc+'[>c')} $inputSession
    }
    InputCheck 'Unknown per-session TN remains silent' '' {
        $null=Rpc 'session.write' @{text=($esc+'P+q544E'+$esc+'\')} $inputSession
    }
    InputCheck 'Blink capability reaches the child' ($esc+'P1+r626C696E6B=1B5B356D'+$esc+'\') {
        $null=Rpc 'session.write' @{text=($esc+'P+q626C696E6B'+$esc+'\')} $inputSession
    }
    InputCheck 'Truecolor capability survives session transport' ($esc+'P1+r524742=38'+$esc+'\') {
        $null=Rpc 'session.write' @{text=($esc+'P+q524742'+$esc+'\')} $inputSession
    }
    InputCheck 'Colored underline and overline capabilities reach the child' ($esc+'P1+r5375'+$esc+'\'+$esc+'P1+r536D6F6C=1B5B35336D'+$esc+'\') {
        $null=Rpc 'session.write' @{text=($esc+'P+q5375;536D6F6C'+$esc+'\')} $inputSession
    }
    $null=Rpc 'session.write' @{text=($esc+'[?9001l')} $inputSession
    . "$PSScriptRoot/ghostty-clipboard-ui-cases.ps1"
    [void][GhosttyInputFixture]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 150
    if([GhosttyInputFixture]::GetForegroundWindow()-ne $hwnd){throw 'Input fixture does not own foreground'}
    InputLayout '00000409'
    InputCheck 'F2 reaches the terminal application' ($esc+'OQ') { [GhosttyInputFixture]::Tap(0x71) }
    [GhosttyInputFixture]::Tap(0x75)
    [GhosttyInputFixture]::Tap(0x71)
    Start-Sleep -Milliseconds 150
    $renameEdit=[GhosttyInputFixture]::FindRename($hwnd)
    Check 'F2 in sidebar opens session rename' ($renameEdit-ne [IntPtr]::Zero)
    [GhosttyInputFixture]::Tap(0x1B)
    [GhosttyInputFixture]::Tap(0x1B)
    InputCheck 'Ghostty real US key arrives once' 'a' { [GhosttyInputFixture]::Tap(0x41) }
    InputCheck 'Ghostty real shifted key arrives once' 'A' {
        try{[GhosttyInputFixture]::Key(0xA0,$false);[GhosttyInputFixture]::Tap(0x41)}finally{[GhosttyInputFixture]::Key(0xA0,$true)}
    }
    InputLayout '00000419'
    InputCheck 'Ghostty real Russian layout reaches child' 'ф' { [GhosttyInputFixture]::Tap(0x41) }
    InputLayout '00000407'
    InputCheck 'Ghostty real AltGr emits layout text without Ctrl-Alt prefix' '@' {
        try{[GhosttyInputFixture]::Key(0xA5,$false);[GhosttyInputFixture]::Tap(0x51)}finally{[GhosttyInputFixture]::Key(0xA5,$true)}
    }
    InputLayout '00020409'
    InputCheck 'Ghostty dead-key composition reaches child once' 'é' {
        [GhosttyInputFixture]::Tap(0xDE);[GhosttyInputFixture]::Tap(0x45)
    }
    InputLayout '00000409'
    InputCheck 'Ghostty committed UTF-16 text preserves surrogate pair' '😀' {
        foreach($c in '😀'.ToCharArray()){[void][GhosttyInputFixture]::SendMessageW($hwnd,0x102,[IntPtr][int]$c,[IntPtr]1)}
    }
    $null=Rpc 'session.write' @{text=($esc+'[?9001h')} $inputSession
    InputCheck 'Ghostty committed text also reaches Win32-input session' '漢' {
        [void][GhosttyInputFixture]::SendMessageW($hwnd,0x102,[IntPtr][int][char]'漢',[IntPtr]1)
    }
    InputCheck 'Ghostty Win32-input key is not duplicated by WM_CHAR' 'a' { [GhosttyInputFixture]::Tap(0x41) }
    InputLayout '00020409'
    InputCheck 'Ghostty Win32-input dead-key composition arrives once' 'é' {
        [GhosttyInputFixture]::Tap(0xDE);[GhosttyInputFixture]::Tap(0x45)
    }
    InputCheck 'Ghostty Win32-input preserves both characters of an uncombined dead key' "'x" {
        [GhosttyInputFixture]::Tap(0xDE);[GhosttyInputFixture]::Tap(0x58)
    }
    InputLayout '00000409'
    $null=Rpc 'session.write' @{text=($esc+'[?9001l')} $inputSession
    InputCheck 'Sidebar focus does not leak printable text into terminal' '' {
        [GhosttyInputFixture]::Tap(0x75);[GhosttyInputFixture]::Tap(0x41);[GhosttyInputFixture]::Tap(0x1B)
    }
    InputCheck 'Held function key repeats stay with original session after selection changes' ($esc+'OR'+$esc+'OR') {
        try {
            [GhosttyInputFixture]::Key(0x72,$false)
            Start-Sleep -Milliseconds 60
            $null=Rpc 'session.select' @{} $session
            [GhosttyInputFixture]::Key(0x72,$false)
            Start-Sleep -Milliseconds 60
        } finally {
            [GhosttyInputFixture]::Key(0x72,$true)
            $null=Rpc 'session.select' @{} $inputSession
        }
    }
    $secondLog=Join-Path $artifact 'ghostty-input-second.txt'
    $secondCommand=$inputCommand.Replace($inputLog,$secondLog)
    $secondSession=[string](Rpc 'session.new' @{name='ghostty-input-second';command=$secondCommand;'command-mode'='direct'})
    try {
        $secondReady=$false
        for($i=0;$i-lt 100;$i++) {if(([string](Rpc 'session.text' @{} $secondSession)).Contains('INPUT-SINK-READY')){$secondReady=$true;break};Start-Sleep -Milliseconds 100}
        if(-not $secondReady){throw 'Second input sink did not start'}
        $null=Rpc 'session.write' @{text=($esc+'[?9001l'+$esc+'[?1h')} $secondSession
        $null=Rpc 'session.write' @{text=($esc+'[?1l')} $inputSession
        $null=Rpc 'session.select' @{} $inputSession
        $null=Rpc 'broadcast' @{op='on'}
        InputCheck 'Broadcast encodes normal cursor mode for first recipient' ($esc+'[A') { [GhosttyInputFixture]::Tap(0x26) }
        Check 'Broadcast encodes application cursor mode for second recipient' ((InputBytes $secondLog)-ceq '1B4F41')
        InputCheck 'Broadcast preserves committed Unicode for first recipient' '😀' {
            foreach($c in '😀'.ToCharArray()){[void][GhosttyInputFixture]::SendMessageW($hwnd,0x102,[IntPtr][int]$c,[IntPtr]1)}
        }
        Check 'Broadcast preserves committed Unicode for second recipient' ((InputBytes $secondLog)-ceq '1B4F41F09F9880')
        $null=Rpc 'session.readonly' @{op='on'} $secondSession
        InputCheck 'Broadcast still reaches writable recipient' 'a' { [GhosttyInputFixture]::Tap(0x41) }
        Check 'Broadcast skips read-only recipient' ((InputBytes $secondLog)-ceq '1B4F41F09F9880')
    } finally {
        $null=Rpc 'broadcast' @{op='off'}
        $null=Rpc 'session.close' @{} $secondSession
        $null=Rpc 'session.select' @{} $inputSession
    }
    foreach($legacyCore in @('managed','rust')) {
        $legacyLog=Join-Path $artifact ("ghostty-input-"+$legacyCore+'.txt')
        $null=Rpc 'config.set' @{key='emulator-core';value=$legacyCore}
        Check "Old $legacyCore setting resolves to Ghostty" ((Rpc 'config.get' @{key='emulator-core'}) -eq 'ghostty')
        $legacySession=$null
        try {
            $legacySession=[string](Rpc 'session.new' @{name=('input-'+$legacyCore);command=$inputCommand.Replace($inputLog,$legacyLog);'command-mode'='direct'})
            $legacyReady=$false
            for($i=0;$i-lt 100;$i++) {if(([string](Rpc 'session.text' @{} $legacySession)).Contains('INPUT-SINK-READY')){$legacyReady=$true;break};Start-Sleep -Milliseconds 100}
            if(-not $legacyReady){throw "Legacy input sink did not start: $legacyCore"}
            $null=Rpc 'session.write' @{text=($esc+'[?9001h')} $legacySession
            $null=Rpc 'session.write' @{text=($esc+'[?9001h')} $inputSession
            $null=Rpc 'session.select' @{} $inputSession
            $null=Rpc 'broadcast' @{op='on'}
            InputCheck "Broadcast after $legacyCore migration sends the key once to the original recipient" 'a' { [GhosttyInputFixture]::Tap(0x41) }
            Check "Broadcast after $legacyCore migration sends the key once to the new recipient" ((InputBytes $legacyLog)-ceq '61')
            InputCheck "Broadcast after $legacyCore migration keeps committed text" '漢' {
                [void][GhosttyInputFixture]::SendMessageW($hwnd,0x102,[IntPtr][int][char]'漢',[IntPtr]1)
            }
            Check "Broadcast after $legacyCore migration delivers committed text to the new recipient" ((InputBytes $legacyLog)-ceq '61E6BCA2')
        } finally {
            $null=Rpc 'broadcast' @{op='off'}
            $null=Rpc 'config.set' @{key='emulator-core';value='ghostty'}
            if($legacySession){$null=Rpc 'session.close' @{} $legacySession}
            $null=Rpc 'session.select' @{} $inputSession
        }
    }
} finally {
    foreach($key in @(0xA0,0xA1,0xA2,0xA3,0xA4,0xA5)){[GhosttyInputFixture]::Key($key,$true)}
    InputLayout '00000409'
    $null=Rpc 'session.close' @{} $inputSession
    $null=Rpc 'session.select' @{} $session
}
