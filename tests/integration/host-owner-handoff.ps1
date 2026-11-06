param([Parameter(Mandatory)][string]$Exe,[Parameter(Mandatory)][string]$Ctl)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test HostOwnerHandoff -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
if ($env:AGWINTERM_HYPERV_UI -ne '1' -or -not (& "$PSScriptRoot/hyperv-ui-context.ps1")) {
    throw 'Default-terminal registration is permitted only in the disposable worker VM.'
}
. "$PSScriptRoot/../ui/lib.ps1"
$directory=Split-Path ([IO.Path]::GetFullPath($Exe))
$pipe='agwinterm-handoff-'+[guid]::NewGuid().ToString('N')
$s=$null
$child=$null
try {
    # Windows activates the release COM class. The app deliberately does
    # not register that class when a development identity is running.
    $dataRoot=[Environment]::GetFolderPath('LocalApplicationData')
    $profile=Join-Path $dataRoot 'agwinterm'
    if(Test-Path -LiteralPath $profile){throw 'Handoff requires a fresh release profile inside the disposable VM'}
    $null=New-Item -Type Directory $profile
    @('session-host = server','update-check = false','fresh-env = false') | Set-Content (Join-Path $profile 'agwinterm.conf')
    $s=[pscustomobject]@{Proc=$null;Exe=$Exe;Ctl=$Ctl;Pipe=$pipe;AppId='agwinterm';AppDir=$profile;Hwnd=[IntPtr]::Zero;Width=1000;Height=700}
    $env:AGWINTERM_PTYLOG=Join-Path $profile 'handoff.log'
    foreach($name in 'Google.Protobuf.dll','Agwinterm.Core.dll','Agwinterm.Pty.dll'){
        $null=[Reflection.Assembly]::LoadFrom((Join-Path $directory $name))
    }
    if(-not(Start-SandboxProcess $s @('--pipe',$pipe,'--app-id','agwinterm','--no-restore'))){throw 'Handoff UI did not start'}
    $initial=@((Get-CtlResult $s @('tree')).workspaces.sessions.id)
    if(-not $initial.Count){throw 'Initial hosted pane missing'}
    & "$PSScriptRoot/../../tools/defterm-register.ps1" -BuildDir $directory
    $marker='HANDOFF_'+[guid]::NewGuid().ToString('N').Substring(0,12)
    # A new normal console is the Windows activation trigger under test.
    $launch=[Diagnostics.ProcessStartInfo]::new("$env:SystemRoot/System32/cmd.exe","/d /q /k echo $marker")
    $launch.UseShellExecute=$true
    $child=[Diagnostics.Process]::Start($launch)
    $handed=$null
    for($i=0;$i -lt 150 -and -not $handed;$i++){
        foreach($candidate in @((Get-CtlResult $s @('tree')).workspaces.sessions.id)){
            if($candidate -and $candidate -notin $initial -and ([string](Get-CtlResult $s @('session','text','--target',$candidate))).Contains($marker)){
                $handed=$candidate;break
            }
        }
        if(-not $handed){Start-Sleep -Milliseconds 100}
    }
    if(-not $handed){
        Get-Process cmd,OpenConsole,conhost,Agwinterm.Win32 -ErrorAction SilentlyContinue | Select-Object Name,Id,SessionId | Out-Host
        if(Test-Path $env:AGWINTERM_PTYLOG){Get-Content $env:AGWINTERM_PTYLOG -Tail 50 | Out-Host}
        throw 'Windows did not hand the new console to the running test window'
    }
    $owner=[Agwinterm.Pty.PtyHostClient]::Connect($pipe+'-owner-v1',3000)
    try {
        $hosted=@($owner.List() | ForEach-Object Id)
        if($initial[0] -notin $hosted -or $handed -in $hosted){throw 'Handoff did not retain its direct local owner'}
    } finally {$owner.Dispose()}
    $typed='HANDOFF_INPUT_'+[guid]::NewGuid().ToString('N').Substring(0,8)
    $null=Get-CtlResult $s @('session','type',("echo $typed"+[char]13),'--target',$handed)
    for($i=0;$i -lt 100;$i++){
        $text=[string](Get-CtlResult $s @('session','text','--target',$handed))
        if(($text -split "`r?`n") -contains $typed){break}
        Start-Sleep -Milliseconds 100
    }
    if(($text -split "`r?`n") -notcontains $typed){throw 'Handoff child did not receive input'}
    'PASS real Windows default-terminal handoff retains a direct Ghostty owner and interactive child'
} finally {
    if($child){try{if(-not $child.HasExited){$child.Kill();$null=$child.WaitForExit(5000)}}finally{$child.Dispose()}}
    if($s){Stop-Sandbox $s}
    if($s -and [Agwinterm.Pty.PtyHostClient]::IsRunning($pipe+'-owner-v1')){
        $cleanup=[Agwinterm.Pty.PtyHostClient]::Connect($pipe+'-owner-v1',2000)
        try{$cleanup.Shutdown()}finally{$cleanup.Dispose()}
    }
    & "$PSScriptRoot/../../tools/defterm-restore-conhost.ps1"
}
