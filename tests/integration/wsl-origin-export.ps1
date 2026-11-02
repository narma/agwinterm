param([string]$Distro='Ubuntu', [string]$LinuxUser='alzo', [Parameter(Mandatory)][string]$BuildDirectory,
    [string]$Destination, [string]$AgentSocket, [string]$MacAgentSocket, [string]$MacApp, [string]$PeerHost='wsl')
$ErrorActionPreference='Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslOriginExport -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$PSNativeCommandUseErrorActionPreference=$false
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$bin=Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64'
$exe=Join-Path $bin 'Agwinterm.Win32.exe'
$ctl=Join-Path $bin 'agwintermctl.exe'
$id='agwinterm-origin-test-'+[guid]::NewGuid().ToString('N')
$state=Join-Path $env:LOCALAPPDATA $id
$ui=$null
$linuxRoot=$null
function Ctl([string[]]$Command) {
    $raw=& $ctl @Command --pipe $id --json --timeout 40
    if ($LASTEXITCODE -ne 0) { throw "Control failed: $Command $raw" }
    $reply=($raw -join "`n") | ConvertFrom-Json -Depth 100
    if (-not $reply.ok) { throw "Control refused: $raw" }
    return $reply.result
}
function Linux([string[]]$Command) {
    $raw=& wsl.exe -d $Distro -u $LinuxUser --exec @Command
    if ($LASTEXITCODE -ne 0) { throw "Linux failed: $Command $raw" }
    return ($raw -join "`n")
}
function Start-Ui {
    $script:ui=Start-Process $exe -ArgumentList @('--app-id',$id,'--pipe',$id,'--default-session-host','server-rust') -WindowStyle Hidden -PassThru
    for ($i=0;$i -lt 80;$i++) {
        Start-Sleep -Milliseconds 200
        $null=& $ctl ping --pipe $id --json 2>$null
        if ($LASTEXITCODE -eq 0) {
            $windows=(Ctl @('window','list')).windows
            if (@($windows | Where-Object open).Count -gt 0) { return }
        }
    }
    throw 'Origin UI did not start'
}
function Live([string]$pane) {
    foreach ($w in (Ctl @('window','list')).windows | Where-Object open) {
        foreach ($s in (Ctl @('tree','--window',$w.id)).workspaces.sessions) {
            if ($s.liveSessions -and $s.liveSessions.PSObject.Properties[$pane]) { return $s.liveSessions.$pane }
        }
    }
    return $null
}
function Await-Live([string]$pane) {
    for ($i=0;$i -lt 100;$i++) {
        $value=Live $pane
        if ($value.recovery -in @('fresh-started','zmx-reattached','host-adopted') -and $value.role -ne 'unknown') { return $value }
        Start-Sleep -Milliseconds 200
    }
    throw "Origin pane unavailable: $($value | ConvertTo-Json -Depth 12)"
}
function Driver([string]$Mode) {
    $result=Linux @('python3',$script:driver,$Mode,"$linuxRoot/run.json")
    Write-Host $result
}
try {
    $null=New-Item -ItemType Directory -Path $state
    @('session-host = server-rust','restore-commands = true','restore-buffer = true') | Set-Content (Join-Path $state 'agwinterm.conf')
    @{default='Origin';profiles=@(@{name='Origin';command='wsl.exe';args=@('-d',$Distro,'--user',$LinuxUser,'--exec','bash','--noprofile','--norc');zmx=@{enabled=$true;executable="$BuildDirectory/zmx/zig-out/bin/zmx"}})} |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $state 'profiles.json')
    Start-Ui
    $window=[string]((Ctl @('window','list')).windows | Where-Object open)[0].id
    $pane=[string](Ctl @('session','new','--profile','Origin','--window',$window,'--no-select'))
    $live=Await-Live $pane
    $linuxRoot=Linux @('mktemp','-d','/tmp/aw-stage3-origin-XXXXXXXX')
    $helper=Linux @('wslpath','-u',(Join-Path $repo 'artifacts/wsl-hook/agwinterm-hook-linux-x64'))
    $script:driver=Linux @('wslpath','-u',(Join-Path $PSScriptRoot 'zmx/origin-acceptance.py'))
    $linuxCtl=Linux @('wslpath','-u',$ctl)
    $config=@{distribution=$Distro;user=$LinuxUser;origin=$id;executable=$live.endpoint.Executable;socketDirectory=$live.endpoint.SocketDirectory;cli=$linuxCtl;pipe=$id}
    $raw=($config | ConvertTo-Json -Compress) | & wsl.exe -d $Distro -u $LinuxUser --exec $helper --zmx-export-provision "$linuxRoot/endpoint"
    if ($LASTEXITCODE -ne 0) { throw "Provision refused: $raw" }
    $foreign=& wsl.exe -d $Distro -u root --exec "$linuxRoot/endpoint/agtermctl" zmx tree --json
    if ($LASTEXITCODE -eq 0 -or ($foreign -join '') -notlike '*owned by this Linux user*') { throw "Foreign Linux user was not refused: $foreign" }
    Write-Host 'PASS Linux UID isolation (root cannot use another user export)'
    $run=@{root=$linuxRoot;helper=$helper;config=$config;pane=$pane;window=$window;destination=$Destination;agentSocket=$AgentSocket;macAgentSocket=$MacAgentSocket;macApp=$MacApp;peerHost=$PeerHost}
    ($run | ConvertTo-Json -Depth 10 -Compress) | & wsl.exe -d $Distro -u $LinuxUser --exec python3 -c 'import pathlib,sys; p=pathlib.Path(sys.argv[1]); p.write_text(sys.stdin.read()); p.chmod(0o600)' "$linuxRoot/run.json"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot save isolated test configuration' }
    Driver 'local'
    if ($Destination) { Driver 'peer-single' }
    $right=[string](Ctl @('session','split','on','--target',$pane,'--window',$window))
    $null=Await-Live $right
    Driver 'split'
    if ($Destination) { Driver 'peer-split' }
    $ui.Kill(); $ui.WaitForExit()
    Driver 'ui-down'
    Start-Ui
    $null=Await-Live $pane
    $null=Await-Live $right
    Driver 'restored'
    if ($Destination) { Driver 'peer-restart' }
    $null=Ctl @('zmx','terminate','--target',$right,'--window',$window,'--force')
    Driver 'partial'
    if ($Destination) { Driver 'peer-stop' }
    $null=Ctl @('zmx','terminate','--target',$pane,'--window',$window,'--force')
    Driver 'terminated'
    Write-Host "PASS origin export acceptance; evidence=$linuxRoot"
} finally {
    if ($linuxRoot -and $Destination) { try { Driver 'peer-stop' } catch { Write-Warning $_ } }
    if ($ui -and -not $ui.HasExited) {
        try { foreach ($w in (Ctl @('window','list')).windows | Where-Object open) { $null=Ctl @('window','close',$w.id) } } catch { Write-Warning $_ }
        if (-not $ui.WaitForExit(5000)) { $ui.Kill(); $ui.WaitForExit() }
    }
}
