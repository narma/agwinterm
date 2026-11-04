# Runs only inside the existing owned Hyper-V/Sandbox HUD fixture.
$esc = [string][char]27
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GhosttyLoaderProbe {
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32")]
    public static extern bool FreeLibrary(IntPtr handle);
}
'@
$nativeDirectory = Split-Path ([IO.Path]::GetFullPath($Exe))
$upstream = [GhosttyLoaderProbe]::LoadLibrary((Join-Path $nativeDirectory 'ghostty-vt.dll'))
$adapter = [GhosttyLoaderProbe]::LoadLibrary((Join-Path $nativeDirectory 'agwinterm_ghostty.dll'))
$loadError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Check 'Ghostty native DLLs load on clean Windows' ($upstream -ne [IntPtr]::Zero -and $adapter -ne [IntPtr]::Zero) "Win32 error $loadError"
if ($adapter -ne [IntPtr]::Zero) { [void][GhosttyLoaderProbe]::FreeLibrary($adapter) }
if ($upstream -ne [IntPtr]::Zero) { [void][GhosttyLoaderProbe]::FreeLibrary($upstream) }
$null = Rpc 'session.type' @{text="echo GHOSTTY_CONPTY_READY`r"} $session
$shellReady = $false
for ($attempt = 0; $attempt -lt 50; $attempt++) {
    $text = [string](Rpc 'session.text' @{} $session)
    if ($text -match 'GHOSTTY_CONPTY_READY') { $shellReady = $true; break }
    Start-Sleep -Milliseconds 100
}
Check 'Ghostty ConPTY shell responds to input' $shellReady
Start-Sleep -Milliseconds 500
foreach ($key in ('echo ghosttykeyboardok'+"`r").ToCharArray()) {
    $vk = if ([int]$key -ge 97 -and [int]$key -le 122) { [int]$key - 32 } else { [int]$key }
    [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]$vk,[IntPtr]1)
    [void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr][int]$key,[IntPtr]1)
    [void][HudOwnedJob]::SendMessageW($hwnd,0x101,[IntPtr]$vk,[IntPtr]1)
}
$keyboardReady = $false
for ($attempt = 0; $attempt -lt 50; $attempt++) {
    $text = [string](Rpc 'session.text' @{} $session)
    if ($text -match '(?m)^ghosttykeyboardok\s*$') { $keyboardReady = $true; break }
    Start-Sleep -Milliseconds 100
}
Check 'Ghostty Windows keyboard events reach ConPTY once' $keyboardReady
$cluster = "e$([char]0x301)"
$scene = $esc+'[?1049h'+$esc+'[?2027h'+$esc+'[2J'+$esc+'[H'+'Ghostty native VT'+"`r`n"+
    $esc+'[31;1mRED'+$esc+'[0m '+$cluster+' 👩🏽‍💻 中文'+"`r`n"+
    $esc+']8;;https://example.org/'+$esc+'\'+'LINK'+$esc+']8;;'+$esc+'\'
$null = Rpc 'session.write' @{text=$scene} $session
$text = [string](Rpc 'session.text' @{} $session)
$text | Set-Content (Join-Path $artifact 'ghostty-screen.txt')
Check 'Ghostty active screen readback' ($text.Contains('Ghostty native VT'))
Check 'Ghostty combining cluster survives application text API' ($text.Contains($cluster))
Check 'Ghostty emoji cluster survives application text API' ($text.Contains('👩🏽‍💻'))
Check 'Ghostty hyperlink text survives application text API' ($text.Contains('LINK'))
$png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=='
$images = $esc+'[5;5H'+$esc+'_Ga=T,f=100,i=101,c=8,r=4,C=1;'+$png+$esc+'\'+
    $esc+'[5;17H'+$esc+'_Ga=T,f=32,s=1,v=1,i=102,c=8,r=4,C=1;Ed9J/w=='+$esc+'\'
$null = Rpc 'session.write' @{text=$images} $session
Start-Sleep -Milliseconds 1200
$rc = [HudOwnedJob+RECT]::new(); [void][HudOwnedJob]::GetClientRect($hwnd, [ref]$rc)
$bitmap = [Drawing.Bitmap]::new($rc.right, $rc.bottom); $graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $dc = $graphics.GetHdc()
    try { Check 'Ghostty frame capture' ([HudOwnedJob]::PrintWindow($hwnd, $dc, 3)) }
    finally { $graphics.ReleaseHdc($dc) }
    $bitmap.Save((Join-Path $artifact 'ghostty-frame.png'))
    $red = 0; $green = 0
    for ($y = 0; $y -lt $bitmap.Height; $y += 2) {
        for ($x = 0; $x -lt $bitmap.Width; $x += 2) {
            $pixel = $bitmap.GetPixel($x, $y)
            if ($pixel.R -eq 255 -and $pixel.G -eq 0 -and $pixel.B -eq 0) { $red++ }
            if ($pixel.R -eq 17 -and $pixel.G -eq 223 -and $pixel.B -eq 73) { $green++ }
        }
    }
    Check 'Ghostty native PNG reaches Direct2D' ($red -gt 300) "red samples=$red"
    Check 'Ghostty native RGBA reaches Direct2D' ($green -gt 300) "green samples=$green"
} finally { $graphics.Dispose(); $bitmap.Dispose() }
$null = Rpc 'session.write' @{text=($esc+'[?1049l')} $session
$text = [string](Rpc 'session.text' @{} $session)
Check 'Ghostty returns to primary shell screen' ($text.Contains('GHOSTTY_CONPTY_READY'))
