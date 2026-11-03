# Runs only through hud-ui.ps1 and its background Sandbox routing. The clipboard belongs
# to the Sandbox. Keyboard injection below is confined to that guest's desktop.
if ($env:AGWINTERM_SANDBOX_UI -ne '1' -or $env:USERNAME -ne 'WDAGUtilityAccount') {
    throw 'Clipboard fixture requires the Windows Sandbox guest; host and direct CI execution are forbidden.'
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PasteClipboardFixture {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr owner);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr data);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr data);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr data);
    public static void Image(IntPtr owner) {
        // One red pixel: BITMAPINFOHEADER followed by a padded 24-bit BGR row.
        byte[] dib = new byte[44];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(1).CopyTo(dib, 4);
        BitConverter.GetBytes(1).CopyTo(dib, 8);
        dib[12] = 1; dib[14] = 24; dib[20] = 4; dib[42] = 255;
        IntPtr data = GlobalAlloc(2, (UIntPtr)dib.Length);
        if (data == IntPtr.Zero) throw new Exception("GlobalAlloc failed");
        try {
            IntPtr p = GlobalLock(data);
            if (p == IntPtr.Zero) throw new Exception("GlobalLock failed");
            try { Marshal.Copy(dib, 0, p, dib.Length); } finally { GlobalUnlock(data); }
            bool opened = false;
            for (int i = 0; i < 20 && !opened; i++) {
                opened = OpenClipboard(owner);
                if (!opened) System.Threading.Thread.Sleep(20);
            }
            if (!opened) throw new Exception("OpenClipboard failed");
            try {
                if (!EmptyClipboard() || SetClipboardData(8, data) == IntPtr.Zero)
                    throw new Exception("SetClipboardData failed");
                data = IntPtr.Zero;
            } finally { CloseClipboard(); }
        } finally { if (data != IntPtr.Zero) GlobalFree(data); }
    }
}
'@
$sinkScript=Join-Path $artifact 'clipboard-sink.ps1'
$sinkLog=Join-Path $artifact 'clipboard-input.txt'
@'
param([string]$Log)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class RawPasteSink {
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);
    public static void Run(string log) {
        if (!SetConsoleMode(GetStdHandle(-10), 0x200)) throw new Exception("Raw input failed");
        if (!SetConsoleMode(GetStdHandle(-11), 7)) throw new Exception("VT output failed");
        Console.Write("\x1b[?2004hPASTE-SINK-READY\r\n");
        using (var input = Console.OpenStandardInput()) {
            for (;;) {
                int b = input.ReadByte();
                if (b < 0) return;
                System.IO.File.AppendAllText(log, b.ToString("X2"));
            }
        }
    }
}
"@
[RawPasteSink]::Run($Log)
'@ | Set-Content -LiteralPath $sinkScript
function PasteWait([scriptblock]$condition) {
    for($i=0;$i-lt 60;$i++){if(& $condition){return $true};Start-Sleep -Milliseconds 100}
    return $false
}
function PasteBytes {
    if(Test-Path -LiteralPath $sinkLog){
        # The child appends each byte while this poll reads; allow its open writer.
        $stream=[IO.File]::Open($sinkLog,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        $reader=[IO.StreamReader]::new($stream)
        try {return $reader.ReadToEnd()} finally {$reader.Dispose()}
    }
    return ''
}
function MenuPaste {
    [void][HudOwnedJob]::PostMessageW($hwnd,0x100,[IntPtr]0x77,[IntPtr]1)
    [void][HudOwnedJob]::PostMessageW($hwnd,0x101,[IntPtr]0x77,[IntPtr]1)
}
function InteractivePaste([bool]$shift=$false) {
    [void][PasteClipboardFixture]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 100
    if([PasteClipboardFixture]::GetForegroundWindow()-ne $hwnd){throw 'Paste fixture does not own foreground'}
    try {
        [PasteClipboardFixture]::keybd_event(0x11,0,0,[UIntPtr]::Zero)
        if($shift){[PasteClipboardFixture]::keybd_event(0x10,0,0,[UIntPtr]::Zero)}
        [PasteClipboardFixture]::keybd_event(0x56,0,0,[UIntPtr]::Zero)
        [PasteClipboardFixture]::keybd_event(0x56,0,2,[UIntPtr]::Zero)
    } finally {
        if($shift){[PasteClipboardFixture]::keybd_event(0x10,0,2,[UIntPtr]::Zero)}
        [PasteClipboardFixture]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
    }
}
$command='"'+(Join-Path $PSHOME 'pwsh.exe')+'" -NoProfile -NonInteractive -File "'+$sinkScript+'" -Log "'+$sinkLog+'"'
$sink=[string](Rpc 'session.new' @{name='clipboard-sink';command=$command;'command-mode'='direct'})
if(-not (PasteWait {([string](Rpc 'session.text' @{} $sink)).Contains('PASTE-SINK-READY')})) {
    throw 'Raw paste sink did not start; no clipboard input sent'
}
$esc=[string][char]27
$expected='16'
try {
    [PasteClipboardFixture]::Image($hwnd)
    Check 'image-only clipboard has DIB and no text' ([PasteClipboardFixture]::IsClipboardFormatAvailable(8) -and -not [PasteClipboardFixture]::IsClipboardFormatAvailable(13))
    InteractivePaste
    Check 'image Ctrl+V reaches the child once as a key, not an empty paste' (PasteWait {(PasteBytes)-ceq $expected}) (PasteBytes)
    Check 'image remains available for the child to read' ([PasteClipboardFixture]::IsClipboardFormatAvailable(8))

    $null=Rpc 'session.paste' @{} $sink
    Start-Sleep -Milliseconds 300
    Check 'scripted text paste with an image remains a no-op' ((PasteBytes)-ceq $expected)
    MenuPaste
    Start-Sleep -Milliseconds 300
    Check 'menu/keymap paste without text does not synthesize a key or empty paste' ((PasteBytes)-ceq $expected)

    $null=Rpc 'session.readonly' @{op='on'} $sink
    InteractivePaste
    Start-Sleep -Milliseconds 300
    Check 'read-only pane rejects the image key' ((PasteBytes)-ceq $expected)
    $null=Rpc 'session.readonly' @{op='off'} $sink

    $null=Rpc 'session.write' @{text=($esc+'[?2004l')} $sink
    InteractivePaste
    $expected+='16'
    Check 'image Ctrl+V also passes through without bracketed mode' (PasteWait {(PasteBytes)-ceq $expected}) (PasteBytes)

    # ConPTY enables Win32 input mode, which takes precedence over Kitty encoding.
    $null=Rpc 'session.write' @{text=($esc+'[?9001l'+$esc+'[>1u')} $sink
    InteractivePaste
    $expected+='1B5B3131383B3575'
    Check 'Kitty mode encodes Ctrl+V once with its original modifier' (PasteWait {(PasteBytes)-ceq $expected}) (PasteBytes)
    InteractivePaste $true
    $expected+='1B5B3131383B3675'
    Check 'Kitty mode preserves Ctrl+Shift+V' (PasteWait {(PasteBytes)-ceq $expected}) (PasteBytes)
    $null=Rpc 'session.write' @{text=($esc+'[<u')} $sink

    $null=Rpc 'session.write' @{text=($esc+'[?2004h')} $sink
    Set-Clipboard -Value 'hello'
    InteractivePaste
    $expected+='1B5B3230307E68656C6C6F1B5B3230317E'
    Check 'ordinary text still arrives in one bracketed paste packet' (PasteWait {(PasteBytes)-ceq $expected}) (PasteBytes)
} finally {
    $null=Rpc 'session.close' @{} $sink -AllowError
}
