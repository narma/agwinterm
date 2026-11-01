param(
    [string]$InputDirectory = 'C:\AgwintermInput',
    [string]$ResultsDirectory = 'C:\AgwintermResults',
    [string]$WorkDirectory = 'C:\AgwintermTests',
    [string]$LocalDataDirectory = [Environment]::GetFolderPath('LocalApplicationData')
)
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') {
    throw 'This entry point must run inside Windows Sandbox; use sandbox-ui.ps1 on the host.'
}
$results = $ResultsDirectory
$work = $WorkDirectory
$exitCode = 2
$failure = $null
try {
    $null = New-Item -ItemType Directory -Path $work
    foreach ($item in Get-ChildItem -LiteralPath $inputDirectory -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $work -Recurse
    }
    $run = Get-Content -LiteralPath (Join-Path $work 'run.json') -Raw | ConvertFrom-Json
    $env:CI = 'true'
    $env:AGWINTERM_SANDBOX_UI = '1'
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
    $process = Start-Process -FilePath (Join-Path $work 'powershell/pwsh.exe') -ArgumentList @(
        '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',
        '-File',"`"$runner`"",'-WorkDirectory',"`"$work`""
    ) -RedirectStandardOutput (Join-Path $results 'stdout.log') -RedirectStandardError (Join-Path $results 'stderr.log') -Wait -PassThru
    $exitCode = $process.ExitCode
} catch {
    $failure = $_.ToString()
} finally {
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
    $temporaryResult = Join-Path $results 'result.json.tmp'
    @{exitCode=$exitCode; error=$failure} | ConvertTo-Json |
        Set-Content -LiteralPath $temporaryResult -Encoding utf8
    Move-Item -LiteralPath $temporaryResult -Destination (Join-Path $results 'result.json')
}
