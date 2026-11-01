#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'sandbox-ui.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('agwinterm sandbox & tests ' + [guid]::NewGuid().ToString('N'))
$global:AgwintermSandboxUiTestState = @{mode='success'; running=$false; available=$true; launches=0; cliAvailable=$false; cliRunning=$false; cliMode='success'; cliCommands=@(); viewerAvailable=$true}
$script:checks = 0
$script:failed = 0
$ciBefore = $env:CI

function Check([string]$Name, [bool]$Passed) {
    $script:checks++
    if ($Passed) { Write-Host "PASS $Name" }
    else { $script:failed++; Write-Host "FAIL $Name" }
}
function Refuses([string]$Name, [scriptblock]$Action, [string]$Message) {
    $caught = $false
    try { & $Action } catch { $caught = $_.Exception.Message -like $Message }
    Check $Name $caught
}
function Get-Command {
    param([string]$Name, $ErrorAction)
    if ($Name -eq 'wsb.exe') {
        if ($global:AgwintermSandboxUiTestState.cliAvailable) { [pscustomobject]@{Source='Invoke-TestSandboxCli'} }
        return
    }
    if ($Name -eq 'WindowsSandboxRemoteSession.exe') {
        if ($global:AgwintermSandboxUiTestState.viewerAvailable) { [pscustomobject]@{Source='fixture-viewer.exe'} }
        return
    }
    if ($global:AgwintermSandboxUiTestState.available) { [pscustomobject]@{Source='sandbox-test-launcher.exe'} }
}
function Get-Process {
    param([string[]]$Name, $ErrorAction)
    if ($global:AgwintermSandboxUiTestState.running) { [pscustomobject]@{Id=12345} }
}
function Start-Process {
    param([string]$FilePath, [string]$ArgumentList)
    $global:AgwintermSandboxUiTestState.launches++
    $configuration = $ArgumentList.Substring(1, $ArgumentList.Length - 2)
    $xml = [xml](Get-Content -LiteralPath $configuration -Raw)
    Write-TestResult $xml
}
function Write-TestResult([xml]$Configuration) {
    $results = $Configuration.Configuration.MappedFolders.MappedFolder[1].HostFolder
    switch ($global:AgwintermSandboxUiTestState.mode) {
        'success' { '{"exitCode":0,"error":null}' | Set-Content -LiteralPath (Join-Path $results 'result.json') }
        'failure' { '{"exitCode":1,"error":null}' | Set-Content -LiteralPath (Join-Path $results 'result.json') }
        'setup' { '{"exitCode":2,"error":"setup failed"}' | Set-Content -LiteralPath (Join-Path $results 'result.json') }
        'invalid' { '{"error":"no exit code"}' | Set-Content -LiteralPath (Join-Path $results 'result.json') }
        'timeout' { }
    }
}
function Invoke-TestSandboxCli {
    $operation = $args[0]
    $global:AgwintermSandboxUiTestState.cliCommands += $operation
    $global:LASTEXITCODE = 0
    if ($global:AgwintermSandboxUiTestState.cliMode -eq ($operation + 'Failure')) {
        $global:LASTEXITCODE = 1
        return
    }
    switch ($operation) {
        'list' {
            if ($global:AgwintermSandboxUiTestState.cliRunning) { '{"WindowsSandboxEnvironments":[{"Id":"existing"}]}' }
            else { '{"WindowsSandboxEnvironments":[]}' }
        }
        'start' {
            $configIndex = [Array]::IndexOf($args, '--config')
            Write-TestResult ([xml]$args[$configIndex + 1])
            if ($global:AgwintermSandboxUiTestState.cliMode -eq 'missingId') { '{}' }
            else { '{"Id":"00000000-0000-0000-0000-000000000123"}' }
        }
        'connect' {
            $global:AgwintermSandboxUiTestState.connectedCorrectId = ($args[1] -eq '--id' -and $args[2] -eq '00000000-0000-0000-0000-000000000123')
        }
    }
}
function Invoke-GuestCase([string]$Directory, [switch]$FailStart, [switch]$FailEvidence, [string]$Suite = 'Navigation') {
    function Start-Process {
        param($FilePath, $ArgumentList, $RedirectStandardOutput, $RedirectStandardError, [switch]$Wait, [switch]$PassThru)
        if ($FailStart) { throw 'simulated guest launch failure' }
        $suiteIndex = [Array]::IndexOf($ArgumentList, '-Suite')
        $global:AgwintermSandboxUiTestState.guestArgumentsMatch = ($suiteIndex -ge 0 -and $ArgumentList[$suiteIndex + 1] -ceq $Suite -and $ArgumentList -contains '-Strict' -and $Wait -and $PassThru -and $FilePath -eq (Join-Path $Directory 'work/powershell/pwsh.exe') -and $ArgumentList -contains ('"' + (Join-Path $Directory 'work/tests/integration/hud-ui.ps1') + '"'))
        'test failed' | Set-Content -LiteralPath $RedirectStandardOutput
        '' | Set-Content -LiteralPath $RedirectStandardError
        [pscustomobject]@{ExitCode=1}
    }
    $inputPath = Join-Path $Directory 'input'
    $resultPath = Join-Path $Directory 'results'
    $null = New-Item -ItemType Directory -Path (Join-Path $inputPath '.revmux/navigation-run'), $resultPath -Force
    @{suite=$Suite; exeName='Agwinterm.Win32.exe'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputPath 'run.json')
    'screenshot evidence' | Set-Content -LiteralPath (Join-Path $inputPath '.revmux/navigation-run/tooltip.png')
    if ($FailEvidence) { 'cannot overwrite this file with a directory' | Set-Content -LiteralPath (Join-Path $resultPath 'evidence') }
    $usernameBefore = $env:USERNAME
    $pathBefore = $env:PATH
    $global:AgwintermSandboxUiTestState.guestArgumentsMatch = $false
    try {
        $env:USERNAME = 'WDAGUtilityAccount'
        & (Join-Path $PSScriptRoot 'sandbox-ui-guest.ps1') -InputDirectory $inputPath -ResultsDirectory $resultPath -WorkDirectory (Join-Path $Directory 'work')
    } finally {
        $env:USERNAME = $usernameBefore
        $env:PATH = $pathBefore
        $env:CI = $ciBefore
    }
    if (-not $FailStart) {
        Check "guest dispatches $Suite to the strict UI runner" $global:AgwintermSandboxUiTestState.guestArgumentsMatch
    }
    Get-Content -LiteralPath (Join-Path $resultPath 'result.json') -Raw | ConvertFrom-Json
}

