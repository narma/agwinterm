param([string]$Ctl,[Parameter(Mandatory)][string]$OldSetup,[Parameter(Mandatory)][string]$NewSetup)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test InstalledSelfUpdate -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
. "$PSScriptRoot/../ui/lib.ps1"
$install=Join-Path $env:LOCALAPPDATA 'Programs/agwinterm'
if(Test-Path -LiteralPath $install){throw 'This test requires a guest without an installed copy.'}
$appId='agwinterm-upgrade-'+[guid]::NewGuid().ToString('N')
$data=Join-Path $env:LOCALAPPDATA $appId
$null=New-Item -ItemType Directory -Path $data
$scratch=New-Item -ItemType Directory -Path (Join-Path $env:TEMP $appId)
$oldInstaller=Join-Path $scratch 'old-setup.exe'
Copy-Item -LiteralPath $OldSetup -Destination $oldInstaller
$setup=Start-Process $oldInstaller -ArgumentList '/VERYSILENT','/NORESTART','/SUPPRESSMSGBOXES' -PassThru -Wait
if($setup.ExitCode){throw "Old setup failed: $($setup.ExitCode)"}
$exe=Join-Path $install 'Agwinterm.Win32.exe'
$newHost=Join-Path $install 'host/Agwinterm.Host.exe'
$oldHost=Join-Path $install 'agwinterm-ptyhost.exe'
$s=[pscustomobject]@{Exe=$exe;Ctl=$Ctl;Pipe=$appId;Proc=$null;Hwnd=[IntPtr]::Zero;Width=1000;Height=700}
$env:AGWINTERM_APP_ID=$appId
$env:AGWINTERM_UPDATE_API=Join-Path $scratch 'release.json'
$asset=@{name='agwinterm-setup-0.30.1.exe';browser_download_url=$NewSetup;digest='sha256:'+(Get-FileHash $NewSetup).Hash}
@{tag_name='v0.30.1';assets=@($asset)}|ConvertTo-Json -Depth 4|Set-Content $env:AGWINTERM_UPDATE_API
@('session-host = server-rust','restore-buffer = true','update-check = false','claude-update-check = false')|Set-Content (Join-Path $data 'agwinterm.conf')
try {
    if(-not (Start-SandboxProcess $s @('--app-id',$appId))){throw 'Old GUI did not start'}
    $tree=Get-CtlResult $s @('tree')
    $pane=$tree.workspaces[0].sessions[0].id
    $marker='OLD_HOST_'+[guid]::NewGuid().ToString('N')
    $null=Send-Ctl $s @('session','type',("echo "+$marker+[char]13),'--target',$pane)
    for($i=0;$i -lt 100;$i++){
        if((Get-CtlResult $s @('session','text','--all','--target',$pane)) -like "*$marker*"){break}
        Start-Sleep -Milliseconds 100
    }
    if($i -eq 100){throw 'Old shell did not display the marker'}
    if(-not (Get-Process agwinterm-ptyhost -ErrorAction SilentlyContinue)){throw 'Old Rust host did not start'}
    $reply=Send-Ctl $s @('app','update')
    if($reply -notmatch '"ok":true' -or -not $s.Proc.WaitForExit(90000)){throw "Old GUI failed to hand off its update: $reply"}
    for($i=0;$i -lt 180;$i++){
        if((Test-Path $newHost) -and (Get-Process Agwinterm.Host -ErrorAction SilentlyContinue)){break}
        $failure=Get-ChildItem "$env:LOCALAPPDATA/agwinterm-upgrade-backups/*/upgrade-error.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
        # The old helper relaunches even when setup fails. Wait for it so the
        # fixture can clean up that process before leaving the disposable VM.
        if($failure -and (Get-Process Agwinterm.Win32 -ErrorAction SilentlyContinue | Where-Object Path -eq $exe)){
            throw (Get-Content -LiteralPath $failure.FullName -Raw)
        }
        Start-Sleep -Seconds 1
    }
    if($i -eq 180){throw 'Setup or relaunch failed'}
    if((Test-Path $oldHost) -or (Get-Process agwinterm-ptyhost -ErrorAction SilentlyContinue)){throw 'Legacy host still present'}
    $restored=Get-CtlResult $s @('tree')
    if($restored.workspaces[0].sessions[0].id -ne $pane){throw 'Pane identity changed'}
    if((Get-CtlResult $s @('session','text','--all','--target',$pane)) -notlike "*$marker*"){throw 'Saved terminal history missing'}
    'PASS upgrade replaced the legacy Rust host and restored the pane and history'
} finally {
    foreach($proc in @(Get-Process Agwinterm.Win32,Agwinterm.Host,agwinterm-ptyhost -ErrorAction SilentlyContinue)){
        if($proc.Path -in $exe,$newHost,$oldHost){$proc.Kill();$null=$proc.WaitForExit(10000)}
    }
}
