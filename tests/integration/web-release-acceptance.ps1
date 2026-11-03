param([ValidateSet('Inventory','Installer','Portable')][string]$Mode='Inventory',
    [ValidateSet('Present','Absent')][string]$Runtime='Present', [string]$Installer, [string]$Portable)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WebReleaseAcceptance -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $repo 'artifacts/web-release-acceptance'
$null=New-Item -ItemType Directory -Force -Path $output
$runtimeRoots=@("${env:ProgramFiles(x86)}/Microsoft/EdgeWebView/Application", "$env:ProgramFiles/Microsoft/EdgeWebView/Application")
$report=[ordered]@{
    commit=$env:AGWINTERM_TEST_COMMIT
    os=Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber
    identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    network=@(Get-NetAdapter -ErrorAction SilentlyContinue | Select-Object Name,Status,InterfaceDescription)
    runtimes=@(foreach($root in $runtimeRoots){Get-ChildItem -LiteralPath $root -Filter msedgewebview2.exe -Recurse -ErrorAction SilentlyContinue | Select-Object FullName,@{n='Version';e={$_.VersionInfo.FileVersion}}})
    imeFiles=@(Get-ChildItem "$env:windir/System32/IME" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    languages=& powershell.exe -NoProfile -Command 'Get-WinUserLanguageList | Select-Object LanguageTag,InputMethodTips | ConvertTo-Json -Depth 5'
    capabilities=& powershell.exe -NoProfile -Command 'Get-WindowsCapability -Online | Where-Object Name -match "Language.(Basic|OCR|Handwriting).*(ja-JP|zh-CN|ko-KR)" | Select-Object Name,State | ConvertTo-Json'
    installed=@(Get-ItemProperty 'HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall/*','HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/*' -ErrorAction SilentlyContinue | Where-Object DisplayName -Match 'agwinterm|WebView' | Select-Object DisplayName,DisplayVersion)
}
$report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'inventory.json') -Encoding utf8
if($Mode -eq 'Inventory') {
    Write-Host 'PASS clean guest inventory exported; no application acceptance is claimed by inventory alone'
    return
}
function Require([bool]$Condition,[string]$Message) {
    if(-not $Condition){throw $Message}
    Write-Host "PASS $Message"
}
$result=[ordered]@{mode=$Mode;runtime=$Runtime;outcome='FAIL';checks=@();error=$null}
$process=$null
try {
    Require ($env:AGWINTERM_HYPERV_UI -eq '1' -and (& "$PSScriptRoot/hyperv-ui-context.ps1")) 'release acceptance is confined to its disposable Hyper-V guest'
    Require ($report.network.Count -eq 0) 'offline guest has no network adapter'
    Require (@($report.installed | Where-Object DisplayName -match 'agwinterm').Count -eq 0) 'no preceding Agwinterm installation'
    foreach($key in @('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER','AGWINTERM_TEST_WEBVIEW2','AGWINTERM_APP_ID','AGWINTERM_PIPE','AGWINTERM_SESSION_ID','AGWINTERM_PANE_ID')) {
        [Environment]::SetEnvironmentVariable($key,$null)
    }
    if($Runtime -eq 'Absent') {
        # Only a disposable clone is modified; the prepared image and host are immutable.
        foreach($runtimeFile in $report.runtimes) {
            $setup=Join-Path (Split-Path $runtimeFile.FullName) 'Installer/setup.exe'
            Require (Test-Path -LiteralPath $setup) 'runtime supplies its own uninstaller'
            $uninstall=Start-Process $setup -ArgumentList @('--uninstall','--msedgewebview','--system-level','--force-uninstall','--verbose-logging') -WindowStyle Hidden -PassThru
            if(-not $uninstall.WaitForExit(90000)){throw 'Runtime uninstaller timed out'}
            $result.runtimeUninstallExit=$uninstall.ExitCode
        }
        # Windows can refuse removal of its inbox runtime (setup exit 93).
        # Construct an actual missing-runtime fixture only in this verified,
        # disposable guest. Never change the worker's base image or host.
        foreach($root in $runtimeRoots) {
            $absolute=[IO.Path]::GetFullPath($root)
            $expected=Join-Path ([IO.Path]::GetFullPath((Split-Path (Split-Path (Split-Path $root))))) 'Microsoft/EdgeWebView/Application'
            Require ($absolute -eq [IO.Path]::GetFullPath($expected) -and $absolute -match '\\Microsoft\\EdgeWebView\\Application$') 'runtime removal target is the exact guest application directory'
            if(Test-Path -LiteralPath $absolute){
                $runtimeProcesses=@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($absolute+'\',[StringComparison]::OrdinalIgnoreCase) })
                $result.runtimeProcessesStopped=@($runtimeProcesses | Select-Object ProcessId,ExecutablePath)
                foreach($runtimeProcess in $runtimeProcesses){Stop-Process -Id $runtimeProcess.ProcessId -Force -ErrorAction SilentlyContinue}
                Remove-Item -LiteralPath $absolute -Recurse -Force
            }
        }
        $remaining=@(foreach($root in $runtimeRoots){Get-ChildItem -LiteralPath $root -Filter msedgewebview2.exe -Recurse -ErrorAction SilentlyContinue})
        Require ($remaining.Count -eq 0) 'WebView2 executable is physically absent; no simulated missing-runtime override'
        # Remove the product's documented Evergreen discovery entries as an
        # uninstall would. A stale registration describes a broken installation,
        # not the clean missing-runtime case this fixture is intended to test.
        $runtimeRegistration=@(
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
            'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
            'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')
        foreach($key in $runtimeRegistration){if(Test-Path -LiteralPath $key){Remove-Item -LiteralPath $key -Recurse -Force}}
        Require (@($runtimeRegistration | Where-Object {Test-Path -LiteralPath $_}).Count -eq 0) 'Evergreen runtime discovery registrations are absent'
    } else { Require ($report.runtimes.Count -gt 0) 'prepared guest has a real installed WebView2 runtime' }
    $package=if($Mode -eq 'Installer'){$Installer}else{$Portable}
    Require ([bool]$package -and (Test-Path -LiteralPath $package)) 'candidate package supplied'
    $result.packageSha256=(Get-FileHash -LiteralPath $package).Hash
    $installRoot=Join-Path $env:LOCALAPPDATA 'Programs/agwinterm'
    Require (-not (Test-Path -LiteralPath $installRoot)) 'application install directory starts absent'
    if($Mode -eq 'Installer') {
        $setupCopy=Join-Path $output 'candidate-setup.exe'
        Copy-Item -LiteralPath $package -Destination $setupCopy
        $setupProcess=Start-Process $setupCopy -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=`"$output/install.log`"") -WindowStyle Hidden -PassThru
        if(-not $setupProcess.WaitForExit(120000)){throw 'Candidate installation timed out'}
        Require ($setupProcess.ExitCode -eq 0) 'installer completed offline'
        $exe=Join-Path $installRoot 'Agwinterm.Win32.exe'
        $ctl=Join-Path $installRoot 'agwintermctl.exe'
        Remove-Item -LiteralPath $setupCopy
    } else {
        $bare=Join-Path $env:LOCALAPPDATA 'stage4-bare-portable'
        $null=New-Item -ItemType Directory -Path $bare
        $exe=Join-Path $bare 'portable.exe'
        Copy-Item -LiteralPath $package -Destination $exe
        Require (@(Get-ChildItem -LiteralPath $bare).Count -eq 1) 'portable directory contains exactly one EXE and no SDK/loader sidecars'
        $bundle=Join-Path $env:LOCALAPPDATA 'stage4-portable-bundle'
        Require (-not(Test-Path -LiteralPath $bundle)) 'portable bundle cache starts absent'
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$bundle
        $tools=Join-Path $env:LOCALAPPDATA 'stage4-extracted-tools'
        $extraction=Start-Process $exe -ArgumentList @('--extract-tools',"`"$tools`"") -WindowStyle Hidden -PassThru
        if(-not $extraction.WaitForExit(90000)){throw 'Portable tool extraction timed out'}
        Require ($extraction.ExitCode -eq 0) 'portable extracts its own CLI offline'
        $ctl=@(Get-ChildItem -LiteralPath $tools -Filter agwintermctl.exe -Recurse)[0].FullName
    }
    $result.executableSha256=(Get-FileHash -LiteralPath $exe).Hash
    $result.cliSha256=(Get-FileHash -LiteralPath $ctl).Hash
    if($Runtime -eq 'Present') {
        # The existing browser gate covers editing/focus, real submit/cancel,
        # cleanup and restart against these exact published binaries.
        & "$PSScriptRoot/web-overlay-host.ps1" -Exe $exe -Ctl $ctl -Cookbook
        Require ($LASTEXITCODE -eq 0) 'published browser host acceptance completed offline'
        $result.checks+= 'published-web-host'
    }
    $identity='agwinterm-release-'+[guid]::NewGuid().ToString('N')
    $data=Join-Path $env:LOCALAPPDATA $identity
    $null=New-Item -ItemType Directory -Path $data
    @{default='Release';profiles=@(@{name='Release';command='cmd.exe';args=@('/d','/q','/k','echo AW_RELEASE_TERMINAL');cwd=$data})} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $data 'profiles.json')
    $source=Join-Path $data 'page.html'
    '<!doctype html><meta charset="utf-8"><title>Offline release</title><textarea>offline</textarea>' | Set-Content $source
    function Call([string[]]$Words) { $raw=& $ctl @Words --pipe $identity --timeout 5 --json 2>$null; if(-not $raw){throw "No CLI reply: $Words"}; return ($raw | ConvertFrom-Json) }
    function Start-App {
        $script:process=Start-Process $exe -ArgumentList @('--app-id',$identity,'--pipe',$identity,'--default-session-host','server-rust') -WindowStyle Hidden -PassThru
        $deadline=[DateTime]::UtcNow.AddSeconds(30)
        do {
            if($script:process.HasExited){throw "Published application exited during startup: $($script:process.ExitCode)"}
            try{if((Call @('session','text')).result -match 'AW_RELEASE_TERMINAL'){return}}catch{$lastStartupError=$_.ToString()}
            Start-Sleep -Milliseconds 200
        } while([DateTime]::UtcNow -lt $deadline)
        throw "Published terminal failed to become ready: $lastStartupError"
    }
    function Stop-App {
        Require (-not $script:process.HasExited) 'published process is alive before normal close'
        $null=& $ctl window close --pipe $identity --json 2>&1
        if(-not $script:process.WaitForExit(15000)){throw 'Published application did not quit'}
        Require ($script:process.ExitCode -eq 0) 'published window closes normally'
        $global:LASTEXITCODE=0
        $script:process=$null
    }
    Start-App
    $owner=(Call @('tree')).result.workspaces[0].sessions[0].id
    Require ((Call @('session','text','--target',$owner)).result -match 'AW_RELEASE_TERMINAL') 'published terminal runs offline'
    $page=Call @('session','overlay','open','--html',$source,'--target',$owner)
    if($Runtime -eq 'Absent') {
        Require (-not $page.ok -and $page.error -match 'WebView2.*install') 'actual missing runtime gives explicit install guidance'
    } else {
        Require $page.ok 'published local page opens with installed runtime'
        for($i=0;$i -lt 100;$i++){$localPage=(Call @('tree')).result.workspaces[0].sessions[0].htmlOverlays[0];if($localPage.state -ne 'loading'){break};Start-Sleep -Milliseconds 100}
        Require ($localPage.state -eq 'loaded') 'published local page finishes loading offline'
        $loaders=@($process.Modules | Where-Object ModuleName -eq 'WebView2Loader.dll')
        Require ($loaders.Count -eq 1) 'published process loads one native WebView2 loader'
        $result.nativeLoader=$loaders[0].FileName
        $loaderRoot=if($Mode -eq 'Portable'){$bundle}else{$installRoot}
        Require ($result.nativeLoader.StartsWith($loaderRoot+'\',[StringComparison]::OrdinalIgnoreCase)) 'native loader comes from the published install or fresh portable bundle, not the test SDK'
        $null=Call @('session','overlay','close','--target',$owner)
        $url=Call @('session','overlay','open','--url','https://example.com','--target',$owner)
        Require $url.ok 'offline URL reserves only a page'
        for($i=0;$i -lt 100;$i++){ $row=(Call @('tree')).result.workspaces[0].sessions[0]; if($row.htmlOverlays[0].state -eq 'failed'){break};Start-Sleep -Milliseconds 200 }
        Require ($row.htmlOverlays[0].state -eq 'failed') 'offline URL fails within its page'
        $null=Call @('session','overlay','close','--target',$owner)
    }
    Require ((Call @('session','overlay','open','cmd /d /q /k echo AW_RELEASE_PROGRAM','--target',$owner)).ok) 'program overlay works offline'
    $null=Call @('session','overlay','hide','--target',$owner)
    $before=(Call @('tree')).result.workspaces[0].sessions[0].overlayDetails | ConvertTo-Json -Depth 8 -Compress
    $refused=Call @('session','overlay','open','--html',$source,'--target',$owner)
    Require (-not $refused.ok) 'page request refuses over an existing program'
    $after=(Call @('tree')).result.workspaces[0].sessions[0].overlayDetails | ConvertTo-Json -Depth 8 -Compress
    Require ($before -eq $after) 'refused page preserves the original program'
    $null=Call @('session','overlay','close','--target',$owner)
    Require ((Call @('session','text','--target',$owner)).result -match 'AW_RELEASE_TERMINAL') 'base terminal marker survives page and program operations'
    if($Runtime -eq 'Present'){$null=Call @('session','overlay','open','--html',$source,'--target',$owner)}
    Stop-App
    Start-App
    $restored=(Call @('tree')).result.workspaces[0].sessions[0]
    Require ($restored.id -eq $owner -and -not $restored.htmlOverlays) 'restart restores the terminal identity without ephemeral pages'
    Stop-App
    $result.checks+='terminal-program-page-restart'
    $result.outcome='PASS'
} catch { $result.error=$_.ToString(); throw }
finally {
    if($process -and -not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
    if($identity){
        # Rust hosts deliberately survive a normal window close. End only the
        # private fixture host after testing restoration, so the VM runner's
        # process-tree wait can finish without changing application semantics.
        $pipePattern='--pipe\s+"?'+[regex]::Escape($identity+'-rust')+'"?(\s|$)'
        $hosts=@(Get-CimInstance Win32_Process -Filter "Name = 'agwinterm-ptyhost.exe'" | Where-Object CommandLine -match $pipePattern)
        foreach($hostProcess in $hosts){
            $owned=Get-Process -Id $hostProcess.ProcessId -ErrorAction SilentlyContinue
            if($owned){$owned.Kill($true);$null=$owned.WaitForExit(10000)}
        }
        $result.cleanedPrivateHosts=@($hosts | ForEach-Object ProcessId)
    }
    $result | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'acceptance.json') -Encoding utf8
}