try {
    foreach ($directory in @('app','powershell','dotnet/host/fxr/10.0.0','dotnet/shared/Microsoft.NETCore.App/10.0.0','dotnet/sdk')) {
        $null = New-Item -ItemType Directory -Path (Join-Path $temporary $directory) -Force
    }
    foreach ($file in @('app/Agwinterm.Win32.exe','app/agwintermctl.exe','app/conpty.dll','powershell/pwsh.exe','dotnet/dotnet.exe','dotnet/host/fxr/10.0.0/hostfxr.dll','dotnet/shared/Microsoft.NETCore.App/10.0.0/coreclr.dll','dotnet/sdk/not-needed.txt')) {
        'fixture' | Set-Content -LiteralPath (Join-Path $temporary $file)
    }
    $parameters = @{
        Exe=(Join-Path $temporary 'app/Agwinterm.Win32.exe')
        CtlExe=(Join-Path $temporary 'app/agwintermctl.exe')
        PowerShellHome=(Join-Path $temporary 'powershell')
        DotnetHome=(Join-Path $temporary 'dotnet')
    }
    # Parse parameter metadata without invoking the real UI runner or the mocked Get-Command.
    function SuiteNames([string]$Path) {
        $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$null)
        $parameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Suite' }
        $validation = $parameter.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateSet' }
        $validation.PositionalArguments | ForEach-Object { $_.SafeGetValue() }
    }
    $suites = @(SuiteNames (Join-Path $PSScriptRoot 'hud-ui.ps1'))
    $wrapperSuites = @(SuiteNames $runner)
    Check 'Sandbox supports exactly the direct UI runner suites' ($suites.Count -gt 0 -and -not (Compare-Object $suites $wrapperSuites))
    foreach ($suite in $suites) {
        $suiteDirectory = Join-Path $temporary "suite-$suite"
        & $runner @parameters -Suite $suite -OutputDirectory $suiteDirectory -PrepareOnly
        $run = Get-Content -LiteralPath (Join-Path $suiteDirectory 'input/run.json') -Raw | ConvertFrom-Json
        Check "$suite is staged with the current UI runner" ($run.suite -ceq $suite -and (Test-Path -LiteralPath (Join-Path $suiteDirectory 'input/tests/integration/hud-ui.ps1')))
        $result = Invoke-GuestCase (Join-Path $temporary "dispatch-$suite") -Suite $suite
        Check "$suite guest returns the runner outcome" ($result.exitCode -eq 1)
    }
    $checkout = Join-Path $temporary 'standard-build'
    $appOutput = Join-Path $checkout 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64'
    $ctlOutput = Join-Path $checkout 'src/Agwinterm.Ctl/bin/Release/net10.0-windows'
    $scriptDirectory = Join-Path $checkout 'tests/integration'
    $oldOutput = Join-Path $checkout 'artifacts/local'
    $null = New-Item -ItemType Directory -Path $appOutput, $ctlOutput, $scriptDirectory, $oldOutput -Force
    Copy-Item -LiteralPath $runner -Destination $scriptDirectory
    'fresh app' | Set-Content -LiteralPath (Join-Path $appOutput 'Agwinterm.Win32.exe')
    'fresh CLI' | Set-Content -LiteralPath (Join-Path $ctlOutput 'agwintermctl.exe')
    'stale app' | Set-Content -LiteralPath (Join-Path $oldOutput 'Agwinterm.Win32.exe')
    'stale CLI' | Set-Content -LiteralPath (Join-Path $oldOutput 'agwintermctl.exe')
    $standardResult = Join-Path $temporary 'standard-result'
    & (Join-Path $scriptDirectory 'sandbox-ui.ps1') -PowerShellHome $parameters.PowerShellHome -OutputDirectory $standardResult -PrepareOnly
    Check 'default application path uses the standard x64 Release output' ((Get-Content -LiteralPath (Join-Path $standardResult 'input/app/Agwinterm.Win32.exe')) -eq 'fresh app')
    Check 'default CLI path uses its standard Release output' ((Get-Content -LiteralPath (Join-Path $standardResult 'input/src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe')) -eq 'fresh CLI')
    $prepared = Join-Path $temporary 'prepared'
    & $runner @parameters -OutputDirectory $prepared -PrepareOnly
    $xml = [xml](Get-Content -LiteralPath (Join-Path $prepared 'ui-tests.wsb') -Raw)
    $mappings = $xml.Configuration.MappedFolders.MappedFolder
    Check 'payload is read-only and paths with spaces and ampersands round-trip' ($mappings[0].HostFolder -eq (Join-Path $prepared 'input') -and $mappings[0].ReadOnly -eq 'true')
    Check 'only this run results are writable' ($mappings.Count -eq 2 -and $mappings[1].HostFolder -eq (Join-Path $prepared 'results') -and $mappings[1].ReadOnly -eq 'false')
    Check 'networking and clipboard sharing are disabled' ($xml.Configuration.Networking -eq 'Disable' -and $xml.Configuration.ClipboardRedirection -eq 'Disable')
    Check 'vGPU is enabled by default' ($xml.Configuration.vGPU -eq 'Enable')
    Check 'guest bootstrap is selected' ($xml.Configuration.LogonCommand.Command -like '*sandbox-ui-guest.ps1')
    Check 'CLI and native application dependencies are staged' ((Test-Path -LiteralPath (Join-Path $prepared 'input/src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe')) -and (Test-Path -LiteralPath (Join-Path $prepared 'input/app/conpty.dll')))
    Check 'self-contained payload does not copy host dotnet' (-not (Test-Path -LiteralPath (Join-Path $prepared 'input/dotnet')))
    Check 'prepare-only does not launch Sandbox or write a test result' ($global:AgwintermSandboxUiTestState.launches -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $prepared 'results/result.json')))

    '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' |
        Set-Content -LiteralPath (Join-Path $temporary 'app/Agwinterm.Win32.runtimeconfig.json')
    $framework = Join-Path $temporary 'framework'
    & $runner @parameters -OutputDirectory $framework -DisableVGpu -PrepareOnly
    $xml = [xml](Get-Content -LiteralPath (Join-Path $framework 'ui-tests.wsb') -Raw)
    Check 'software rendering is configurable' ($xml.Configuration.vGPU -eq 'Disable')
    Check 'framework-dependent payload includes runtime and hostfxr' ((Test-Path -LiteralPath (Join-Path $framework 'input/dotnet/shared/Microsoft.NETCore.App/10.0.0/coreclr.dll')) -and (Test-Path -LiteralPath (Join-Path $framework 'input/dotnet/host/fxr/10.0.0/hostfxr.dll')))
    Check 'SDK is not staged' (-not (Test-Path -LiteralPath (Join-Path $framework 'input/dotnet/sdk')))

    Refuses 'recursive source/output layout is rejected before copying' {
        & $runner @parameters -OutputDirectory (Join-Path $temporary 'app/recursive') -PrepareOnly
    } '*outside staged source folders*'
    Refuses 'existing output directory cannot overwrite earlier evidence' {
        & $runner @parameters -OutputDirectory $prepared -PrepareOnly
    } '*already exists*'
    $global:AgwintermSandboxUiTestState.available = $false
    Refuses 'missing Windows Sandbox is reported before staging' {
        & $runner @parameters -OutputDirectory (Join-Path $temporary 'unavailable')
    } '*Enable Windows Sandbox*'
    $global:AgwintermSandboxUiTestState.available = $true
    $global:AgwintermSandboxUiTestState.running = $true
    Refuses 'existing Sandbox is left alone' {
        & $runner @parameters -OutputDirectory (Join-Path $temporary 'already-running')
    } '*Close the existing Windows Sandbox*'
    $global:AgwintermSandboxUiTestState.running = $false

    foreach ($case in @(@{mode='success';code=0}, @{mode='failure';code=1}, @{mode='setup';code=2})) {
        $global:AgwintermSandboxUiTestState.mode = $case.mode
        $global:LASTEXITCODE = 99
        & $runner @parameters -OutputDirectory (Join-Path $temporary $case.mode)
        Check "guest $($case.mode) exit code is propagated" ($LASTEXITCODE -eq $case.code)
    }
    $global:AgwintermSandboxUiTestState.mode = 'invalid'
    Refuses 'incomplete result cannot become a false pass' {
        & $runner @parameters -OutputDirectory (Join-Path $temporary 'invalid')
    } '*Invalid Sandbox result*'
    $global:AgwintermSandboxUiTestState.mode = 'timeout'
    Refuses 'missing completion times out instead of passing' {
        & $runner @parameters -OutputDirectory (Join-Path $temporary 'timeout') -TimeoutSeconds 1
    } '*did not return a result*'
    $global:AgwintermSandboxUiTestState.cliAvailable = $true
    $global:AgwintermSandboxUiTestState.available = $false
    $global:AgwintermSandboxUiTestState.mode = 'success'
    & $runner @parameters -OutputDirectory (Join-Path $temporary 'modern-client')
    Check 'modern client succeeds without the legacy launcher and opens its new guest' ($LASTEXITCODE -eq 0 -and ($global:AgwintermSandboxUiTestState.cliCommands -join ',') -eq 'list,start,connect' -and $global:AgwintermSandboxUiTestState.connectedCorrectId)
    # Replace only the native viewer boundary in an isolated fixture checkout.
    @'
