# C5: private owned app, profiles and directories only.
function RecentList(){Rpc 'recent.list' @{} -NoTarget -Window ''}
function RecentOpen([string]$id,[string]$window='active'){Rpc 'recent.open' @{id=$id} -NoTarget -Window $window}
function RecentWait([scriptblock]$condition){for($i=0;$i-lt 50;$i++){try{if(& $condition){return $true}}catch{};Start-Sleep -Milliseconds 100};return $false}
$null=Rpc 'recent.clear' @{} -NoTarget -Window ''
$null=Rpc 'session.rename' @{name='C5 split'} $session
$null=Rpc 'session.context' @{context='C5 context'} $session
$null=Rpc 'session.flag' @{op='on'} $session
$right=[string](Rpc 'session.split' @{op='on';axis='horizontal'} $session)
$null=Rpc 'session.restore' @{command='echo PIN_ONLY'} $right
$null=Rpc 'session.swap' @{} $session
$before=Node
$null=Rpc 'session.close' @{} $session
$entry=@((RecentList).entries)[0]
Check 'closed session has distinct stable entry ID' ($entry.kind-eq 'session' -and $entry.id-ne $session -and $entry.sessionCount-eq 1)
$bad=Rpc 'recent.list' @{} $session -Window '' -AllowError
Check 'raw recent refuses a session target' (-not $bad.ok)
$bad=Rpc 'recent.open' @{id=$entry.id;extra=$true} -NoTarget -AllowError
Check 'raw recent rejects unknown arguments without consumption' (-not $bad.ok -and @((RecentList).entries).Count-eq 1)
$bad=Rpc 'recent.open' @{id=$entry.id} -NoTarget -Window 'quick' -AllowError
Check 'quick is not an eligible destination' (-not $bad.ok)
$opened=RecentOpen $entry.id
$after=Node
Check 'split restore retains free session and swapped pane identities' ($opened.outcome-eq 'completed' -and $opened.members[0].sessionId-eq $session -and ($after.paneIds -join ',')-eq ($before.paneIds -join ','))
Check 'split restore retains axis context flag and name' ($after.axis-eq 'horizontal' -and $after.context-eq 'C5 context' -and $after.flagged -and $after.customName-eq 'C5 split')
Check 'restore pins survive without being executed' (($after.restoreCommands -join ',')-eq ($before.restoreCommands -join ',') -and (Rpc 'session.text' @{} $right)-notmatch 'PIN_ONLY')
Check 'successful reopen consumes only its entry' (@((RecentList).entries).Count-eq 0)
# Two profiles let a deterministic missing-profile failure exercise workspace partial recovery.
$profiles=@{default='HUD-test';profiles=@(@{name='HUD-test';command='cmd.exe';args=@('/d');cwd=$artifact},@{name='C5-extra';command='cmd.exe';args=@('/d');cwd=$artifact})}
$profiles|ConvertTo-Json -Depth 5|Set-Content (Join-Path $appDir 'profiles.json')
$null=Rpc 'profiles.reload' @{} -NoTarget
$ws=[string](Rpc 'workspace.new' @{name='C5 workspace'} -NoTarget)
$a=[string](Rpc 'session.new' @{workspace=$ws;profile='HUD-test';name='good'})
$b=[string](Rpc 'session.new' @{workspace=$ws;profile='C5-extra';name='retry'})
$null=Rpc 'workspace.delete' @{} $ws
$group=@((RecentList).entries|Where-Object kind -eq 'workspace')[0]
@{default='HUD-test';profiles=@($profiles.profiles[0])}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $appDir 'profiles.json')
$null=Rpc 'profiles.reload' @{} -NoTarget
$partial=RecentOpen $group.id
Check 'partial workspace reports success and error honestly' ($partial.outcome-eq 'partial' -and $partial.remaining-eq 1 -and @($partial.members|Where-Object error).Count-eq 1 -and $null-ne (Node $a))
$retained=@((RecentList).entries|Where-Object id -eq $group.id)[0]
Check 'partial entry retains ID and destination with only failed member' ($retained.sessionCount-eq 1 -and $retained.destinationWorkspaceId-eq $partial.workspaceId)
$profiles|ConvertTo-Json -Depth 5|Set-Content (Join-Path $appDir 'profiles.json')
$null=Rpc 'profiles.reload' @{} -NoTarget
$retry=RecentOpen $group.id
Check 'retry restores only the failed member' ($retry.outcome-eq 'completed' -and $retry.members.Count-eq 1 -and $retry.members[0].sessionId-eq $b -and @((Rpc 'tree').workspaces.sessions|Where-Object id -eq $a).Count-eq 1)
# Empty workspaces are history too.
$empty=[string](Rpc 'workspace.new' @{name='C5 empty'} -NoTarget)
$null=Rpc 'workspace.delete' @{} $empty
$emptyEntry=@((RecentList).entries|Where-Object name -eq 'C5 empty')[0]
$opened=RecentOpen $emptyEntry.id
Check 'empty workspace can reopen' ($opened.outcome-eq 'completed' -and $opened.members.Count-eq 0 -and @((Rpc 'tree').workspaces|Where-Object id -eq $empty).Count-eq 1)
# Close the source window after recording an entry; recover into another live window.
$null=Rpc 'session.close' @{} $b
$entry=@((RecentList).entries|Where-Object name -eq 'retry')[0]
$source=$entry.sourceWindowId
$dest=[string](Rpc 'window.new' @{name='C5 destination'} -NoTarget -Window '')
Check 'destination window becomes available' (RecentWait {@((Rpc 'window.list' @{} -NoTarget -Window '').windows|Where-Object {$_.id-eq $dest -and $_.open}).Count-gt 0})
$null=Rpc 'window.close' @{} $source -Window ''
Check 'source window closes while history remains' (RecentWait {@((RecentList).entries|Where-Object id -eq $entry.id).Count-eq 1 -and -not @((Rpc 'window.list' @{} -NoTarget -Window '').windows|Where-Object id -eq $source)[0].open})
$bad=Rpc 'recent.open' @{id=$entry.id} -NoTarget -Window $source -AllowError
Check 'closed destination refuses without consuming history' (-not $bad.ok)
$null=Rpc 'window.delete' @{} $source -Window ''
Check 'source window deletion retains independent history' (@((RecentList).entries|Where-Object id -eq $entry.id).Count-eq 1)
$opened=RecentOpen $entry.id $dest
Check 'explicit destination restores from a closed source' ($opened.windowId-eq $dest -and $opened.outcome-eq 'completed')
$null=Rpc 'session.close' @{} $opened.members[0].sessionId -Window $dest
$entry=@((RecentList).entries)[0]
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$json=(& $ctl recent list --pipe $pipe --json)|ConvertFrom-Json
Check 'actual CLI lists the same stable entry' ($LASTEXITCODE-eq 0 -and @($json.result.entries|Where-Object id -eq $entry.id).Count-eq 1)
$savedHistory=Get-Content (Join-Path $appDir 'recent.json') -Raw
$firstOpen=RecentOpen $entry.id $dest
Start-Sleep -Milliseconds 400
$job.Finish()
[IO.File]::WriteAllText((Join-Path $appDir 'recent.json'),$savedHistory)
$job=[HudOwnedJob]::new()
$job.Start([IO.Path]::GetFullPath($Exe),"--app-id $appId --pipe $pipe",$root)
Check 'restart loads stable history independently of live restored sessions' (RecentWait {try{@((RecentList).entries|Where-Object id -eq $entry.id).Count-eq 1 -and @((Rpc 'tree' @{} -NoTarget -Window $dest).workspaces.sessions|Where-Object id -eq $firstOpen.members[0].sessionId).Count-eq 1}catch{$false}})
$collision=RecentOpen $entry.id $dest
Check 'stale history cannot duplicate a live identity' ($collision.members[0].sessionId-ne $firstOpen.members[0].sessionId -and $collision.outcome-eq 'completed')
$null=Rpc 'session.close' @{} $collision.members[0].sessionId -Window $dest
$entry=@((RecentList).entries)[0]
$null=& $ctl recent clear $entry.id --pipe $pipe --json
Check 'actual CLI clear is isolated from live sessions' ($LASTEXITCODE-eq 0 -and @((RecentList).entries|Where-Object id -eq $entry.id).Count-eq 0 -and @((Rpc 'tree' @{} -NoTarget -Window $dest).workspaces.sessions).Count-gt 0)
$null=Rpc 'recent.clear' @{} -NoTarget -Window ''
Check 'clear is persisted independently of restore state' (((Get-Content (Join-Path $appDir 'recent.json') -Raw|ConvertFrom-Json).Sessions.Count)-eq 0 -and (Test-Path (Join-Path $appDir 'windows')))
