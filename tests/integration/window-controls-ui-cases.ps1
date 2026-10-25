# Dot-sourced inside the private owned instance; no global input or Explorer launch.
function WCtrl([string]$verb,$params=@{},[string]$target='active',[switch]$NoTarget,[switch]$AllowError){Rpc $verb $params $target -Window '' -NoTarget:$NoTarget -AllowError:$AllowError}
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
Check 'fullscreen on is idempotent' ((WCtrl 'window.fullscreen' @{op='on'} $second).fullscreen -and (WCtrl 'window.fullscreen' @{op='on'} $second).fullscreen)
Check 'fullscreen off restores ordinary state' (-not (WCtrl 'window.fullscreen' @{op='off'} $second).fullscreen)
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
