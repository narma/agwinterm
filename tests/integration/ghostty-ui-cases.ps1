# Runs only inside the existing owned Hyper-V/Sandbox HUD fixture.
$esc = [string][char]27
Check 'Absent core setting defaults to Ghostty' ((Rpc 'config.get' @{key='emulator-core'}) -eq 'ghostty')
function Find-GhosttyText([string]$Query) {
    $status = Rpc 'session.search' @{query=$Query} $session
    for ($poll = 0; $poll -lt 100 -and $status -eq 'searching…'; $poll++) {
        Start-Sleep -Milliseconds 50
        $status = Rpc 'session.search' @{} $session
    }
    return $status
}
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
Check 'Ghostty find locates the complete emoji cluster' ((Find-GhosttyText '👩🏽‍💻') -eq '1 of 1')
Check 'Ghostty find locates combining text' ((Find-GhosttyText $cluster) -eq '1 of 1')
$null = Rpc 'session.write' @{text=($esc+'[5;1HПРИВЕТ σ ς 𐐀')} $session
Check 'Ghostty find folds Cyrillic case' ((Find-GhosttyText 'привет') -eq '1 of 1')
Check 'Ghostty find folds Greek final sigma' ((Find-GhosttyText 'Σ') -eq '1 of 2')
$selectedBefore = Rpc 'session.search' @{action='next'} $session
$null = Rpc 'session.write' @{text=($esc+'[6;1Hunrelated status update')} $session
$selectedAfter = Rpc 'session.search' @{} $session
for ($poll = 0; $poll -lt 100 -and $selectedAfter -eq 'searching…'; $poll++) {
    Start-Sleep -Milliseconds 50
    $selectedAfter = Rpc 'session.search' @{} $session
}
Check 'Ghostty automatic refresh keeps the selected match' ($selectedBefore -eq '2 of 2' -and $selectedAfter -eq '2 of 2') "before=$selectedBefore; after=$selectedAfter"
Check 'Ghostty find folds astral Unicode case' ((Find-GhosttyText '𐐨') -eq '1 of 1')
$null = Rpc 'session.write' @{text=($esc+'[7;1HStraße ﬃ')} $session
Check 'Ghostty native find expands sharp-s' ((Find-GhosttyText 'STRASSE') -eq '1 of 1')
Check 'Ghostty native find expands ligatures' ((Find-GhosttyText 'FFI') -eq '1 of 1')
$null = Rpc 'session.search' @{query='missing old query'} $session
Check 'Ghostty newer query supersedes pending scan' ((Find-GhosttyText 'привет') -eq '1 of 1')
$null = Rpc 'session.search' @{query='missing cancelled query'} $session
$null = Rpc 'session.search' @{action='close'} $session
Check 'Ghostty reopen does not publish cancelled results' ((Find-GhosttyText 'привет') -eq '1 of 1')
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
function Check-PalettePixels([string]$label, [int]$red, [int]$green, [int]$blue,
    [int]$minimum=200, [int]$maximum=[int]::MaxValue) {
    Start-Sleep -Milliseconds 350
    $rect = [HudOwnedJob+RECT]::new(); [void][HudOwnedJob]::GetClientRect($hwnd, [ref]$rect)
    $image = [Drawing.Bitmap]::new($rect.right, $rect.bottom); $g = [Drawing.Graphics]::FromImage($image)
    try {
        $dc = $g.GetHdc()
        try { [void][HudOwnedJob]::PrintWindow($hwnd, $dc, 3) } finally { $g.ReleaseHdc($dc) }
        $image.Save((Join-Path $artifact ($label+'.png')))
        $count = 0
        for ($y=0;$y -lt $image.Height;$y+=2) { for ($x=0;$x -lt $image.Width;$x+=2) {
            $p=$image.GetPixel($x,$y)
            if ($p.R -eq $red -and $p.G -eq $green -and $p.B -eq $blue) { $count++ }
        } }
        Check $label ($count -ge $minimum -and $count -le $maximum) "color samples=$count"
    } finally { $g.Dispose(); $image.Dispose() }
}
$null = Rpc 'session.write' @{text=($esc+']4;1;#0c2238'+[char]7+$esc+'[12;1H'+$esc+'[41m'+(' '*30)+$esc+'[0m')} $session
Check-PalettePixels 'Ghostty OSC palette reaches actual pixels' 12 34 56
$null = Rpc 'config.set' @{key='theme';value='Nord'}
Check-PalettePixels 'Ghostty OSC palette survives live theme change' 12 34 56
$null = Rpc 'session.write' @{text=($esc+']104;1'+[char]7)} $session
Check-PalettePixels 'Ghostty palette reset uses new theme' 191 97 106
$null = Rpc 'config.set' @{key='theme';value='default'}
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
# Relative placement follows a native parent; deleting that parent must remove
# the child from the next rendered frame, including the Direct2D texture path.
$relative=$esc+'[12;5H'+$esc+'_Ga=T,f=32,s=1,v=1,i=103,p=1,c=2,r=2,C=1;/wAA/w=='+$esc+'\'+
    $esc+'_Ga=T,f=32,s=1,v=1,i=104,p=2,P=103,Q=1,H=3,V=1,c=4,r=2,C=1;ZIzw/w=='+$esc+'\'
