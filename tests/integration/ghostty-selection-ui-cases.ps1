# Mouse selection and retained native anchors, on the enclosing owned VM only.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GhosttySelectionWindow {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
}
'@
$selectionCommand='"'+(Join-Path $PSHOME 'pwsh.exe')+'" -NoProfile -NonInteractive -Command "[Console]::WriteLine(''SELECTION-READY''); [Threading.Thread]::Sleep(120000)"'
$selectionSession=[string](Rpc 'session.new' @{name='ghostty-selection';command=$selectionCommand;'command-mode'='direct'})
$windowRect=[GhosttySelectionWindow+Rect]::new()
[void][GhosttySelectionWindow]::GetWindowRect($hwnd,[ref]$windowRect)
try {
    for($poll=0;$poll -lt 100;$poll++) {
        if(([string](Rpc 'session.text' @{} $selectionSession)).Contains('SELECTION-READY')){break}
        Start-Sleep -Milliseconds 100
    }
    Check 'Selection fixture child is ready' (([string](Rpc 'session.text' @{} $selectionSession)).Contains('SELECTION-READY'))
    $selectionMetrics=Rpc 'session.metrics' @{} $selectionSession
    $logical='SELECT-'+('x'*($selectionMetrics.cols+3))
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[3J'+$esc+'[H'+$logical+$esc+'[H')} $selectionSession
    [void][HudOwnedJob]::SendMessageW($hwnd,7,[IntPtr]::Zero,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 350
    $caret=[HudOwnedJob]::CaretRect($hwnd)
    $scale=[GhosttySelectionWindow]::GetDpiForWindow($hwnd)/96.0
    $cellW=$selectionMetrics.widthPx/$selectionMetrics.cols/$scale
    $cellH=$selectionMetrics.heightPx/$selectionMetrics.rows/$scale
    $x1=[int]($caret.left+$cellW*0.2);$y1=[int]($caret.top+$cellH*0.5)
    $x2=[int]($caret.left+$cellW*9.8);$y2=[int]($caret.top+$cellH*1.5)
    [void][HudOwnedJob]::SendMessageW($hwnd,0x201,[IntPtr]1,[IntPtr](($y1 -shl 16)-bor $x1))
    [void][HudOwnedJob]::SendMessageW($hwnd,0x200,[IntPtr]1,[IntPtr](($y2 -shl 16)-bor $x2))
    [void][HudOwnedJob]::SendMessageW($hwnd,0x202,[IntPtr]::Zero,[IntPtr](($y2 -shl 16)-bor $x2))
    $copied=[string](Rpc 'session.copy' @{} $selectionSession)
    Check 'Mouse selection joins native soft wraps without newline' ($copied -ceq $logical) "expected=$($logical.Length) actual=$($copied.Length)"
    foreach($width in @(620,980,540,1040)) {
        [void][GhosttySelectionWindow]::SetWindowPos($hwnd,[IntPtr]::Zero,0,0,$width,650,6)
        Start-Sleep -Milliseconds 250
        $copied=[string](Rpc 'session.copy' @{} $selectionSession)
        Check "Mouse selection survives reflow at window width $width" ($copied -ceq $logical) "expected=$($logical.Length) actual=$($copied.Length)"
    }
} finally {
    [void][GhosttySelectionWindow]::SetWindowPos($hwnd,[IntPtr]::Zero,0,0,($windowRect.Right-$windowRect.Left),($windowRect.Bottom-$windowRect.Top),6)
    $null=Rpc 'session.close' @{} $selectionSession
    $null=Rpc 'session.select' @{} $session
}
