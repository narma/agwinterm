# C4, isolated owner fixture. No global input, clipboard, install or configuration mutation.
function HudEvents(){@((Rpc 'events' @{kinds=@('hud')} -NoTarget).events)}
function HudWait([scriptblock]$condition){for($i=0;$i-lt 40;$i++){if(& $condition){return $true};Start-Sleep -Milliseconds 50};return $false}
for($i=0;$i-lt 50 -and ([string](Rpc 'session.text' @{} $session))-notmatch '>'; $i++){Start-Sleep -Milliseconds 100}
$text=Rpc 'session.text' @{all=$true} $session
$metrics=Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress
$reply=Rpc 'session.hud.open' @{message='Caption';sticky=$true;frame=$false;position='top';'size-percent'=100;'font-size'=20;color='#125a9f'} $session
$h=(Node).hud
Check 'sticky frameless font options read back with effective geometry' ($h.sticky -and -not $h.frame -and $h.sizePercent-eq 100 -and $h.fontSize-eq 20 -and $h.effectiveFontSize-eq 20 -and $h.visible -and $h.geometry.width-gt 0)
$box=Shot 'c4-sticky-caption'
Check 'readback matches actually painted client pixels' ([Math]::Abs($box[0]-$h.geometry.x)-le 2 -and [Math]::Abs($box[1]-$h.geometry.y)-le 2 -and [Math]::Abs(($box[2]-$box[0]+1)-$h.geometry.width)-le 2) "paint=$box geometry=$($h.geometry|ConvertTo-Json -Compress)"
$null=Rpc 'session.hud.update' @{message='Caption';'font-size'=30} $session
$h=(Node).hud
Check 'native font changes in place while update resets optional style and retains background' ($h.fontSize-eq 30 -and $h.frame -and -not $h.sticky -and $h.position-eq 'center' -and $h.backgroundColor-eq '#125a9f')
$markdown=@'
# Отчёт C4
**Ready** and *reviewed*, with `code`.
- Native list
> Passive quote
---
| Component | Status |
| --- | --- |
| Windows | ready |
| WSL | ready |
```text
literal $HOME <script>
```
'@
$null=Rpc 'session.hud.update' @{message=$markdown;markdown=$true;'font-size'=17;'size-percent'=70} $session
$before=(HudEvents).Count
$null=Rpc 'session.hud.update' @{message=$markdown;markdown=$true;'font-size'=17;'size-percent'=70} $session
Check 'identical untimed Markdown update emits no false event' ((HudEvents).Count-eq $before)
$null=Shot 'c4-markdown'
Check 'formatting is opt-in and readback preserves source' ((Node).hud.markdown -and (Node).hud.message-ceq $markdown)
Check 'formatted HUD does not change terminal history or grid' ((Rpc 'session.text' @{all=$true} $session)-ceq $text -and (Rpc 'session.metrics' @{} $session|ConvertTo-Json -Compress)-ceq $metrics)
$bad=Rpc 'session.hud.update' @{message='wrong';'font-size'=100} $session -AllowError
Check 'invalid style does not replace formatted HUD' (-not $bad.ok -and (Node).hud.message-ceq $markdown)
$right=[string](Rpc 'session.split' @{op='on'} $session)
$null=Rpc 'session.hud.open' @{message='Pane caption';pane='right';sticky=$true;position='top';'size-percent'=100;color='#125a9f'} $session
$h=(Node).hud;$oldX=$h.geometry.x
Check 'pane selector pins the exact right pane ID' ($h.anchorPaneId-eq $right)
$null=Rpc 'session.swap' @{} $session
$h=(Node).hud
Check 'swap moves HUD with its stable pane identity' ($h.anchorPaneId-eq $right -and $h.geometry.x-lt $oldX)
$null=Rpc 'session.scratch' @{op='on'} $session
Check 'scratch obscures without losing anchored HUD' (-not (Node).hud.visible -and (Node).hud.anchorPaneId-eq $right)
$null=Rpc 'session.scratch' @{op='off'} $session
$null=Rpc 'session.split.close' @{} $right
Check 'anchor removal closes HUD' (HudWait {$null-eq (Node).hud})
$removed=@(HudEvents|Where-Object {$_.reason-eq 'owner-removed' -and $_.anchorPaneId-eq $right})
Check 'anchor removal event retains removed identity' ($removed.Count-eq 1)
$other=[string](Rpc 'session.new' @{name='C4 background';'no-select'=$true})
$null=Rpc 'session.hud.open' @{message='Timed background';'hide-after'=0.3} $other
Check 'background readback reports invisible retained HUD' (-not (Node $other).hud.visible -and (Node).active)
Check 'background lifetime expires without selecting the owner' (HudWait {$null-eq (Node $other).hud -and (Node).active})
Check 'expiry publishes explicit reason' (@(HudEvents|Where-Object {$_.reason-eq 'expired' -and $_.sessionId-eq $other}).Count-eq 1)
$null=Rpc 'session.hud.open' @{message='Old timer';'hide-after'=0.2} $session
$null=Rpc 'session.hud.open' @{message='Replacement'} $session
Start-Sleep -Milliseconds 350
Check 'old timer cannot close an untimed replacement' ((Node).hud.message-eq 'Replacement')
$null=Rpc 'session.hud.open' @{message='Retain timer';'hide-after'=0.6} $session
$bad=Rpc 'session.hud.update' @{message='bad';pane='unknown'} $session -AllowError
Check 'bad anchor update preserves current message and deadline' (-not $bad.ok -and (Node).hud.message-eq 'Retain timer')
Check 'refused update did not cancel the deadline' (HudWait {$null-eq (Node).hud})
$null=Rpc 'session.hud.open' @{message='Old timer';'hide-after'=0.2} $session
$overlay=Rpc 'session.overlay' @{action='open';command='cmd /d /k echo C4'} $session
Start-Sleep -Milliseconds 350
Check 'old timer cannot close a replacing program overlay' ((Node).overlay -and $null-eq (Node).hud)
$null=Rpc 'session.overlay' @{action='hide'} $session
$bad=Rpc 'session.hud.open' @{message='must refuse'} $session -AllowError
Check 'hidden program still owns the shared HUD slot' (-not $bad.ok)
$null=Rpc 'session.overlay' @{action='close'} $session
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$file=Join-Path $artifact 'message with spaces.md';[IO.File]::WriteAllText($file,$markdown)
$null=& $ctl session hud --file $file --markdown --no-frame --sticky --position top --size-percent 100 --hide-after 0 --font-size 16 --target $session --pipe $pipe --json
Check 'actual CLI reads bounded UTF-8 file with additive options' ($LASTEXITCODE-eq 0 -and (Node).hud.markdown -and (Node).hud.fontSize-eq 16)
$null=& $ctl session hud 'conflict' --file $file --target $session --pipe $pipe --json 2>$null
Check 'file and positional message refuse before mutation' ($LASTEXITCODE-eq 2 -and (Node).hud.message-ceq $markdown)
$null=Rpc 'window.resize' @{w=640;h=480} -NoTarget
Start-Sleep -Milliseconds 150
$small=(Node).hud.geometry
$oldDpi=[HudOwnedJob]::SetThreadDpiAwarenessContext([IntPtr](-4))
try{$client=[HudOwnedJob+RECT]::new();[void][HudOwnedJob]::GetClientRect($hwnd,[ref]$client)}finally{[void][HudOwnedJob]::SetThreadDpiAwarenessContext($oldDpi)}
Check 'small-window Markdown remains within actual client bounds' ($small.x-ge 0 -and $small.y-ge 0 -and $small.x+$small.width-le $client.right -and $small.y+$small.height-le $client.bottom)
$null=Shot 'c4-markdown-small'
$null=Rpc 'session.hud.close' @{} $session
$null=Rpc 'session.close' @{} $other
