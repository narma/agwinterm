param(
    [string]$InputDirectory = 'C:\AgwintermInput',
    [string]$ResultsDirectory = 'C:\AgwintermResults',
    [string]$WorkDirectory = 'C:\AgwintermTests',
    [string]$LocalDataDirectory = [Environment]::GetFolderPath('LocalApplicationData')
)
$ErrorActionPreference = 'Stop'
$hyperv = $false
if ($env:AGWINTERM_HYPERV_UI -eq '1') { $hyperv = & "$PSScriptRoot/hyperv-ui-context.ps1" }
if (-not $hyperv -and $env:USERNAME -ne 'WDAGUtilityAccount') {
    throw 'This entry point must run inside Windows Sandbox; use sandbox-ui.ps1 on the host.'
}
$results = $ResultsDirectory
$work = $WorkDirectory
$exitCode = 2
$failure = $null
$guestTimer = [Diagnostics.Stopwatch]::StartNew()
$guestTimings = @{}
$testStartedAt = $null
try {
    $inputPlan = Get-Content -LiteralPath (Join-Path $InputDirectory 'run.json') -Raw | ConvertFrom-Json
    if ($hyperv -and $inputPlan.hypervDirectPayload) {
        # Hyper-V owns this writable payload disk. Retain the usual C: work path
        # without copying the runtimes a second time into the OS differencing disk.
        $null = New-Item -ItemType Junction -Path $work -Target (Resolve-Path -LiteralPath $InputDirectory).Path
    } else {
        $null = New-Item -ItemType Directory -Path $work
        foreach ($item in Get-ChildItem -LiteralPath $inputDirectory -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $work -Recurse
        }
    }
    $guestTimings.copyPayloadSeconds = [Math]::Round($guestTimer.Elapsed.TotalSeconds,3)
    $run = Get-Content -LiteralPath (Join-Path $work 'run.json') -Raw | ConvertFrom-Json
    $env:CI = 'true'
    if (-not $hyperv) { $env:AGWINTERM_SANDBOX_UI = '1' }
    if (Test-Path -LiteralPath (Join-Path $work 'webview2/msedgewebview2.exe')) {
        $env:AGWINTERM_TEST_WEBVIEW2 = Join-Path $work 'webview2'
        $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER = $env:AGWINTERM_TEST_WEBVIEW2
    }
    if (Test-Path -LiteralPath (Join-Path $work 'python/python.exe')) { $env:AGWINTERM_TEST_PYTHON = Join-Path $work 'python/python.exe' }
    $env:PATH = (Join-Path $work 'powershell') + ';' + $env:PATH
    $runtime = Join-Path $work 'dotnet'
    if (Test-Path -LiteralPath $runtime) {
        $env:DOTNET_ROOT = $runtime
        $env:DOTNET_ROOT_X64 = $runtime
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        $env:PATH = $runtime + ';' + $env:PATH
    }
    $runner = Join-Path $work 'tests/integration/sandbox-ui-dispatch.ps1'
    if ($hyperv -and $run.hudSuites) { $runner = Join-Path $work 'tests/integration/hyperv-ui-batch.ps1' }
    $processArguments = @(
        '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',
        '-File',"`"$runner`"",'-WorkDirectory',"`"$work`""
    )
    if ($hyperv -and $run.hudSuites) { $processArguments += @('-ResultsDirectory',"`"$results`"") }
    $testStartedAt = $guestTimer.Elapsed.TotalSeconds
    $guestTimings.setupSeconds = [Math]::Round($testStartedAt,3)
    $process = Start-Process -FilePath (Join-Path $work 'powershell/pwsh.exe') -ArgumentList $processArguments -RedirectStandardOutput (Join-Path $results 'stdout.log') -RedirectStandardError (Join-Path $results 'stderr.log') -Wait -PassThru
    $exitCode = $process.ExitCode
} catch {
    $failure = $_.ToString()
} finally {
    $evidenceAt = $guestTimer.Elapsed.TotalSeconds
    if ($null -ne $testStartedAt) { $guestTimings.testSeconds = [Math]::Round($evidenceAt - $testStartedAt,3) }
    try {
        $evidence = Join-Path $work '.revmux'
        if (Test-Path -LiteralPath $evidence) {
            Copy-Item -LiteralPath $evidence -Destination (Join-Path $results 'evidence') -Recurse
        }
        $artifacts = Join-Path $work 'artifacts'
        if (Test-Path -LiteralPath $artifacts) { Copy-Item -LiteralPath $artifacts -Destination (Join-Path $results 'artifacts') -Recurse }
        $state = Join-Path $results 'state'
        $null = New-Item -ItemType Directory -Path $state -Force
        foreach ($directory in Get-ChildItem -LiteralPath $LocalDataDirectory -Directory -Filter 'agwinterm-*') {
            Copy-Item -LiteralPath $directory.FullName -Destination (Join-Path $results "state/$($directory.Name)") -Recurse -Force
        }
    } catch {
        $exitCode = 2
        $failure = "Cannot copy test evidence: $_"
    }
    $guestTimings.evidenceSeconds = [Math]::Round($guestTimer.Elapsed.TotalSeconds - $evidenceAt,3)
    $guestTimings.totalSeconds = [Math]::Round($guestTimer.Elapsed.TotalSeconds,3)
    $temporaryResult = Join-Path $results 'result.json.tmp'
    @{exitCode=$exitCode; error=$failure; timings=$guestTimings} | ConvertTo-Json |
        Set-Content -LiteralPath $temporaryResult -Encoding utf8
    Move-Item -LiteralPath $temporaryResult -Destination (Join-Path $results 'result.json')
}
