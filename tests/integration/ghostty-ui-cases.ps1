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
Check 'Ghostty find locates the complete emoji cluster' ((Rpc 'session.search' @{query='👩🏽‍💻'} $session) -eq '1 of 1')
Check 'Ghostty find locates combining text' ((Rpc 'session.search' @{query=$cluster} $session) -eq '1 of 1')
$null = Rpc 'session.search' @{action='close'} $session
$uiaHome = if ($PSVersionTable.PSEdition -eq 'Core') { $PSHOME } else { "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/WPF" }
Add-Type -Path "$uiaHome/UIAutomationTypes.dll"
Add-Type -Path "$uiaHome/UIAutomationClient.dll"
$uiaRoot = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$document = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Document))
$pattern = $document.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
$emojiRange = $pattern.DocumentRange.FindText('👩🏽‍💻', $false, $false)
$combiningRange = $pattern.DocumentRange.FindText($cluster, $false, $false)
Check 'Ghostty UIA exposes complete graphemes' ($null -ne $emojiRange -and $null -ne $combiningRange)
if ($null -ne $emojiRange -and $null -ne $combiningRange) {
    $emojiBoxes = $emojiRange.GetBoundingRectangles(); $combiningBoxes = $combiningRange.GetBoundingRectangles()
    Check 'Ghostty UIA geometry follows cells instead of UTF-16 lengths' ($emojiBoxes.Count -eq 1 -and $combiningBoxes.Count -eq 1 -and
        [Math]::Abs($emojiBoxes[0].Width - 2 * $combiningBoxes[0].Width) -lt 1)
    $character = $emojiRange.Clone()
    $character.ExpandToEnclosingUnit([System.Windows.Automation.Text.TextUnit]::Character)
    Check 'Ghostty UIA character navigation keeps the emoji intact' ($character.GetText(-1) -eq '👩🏽‍💻')
}
# Select through the real Settings dropdown, starting from a different value.
# The already-running terminal keeps its native core throughout this setting change.
$null = Rpc 'config.set' @{key='emulator-core';value='managed'}
$null = Rpc 'settings.open' @{}
Start-Sleep -Milliseconds 150
$coreControl = @($uiaRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name.StartsWith('Terminal core (new sessions),') })
Check 'Settings exposes the terminal-core dropdown' ($coreControl.Count -eq 1)
if ($coreControl.Count -ne 1) { throw 'No unique core setting' }
$coreControl[0].SetFocus()
$coreControl[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 100
foreach ($character in 'ghostty'.ToCharArray()) {
    [void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr][int]$character,[IntPtr]1)
}
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]13,[IntPtr]1)
Check 'Settings dropdown selects and persists Ghostty' ((Rpc 'config.get' @{key='emulator-core'}) -eq 'ghostty')
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]27,[IntPtr]1)
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

# Search positions the actual application viewport over a marker in history.
# The image below it must follow that same native viewport, including clipping.
$historyScene = $esc+'[2J'+$esc+'[H'+$esc+'[5;5H'+'GHOSTTY_HISTORY_IMAGE'+
    $esc+'[6;5H'+$esc+'_Ga=T,f=32,s=1,v=1,i=901,c=8,r=4,C=1;Ed9J/w=='+$esc+'\'+
    $esc+'[20;1H'+("history padding`r`n" * 100)
$null = Rpc 'session.write' @{text=$historyScene} $session
$found = Rpc 'session.search' @{query='GHOSTTY_HISTORY_IMAGE'} $session
Start-Sleep -Milliseconds 1200
$bitmap = [Drawing.Bitmap]::new($rc.right, $rc.bottom); $graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $dc = $graphics.GetHdc()
    try { [void][HudOwnedJob]::PrintWindow($hwnd, $dc, 3) }
    finally { $graphics.ReleaseHdc($dc) }
    $bitmap.Save((Join-Path $artifact 'ghostty-history-frame.png'))
    $green = 0
    for ($y = 0; $y -lt $bitmap.Height; $y += 2) {
        for ($x = 0; $x -lt $bitmap.Width; $x += 2) {
            $pixel = $bitmap.GetPixel($x, $y)
            if ($pixel.R -eq 17 -and $pixel.G -eq 223 -and $pixel.B -eq 73) { $green++ }
        }
    }
    Check 'Ghostty history image reaches Direct2D after search scroll' ($green -gt 300) "green samples=$green; search=$found"
} finally { $graphics.Dispose(); $bitmap.Dispose() }
$null = Rpc 'session.search' @{action='close'} $session

$null = Rpc 'config.set' @{key='session-host';value='server'}
$serverSession = [string](Rpc 'session.new' @{name='ghostty-server';'no-select'=$true})
try {
    $serverReady = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        $null = Rpc 'session.type' @{text="echo GHOSTTY_SERVER_READY`r"} $serverSession -AllowError
        Start-Sleep -Milliseconds 200
        $serverText = [string](Rpc 'session.text' @{} $serverSession)
        if ($serverText -match '(?m)^GHOSTTY_SERVER_READY\s*$') { $serverReady = $true; break }
    }
    $serverText | Set-Content (Join-Path $artifact 'ghostty-server-shell.txt')
    Check 'Ghostty headless application host responds through the UI replica' $serverReady
} finally {
    $null = Rpc 'session.close' @{} $serverSession
    $null = Rpc 'config.set' @{key='session-host';value='in-process'}
}

# Optional self-contained server contract payload, staged only for this test run.
$serverHarness = Join-Path $nativeDirectory 'ghostty-server-tests/Agwinterm.GhosttyServerHarness.exe'
if (Test-Path -LiteralPath $serverHarness) {
    & $serverHarness $nativeDirectory 2>&1 | Tee-Object -FilePath (Join-Path $artifact 'ghostty-server.txt')
    Check 'Ghostty server snapshot and reattach contracts' ($LASTEXITCODE -eq 0)
}
