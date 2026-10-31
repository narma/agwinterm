# C0 acceptance: real Win32 host, private state/pipe, no global input or clipboard.
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe = Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl = Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$id = 'agwinterm-c0-' + [guid]::NewGuid().ToString('N')
$state = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $id
$saved = @{}
foreach ($name in 'AGWINTERM_SESSION_ID','AGWINTERM_PANE_ID','AGWINTERM_PIPE','AGWINTERM_APP_ID','AGWINTERM_DUMP') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $null)
}
$env:AGWINTERM_APP_ID = $id + '-fallback'
$process = $null
function Reply([string[]]$a) {
    $raw = (& $ctl @a --pipe $id --json) -join "`n"
    if (-not $raw) { throw "No reply: $a" }
    $r = $raw | ConvertFrom-Json
    if (($r.ok -and $LASTEXITCODE -ne 0) -or (-not $r.ok -and $LASTEXITCODE -ne 1)) { throw "Incorrect exit code: $a" }
    return $r
}
function Ctl([string[]]$a) {
    $r = Reply $a
    if (-not $r.ok) { throw "$a : $($r.error)" }
    return $r.result
}
function Check([bool]$ok, [string]$message) { if (-not $ok) { throw $message }; "PASS $message" }
function Node([string]$target, [string]$window) {
    (Ctl @('tree','--window',$window)).workspaces.sessions | Where-Object id -eq $target
}
function AwaitFile([string]$path) {
    for ($i=0; $i -lt 50; $i++) {
        if (Test-Path -LiteralPath $path) {
            try {
                $content = [IO.File]::ReadAllText($path).Trim()
                if ($content.Length -gt 0) { return $content }
            } catch [IO.IOException] {
                # Redirection creates the file before the writer releases its handle.
                # Retry reads within the existing deadline; never rerun the command.
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Command did not create $path"
}
try {
    if (Test-Path -LiteralPath $state) { throw 'Unexpected existing fixture directory' }
    New-Item -ItemType Directory -Path $state | Out-Null
    $cwdA = Join-Path $state 'cwd A'; $cwdB = Join-Path $state 'cwd B'
    New-Item -ItemType Directory -Path $cwdA,$cwdB | Out-Null
    @{default='C0 cmd'; profiles=@(@{name='C0 cmd';command='cmd.exe';args=@('/d');cwd=$cwdA})} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $state 'profiles.json')
    'command [send] C0Named = echo C0-NAMED-{AGW_PANE_ID}' | Set-Content -LiteralPath (Join-Path $state 'keymap.conf')
    $process = Start-Process $exe -ArgumentList @('--app-id',$id,'--pipe',$id,'--no-restore','--default-session-host','inprocess') -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 40; $i++) {
        try { $null = Ctl @('ping'); break } catch { if ($i -eq 39) { throw }; Start-Sleep -Milliseconds 250 }
    }
    Check (-not (Test-Path -LiteralPath ($state+'-fallback'))) 'Explicit app-id adopted'
    $windows = Ctl @('window','list'); $winA = @($windows)[0].id
    # window.list is an object with windows on some serving versions.
    if (-not $winA) { $winA = @($windows.windows)[0].id }
    $a = Ctl @('session','new','--name','C0 background','--cwd',$cwdA,'--profile','C0 cmd','--window',$winA)
    # Session creation is queued: wait for this exact ID, never retry the creation itself.
    for ($i = 0; $i -lt 60 -and -not (Node $a $winA); $i++) { Start-Sleep -Milliseconds 100 }
    Check ([bool](Node $a $winA)) 'New background session is ready before splitting'
    $pane = Ctl @('session','split','on','--target',$a,'--window',$winA)
    $recent = Ctl @('session','new','--name','C0 recent','--window',$winA)
    $b = Ctl @('session','new','--name','C0 visible','--cwd',$cwdB,'--window',$winA)
    $null = Ctl @('notify','C0 pending','--target',$a,'--window',$winA)
    $before = Node $a $winA
    $null = Ctl @('session','focus','right','--target',$pane,'--window',$winA)
    $null = Ctl @('session','resize','--split-ratio','0.3','--target',$a,'--window',$winA)
    $after = Node $a $winA
    Check ($after.focusedPane -eq 1 -and [Math]::Abs($after.splitRatios[0] - 0.3) -lt 0.001 -and (Node $b $winA).active) 'Background focus/ratio preserve visible session'
    Check ($after.notifications -eq $before.notifications) 'Background split preserves unseen notification count'
    $null = Ctl @('session','switch','next','--window',$winA)
    Check ((Node $recent $winA).active) 'Background split does not change MRU order'
    $null = Ctl @('session','switch','cancel','--window',$winA)
    $null = Ctl @('session','scratch','on','--target',$a,'--window',$winA)
    $refused = Reply @('session','resize','--split-ratio','0.6','--target',($a+':scratch'),'--window',$winA)
    Check (-not $refused.ok -and $refused.error -match 'main or split pane' -and (Node $a $winA).splitRatios[0] -eq $after.splitRatios[0]) 'Auxiliary pane refused without split mutation'
    $null = Ctl @('session','scratch','off','--target',$a,'--window',$winA)
    $gone = Ctl @('session','new','--name','C0 deleted','--no-select','--window',$winA)
    $null = Ctl @('session','close',$gone,'--window',$winA)
    Check (-not (Reply @('session','focus','other','--target',$gone,'--window',$winA)).ok) 'Deleted target refuses without active fallback'
    foreach ($target in @('unknown-target','C0 ambiguous')) {
        if ($target -eq 'C0 ambiguous') {
            $null = Ctl @('session','new','--name',$target,'--no-select','--window',$winA)
            $null = Ctl @('session','new','--name',$target,'--no-select','--window',$winA)
        }
        Check (-not (Reply @('session','focus','left','--target',$target,'--window',$winA)).ok) "Refuse $target"
    }
    Check (-not (Reply @('session','focus','top','--target',$a,'--window',$winA)).ok) 'Axis mismatch refused'
    Check (-not (Reply @('session','resize','--grow-left','1','--target',$b,'--window',$winA)).ok) 'Single pane refused'
    $null = Ctl @('session','resize','--grow-left','2','--target',$a,'--window',$winA)
    Check ((Node $a $winA).splitRatios[0] -ne $after.splitRatios[0]) 'Integer column growth changes target divider'
    $null = Ctl @('session','swap','--target',$a,'--window',$winA)
    $null = Ctl @('session','focus','left','--target',$pane,'--window',$winA)
    Check ((Node $a $winA).paneIds[0] -eq $pane -and (Node $a $winA).focusedPane -eq 0) 'Swapped pane ID still resolves owning session'
    $env:AGWINTERM_SESSION_ID = $a
    Check (-not (Reply @('session','focus','other','--window',$winA)).ok) 'Omitted target uses visible single pane despite caller environment'
    $env:AGWINTERM_SESSION_ID = $null
    $null = Ctl @('broadcast','on','--window',$winA)
    $marker = 'C0SEND' + [guid]::NewGuid().ToString('N')
    $null = Ctl @('command','run',"echo $marker-{AGW_PANE_ID}",'--mode','send','--target',$pane,'--window',$winA)
    Start-Sleep -Milliseconds 500
    $text = Ctl @('session','text','--target',$pane,'--window',$winA)
    Check (($text -replace '\s','').Contains("$marker-$pane")) 'Targeted send expands swapped pane identity'
    Check (-not (Ctl @('session','text','--target',$b,'--window',$winA)).Contains($marker)) 'Targeted send does not broadcast to visible session'
    $null = Ctl @('command','run','C0Named','--target',$pane,'--window',$winA)
    Start-Sleep -Milliseconds 200
    Check (((Ctl @('session','text','--target',$pane,'--window',$winA)) -replace '\s','').Contains("C0-NAMED-$pane")) 'Named command uses the same target context'
    $null = Ctl @('session','readonly','on','--target',$pane,'--window',$winA)
    Check (-not (Reply @('command','run','echo forbidden','--mode','send','--target',$pane,'--window',$winA)).ok) 'Readonly targeted command refused'
    $null = Ctl @('session','readonly','off','--target',$pane,'--window',$winA)
    $null = Ctl @('broadcast','off','--window',$winA)
    $out = Join-Path $state 'detached.txt'
    $null = Ctl @('command','run',"(echo {AGW_PANE_ID}^|%AGW_CWD%) > `"$out`"",'--mode','detached','--target',$pane,'--window',$winA)
    Check ((AwaitFile $out) -eq "$pane|$cwdA") 'Detached uses target identity and cwd'
    $out = Join-Path $state 'overlay.txt'
    $null = Ctl @('command','run',"(echo {AGW_PANE_ID}^|%AGW_CWD%) > `"$out`" & ping -n 4 127.0.0.1 >nul",'--mode','overlay','--target',$pane,'--window',$winA)
    Check ((AwaitFile $out) -eq "$pane|$cwdA" -and (Node $a $winA).overlay -and (Node $b $winA).active) 'Overlay belongs to target and does not select source'
    $null = Ctl @('session','overlay','close','--target',$a,'--window',$winA)
    $ws = Ctl @('workspace','new','--name','C0 separate','--window',$winA)
    $source = Ctl @('session','new','--name','C0 source','--workspace',$ws,'--cwd',$cwdA,'--profile','C0 cmd','--window',$winA)
    $null = Ctl @('session','select',$b,'--window',$winA)
    $out = Join-Path $state 'new.txt'
    $newCommand = "[IO.File]::WriteAllText('$($out.Replace("'","''"))', '{AGW_SESSION_ID}|' + `$env:AGW_WORKSPACE)"
    $null = Ctl @('command','run',$newCommand,'--mode','new','--target',$source,'--window',$winA)
    Check ((AwaitFile $out) -eq "$source|C0 separate") 'New command inherits source workspace identity'
    $new = (Ctl @('tree','--window',$winA)).workspaces | Where-Object id -eq $ws
    Check (@($new.sessions | Where-Object active).Count -eq 1 -and -not (Node $source $winA).active) 'New mode selects new session in target workspace'
    $winB = Ctl @('window','new','--name','C0 second')
    Check (-not (Reply @('session','focus','left','--target',$a,'--window',$winB)).ok) 'Explicit window bounds target resolution'
    Check (-not (Reply @('command','run','echo forbidden','--mode','send','--target',$a,'--window',$winB)).ok) 'Command cannot cross explicit window'
    $null = Ctl @('quick','on')
    $null = Ctl @('command','run','echo C0-QUICK-{AGW_PANE}','--mode','send','--target','active','--window','quick')
    $out = Join-Path $state 'quick.txt'
    $null = Ctl @('command','run',"echo {AGW_PANE} > `"$out`"",'--mode','detached','--target','active','--window','quick')
    Check ((AwaitFile $out) -eq 'quick') 'Quick send/detached keep quick context'
    foreach ($mode in 'new','overlay') {
        Check (-not (Reply @('command','run','echo forbidden','--mode',$mode,'--window','quick')).ok) "Quick refuses $mode"
    }
    Check (-not (Reply @('session','resize','--split-ratio','0.5','--window','quick')).ok) 'Quick refuses split operations'
    $null = Ctl @('quick','off')
    'C0 Win32 acceptance passed'
}
finally {
    try {
        if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $parent = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData'))
        foreach ($dir in @($state, $state+'-fallback')) {
            $full = [IO.Path]::GetFullPath($dir)
            if ([IO.Path]::GetDirectoryName($full) -eq $parent -and [IO.Path]::GetFileName($full) -match '^agwinterm-c0-[a-f0-9]{32}(-fallback)?$') {
                # Kill(true) waits for the app, but descendant shells can still be
                # releasing their cwd handles. Retry cleanup, never the test commands.
                for ($attempt = 0; $attempt -lt 30; $attempt++) {
                    try {
                        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
                        break
                    } catch {
                        if ($attempt -eq 29) { throw }
                        Start-Sleep -Milliseconds 100
                    }
                }
            }
        }
    } finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
    }
}
