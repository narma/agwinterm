# Dot-sourced inside the private owned instance; no global input or Explorer launch.
function WCtrl([string]$verb,$params=@{},[string]$target='active',[switch]$NoTarget,[switch]$AllowError){Rpc $verb $params $target -Window '' -NoTarget:$NoTarget -AllowError:$AllowError}
function WWait([scriptblock]$condition){for($i=0;$i-lt 60;$i++){try{if(& $condition){return $true}}catch{};Start-Sleep -Milliseconds 100};return $false}
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$first=(WCtrl 'window.geometry').id
$foreground=[HudOwnedJob]::GetForegroundWindow()
$new=WCtrl 'window.new' @{name='C7 background';minimized=$true} -NoTarget
$second=$new.id
Check 'minimized creation reports an iconic window' ($new.minimized -and $second-ne $first)
Check 'minimized creation preserves foreground and library active window' ([HudOwnedJob]::GetForegroundWindow()-eq $foreground -and (WCtrl 'window.geometry').id-eq $first)
$restored=WCtrl 'window.restore' @{} $second
Check 'restore shows background window without foreground activation' (-not $restored.minimized -and [HudOwnedJob]::GetForegroundWindow()-eq $foreground)
$bad=WCtrl 'window.minimize' @{} 'missing' -AllowError
Check 'unknown target refuses without minimizing active window' (-not $bad.ok -and -not (WCtrl 'window.geometry' @{} $first).minimized)
Check 'quick refuses library window operations' (-not (WCtrl 'window.restore' @{} 'quick' -AllowError).ok)
$monitors=@(WCtrl 'window.monitors' -NoTarget)
Check 'monitor enumeration exposes physical work areas and device IDs' ($monitors.Count-ge 1 -and @($monitors|Where-Object primary).Count-eq 1 -and $monitors[0].workArea.Width-gt 0)
$moved=WCtrl 'window.move' @{monitor='primary'} $second
$primary=@($monitors|Where-Object primary)[0]
Check 'monitor movement is bounded and reports actual geometry' ($moved.monitor-eq $primary.id -and $moved.rect.X-ge $primary.workArea.X -and $moved.rect.Y-ge $primary.workArea.Y -and ($moved.rect.X+$moved.rect.Width)-le ($primary.workArea.X+$primary.workArea.Width))
$before=$moved.rect|ConvertTo-Json -Compress
Check 'unknown monitor refuses without moving' (-not (WCtrl 'window.move' @{monitor='missing'} $second -AllowError).ok -and ((WCtrl 'window.geometry' @{} $second).rect|ConvertTo-Json -Compress)-eq $before)
$null=WCtrl 'window.minimize' @{} $second
$moved=WCtrl 'window.move' @{monitor=$primary.id} $second
Check 'minimized placement changes restore bounds without showing or activating' ($moved.minimized -and $moved.restoreRect.X-ge $primary.workArea.X -and [HudOwnedJob]::GetForegroundWindow()-eq $foreground)
$null=WCtrl 'window.restore' @{} $second
$firstBefore=(WCtrl 'window.geometry' @{} $first).rect|ConvertTo-Json -Compress
$moveX=$primary.workArea.X+45;$moveY=$primary.workArea.Y+55
$cli=(& $ctl window move --target $second --x $moveX --y $moveY --pipe $pipe --json)|ConvertFrom-Json
Check 'coordinate CLI move honors target and leaves active window unchanged' ($LASTEXITCODE-eq 0 -and $cli.ok -and (WWait {$g=WCtrl 'window.geometry' @{} $second;$g.rect.X-eq $moveX -and $g.rect.Y-eq $moveY}) -and ((WCtrl 'window.geometry' @{} $first).rect|ConvertTo-Json -Compress)-eq $firstBefore)
$normalRestore=(WCtrl 'window.geometry' @{} $second).restoreRect|ConvertTo-Json -Compress
Check 'fullscreen on is idempotent' ((WCtrl 'window.fullscreen' @{op='on'} $second).fullscreen -and (WCtrl 'window.fullscreen' @{op='on'} $second).fullscreen)
Check 'fullscreen reports its actual future restore bounds' (((WCtrl 'window.geometry' @{} $second).restoreRect|ConvertTo-Json -Compress)-eq $normalRestore)
Check 'fullscreen off restores ordinary state' (-not (WCtrl 'window.fullscreen' @{op='off'} $second).fullscreen)
Check 'leaving fullscreen returns to the reported restore bounds' (((WCtrl 'window.geometry' @{} $second).restoreRect|ConvertTo-Json -Compress)-eq $normalRestore)
Check 'fullscreen malformed operation refuses without toggling' (-not (WCtrl 'window.fullscreen' @{op=$false} $second -AllowError).ok -and -not (WCtrl 'window.geometry' @{} $second).fullscreen)
$next=WCtrl 'window.next' @{} $first
Check 'next selects the other open window' ($next.id-eq $second)
Check 'previous wraps through open library order' ((WCtrl 'window.previous' @{} $second).id-eq $first)
$null=WCtrl 'window.close' @{} $second
Start-Sleep -Milliseconds 300
Check 'navigation refuses with only one open window' (-not (WCtrl 'window.next' @{} $first -AllowError).ok)
Check 'closed library window is not restored implicitly' (-not (WCtrl 'window.restore' @{} $second -AllowError).ok)
$reveal=Rpc 'session.reveal' @{'path-only'=$true} $session -Window $first
Check 'Windows Reveal resolves a real directory without launching Explorer' (-not $reveal.opened -and (Test-Path -LiteralPath $reveal.path -PathType Container))
Check 'unknown Reveal target refuses' (-not (Rpc 'session.reveal' @{'path-only'=$true} 'missing' -Window $first -AllowError).ok)
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$cli=(& $ctl window new --minimized --name 'C7 CLI' --pipe $pipe --json)|ConvertFrom-Json
Check 'actual CLI minimized creation returns effective state' ($LASTEXITCODE-eq 0 -and $cli.ok -and $cli.result.minimized)
$cli=(& $ctl session reveal --target $session --window $first --path-only --pipe $pipe --json)|ConvertFrom-Json
Check 'actual CLI path-only targets its session' ($LASTEXITCODE-eq 0 -and $cli.result.path-eq $reveal.path -and -not $cli.result.opened)
$survivingPane=[string](Rpc 'session.split' @{op='on'} $session -Window $first)
$survivingPath=(Rpc 'session.reveal' @{'path-only'=$true} $survivingPane -Window $first).path
$null=Rpc 'session.split.close' @{} $session -Window $first
Check 'reveal accepts stable session ID after its original pane closes' ((Rpc 'session.reveal' @{'path-only'=$true} $session -Window $first).path-eq $survivingPath -and (Rpc 'session.reveal' @{'path-only'=$true} $survivingPane -Window $first).path-eq $survivingPath)
$missingPath=Join-Path $artifact ('missing-reveal-'+[Guid]::NewGuid().ToString('N'))
$esc=[string][char]27
$null=Rpc 'session.write' @{text=($esc+']7;'+([Uri]$missingPath).AbsoluteUri+[char]7)} $survivingPane -Window $first
if(-not (WWait {(Rpc 'tree' @{} -NoTarget -Window $first).workspaces.sessions.paneCwds.$survivingPane-eq $missingPath})){throw 'Missing-directory OSC probe was not observed'}
Check 'missing reported directory refuses instead of opening launch directory' (-not (Rpc 'session.reveal' @{'path-only'=$true} $session -Window $first -AllowError).ok)
