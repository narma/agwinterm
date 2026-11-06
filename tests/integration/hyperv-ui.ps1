#Requires -Version 7.0
param(
    [string]$BaseVhd = $env:AGWINTERM_UI_BASE_VHD,
    [string]$Exe = "$PSScriptRoot/../../src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe",
    [string]$CtlExe = "$PSScriptRoot/../../src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe",
    [ValidateSet('Hud','HudAdvanced','Recent','WorkingSets','Windows','Presentation','Quick','Navigation','Picker','Ask','Accessibility','Command','Menu','Clipboard','TerminalRendering','TerminalPerformance','TerminalLatency','Ghostty')]
    [string[]]$Suite = @('Navigation'),
    [string]$Test = 'Hud',
    [hashtable]$TestParameters = @{},
    [string]$WebView2Home,
    [string]$PythonHome,
    [string]$GateExe = "$PSScriptRoot/../WebViewHost.Gate/bin/Release/net10.0-windows/win-x64/WebViewHost.Gate.exe",
    [string]$PowerShellHome = $PSHOME,
    [string]$DotnetHome = "$env:ProgramFiles/dotnet",
    [string]$OutputDirectory = "$PSScriptRoot/../../artifacts/hyperv-ui/$([guid]::NewGuid().ToString('N'))",
    [ValidateRange(30,86400)][int]$TimeoutSeconds = 900,
    [ValidateRange(2048,65536)][int]$MemoryInMB = 4096,
    [ValidateRange(1,64)][int]$ProcessorCount = 2,
    [ValidateRange(1,100)][int]$CpuMaximum = $(if ([Environment]::ProcessorCount -ge 16) { 100 } else { 50 }),
    [ValidateRange(1,16)][int]$MaxParallel = 2,
    [ValidateRange(2048,262144)][int]$MemoryBudgetInMB = 8192,
    [ValidateRange(1,86400)][int]$QueueTimeoutSeconds = 1800,
    [switch]$KeepVM,
    [switch]$TrimRuntime = $true,
    [switch]$DirectPayload = $true,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/hyperv-ui-host.ps1"
. "$PSScriptRoot/hyperv-ui-client.ps1"
$hostConfiguration = Get-HyperVUiHostConfiguration
if (-not $BaseVhd -and $hostConfiguration) { $BaseVhd = $hostConfiguration.baseVhd }
$administrator = ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$useWorker = -not $PrepareOnly -and -not $administrator
$definitions = Import-PowerShellDataFile "$PSScriptRoot/sandbox-ui-tests.psd1"
if (-not $definitions.ContainsKey($Test)) { throw "Unknown Hyper-V UI test: $Test" }
if ($definitions[$Test].Wsl) { throw 'WSL tests use wsl-ui.ps1 and the configured host distribution, not Hyper-V UI images.' }
if ($MemoryInMB -gt $MemoryBudgetInMB) { throw 'Requested VM memory exceeds -MemoryBudgetInMB.' }
if ($Suite.Count -gt 1 -and ($Test -ne 'Hud' -or $TestParameters.ContainsKey('Suite'))) { throw 'Multiple suites require -Test Hud and no TestParameters.Suite override.' }
if (-not $PrepareOnly) {
    if (-not $BaseVhd) { throw 'Pass -BaseVhd or set AGWINTERM_UI_BASE_VHD. See docs/hyperv-ui-tests.md for image preparation.' }
    $BaseVhd = (Resolve-Path -LiteralPath $BaseVhd).Path
    if ($useWorker) {
        if (-not $hostConfiguration) { throw 'Install the Hyper-V UI host once with tools/hyperv-ui-install-host.ps1. Subsequent runs need no UAC.' }
        if ($BaseVhd -ne $hostConfiguration.baseVhd -or $KeepVM -or $MemoryInMB -ne 4096 -or $ProcessorCount -ne 2 -or $CpuMaximum -ne $hostConfiguration.cpuMaximum -or $MaxParallel -ne 2 -or $MemoryBudgetInMB -ne 8192 -or $TimeoutSeconds -gt 1800) { throw 'The installed UI host uses its configured base, two 4-GiB/two-vCPU slots, and a maximum 1800-second run. Custom VM policies and KeepVM require a direct administrator run.' }
    } else { Assert-HyperVUiHost }
}
# Reuse exactly the Sandbox payload/registry; PrepareOnly never opens Sandbox,
# acquires its singleton lock, or requires its Windows feature to be installed.
$stage = @{Exe=$Exe; CtlExe=$CtlExe; Suite=$Suite[0]; Test=$Test; TestParameters=$TestParameters;
    GateExe=$GateExe; PowerShellHome=$PowerShellHome; DotnetHome=$DotnetHome; OutputDirectory=$OutputDirectory; PrepareOnly=$true}
if ($WebView2Home) { $stage.WebView2Home = $WebView2Home }
if ($PythonHome) { $stage.PythonHome = $PythonHome }
if ($TrimRuntime) { $stage.TrimRuntime = $true }
$stageTimer = [Diagnostics.Stopwatch]::StartNew()
& "$PSScriptRoot/sandbox-ui.ps1" @stage
if ($Suite.Count -gt 1 -or $DirectPayload) {
    $planPath = Join-Path $OutputDirectory 'input/run.json'
    $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json -AsHashtable
    if ($Suite.Count -gt 1) { $plan.hudSuites = @($Suite) }
    if ($DirectPayload) { $plan.hypervDirectPayload = $true }
    $plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $planPath -Encoding utf8
}
$stageTimer.Stop()
$payloadStats = Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'input') -File -Recurse | Measure-Object -Property Length -Sum
@{elapsedSeconds=[Math]::Round($stageTimer.Elapsed.TotalSeconds,3); payloadBytes=$payloadStats.Sum; payloadFiles=$payloadStats.Count} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'staging-timing.json') -Encoding utf8
if ($PrepareOnly) { return }
$result = if ($useWorker) {
    Invoke-HyperVUiWorker -Configuration $hostConfiguration -OutputDirectory $OutputDirectory -TimeoutSeconds $TimeoutSeconds -QueueTimeoutSeconds $QueueTimeoutSeconds
} else {
    Invoke-HyperVUiRun -BaseVhd $BaseVhd -OutputDirectory $OutputDirectory -TimeoutSeconds $TimeoutSeconds -MemoryInMB $MemoryInMB -ProcessorCount $ProcessorCount -CpuMaximum $CpuMaximum -MaxParallel $MaxParallel -MemoryBudgetInMB $MemoryBudgetInMB -QueueTimeoutSeconds $QueueTimeoutSeconds -KeepVM:$KeepVM
}
if ($result.exitCode -eq 0) {
    foreach ($key in $definitions[$Test].Outputs) {
        if (-not $TestParameters.ContainsKey($key)) { continue }
        $source = Join-Path $OutputDirectory "results/artifacts/$Test-$key.json"
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing $Test output: $source" }
        Copy-Item -LiteralPath $source -Destination ([IO.Path]::GetFullPath($TestParameters[$key])) -Force
    }
}
$displaySuite = if ($TestParameters.ContainsKey('Suite')) { $TestParameters.Suite } else { $Suite -join ',' }
Write-Host "Hyper-V $Test/$displaySuite completed with exit code $($result.exitCode). $($result.error)"
exit $result.exitCode