param([string]$Exe, [guid]$SandboxId)
if ($global:AgwintermSandboxUiTestState.viewerFails) { throw 'simulated viewer startup failure' }
$global:AgwintermSandboxUiTestState.backgroundViewer = "$Exe|$SandboxId"
$viewer = [pscustomobject]@{HasExited=[bool]$global:AgwintermSandboxUiTestState.viewerExited; ExitCode=42}
$viewer | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $global:AgwintermSandboxUiTestState.viewerDisposed = $true }
$viewer
'@ | Set-Content -LiteralPath (Join-Path $scriptDirectory 'sandbox-ui-window.ps1')
    # The fake CLI uses real-looking ids so the same argument validation applies.
    $global:AgwintermSandboxUiTestState.cliCommands = @()
    & (Join-Path $scriptDirectory 'sandbox-ui.ps1') @parameters -Background -OutputDirectory (Join-Path $temporary 'background-client')
    Check 'background mode connects its own viewer without opening a host viewer via CLI' ($LASTEXITCODE -eq 0 -and ($global:AgwintermSandboxUiTestState.cliCommands -join ',') -eq 'list,start' -and $global:AgwintermSandboxUiTestState.backgroundViewer -eq 'fixture-viewer.exe|00000000-0000-0000-0000-000000000123' -and $global:AgwintermSandboxUiTestState.viewerDisposed)
    $global:AgwintermSandboxUiTestState.viewerFails = $true
    Refuses 'background viewer failure is reported with stop instructions' {
        & (Join-Path $scriptDirectory 'sandbox-ui.ps1') @parameters -Background -OutputDirectory (Join-Path $temporary 'background-failure')
    } '*simulated viewer startup failure*wsb stop --id*'
    $global:AgwintermSandboxUiTestState.viewerFails = $false
    $global:AgwintermSandboxUiTestState.viewerExited = $true
    $global:AgwintermSandboxUiTestState.mode = 'timeout'
    Refuses 'background viewer exit fails immediately with diagnostics' {
        & (Join-Path $scriptDirectory 'sandbox-ui.ps1') @parameters -Background -TimeoutSeconds 10 -OutputDirectory (Join-Path $temporary 'background-exited')
    } '*viewer exited with code 42*wsb stop --id*'
    $global:AgwintermSandboxUiTestState.viewerExited = $false
    $global:AgwintermSandboxUiTestState.mode = 'success'
    $global:AgwintermSandboxUiTestState.viewerAvailable = $false
    Refuses 'background mode requires the viewer alias before staging' {
        & $runner @parameters -Background -OutputDirectory (Join-Path $temporary 'background-no-alias')
    } '*require the WindowsSandboxRemoteSession.exe*'
    $global:AgwintermSandboxUiTestState.viewerAvailable = $true
    $global:AgwintermSandboxUiTestState.cliAvailable = $false
    $global:AgwintermSandboxUiTestState.available = $true
    Refuses 'background mode does not silently open a legacy viewer' {
        & $runner @parameters -Background -OutputDirectory (Join-Path $temporary 'background-legacy')
    } '*require the modern Windows Sandbox client*'
    $global:AgwintermSandboxUiTestState.cliAvailable = $true
    $global:AgwintermSandboxUiTestState.cliRunning = $true
    Refuses 'modern client refuses an existing guest without a viewer' {
        & $runner @parameters -OutputDirectory (Join-Path $temporary 'modern-running')
    } '*Close the existing Windows Sandbox*'
    $global:AgwintermSandboxUiTestState.cliRunning = $false
    foreach ($case in @(
        @{mode='listFailure';message='*Cannot list running*'},
        @{mode='startFailure';message='*could not start*'},
        @{mode='missingId';message='*did not return an environment id*'},
        @{mode='connectFailure';message='*Cannot open Windows Sandbox*'}
    )) {
        $global:AgwintermSandboxUiTestState.cliMode = $case.mode
        Refuses "modern client reports $($case.mode)" {
            & $runner @parameters -OutputDirectory (Join-Path $temporary $case.mode)
        } $case.message
    }
    Check 'host CI environment is unchanged' ($env:CI -ceq $ciBefore)
    if ($env:USERNAME -ne 'WDAGUtilityAccount') {
        Refuses 'guest bootstrap refuses accidental host execution' {
            & (Join-Path $PSScriptRoot 'sandbox-ui-guest.ps1')
        } '*must run inside Windows Sandbox*'
    }
    $guestFailure = Join-Path $temporary 'guest-failure'
    $result = Invoke-GuestCase $guestFailure
    Check 'guest preserves test failure and exports screenshot evidence' ($result.exitCode -eq 1 -and (Test-Path -LiteralPath (Join-Path $guestFailure 'results/evidence/navigation-run/tooltip.png')))
    Check 'guest publishes completion without a partial result file' (-not (Test-Path -LiteralPath (Join-Path $guestFailure 'results/result.json.tmp')))
    $result = Invoke-GuestCase (Join-Path $temporary 'guest-setup') -FailStart
    Check 'guest launch error becomes setup failure with diagnostics' ($result.exitCode -eq 2 -and $result.error -like '*simulated guest launch failure*')
    $result = Invoke-GuestCase (Join-Path $temporary 'guest-evidence') -FailEvidence
    Check 'lost guest evidence cannot be reported as a normal test result' ($result.exitCode -eq 2 -and $result.error -like '*Cannot copy test evidence*')
} finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
    Remove-Variable -Name AgwintermSandboxUiTestState -Scope Global
}
Write-Host "$script:checks checks, $script:failed failed"
if ($script:failed) { exit 1 }
exit 0