$null=Rpc 'session.write' @{text=$relative} $session
Check-PalettePixels 'Ghostty relative image reaches actual pixels' 100 140 240
$null=Rpc 'session.write' @{text=($esc+'_Ga=d,d=I,i=103;'+$esc+'\')} $session
Check-PalettePixels 'Ghostty parent deletion removes relative image pixels' 100 140 240 0 0
$placeholder=[char]::ConvertFromUtf32(0x10EEEE)
$virtualPixels=[Convert]::ToBase64String([byte[]](100,140,240,255,100,140,240,255,100,140,240,255,100,140,240,255))
$virtual=$esc+'_Ga=t,f=32,s=2,v=2,i=105;'+$virtualPixels+$esc+'\'+
    $esc+'_Ga=p,i=105,U=1,c=2,r=2;'+$esc+'\'+$esc+'[12;20H'+$esc+'[38;2;0;0;105m'+
    $placeholder+[char]0x0305+[char]0x0305+$placeholder+[char]0x0305+[char]0x030D+
    $esc+'[13;20H'+$placeholder+[char]0x030D+[char]0x0305+$placeholder+[char]0x030D+[char]0x030D+$esc+'[0m'
$null=Rpc 'session.write' @{text=$virtual} $session
$virtualMetrics=Rpc 'session.metrics' @{} $session
# Virtual placements preserve aspect ratio: this square fits the two-cell width,
# centered vertically. The sampler visits one quarter of its pixels.
$virtualMinimum=[int][Math]::Floor([Math]::Pow([Math]::Min($virtualMetrics.cellWidth,$virtualMetrics.cellHeight),2)*0.7)
Check-PalettePixels 'Ghostty Unicode placeholders render native virtual image' 100 140 240 $virtualMinimum
$null=Rpc 'session.write' @{text=($esc+'_Ga=d,d=I,i=105;'+$esc+'\')} $session
Check-PalettePixels 'Ghostty virtual image deletion clears cached pixels' 100 140 240 0 0
$null = Rpc 'session.write' @{text=($esc+'[?1049l')} $session
$text = [string](Rpc 'session.text' @{} $session)
Check 'Ghostty returns to primary shell screen' ($text.Contains('GHOSTTY_CONPTY_READY'))

# Search positions the actual application viewport over a marker in history.
# The image below it must follow that same native viewport, including clipping.
$historyScene = $esc+'[2J'+$esc+'[H'+$esc+'[5;5H'+'GHOSTTY_HISTORY_IMAGE'+
    $esc+'[6;5H'+$esc+'_Ga=T,f=32,s=1,v=1,i=901,c=8,r=4,C=1;Ed9J/w=='+$esc+'\'+
    $esc+'[20;1H'+("history padding`r`n" * 100)
$null = Rpc 'session.write' @{text=$historyScene} $session
$found = Find-GhosttyText 'GHOSTTY_HISTORY_IMAGE'
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

$null = Rpc 'session.write' @{text=("Latin русский 😀 search fixture`r`n" * 5000)} $session
$timer = [Diagnostics.Stopwatch]::StartNew()
$pendingStatus = Rpc 'session.search' @{query='РУССКИЙ'} $session
$timer.Stop()
Check 'Ghostty large-history search returns pending without a UI scan' ($pendingStatus -eq 'searching…') "status=$pendingStatus; requestMs=$($timer.ElapsedMilliseconds)"
$largeStatus = $pendingStatus
for ($poll = 0; $poll -lt 200 -and $largeStatus -eq 'searching…'; $poll++) {
    Start-Sleep -Milliseconds 50
    $largeStatus = Rpc 'session.search' @{} $session
}
$retainedText = [string](Rpc 'session.text' @{all=$true} $session)
$retainedMatches = [regex]::Matches($retainedText, 'русский').Count
Check 'Ghostty large-history Unicode results match retained native history' ($retainedMatches -ge 4000 -and $largeStatus -eq "1 of $retainedMatches") "status=$largeStatus; retained=$retainedMatches; emitted=5000"
$null = Rpc 'session.search' @{query='РУССКИЙ'} $session
$foundDuringOutput = $false
for ($tick = 0; $tick -lt 40; $tick++) {
    Start-Sleep -Milliseconds 25
    $liveStatus = Rpc 'session.search' @{} $session
    if ($liveStatus -match '^\d+ of \d+') { $foundDuringOutput = $true }
    $null = Rpc 'session.write' @{text=($esc+'7'+$esc+"[1;1Hlive tick $tick"+$esc+'8')} $session
}
Check 'Ghostty native find makes progress during continuing output' $foundDuringOutput "last status=$liveStatus"
$null = Rpc 'session.write' @{text="freshuniquehistorymarker`r`n"} $session
Check 'Ghostty search sees output after a completed scan' ((Find-GhosttyText 'FRESHUNIQUEHISTORYMARKER') -eq '1 of 1')
$null = Rpc 'session.search' @{action='close'} $session

$null = Rpc 'config.set' @{key='emulator-core';value='managed'}
$legacySearch = [string](Rpc 'session.new' @{name='legacy-search-control'})
try {
    $null = Rpc 'session.type' @{text="echo LEGACY_SEARCH_READY`r"} $legacySearch
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if (([string](Rpc 'session.text' @{} $legacySearch)).Contains('LEGACY_SEARCH_READY')) { break }
        Start-Sleep -Milliseconds 100
    }
    Start-Sleep -Milliseconds 250
    $null = Rpc 'session.write' @{text=($esc+'[?1049h'+$esc+'[2J'+$esc+'[Hlegacyfind legacyfind')} $legacySearch
    $initial = Rpc 'session.search' @{query='legacyfind'} $legacySearch
    $next = Rpc 'session.search' @{action='next'} $legacySearch
    $polled = Rpc 'session.search' @{} $legacySearch
    Check 'Legacy search poll preserves selected occurrence' ($initial -eq '1 of 2' -and $next -eq '2 of 2' -and $polled -eq '2 of 2') "initial=$initial; next=$next; poll=$polled"
    $null = Rpc 'session.write' @{text="`r`nlegacyfind"} $legacySearch
    Check 'Legacy search poll refreshes output without resetting selection' ((Rpc 'session.search' @{} $legacySearch) -eq '2 of 3')
} finally {
    $null = Rpc 'session.search' @{action='close'} $legacySearch
    $null = Rpc 'session.close' @{} $legacySearch
    $null = Rpc 'config.set' @{key='emulator-core';value='ghostty'}
}

$null = Rpc 'config.set' @{key='session-host';value='server-rust'}
Check 'Ghostty redirects legacy Rust-host preference to native-capable server' ((Rpc 'config.get' @{key='session-host'}) -eq 'server')
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

# Real keyboard acceptance runs only on the owned guest desktop.
. "$PSScriptRoot/ghostty-selection-ui-cases.ps1"
. "$PSScriptRoot/ghostty-input-ui-cases.ps1"

# Optional self-contained server contract payload, staged only for this test run.
$serverHarness = Join-Path $nativeDirectory 'ghostty-server-tests/Agwinterm.GhosttyServerHarness.exe'
if (Test-Path -LiteralPath $serverHarness) {
    & $serverHarness $nativeDirectory 2>&1 | Tee-Object -FilePath (Join-Path $artifact 'ghostty-server.txt')
    Check 'Ghostty server snapshot and reattach contracts' ($LASTEXITCODE -eq 0)
}
