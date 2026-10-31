param(
    [string]$Exe = "$PSScriptRoot/../../src/Agwinterm.Win32/bin/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe",
    [string]$Ctl = "$PSScriptRoot/../../src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe"
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$Ctl = (Resolve-Path $Ctl).Path
$identity = 'agwinterm-web-parity-' + [guid]::NewGuid().ToString('N')
$directory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $identity
New-Item -ItemType Directory -Path $directory | Out-Null
@{ default='Test'; profiles=@(@{name='Test'; command='cmd.exe'; args=@('/d','/q'); cwd=$directory}) } | ConvertTo-Json -Depth 5 | Set-Content "$directory/profiles.json"
"session-host = in-process`nemulator-core = managed" | Set-Content "$directory/agwinterm.conf"
'<!doctype html><title>Pane page</title><textarea>retained draft</textarea>' | Set-Content "$directory/page.html"
$saved = @{}
foreach ($key in @('AGWINTERM_APP_ID','AGWINTERM_PIPE','AGWINTERM_SESSION_ID','AGWINTERM_PANE_ID')) {
    $saved[$key]=[Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key,$null)
}
$process = $null
$server = $null
function Call([string[]]$Words) {
    $reply = & $Ctl @Words --pipe $identity --json 2>$null
    $script:ctlExitCode = $LASTEXITCODE
    if (-not $reply) { throw "No response: $Words" }
    $reply | ConvertFrom-Json
}
function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}
function Call-Raw($Request) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $identity, [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000)
        $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true)
        $reader = [IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
        try {
            $writer.WriteLine(($Request | ConvertTo-Json -Depth 8 -Compress)); $writer.Flush()
            $reply = $reader.ReadLineAsync()
            if (-not $reply.Wait(5000)) { throw 'Raw control response timed out' }
            $reply.Result | ConvertFrom-Json
        } finally { $writer.Dispose(); $reader.Dispose() }
    } finally { $pipe.Dispose() }
}
function Session {
    @((Call @('tree','--window',$script:mainWindow)).result.workspaces | ForEach-Object sessions) | Where-Object id -eq $script:owner
}
function Pages { @((Session).htmlOverlays) }
function Wait-Pages([int]$Count) {
    for ($i=0; $i -lt 150; $i++) {
        $pages = Pages
        if ($pages.Count -eq $Count -and @($pages | Where-Object state -ne 'loaded').Count -eq 0) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Pages did not load: $($pages | ConvertTo-Json -Depth 6 -Compress)"
}
try {
    $process = Start-Process $Exe -ArgumentList @('--app-id',$identity,'--pipe',$identity,'--no-restore') -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 100
        try { if ((Call @('ping')).ok) { break } } catch { }
    }
    $script:owner = (Call @('tree')).result.workspaces[0].sessions[0].id
    $script:mainWindow = @((Call @('window','list')).result.windows | Where-Object open)[0].id
    Require ((Call @('session','split','on','--target',$owner)).ok) 'split enables'
    $left = Call @('session','overlay','open','--html',"$directory/page.html",'--js','--cwd',$directory,'--pane','left','--target',$owner)
    $right = Call @('session','overlay','open','--html',"$directory/page.html",'--js','--cwd',$directory,'--pane','right','--target',$owner)
    Require ($left.ok -and $right.ok -and $left.result.pageID -ne $right.result.pageID) 'two pane pages have independent identities'
    Wait-Pages 2
    $pages = Pages
    Require (@($pages | Where-Object visible).Count -eq 2) 'both pane pages are visible'
    Require ((Call @('font','inc','--target',$owner)).ok) 'host font command changes web zoom'
    Require (@((Pages) | Where-Object { [Math]::Abs($_.zoom - 1.1) -gt 0.001 }).Count -eq 0) 'web zoom is shared by both live pane pages'
    Require ((Call @('browser','links','overlay75')).ok) 'terminal browser-link policy can be changed'
    Require ((Call @('tree')).result.linkOpenMode -eq 'overlay75') 'tree exposes app-wide link policy'
    $a = $pages | Where-Object pane -eq 'left'; $b = $pages | Where-Object pane -eq 'right'
    Require ($a.geometry.x + $a.geometry.width -le $b.geometry.x) 'pane web rectangles do not overlap'
    Require (-not (Call @('session','overlay','open','cmd /d /q','--pane','left','--target',$owner)).ok) 'program refuses an occupied web pane'
    Require ((Call @('session','swap','--target',$owner)).ok) 'pane swap succeeds'
    Require (((Pages) | Where-Object id -eq $left.result.pageID).pane -eq 'right') 'web identity follows swapped pane'
    Require ((Call @('session','overlay','reload','--current','--pane','right','--target',$owner)).ok) 'current reload targets a pane'
    Wait-Pages 2
    Require ((Call @('session','overlay','hide','--pane','right','--target',$owner)).ok) 'pane hide succeeds'
    Require (((Pages) | Where-Object id -eq $left.result.pageID).hidden) 'hide changes only addressed page'
    Require ((Call @('session','overlay','show','--pane','right','--target',$owner)).ok) 'pane show succeeds'
    $cover = Call @('session','overlay','open','--html',"$directory/page.html",'--target',$owner)
    Require $cover.ok 'session-wide page coexists with pane slots'
    Wait-Pages 3
    Require (@((Pages) | Where-Object visible).Count -eq 1) 'session-wide page covers both pane pages'
    Require ((Call @('session','overlay','close','--target',$owner)).ok) 'session-wide close succeeds'
    Require (@((Pages) | Where-Object visible).Count -eq 2) 'closing cover restores pane pages'
    Require ((Call @('session','split','off','--target',$owner)).ok) 'split collapse succeeds'
    Require ((Pages).Count -eq 2 -and @((Pages) | Where-Object visible).Count -eq 1) 'hidden split retains its page without presentation'
    Require ((Call @('session','split','on','--target',$owner)).ok) 'split restores'
    Require (@((Pages) | Where-Object visible).Count -eq 2) 'restored split shows both retained pages'
    $distinctPane = (Session).paneIds | Where-Object { $_ -ne $owner } | Select-Object -First 1
    Require ((Call @('session','overlay','reload','--target',$distinctPane)).ok) 'exact unambiguous pane target reload succeeds'
    '<!doctype html><title>Pane destination</title>' | Set-Content "$directory/next.html"
    '<!doctype html><title>Pane original</title><script>addEventListener("load",()=>setTimeout(()=>{if(!sessionStorage.historyVisited){sessionStorage.historyVisited=1;location.href="next.html"}},100))</script>' | Set-Content "$directory/page.html"
    [void](Call @('session','overlay','reload','--pane','left','--target',$owner))
    function Wait-PaneUrl([string]$Suffix) {
        for ($i=0; $i -lt 100; $i++) {
            $p = (Pages) | Where-Object pane -eq 'left'
            if ($p.state -eq 'loaded' -and $p.currentUrl.EndsWith($Suffix)) { return }
            Start-Sleep -Milliseconds 100
        }
        throw "Pane did not reach $Suffix"
    }
    Wait-PaneUrl '/next.html'
    Require ((Call @('session','overlay','navigate','back','--pane','left','--target',$owner)).ok) 'pane history back succeeds'
    Wait-PaneUrl '/page.html'
    Require ((Call @('session','overlay','navigate','forward','--pane','left','--target',$owner)).ok) 'pane history forward succeeds'
    Wait-PaneUrl '/next.html'
    [void](Call @('session','overlay','reload','--current','--pane','left','--target',$owner))
    Wait-PaneUrl '/next.html'
    Require ((Call @('session','overlay','reload','--pane','left','--target',$owner)).ok) 'bare pane reload restores original source'
    Wait-PaneUrl '/page.html'
    '<!doctype html><title>Pane page</title><textarea>retained draft</textarea>' | Set-Content "$directory/page.html"
    Require ((Call @('session','overlay','close','--pane','left','--target',$owner)).ok) 'left close succeeds'
    Require ((Pages).Count -eq 1) 'one page survives sibling close'
    Require ((Call @('session','overlay','open','cmd /d /q','--pane','left','--target',$owner)).ok) 'program and web coexist in separate panes'
    Require ((Call @('session','overlay','close','--pane','left','--target',$owner)).ok) 'program close leaves web intact'
    Require ((Pages).Count -eq 1) 'web remains after sibling program closes'
    Require ((Call @('session','overlay','close','--pane','right','--target',$owner)).ok) 'last page closes'
    . "$PSScriptRoot/web-overlay-parity-content.ps1"
    Require ((Call @('window','close')).ok) 'private test window closes'
    Require ($process.WaitForExit(10000)) 'private test process exits'
    $process = $null
} finally {
    if ($server -and -not $server.HasExited) { $server.Kill(); $server.WaitForExit() }
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) }
    Write-Host "Private artifacts: $directory"
}
