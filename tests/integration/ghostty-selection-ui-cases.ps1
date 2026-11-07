# Mouse selection and retained native anchors, on the enclosing owned VM only.
if (-not (($env:AGWINTERM_SANDBOX_UI -eq '1' -and $env:USERNAME -eq 'WDAGUtilityAccount') -or
    ($env:AGWINTERM_HYPERV_UI -eq '1' -and (& "$PSScriptRoot/hyperv-ui-context.ps1")))) {
    throw 'Selection acceptance requires a verified isolated Windows guest.'
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GhosttySelectionWindow {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h,IntPtr rect,IntPtr region,uint flags);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
 [DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr owner);
 [DllImport("user32.dll")] public static extern bool CloseClipboard();
 [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
 [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr handle);
 [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr handle);
 public static string Html() {
   if(!OpenClipboard(IntPtr.Zero)) return "";
   try { var h=GetClipboardData(RegisterClipboardFormat("HTML Format")); if(h==IntPtr.Zero) return "";
     var p=GlobalLock(h); if(p==IntPtr.Zero) return "";
     try { return Marshal.PtrToStringUTF8(p) ?? ""; } finally { GlobalUnlock(h); }
   } finally { CloseClipboard(); }
 }
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
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[3J'+$esc+'[H'+$esc+'[31m'+$logical+$esc+'[0m'+$esc+'[H')} $selectionSession
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
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x77,[IntPtr]1)
    for($poll=0;$poll -lt 50;$poll++) { $html=[GhosttySelectionWindow]::Html(); if($html.Contains($logical)){break}; Start-Sleep -Milliseconds 100 }
    Check 'Formatted selection reaches Windows HTML clipboard' ($html.Contains('StartFragment:') -and $html.Contains($logical))
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x78,[IntPtr]1)
    for($poll=0;$poll -lt 50;$poll++) { $vt=[string](Get-Clipboard); if($vt.Contains($esc+'[')){break}; Start-Sleep -Milliseconds 100 }
    Check 'VT selection export retains terminal styling' ($vt.Contains($logical) -and $vt.Contains($esc+'['))
    foreach($width in @(620,980,540,1040)) {
        [void][GhosttySelectionWindow]::SetWindowPos($hwnd,[IntPtr]::Zero,0,0,$width,650,6)
        Start-Sleep -Milliseconds 250
        $copied=[string](Rpc 'session.copy' @{} $selectionSession)
        Check "Mouse selection survives reflow at window width $width" ($copied -ceq $logical) "expected=$($logical.Length) actual=$($copied.Length)"
    }
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[3J'+$esc+'[H'+"alpha beta gamma`r`n0123456789`r`nABCDEFGHIJ"+$esc+'[H')} $selectionSession
    Start-Sleep -Milliseconds 350
    $selectionMetrics=Rpc 'session.metrics' @{} $selectionSession
    $caret=[HudOwnedJob]::CaretRect($hwnd)
    $cellW=$selectionMetrics.widthPx/$selectionMetrics.cols/$scale
    $cellH=$selectionMetrics.heightPx/$selectionMetrics.rows/$scale
    function SelectionMouse([int]$message,[int]$row,[int]$column,[int]$buttons=1) {
        $x=[int]($caret.left+$cellW*($column+0.5)); $y=[int]($caret.top+$cellH*($row+0.5))
        [void][HudOwnedJob]::SendMessageW($hwnd,$message,[IntPtr]$buttons,[IntPtr](($y -shl 16)-bor $x))
    }
    function CheckSelection([string]$name,[string]$expected) {
        for($attempt=0;$attempt -lt 40;$attempt++) {
            $actual=[string](Rpc 'session.copy' @{} $selectionSession)
            if($actual -ceq $expected){break}; Start-Sleep -Milliseconds 50
        }
        Check $name ($actual -ceq $expected) "expected=[$expected] actual=[$actual]"
    }
    SelectionMouse 0x203 0 7
    SelectionMouse 0x200 0 13
    CheckSelection 'Double-click drag extends by whole words' 'beta gamma'
    SelectionMouse 0x200 0 1
    CheckSelection 'Word drag reverses around the pressed word' 'alpha beta'
    SelectionMouse 0x200 0 7
    SelectionMouse 0x202 0 7 0
    CheckSelection 'Word drag retracts to the pressed word' 'beta'
    # A third press immediately after the double-click selects a logical line.
    SelectionMouse 0x203 1 3
    SelectionMouse 0x202 1 3 0
    SelectionMouse 0x201 1 3
    SelectionMouse 0x200 2 2
    SelectionMouse 0x202 2 2 0
    CheckSelection 'Triple-click drag extends by whole lines' "0123456789`r`nABCDEFGHIJ"
    foreach($reverse in @($false,$true)) {
        Start-Sleep -Milliseconds 450
        [void][GhosttySelectionWindow]::SetForegroundWindow($hwnd)
        [GhosttySelectionWindow]::keybd_event(0x12,0,0,[UIntPtr]::Zero)
        Start-Sleep -Milliseconds 70
        try {
            if($reverse) { SelectionMouse 0x201 2 5; SelectionMouse 0x200 1 2; SelectionMouse 0x202 1 2 0 }
            else { SelectionMouse 0x201 1 2; SelectionMouse 0x200 2 5; SelectionMouse 0x202 2 5 0 }
        } finally { [GhosttySelectionWindow]::keybd_event(0x12,0,2,[UIntPtr]::Zero) }
        CheckSelection "Rectangular mouse selection, reverse=$reverse" "2345`r`nCDEF"
    }
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[H'+'replacement')} $selectionSession
    Start-Sleep -Milliseconds 250
    CheckSelection 'Redraw retains the previously selected text' "2345`r`nCDEF"
    $null=Rpc 'session.write' @{text=($esc+'[?1049h'+$esc+'[H'+'TUI-HELLO'+$esc+'[H')} $selectionSession
    Start-Sleep -Milliseconds 450
    SelectionMouse 0x201 0 0
    SelectionMouse 0x200 0 2
    SelectionMouse 0x202 0 2 0
    CheckSelection 'Alternate-screen plain copy uses the same selection path' 'TUI'
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x77,[IntPtr]1)
    for($poll=0;$poll -lt 50;$poll++) { $html=[GhosttySelectionWindow]::Html(); if($html.Contains('TUI')){break}; Start-Sleep -Milliseconds 100 }
    Check 'Alternate-screen HTML copy uses current retained content' ($html.Contains('TUI'))
    Set-Clipboard -Value 'before-vt'
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]0x78,[IntPtr]1)
    for($poll=0;$poll -lt 50;$poll++) { $vt=[string](Get-Clipboard); if($vt.Contains('TUI')){break}; Start-Sleep -Milliseconds 100 }
    Check 'Alternate-screen VT copy uses current retained content' ($vt.Contains('TUI'))
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[H'+'new frame')} $selectionSession
    Start-Sleep -Milliseconds 250
    CheckSelection 'TUI redraw retains selected text independently of its frame' 'TUI'
    $null=Rpc 'session.write' @{text=($esc+'[?1049l')} $selectionSession
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[H'+'VISIBLE'+$esc+'[H')} $selectionSession
    # Hosted output acceptance precedes presentation. Confirm the selection can
    # read the prepared visible frame before freezing it; a fixed sleep races
    # frame credit/publication and can accidentally test an older TUI frame.
    for($attempt=0;$attempt -lt 80;$attempt++) {
        SelectionMouse 0x201 0 0
        SelectionMouse 0x200 0 6
        SelectionMouse 0x202 0 6 0
        if(([string](Rpc 'session.copy' @{} $selectionSession)) -ceq 'VISIBLE'){break}
        Start-Sleep -Milliseconds 50
    }
    if($attempt -ge 80){throw 'Prepared visible selection frame was not published'}
    $null=Rpc 'selection.clear' @{} $selectionSession
    # Local copy reads the live native buffer, whereas synchronized output holds
    # LastPaintFrame. Finish painting that buffer before entering synchronized mode.
    [void][GhosttySelectionWindow]::RedrawWindow($hwnd,[IntPtr]::Zero,[IntPtr]::Zero,0x101)
    $null=Rpc 'session.write' @{text=($esc+'[?2026h'+$esc+'[H'+'HIDDEN!')} $selectionSession
    SelectionMouse 0x201 0 0
    SelectionMouse 0x200 0 6
    SelectionMouse 0x202 0 6 0
    $heldCopy=[string](Rpc 'session.copy' @{} $selectionSession)
    $null=Rpc 'session.write' @{text=($esc+'[?2026l')} $selectionSession
    Check 'Copy during synchronized output retains the displayed frame' ($heldCopy -ceq 'VISIBLE') "actual=[$heldCopy]"
} finally {
    [void][GhosttySelectionWindow]::SetWindowPos($hwnd,[IntPtr]::Zero,0,0,($windowRect.Right-$windowRect.Left),($windowRect.Bottom-$windowRect.Top),6)
    $null=Rpc 'session.close' @{} $selectionSession
    $null=Rpc 'session.select' @{} $session
}
