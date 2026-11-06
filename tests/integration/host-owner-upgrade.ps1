param([Parameter(Mandatory)][string]$Exe,[switch]$Contracts,[switch]$Preview)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test HostOwnerUpgrade -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
if($Contracts){ & "$PSScriptRoot/host-owner-contracts.ps1" -Exe $Exe }
. "$PSScriptRoot/../ui/lib.ps1"
$directory=Split-Path ([IO.Path]::GetFullPath($Exe))
$old=Join-Path $directory $(if($Preview){'upgrade-preview/Agwinterm.Win32.exe'}else{'upgrade-source/Agwinterm.Win32.exe'})
$prepare=Join-Path $directory 'prepare-owner-upgrade.ps1'
if(-not(Test-Path $old) -or -not(Test-Path $prepare)){throw 'Pinned installed-version package and upgrade helper are required.'}
$appId='agwinterm-upgrade-test-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$controlPipe=$appId+'-control'
$appDir=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $appId
$null=New-Item -ItemType Directory -Path $appDir
@($(if($Preview){'session-host = server'}else{'session-host = server-rust'}),
  $(if($Preview){'emulator-core = ghostty'}else{'emulator-core = rust'}),
  'restore-buffer = true','restore-commands = false','update-check = false','claude-update-check = false','fresh-env = false') | Set-Content (Join-Path $appDir 'agwinterm.conf')
