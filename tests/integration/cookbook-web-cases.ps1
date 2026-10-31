# Sourced by web-overlay-host.ps1 inside its disposable app/pipe.
$fixtures = [IO.Path]::GetFullPath("$PSScriptRoot/../fixtures/cookbook")
$savedCookbook = @{}
foreach ($key in @('AGWINTERMCTL','AGWINTERM_PIPE','AGWINTERM_SESSION_ID')) {
    $savedCookbook[$key] = [Environment]::GetEnvironmentVariable($key)
}
$env:AGWINTERMCTL = $Ctl
$env:AGWINTERM_PIPE = $identity
$env:AGWINTERM_SESSION_ID = $owner
$dashboard = $null
function Web-Element([string]$Name) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([WebGateWindow]::Find($process.Id))
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    for ($attempt=0; $attempt -lt 100; $attempt++) {
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 100
    }
    Write-Host ((Call @('tree')).result | ConvertTo-Json -Depth 10)
    $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { Write-Host "UIA $($_.Current.ControlType.ProgrammaticName) $($_.Current.Name)" }
    throw "Web element unavailable: $Name"
}
function Web-Invoke([string]$Name) {
    (Web-Element $Name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Wait-WebUrl([string]$Expected) {
    for ($i=0; $i -lt 100; $i++) {
        $node = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
        $page = @($node.htmlOverlays)[0]
        if ($page.state -eq 'loaded' -and $page.currentUrl -eq $Expected) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Page did not reach $Expected : $($page | ConvertTo-Json -Compress)"
}
try {
    if (-not $NavigationOnly) {
    $declarative = Join-Path $directory 'declarative.html'
    '<!doctype html><p>Nested local content</p><button type="button" data-agterm="session.rename" data-agterm-args=''{"name":"UNSAFE FRAME COMMAND"}''>Frame command</button><script>document.body.textContent="UNSAFE FRAME SCRIPT";</script>' | Set-Content "$directory/declarative-frame.html"
    @'
<!doctype html><title>Declarative commands</title>
<script>document.title='UNEXPECTED SCRIPT';</script>
<form data-agterm="session.rename" data-agterm-into="#answer">
<input name="name" value="Declarative owner"><button type="submit">Rename through form</button>
</form><pre id="answer"></pre>
<iframe src="declarative-frame.html" title="Local child frame"></iframe>
'@ | Set-Content $declarative
    Require ((Call @('session','overlay','open','--html',$declarative,'--cwd',$directory,'--target',$owner)).ok) 'declarative page opens with JavaScript disabled'
    Web-Invoke 'Rename through form'
    for ($i=0; $i -lt 50; $i++) {
        $declarativeOwner = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0]
        if ($declarativeOwner.name -eq 'Declarative owner') { break }
        Start-Sleep -Milliseconds 100
    }
    Require ($declarativeOwner.name -eq 'Declarative owner') 'declarative form routes named fields to its owning session'
    Require ($declarativeOwner.htmlOverlays[0].title -eq 'Declarative commands') 'declarative controls do not enable document JavaScript'
    [void](Web-Element 'Nested local content')
    Web-Invoke 'Frame command'
    Require (@((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].name -eq 'Declarative owner') 'local child frame renders without document scripts or a declarative bridge'
    [void](Call @('session','overlay','close','--target',$owner))
    foreach ($design in @('editorial','brief','classic','custom')) {
        $pageId = & py -3 "$fixtures/show.py" "$fixtures/html-doc-overlay/assets/$design.html"
        Require ($LASTEXITCODE -eq 0 -and $pageId) "cookbook $design template loads through adapter"
        $again = & py -3 "$fixtures/show.py" "$fixtures/html-doc-overlay/assets/$design.html"
        Require ($LASTEXITCODE -eq 0 -and $again -eq $pageId) "cookbook $design reload preserves page ID"
    }
    [void](Call @('session','overlay','close','--target',$owner))
    $backgroundOwner = (Call @('session','new','--name','document-background','--no-select')).result
    & py -3 "$fixtures/show.py" "$fixtures/html-doc-overlay/assets/brief.html" --target $backgroundOwner
    Require ($LASTEXITCODE -eq 0) 'cookbook adapter opens a background owner'
    $tree = (Call @('tree')).result
    $background = @($tree.workspaces.sessions | Where-Object id -eq $backgroundOwner)[0]
    $foreground = @($tree.workspaces.sessions | Where-Object id -eq $owner)[0]
    Require (-not $background.active -and -not $background.htmlOverlays[0].visible -and $foreground.active) 'background readback preserves selected owner and effective visibility'
    [void](Call @('session','close','--target',$backgroundOwner))
    $closedPort = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $closedPort.Start(); $unusedPort = $closedPort.LocalEndpoint.Port; $closedPort.Stop()
    Require ((Call @('session','overlay','open','--url',"http://127.0.0.1:$unusedPort/",'--target',$owner)).ok) 'unreachable page reserves an asynchronous load'
    for ($i=0; $i -lt 100; $i++) {
        $failure = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
        if ($failure.state -eq 'failed') { break }
        Start-Sleep -Milliseconds 100
    }
    Require ($failure.state -eq 'failed' -and $failure.error) 'load failure exposes a bounded error through tree'
    [void](Call @('session','overlay','close','--target',$owner))
    Require ((Call @('session','overlay','open','cmd /d /q /k','--target',$owner)).ok) 'cookbook refusal test opens program overlay'
    [void](Call @('session','overlay','hide','--target',$owner))
    & py -3 "$fixtures/show.py" "$fixtures/html-doc-overlay/assets/brief.html" 2>"$directory/cookbook-refusal.txt"
    Require ($LASTEXITCODE -eq 1) 'cookbook adapter refuses hidden program overlay'
    [void](Call @('session','overlay','close','--target',$owner))

    $document = Join-Path $directory 'review.md'
    "# Review fixture`n`nA paragraph to annotate." | Set-Content -Encoding utf8 $document
    $backup = Join-Path $directory 'review-output.txt'
    $reviewScript = "$fixtures/annotate-document/scripts/webrev.py"
    & py -3 $reviewScript review $document --fit --output $backup --timeout 0.1 2>"$directory/review-timeout.txt"
    Require ($LASTEXITCODE -eq 1) 'review timeout returns pending without dismissing page'
    $pending = (Get-Content $backup -Raw).Trim()
    Require ($pending -match '^pending [a-f0-9]+$') 'review timeout backup retains page ID'
    $reviewId = $pending.Substring(8)
    $reviewPage = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
    Require ($reviewPage.id -eq $reviewId) 'timed-out review remains recoverable'
    $automation = @'
<script>
setTimeout(() => {
 document.getElementById('file-btn').click();
 document.querySelector('.editor textarea').value='File annotation';
 document.querySelector('[data-act="save"]').click();
 document.querySelector('#gutter .add').click();
 document.querySelector('.editor textarea').value='Block annotation';
 document.querySelector('[data-act="save"]').click();
 document.getElementById('submit-btn').click();
}, 200);
</script>
'@
    Add-Content -Encoding utf8 $reviewPage.file $automation
    [void](Call @('session','overlay','reload','--target',$owner))
    for ($i=0; $i -lt 100; $i++) {
        $outcome = (Call @('session','overlay','result','--page',$reviewId)).result.pageOutcome
        if ($outcome.outcome -ne 'pending') { break }
        Start-Sleep -Milliseconds 100
    }
    Require ($outcome.outcome -eq 'submitted' -and $outcome.value.Contains('File annotation') -and $outcome.value.Contains('Block annotation')) 'actual cookbook review submits file and block annotations'
    $recovered = & py -3 $reviewScript result $reviewId
    Require ($LASTEXITCODE -eq 0 -and ($recovered -join "`n").Contains('Block annotation')) 'review adapter recovers submitted text after timeout'
    Require ((Get-Content $backup -Raw).Trim() -eq $pending) 'pending backup remains an honest recovery pointer after waiter exits'

    & py -3 $reviewScript review $document --output $backup --timeout 0.1 2>"$directory/review-cancel.txt"
    $cancelId = (Get-Content $backup -Raw).Trim().Substring(8)
    [void](Call @('session','overlay','close','--target',$owner))
    & py -3 $reviewScript result $cancelId 2>"$directory/review-dismissed.txt"
    Require ($LASTEXITCODE -eq 2) 'review adapter cancellation uses exit 2 independently of CLI success'
    . "$PSScriptRoot/cookbook-review-storage-cases.ps1"
    }

    $dashboard = Start-Process py -ArgumentList '-3',"$fixtures/scheduled-jobs/dashboard.py" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$directory/dashboard-url.txt" -RedirectStandardError "$directory/dashboard-error.txt"
    for ($i=0; $i -lt 50 -and -not (Get-Content "$directory/dashboard-url.txt" -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 100 }
    $url = (Get-Content "$directory/dashboard-url.txt" -Raw).Trim()
    Require ((Call @('browser','links','overlay75')).ok) 'terminal links use overlay75 policy'
    [void](Call @('session','type',("echo "+$url+"`r"),'--target',$owner))
    $terminalCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Document)
    $handle = [WebGateWindow]::Find($process.Id)
    $terminalRoot = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $linkRects = @()
    for ($i=0; $i -lt 50 -and $linkRects.Count -eq 0; $i++) {
        $terminalDocument = $terminalRoot.FindFirst([System.Windows.Automation.TreeScope]::Children,$terminalCondition)
        if ($terminalDocument) {
            $range = $terminalDocument.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.FindText($url,$true,$false)
            if ($range) { $linkRects = @($range.GetBoundingRectangles()) }
        }
        if ($linkRects.Count -eq 0) { Start-Sleep -Milliseconds 100 }
    }
    Require ($linkRects.Count -gt 0) 'terminal URL has a visible accessibility text range'
    $terminalDocument.SetFocus()
    $linkRect = $linkRects[0]
    [WebGateWindow]::ControlClick($handle,[int]($linkRect.Left+4),[int]($linkRect.Top+$linkRect.Height/2))
    Wait-WebUrl $url
    $linkPage = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
    Require ($linkPage.browse -and $linkPage.javascript -and $linkPage.navigation -and $linkPage.persistent) 'Ctrl-click opens a persistent browse page with JS and navigation'
    [void](Call @('session','overlay','close','--target',$owner))
    [void](Call @('browser','links','browser'))
    Require ((Call @('session','overlay','open','--url',$url,'--browse','--js','--navigation','--target',$owner)).ok) 'localhost htmx dashboard opens with browse and without bridge grant'
    Web-Invoke 'Run Report'
    for ($i=0; $i -lt 30 -and (Invoke-RestMethod ($url+'state')).jobs.report.runs -ne 1; $i++) { Start-Sleep -Milliseconds 100 }
    Require ((Invoke-RestMethod ($url+'state')).jobs.report.runs -eq 1) 'htmx Run button posts to local dashboard'
    Web-Invoke 'Toggle Backup'
    for ($i=0; $i -lt 30 -and (Invoke-RestMethod ($url+'state')).jobs.backup.enabled; $i++) { Start-Sleep -Milliseconds 100 }
    Require (-not (Invoke-RestMethod ($url+'state')).jobs.backup.enabled) 'htmx toggle changes sample job state'
    $refreshBefore = (Invoke-RestMethod ($url+'state')).refreshes
    Start-Sleep -Seconds 6
    Require ((Invoke-RestMethod ($url+'state')).refreshes -gt $refreshBefore) 'dashboard five-second refresh remains active'
    # Scoped terminal output while the dashboard owns the visible session surface.
    if (-not $NavigationOnly) {
    $busyCommand = 'for /L %i in (1,1,20000) do @echo WEB_BUSY_%i'
    Require ((Call @('session','type',($busyCommand+"`r"),'--target',$owner)).ok) 'busy terminal output starts under dashboard'
    $responseWatch = [Diagnostics.Stopwatch]::StartNew()
    Web-Invoke 'Run Report'
    for ($i=0; $i -lt 50 -and (Invoke-RestMethod ($url+'state')).jobs.report.runs -lt 2; $i++) { Start-Sleep -Milliseconds 100 }
    Require ((Invoke-RestMethod ($url+'state')).jobs.report.runs -eq 2) 'dashboard responds while terminal scrolls'
    Write-Host "BUSY dashboard response $($responseWatch.ElapsedMilliseconds) ms"
    Require ((Call @('session','type',"echo WEB_BUSY_DONE`r",'--target',$owner)).ok) 'terminal accepts input while dashboard remains open'
    for ($i=0; $i -lt 100; $i++) {
        $terminalText = (Call @('session','text','--target',$owner)).result
        if ($terminalText -match 'WEB_BUSY_20000' -and $terminalText -match 'WEB_BUSY_DONE') { break }
        Start-Sleep -Milliseconds 100
    }
    $terminalText | Set-Content "$directory/busy-terminal-last.txt"
    Require ($terminalText -match 'WEB_BUSY_20000' -and $terminalText -match 'WEB_BUSY_DONE') 'busy terminal reaches final output and processes subsequent input'
    }
    (Web-Element 'Filter jobs').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Report')
    Start-Sleep -Milliseconds 600
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([WebGateWindow]::Find($process.Id))
    $backupCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Run Backup')
    Require ($null -eq $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$backupCondition)) 'htmx filter excludes unmatched jobs'
    Web-Invoke 'Next page'
    Wait-WebUrl ($url+'next')
    Add-Type -AssemblyName System.Windows.Forms
    $linkClipboard = [System.Windows.Forms.Clipboard]::GetDataObject()
    try {
        Web-Invoke 'Copy link'
        Require ((Get-Clipboard -Raw) -eq ($url+'next')) 'native Copy link copies the current page address'
    } finally {
        if ($linkClipboard) { [System.Windows.Forms.Clipboard]::SetDataObject($linkClipboard,$true) }
        else { [System.Windows.Forms.Clipboard]::Clear() }
    }
    Require ((Call @('session','overlay','navigate','back','--target',$owner)).ok) 'same-origin history goes back'
    Wait-WebUrl $url
    Require ((Call @('session','overlay','navigate','forward','--target',$owner)).ok) 'same-origin history goes forward'
    Wait-WebUrl ($url+'next')
    [void](Call @('session','overlay','reload','--current','--target',$owner))
    Wait-WebUrl ($url+'next')
    $beforeRedirect = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0].id
    Web-Invoke 'Cross-origin redirect'
    Start-Sleep -Milliseconds 500
    $redirected = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
    $crossOriginUrl = $url.Replace('127.0.0.1','localhost')
    Wait-WebUrl $crossOriginUrl
    Require ($redirected.id -eq $beforeRedirect) 'cross-origin redirect retains page identity'
    Require $redirected.browse 'tree reports explicit browse mode'
    [void](Web-Element ($crossOriginUrl.TrimEnd('/') + ' — ' + $redirected.title))
    [void](Call @('session','overlay','navigate','back','--target',$owner))
    Wait-WebUrl ($url+'next')
    [void](Call @('session','overlay','reload','--current','--target',$owner))
    Wait-WebUrl ($url+'next')
    Web-Invoke 'Local file link'
    Start-Sleep -Milliseconds 300
    $linked = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
    Require ($linked.currentUrl -eq ($url+'next')) 'remote page cannot navigate to a local file'
    Require ($linked.state -eq 'loaded' -and -not $linked.error) 'denied local-file navigation preserves the loaded web page'
    [void](Call @('session','overlay','close','--target',$owner))

    Require ((Call @('session','overlay','open','--url',($url+'redirect'),'--target',$owner)).ok) 'default origin-pinned redirect opens asynchronously'
    for ($i=0; $i -lt 100; $i++) {
        $pinned = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
        if ($pinned.state -ne 'loading') { break }; Start-Sleep -Milliseconds 100
    }
    Require ($pinned.state -eq 'failed' -and -not $pinned.browse -and $pinned.error -match '--browse') 'default redirect refusal explains browse without OperationCanceled'
    [void](Call @('session','overlay','close','--target',$owner))
    Require ((Call @('session','overlay','open','--url',$url,'--js','--target',$owner)).ok) 'default web page opens for external-link confirmation'
    Web-Invoke 'Other site'
    $promptCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Open in browser?'))
    $prompt = $null
    for ($i=0; $i -lt 100 -and -not $prompt; $i++) {
        $prompt = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$promptCondition)
        if (-not $prompt) { Start-Sleep -Milliseconds 100 }
    }
    Require ($null -ne $prompt) 'cross-origin clicked link requests native confirmation'
    $cancelAction = $null
    for ($i=0; $i -lt 50 -and -not $cancelAction; $i++) {
        $cancelButtons = $prompt.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Cancel'),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
        foreach ($cancel in $cancelButtons) {
            if ($cancel.Current.IsEnabled -and -not $cancel.Current.IsOffscreen -and
                $cancel.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$cancelAction)) { break }
        }
        if (-not $cancelAction) { Start-Sleep -Milliseconds 100 }
    }
    if (-not $cancelAction) {
        Write-Host "Prompt: $($prompt.Current.ControlType.ProgrammaticName) handle=$($prompt.Current.NativeWindowHandle)"
        $prompt.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { Write-Host "Dialog UIA $($_.Current.ControlType.ProgrammaticName) $($_.Current.Name) enabled=$($_.Current.IsEnabled) offscreen=$($_.Current.IsOffscreen) patterns=$($_.GetSupportedPatterns().ProgrammaticName -join ',')" }
    }
    Require ($null -ne $cancelAction) 'native cancellation button is ready'
    $cancelAction.Invoke()
    Wait-WebUrl $url
    Web-Invoke 'Other site'
    Start-Sleep -Milliseconds 400
    Require ($null -eq [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$promptCondition)) 'declining external handoff suppresses repeated page prompts'
    (Web-Element 'Other site').SetFocus()
    [WebGateWindow]::Key([WebGateWindow]::Browser([WebGateWindow]::Find($process.Id)),0x27)
    Start-Sleep -Milliseconds 100
    Web-Invoke 'Other site'
    $prompt = $null
    for ($i=0; $i -lt 50 -and -not $prompt; $i++) {
        $prompt = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$promptCondition)
        if (-not $prompt) { Start-Sleep -Milliseconds 100 }
    }
    Require ($null -ne $prompt) 'native keyboard input re-enables external confirmation'
    [void](Call @('session','overlay','close','--target',$owner))
    [void](Call @('session','overlay','open','--url',$url,'--js','--target',$owner))
    Web-Invoke 'Other site'
    $prompt = $null
    for ($i=0; $i -lt 100 -and -not $prompt; $i++) {
        $prompt = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$promptCondition)
        if (-not $prompt) { Start-Sleep -Milliseconds 100 }
    }
    Require ($null -ne $prompt) 'new page can request external confirmation independently'
    [void](Call @('session','overlay','hide','--target',$owner))
    for ($i=0; $i -lt 50; $i++) {
        $prompt = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$promptCondition)
        if (-not $prompt) { break }; Start-Sleep -Milliseconds 100
    }
    Require ($null -eq $prompt) 'hiding a page dismisses its pending external prompt'
    [void](Call @('session','overlay','close','--target',$owner))
    Require ((Call @('session','overlay','open','--url',($url+'redirect'),'--browse','--js','--target',$owner)).ok) 'browse redirect page opens asynchronously'
    for ($i=0; $i -lt 100; $i++) {
        $initialRedirect = @((Call @('tree')).result.workspaces.sessions | Where-Object id -eq $owner)[0].htmlOverlays[0]
        if ($initialRedirect.state -ne 'loading') { break }
        Start-Sleep -Milliseconds 100
    }
    Require ($initialRedirect.state -eq 'loaded' -and $initialRedirect.currentUrl -eq $crossOriginUrl -and -not $initialRedirect.error) 'initial cross-origin redirect loads its destination normally'
    [void](Call @('session','overlay','close','--target',$owner))

    $snapshot = Join-Path $directory 'snapshot.html'
    & py -3 "$fixtures/session-snapshot/render.py" "$fixtures/session-snapshot/sample.json" $snapshot --target $owner
    Require ($LASTEXITCODE -eq 0) 'standalone snapshot viewer launches embedded with JS and no bridge'
    $search = Web-Element 'filter sessions… ( / )'
    $searchValue = $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $searchValue.SetValue('no-such-fixture')
    Start-Sleep -Milliseconds 300
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([WebGateWindow]::Find($process.Id))
    $rowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Snapshot fixture')
    Require ($null -eq $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$rowCondition)) 'snapshot search hides unmatched sessions'
    $searchValue.SetValue('Snapshot')
    Require ($null -ne (Web-Element 'Snapshot fixture')) 'snapshot search retains matching session'
    $copyWorkspace = Web-Element 'copy workspace'
    $copyWorkspace.SetFocus()
    for ($i=0; $i -lt 30 -and -not $copyWorkspace.Current.HasKeyboardFocus; $i++) { Start-Sleep -Milliseconds 50 }
    [WebGateWindow]::Key([WebGateWindow]::Renderer([WebGateWindow]::Find($process.Id)),40)
    Require ($null -ne (Web-Element 'copy restore snippet')) 'snapshot keyboard selection opens full details'
    Add-Type -AssemblyName System.Windows.Forms
    $clipboardData = [System.Windows.Forms.Clipboard]::GetDataObject()
    try {
        Web-Invoke 'copy restore snippet'
        for ($i=0; $i -lt 30 -and (Get-Clipboard -Raw) -ne 'agwintermctl session new --name restored-fixture'; $i++) { Start-Sleep -Milliseconds 100 }
        Require ((Get-Clipboard -Raw) -eq 'agwintermctl session new --name restored-fixture') 'snapshot copies exact restore text without executing it'
        Require (@((Call @('tree')).result.workspaces.sessions | Where-Object name -eq 'restored-fixture').Count -eq 0) 'copy does not create or restore a session'
    } finally {
        if ($clipboardData) { [System.Windows.Forms.Clipboard]::SetDataObject($clipboardData,$true) }
        else { [System.Windows.Forms.Clipboard]::Clear() }
    }
    [void](Call @('session','overlay','close','--target',$owner))
} finally {
    if ($dashboard -and -not $dashboard.HasExited) { $dashboard.Kill($true); $dashboard.WaitForExit() }
    foreach ($key in $savedCookbook.Keys) { [Environment]::SetEnvironmentVariable($key,$savedCookbook[$key]) }
}
