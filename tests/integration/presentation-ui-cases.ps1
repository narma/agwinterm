function Appearance([string]$pane){Rpc 'session.appearance' @{op='get'} $pane}
function PWait([scriptblock]$condition){for($i=0;$i-lt 60;$i++){try{if(& $condition){return $true}}catch{};Start-Sleep -Milliseconds 100};return $false}
$right=[string](Rpc 'session.split' @{op='on';axis='vertical'} $session)
$before=Rpc 'session.text' @{} $right
$styled=Rpc 'session.appearance' @{op='set';color='#14283C';text="BUILD SANDBOX`nТолько оформление";opacity=50;'status-color'='#FFAA00';'status-shape'='diamond';'hide-image'=$true} $right
Check 'appearance readback contains local overrides and effective fallback' ($styled.effectiveBackground-eq '#14283C' -and $styled.overrides.StatusShape-eq 'diamond' -and $styled.overrides.HideImage)
Check 'sibling inherits without receiving local overrides' ($null-eq (Appearance $session).overrides)
Check 'watermark does not enter terminal buffer' (([string](Rpc 'session.text' @{} $right))-notmatch 'BUILD SANDBOX')
$null=Rpc 'session.status' @{status='blocked'} $right
$null=Rpc 'session.appearance' @{op='set';color='#14283C';text="BUILD SANDBOX`nТолько оформление";opacity=50;'status-color'='#FFAA00';'status-shape'='diamond'} $right
Check 'appearance does not alter pane status or aggregate priority' ((Node $session).status-eq 'blocked')
$bad=Rpc 'session.appearance' @{op='set';color='red'} $right -AllowError
Check 'invalid appearance refuses atomically' (-not $bad.ok -and (Appearance $right).effectiveBackground-eq '#14283C')
Check 'unknown pane refuses without changing visible pane' (-not (Rpc 'session.appearance' @{op='clear'} 'missing' -AllowError).ok -and (Appearance $right).overrides.Text.Contains('BUILD'))
$esc=[string][char]27
$null=Rpc 'window.resize' @{w=1082;h=700} -NoTarget
Start-Sleep -Milliseconds 400
Check 'explicit ANSI background probe reaches the terminal' (PWait {
    $null=Rpc 'session.write' @{text=($esc+'[2J'+$esc+'[H'+$esc+'[48;2;200;40;40mANSI background'+$esc+'[0m')} $right
    Start-Sleep -Milliseconds 100
    ([string](Rpc 'session.text' @{} $right)).Contains('ANSI background')
})
$null=Rpc 'window.resize' @{w=1084;h=700} -NoTarget
Start-Sleep -Milliseconds 400
$null=Shot 'c8-pane-watermark-status'
$null=Rpc 'session.write' @{text=($esc+']11;rgb:aa/bb/cc'+[char]7)} $right
Check 'OSC dynamic background takes precedence over solid fallback' (PWait {(Appearance $right).effectiveBackground-eq '#AABBCC'})
$null=Rpc 'session.write' @{text=($esc+']111'+[char]7)} $right
$null=Rpc 'session.swap' @{} $session
Check 'swap keeps local appearance on its pane ID' ((Appearance $right).overrides.Color-eq '#14283C' -and $null-eq (Appearance $session).overrides)
$null=Rpc 'session.appearance' @{op='clear'} $right
Check 'clear removes all local overrides' ($null-eq (Appearance $right).overrides)
$null=Rpc 'session.appearance' @{op='set';color='#254565';text='C8 durable';'status-shape'='square'} $right
$savedAppearance=PWait {
    foreach($file in Get-ChildItem (Join-Path $appDir 'windows') -Filter '*.json'){
        try{$saved=Get-Content $file.FullName -Raw|ConvertFrom-Json}catch{continue}
        foreach($ws in $saved.Workspaces){foreach($ses in $ws.Sessions){
            if($ses.Id-eq $session -and $ses.Panes[0].Id-eq $right -and $ses.Panes[0].Appearance.Text-eq 'C8 durable' -and $ses.Panes[0].Appearance.StatusShape-eq 'square'){return $true}
        }}
    };return $false
}
Check 'pane appearance and swapped identity reach disk before restart' $savedAppearance
if(-not $savedAppearance){throw 'Refuse to restart before the expected pane appearance is saved'}
$job.Finish();$job=[HudOwnedJob]::new();$job.Start([IO.Path]::GetFullPath($Exe),"--app-id $appId --pipe $pipe",$root)
Check 'restart restores appearance with swapped pane identity' (PWait {(Appearance $right).overrides.Text-eq 'C8 durable' -and $null-eq (Appearance $session).overrides})
$null=Rpc 'session.close' @{} $session
$entry=@((Rpc 'recent.list' @{} -NoTarget -Window '').entries)[0]
$opened=Rpc 'recent.open' @{id=$entry.id} -NoTarget -Window ''
Check 'recent recovery retains pane-owned presentation' ($opened.outcome-eq 'completed' -and (Appearance $right).overrides.StatusShape-eq 'square')
@('map f5 = toggle_sidebar','unmap ctrl+t','leader = f9','map leader x = toggle_flag')|Add-Content (Join-Path $appDir 'keymap.conf')
$null=Rpc 'keymap.reload' @{} -NoTarget
Check 'effective keymap includes overrides and omits unmaps' (PWait {$map=Rpc 'keymap.list' @{} -NoTarget;$map.bindings.f5-eq 'toggle_sidebar' -and $null-eq $map.bindings.'ctrl+t' -and $map.leader-eq 'f9' -and $map.leaderBindings.x-eq 'toggle_flag'})
Check 'keymap readback exposes reserved behavior separately' ((Rpc 'keymap.list' @{} -NoTarget).reserved.f6.Length-gt 0)
$null=Rpc 'config.set' @{key='theme';value='Solarized Light'}
$null=Rpc 'config.set' @{key='theme-follow-system';value='false'}
Check 'manual theme is effective in readback' ((Appearance $right).effectiveTheme-eq 'Solarized Light')
$null=Rpc 'config.set' @{key='theme-dark';value='Nord'}
$null=Rpc 'config.set' @{key='theme-light';value='Nord'}
$null=Rpc 'config.set' @{key='theme-follow-system';value='true'}
Check 'system theme selection applies configured side' ((Appearance $right).effectiveTheme-eq 'Nord')
$null=Rpc 'config.set' @{key='theme-dark';value=''}
$null=Rpc 'config.set' @{key='theme-light';value=''}
Check 'unset system side falls back to the manual theme' ((Appearance $right).effectiveTheme-eq 'Solarized Light')
$proc=[Diagnostics.Process]::GetProcessById($job.Pid);$proc.Refresh();$hwnd=$proc.MainWindowHandle
$null=Rpc 'window.resize' @{w=1086;h=700} -NoTarget
Start-Sleep -Milliseconds 400
$null=Shot 'c8-light-theme-recovered-pane'
@('','cursor-style = invalid','c8-unknown-key = true')|Add-Content (Join-Path $appDir 'agwinterm.conf')
$reload=Rpc 'config.reload' @{} -NoTarget
Check 'reload returns visible invalid and unknown entry diagnostics' (($reload.diagnostics -join ' ')-match 'cursor-style' -and ($reload.diagnostics -join ' ')-match 'unknown key')
$version=Rpc 'app.version' @{} -NoTarget
($version|ConvertTo-Json -Depth 5)|Set-Content (Join-Path $artifact 'version.json')
Check 'version reports explicit metadata availability and comparison' ($version.running -and $version.path -and $version.source-eq 'running-executable-path' -and $version.comparison-in @('match','different','unknown') -and $version.availability-in @('available','missing-file','metadata-unavailable'))
$ctl=Join-Path $root 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$cli=(& $ctl session appearance get --target $right --pipe $pipe --json)|ConvertFrom-Json
Check 'actual CLI reads persistent appearance' ($LASTEXITCODE-eq 0 -and $cli.result.overrides.Text-eq 'C8 durable')
$cli=(& $ctl keymap list --pipe $pipe --json)|ConvertFrom-Json
Check 'actual CLI reads effective keymap' ($LASTEXITCODE-eq 0 -and $cli.result.bindings.f5-eq 'toggle_sidebar')
$null=Rpc 'session.split.close' @{} $session
$survivorAppearance=Appearance $session
Check 'appearance resolves stable session ID after original pane closes' ($survivorAppearance.paneId-eq $right -and $survivorAppearance.overrides.Text-eq 'C8 durable')