@{default='Upgrade';profiles=@(@{name='Upgrade';command='cmd.exe';args=@('/d');cwd=$appDir})} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $appDir 'profiles.json')
$ctl=Resolve-Binary '' 'agwintermctl.exe' 'Agwinterm.Ctl'
$s=[pscustomobject]@{Proc=$null;Exe=$old;Ctl=$ctl;Pipe=$controlPipe;AppId=$appId;AppDir=$appDir;Hwnd=[IntPtr]::Zero;Width=1000;Height=700}
$artifact=Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) ('.revmux/'+$appId)
$null=New-Item -ItemType Directory -Path $artifact -Force
function Rpc([string]$cmd,$parameters=@{},[string]$target='',[string]$window=$firstWindow) {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$controlPipe,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(3000)
        $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true)
        $reader=[IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
        try {
            $request=@{cmd=$cmd;args=$parameters}
            if($target.Length){$request.target=$target}
            if($window -and $cmd -notlike 'window.*'){$request.window=$window}
            $writer.WriteLine(($request|ConvertTo-Json -Depth 8 -Compress));$writer.Flush()
            $pending=$reader.ReadLineAsync();if(-not $pending.Wait(30000)){throw 'Upgrade fixture RPC timeout'}
            $reply=$pending.Result|ConvertFrom-Json
            if(-not $reply.ok){throw "$cmd failed: $($reply.error)"}
            return $reply.result
        } finally {$writer.Dispose();$reader.Dispose()}
    } finally {$pipe.Dispose()}
}
function Check([string]$name,[bool]$ok) {if(-not $ok){throw "FAIL $name"};"PASS $name"}
try {
    Check 'old installation starts in isolated profile' (Start-SandboxProcess $s @('--pipe',$controlPipe,'--app-id',$appId,'--no-restore'))
    $firstWindow=[string]((Rpc 'window.list').windows | Where-Object open | Select-Object -First 1).id
    $id=[string](Rpc 'tree').workspaces[0].sessions[0].id
    $esc=[string][char]27
    $marker='UPGRADE_HISTORY_'+[guid]::NewGuid().ToString('N')
    $null=Rpc 'session.write' @{text=("`r`n"+$esc+'[38;2;12;34;56m'+$marker+' é 中文'+$esc+'[0m'+"`r`n"+("upgrade padding`r`n"*50))} $id
    $oldText=[string](Rpc 'session.text' @{all=$true} $id)
    $oldText | Set-Content (Join-Path $artifact 'before.txt')
    Check 'legacy readback contains the fixture marker once' (([regex]::Matches($oldText,[regex]::Escape($marker))).Count -eq 1)
    $oldMarkerLine=($oldText -split "`n" | Where-Object { $_.Contains($marker) }).TrimEnd()
    $oldRuns=@((Rpc 'session.text' @{all=$true;styles=$true} $id).rows.runs | Where-Object { $_.text -like "*$marker*" })
    Check 'old fixture exposes an attributed history row' ($oldRuns.Count -eq 1 -and $oldRuns[0].fg -eq '#0c2238')
    $null=Rpc 'session.context' @{context='upgrade-context'} $id
    $splitId=[string](Rpc 'session.split' @{op='on';axis='horizontal'} $id)
    for($attempt=0;$attempt -lt 50;$attempt++) {
        if(([string](Rpc 'session.text' @{} $splitId)).Trim().Length){break}
        Start-Sleep -Milliseconds 100
    }
    $beforeLayout=(Rpc 'tree').workspaces[0].sessions[0]
    Check 'legacy fixture has a horizontal split' ($beforeLayout.axis -eq 'horizontal' -and $beforeLayout.paneIds.Count -eq 2)
    $secondWindow=[string](Rpc 'window.new' @{name='Upgrade second window'})
    for($attempt=0;$attempt -lt 50;$attempt++){
        if(@((Rpc 'window.list').windows | Where-Object open).Count -eq 2){break}
        Start-Sleep -Milliseconds 100
    }
    Check 'two old windows are open before upgrade' (@((Rpc 'window.list').windows | Where-Object open).Count -eq 2)
    $report=Join-Path $appDir 'migration-report.txt'
    $redirectTarget=Join-Path $artifact 'redirected-backgrounds'
    $null=New-Item -ItemType Directory -Path $redirectTarget
    $redirect=Join-Path $appDir 'backgrounds'
    $null=New-Item -ItemType Junction -Path $redirect -Target $redirectTarget
    try {
        & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $prepare -InstallDir (Split-Path $old) -ReportPath $report 2>&1 | Out-String | Write-Host
        Check 'unsafe backup refuses upgrade before closing the old UI' ($LASTEXITCODE -ne 0 -and -not $s.Proc.HasExited)
        Check 'old UI remains usable after refused upgrade' ((Rpc 'tree').workspaces[0].sessions[0].id -eq $id)
    } finally {
        # Remove only our junction itself, never recurse into its target.
        if((Get-Item -LiteralPath $redirect).Attributes -band [IO.FileAttributes]::ReparsePoint) { Remove-Item -LiteralPath $redirect -Force }
    }
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $prepare -InstallDir (Split-Path $old) -ReportPath $report
    Check 'upgrade helper checkpoints and closes the old installation' ($LASTEXITCODE -eq 0)
    Check 'old UI exited' ($s.Proc.WaitForExit(10000))
    $backup=(Get-Content $report -Raw).Trim() -replace '^.*Backup: ',''
    Check 'backup contains migration inventory' (Test-Path (Join-Path $backup 'upgrade.json'))
    Check 'old window state is backed up' (@(Get-ChildItem (Join-Path $backup "$appId/windows") -Filter '*.json').Count -gt 0)
    $s.Exe=$Exe
    Check 'new owner restores the same isolated profile' (Start-SandboxProcess $s @('--pipe',$controlPipe,'--app-id',$appId))
    for($attempt=0;$attempt -lt 50;$attempt++){
        $openIds=@((Rpc 'window.list').windows | Where-Object open | ForEach-Object id)
        if($firstWindow -in $openIds -and $secondWindow -in $openIds){break}
        Start-Sleep -Milliseconds 100
    }
    Check 'both original windows reopen after upgrade' ($firstWindow -in $openIds -and $secondWindow -in $openIds)
    Check 'legacy engine setting migrates to Ghostty' ((Rpc 'config.get' @{key='emulator-core'}) -eq 'ghostty')
    Check 'legacy server setting migrates to host-owner' ((Rpc 'config.get' @{key='session-host'}) -eq 'server')
    Check 'pane identity survives upgrade' ((Rpc 'tree').workspaces[0].sessions[0].id -eq $id)
    Check 'session context survives upgrade' ((Rpc 'tree').workspaces[0].sessions[0].context -eq 'upgrade-context')
    $afterLayout=(Rpc 'tree').workspaces[0].sessions[0]
    Check 'split axis and ordered pane identities survive upgrade' ($afterLayout.axis -eq 'horizontal' -and
        (($afterLayout.paneIds -join ',') -eq ($beforeLayout.paneIds -join ',')))
    $text=[string](Rpc 'session.text' @{all=$true} $id)
    $text | Set-Content (Join-Path $artifact 'after.txt')
    Check 'old history survives upgrade exactly once' (([regex]::Matches($text,[regex]::Escape($marker))).Count -eq 1)
    Check 'all Unicode retained by the old version survives upgrade' ($oldMarkerLine.Contains('中文') -and $text.Contains($oldMarkerLine))
    $newRuns=@((Rpc 'session.text' @{all=$true;styles=$true} $id).rows.runs | Where-Object { $_.text -like "*$marker*" })
    Check 'imported history preserves the old explicit RGB foreground' ($newRuns.Count -eq 1 -and $newRuns[0].fg -eq $oldRuns[0].fg)
    $null=Rpc 'restore.capture'
    $closingPane=[string](Rpc 'tree' @{} '' $secondWindow).workspaces[0].sessions[0].id
    $closeMarker='CLOSE_HISTORY_'+[guid]::NewGuid().ToString('N')
    $null=Rpc 'session.write' @{text=("`r`n"+$closeMarker+"`r`n")} $closingPane $secondWindow
    for($attempt=0;$attempt -lt 100;$attempt++){
        if(([string](Rpc 'session.text' @{} $closingPane $secondWindow)).Contains($closeMarker)){break}
        Start-Sleep -Milliseconds 25
    }
    $null=Rpc 'window.close' @{} $secondWindow
    for($attempt=0;$attempt -lt 50 -and @((Rpc 'window.list').windows | Where-Object open).Count -gt 1;$attempt++){Start-Sleep -Milliseconds 100}
    & (Join-Path $directory 'ghostty-server-tests/Agwinterm.GhosttyServerHarness.exe') --check-window-history (Join-Path $appDir "windows/$secondWindow.json") $closingPane $closeMarker
    Check 'closing one of two windows saves its latest owner history' ($LASTEXITCODE -eq 0)
    $null=Restart-Sandbox $s
    $text=[string](Rpc 'session.text' @{all=$true} $id)
    $text | Set-Content (Join-Path $artifact 'restarted.txt')
    Check 'owner restart does not duplicate imported history' (([regex]::Matches($text,[regex]::Escape($marker))).Count -eq 1)
    'PASS installed-version migration completed'
} finally {
    try {
        if($s.Proc -and -not $s.Proc.HasExited){Stop-Sandbox $s}
    } finally {
    # Graceful UI close deliberately leaves hosts alive. Clean only this fixture's
    # private namespaces in the guest, including a failed migration's old host.
    foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='Agwinterm.Win32.exe' OR Name='agwinterm-ptyhost.exe'")) {
        if($process.ExecutablePath -and ($process.ExecutablePath -ieq $Exe -or
            $process.ExecutablePath -ieq $old -or $process.ExecutablePath -ieq (Join-Path (Split-Path $old) 'agwinterm-ptyhost.exe')) -and
            $process.CommandLine -match ([regex]::Escape($controlPipe)+'(?:-presentation-v1|-owner-v1|-rust)?(?:"|\s|$)')) {
            $ownedProcess=Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
            if($ownedProcess){try {$ownedProcess.Kill();$null=$ownedProcess.WaitForExit(10000)}finally{$ownedProcess.Dispose()}}
        }
    }
    }
}
