# C6 private owned app acceptance.
function WSState(){(Rpc 'tree').workingSet}
function WSSessions([string]$id){@((Rpc 'tree').workspaces|Where-Object id -eq $id).sessions}
function WSWait([scriptblock]$condition){for($i=0;$i-lt 60;$i++){try{if(& $condition){return $true}}catch{};Start-Sleep -Milliseconds 100};return $false}
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$a=[string](Rpc 'workspace.new' @{name='Alpha';collapsed=$true} -NoTarget)
Check 'workspace new collapsed reports an empty folded workspace' (@((Rpc 'tree').workspaces|Where-Object id -eq $a)[0].collapsed -and (WSSessions $a).Count-eq 0)
$a1=[string](Rpc 'session.new' @{workspace=$a;name='A one';'no-select'=$true})
$a2=[string](Rpc 'session.new' @{after=$a1;name='A two';'no-select'=$true} -NoTarget)
$a0=[string](Rpc 'session.new' @{before=$a1;name='A zero';'no-select'=$true} -NoTarget)
Check 'anchored creation inserts in deterministic order' (((WSSessions $a).id -join ',')-eq "$a0,$a1,$a2")
$bad=Rpc 'session.new' @{before=$a1;after=$a2} -NoTarget -AllowError
Check 'two anchors refuse before creating anything' (-not $bad.ok -and (WSSessions $a).Count-eq 3)
$b=[string](Rpc 'workspace.new' @{name='Beta'} -NoTarget)
$b1=[string](Rpc 'session.new' @{workspace=$b;name='B one';'no-select'=$true})
$bad=Rpc 'session.move' @{before=$b1;workspace=$a} $a0 -AllowError
Check 'workspace-anchor conflict leaves original order intact' (-not $bad.ok -and ((WSSessions $a).id -join ',')-eq "$a0,$a1,$a2")
$bad=Rpc 'session.move' @{targets=@($a0,'A zero');after=$b1} -NoTarget -AllowError
Check 'batch aliases cannot move one session twice' (-not $bad.ok -and (WSSessions $a).Count-eq 3)
$a2Right=[string](Rpc 'session.split' @{op='on'} $a2)
$a0Right=[string](Rpc 'session.split' @{op='on'} $a0)
$moved=Rpc 'session.move' @{targets=@($a2,'missing-session',$a0);after=$b1} -NoTarget
Check 'batch move reports per-target partial outcomes' ($moved.outcome-eq 'partial' -and $moved.items.Count-eq 3 -and $moved.items[1].error -and $moved.items[0].changed)
Check 'successful batch moves form one ordered group' (((WSSessions $b).id -join ',')-eq "$b1,$a2,$a0")
Check 'moved session and pane identities remain stable' ((Node $a2).paneIds[0]-eq $a2 -and (Node $a2).paneIds[1]-eq $a2Right -and (Node $a0).paneIds[0]-eq $a0 -and (Node $a0).paneIds[1]-eq $a0Right)
$closed=Rpc 'session.close' @{targets=@($a0,'missing-session')} -NoTarget
Check 'batch close is partial and closes only requested live member' ($closed.outcome-eq 'partial' -and $null-eq (Node $a0) -and $null-ne (Node $a2))
Check 'batch close records recent history' (@((Rpc 'recent.list' @{} -NoTarget -Window '').entries|Where-Object name -eq 'A zero').Count-eq 1)
$null=Rpc 'workspace.collapse' @{} $b
$flagged=Rpc 'workspace.flag' @{op='on'} $b
Check 'workspace flag includes collapsed hidden members' ($flagged.changed-eq 2 -and @((WSSessions $b)|Where-Object flagged).Count-eq 2)
$null=Rpc 'workspace.flag' @{op='on'} $a
$null=Rpc 'workspace.focus' @{op='on'} $a
$null=Rpc 'workspace.focus' @{op='on'} $b
Check 'focus membership holds two workspaces without selecting them' ((WSState).focusedWorkspaceIds.Count-eq 2 -and (WSState).workspaceFilterEnabled)
$null=Rpc 'workspace.filter' @{op='off'} -NoTarget
Check 'disabling filter retains membership' (-not (WSState).workspaceFilterEnabled -and (WSState).focusedWorkspaceIds.Count-eq 2)
$null=Rpc 'workspace.filter' @{op='on'} -NoTarget
$null=Rpc 'workspace.expand' @{} $b
$null=Rpc 'sidebar' @{op='mode:flagged'} -NoTarget
$shotWidth=1040
foreach($layout in @('plain','flat','tree')){
    $null=Rpc 'sidebar.flagged-layout' @{layout=$layout} -NoTarget
    Check "flagged $layout reports visible sessions without changing flags" ((WSState).sidebarFlaggedLayout-eq $layout -and (WSState).visibleSessionIds.Count-eq 3)
    $shotWidth+=2
    $null=Rpc 'window.resize' @{w=$shotWidth;h=680} -NoTarget
    $null=Shot "c6-flagged-$layout"
}
$null=Rpc 'sidebar' @{op='mode:tree'} -NoTarget
$null=Rpc 'session.select' @{} $a1
$null=Rpc 'session.go' @{dir='next'} -NoTarget
Check 'session navigation follows visible filtered order' (WSWait {(Node $b1).active})
$null=Rpc 'session.select' @{} $session
Check 'explicit outside selection disables filter but retains set' (-not (WSState).workspaceFilterEnabled -and (WSState).focusedWorkspaceIds.Count-eq 2)
$json=(& $ctl session new --after $a1 --name 'CLI placed' --no-select --pipe $pipe --json)|ConvertFrom-Json
$cliId=[string]$json.result
Check 'actual CLI supports anchored placement' ($LASTEXITCODE-eq 0 -and (WSSessions $a)[1].id-eq $cliId)
$json=(& $ctl session move --targets "$cliId,$a2" --before $b1 --pipe $pipe --json)|ConvertFrom-Json
Check 'actual CLI batch keeps requested insertion order' ($LASTEXITCODE-eq 0 -and $json.result.outcome-eq 'completed' -and ((WSSessions $b).id -join ',')-eq "$cliId,$a2,$b1")
Start-Sleep -Milliseconds 400
$job.Finish();$job=[HudOwnedJob]::new();$job.Start([IO.Path]::GetFullPath($Exe),"--app-id $appId --pipe $pipe",$root)
Check 'restart preserves disabled focus set and chosen layout' (WSWait {(WSState).focusedWorkspaceIds.Count-eq 2 -and -not (WSState).workspaceFilterEnabled -and (WSState).sidebarFlaggedLayout-eq 'tree'})
$null=Rpc 'workspace.filter' @{op='on'} -NoTarget
Check 'reenabling restored filter recovers both members' ((WSState).workspaceFilterEnabled -and (WSState).focusedWorkspaceIds.Count-eq 2)
$null=Rpc 'workspace.delete' @{} $a
Check 'deleting one workspace preserves other focus membership' ((WSState).focusedWorkspaceIds.Count-eq 1 -and (WSState).focusedWorkspaceIds[0]-eq $b)
$null=Rpc 'workspace.focus' @{op='clear'} -NoTarget
Check 'clear explicitly removes all focus membership' ((WSState).focusedWorkspaceIds.Count-eq 0 -and -not (WSState).workspaceFilterEnabled)
