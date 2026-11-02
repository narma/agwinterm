# Real private-desktop process/window probes; no WSL distribution or app required.
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('agwinterm-wsl-ui-' + [guid]::NewGuid().ToString('N'))
$integration = Join-Path $temporary 'tests/integration'
$null = New-Item -ItemType Directory -Path $integration -Force
$checks = 0
function Check([string]$Name,[bool]$Ok) {
    $script:checks++
    if (-not $Ok) { throw "FAIL $Name" }
    Write-Host "PASS $Name"
}
function Run([string]$Script,[string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($arg in @('-NoProfile','-File',$Script) + $Arguments) { $start.ArgumentList.Add($arg) }
    foreach ($name in @('CI','GITHUB_ACTIONS','AGWINTERM_SANDBOX_UI','AGWINTERM_WSL_UI_DESKTOP')) { $start.Environment.Remove($name) | Out-Null }
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Fixture timed out' }
        @{code=$process.ExitCode; output=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()}
    } finally { $process.Dispose() }
}
try {
    Copy-Item "$PSScriptRoot/wsl-ui.ps1","$PSScriptRoot/wsl-ui-worker.ps1","$PSScriptRoot/wsl-ui-desktop.cs","$PSScriptRoot/sandbox-ui-entry.ps1","$PSScriptRoot/sandbox-ui-tests.psd1" -Destination $integration
    if (-not ('WslUiDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/wsl-ui-desktop.cs" }
    $inputBefore=[WslUiDesktop]::InputDesktop
    @'
param([string]$Distro,[switch]$NativeHooks)
$ErrorActionPreference='Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslControl -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
Add-Type -AssemblyName System.Windows.Forms
$window=[Windows.Forms.Form]::new()
try {
    $window.Text='Private desktop fixture'; $window.Show()
    [Windows.Forms.Application]::DoEvents()
    # A visible real window must still belong to the non-input desktop.
    if (-not $window.Visible -or -not [WslUiDesktop]::IsPrivate($env:AGWINTERM_WSL_UI_DESKTOP)) { throw 'window isolation failed' }
    $child=Start-Process (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 120') -WindowStyle Hidden -PassThru
    @{distro=$Distro; native=[bool]$NativeHooks; child=$child.Id; window=$window.Handle.ToInt64()} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot '../../probe.json')
    Write-Host 'PRIVATE_WINDOW_PROBE_PASSED'
} finally { $window.Dispose() }
exit 7
'@ | Set-Content (Join-Path $integration 'wsl-control.ps1')
    $runner=Join-Path $integration 'wsl-ui.ps1'
    $launch=Join-Path $temporary 'launch.ps1'
    @'
param([string]$Output)
& "$PSScriptRoot/tests/integration/wsl-ui.ps1" -Test WslControl -TestParameters @{Distro='quoted '' ; $(never code) текст';NativeHooks=$true} -OutputDirectory $Output
exit $LASTEXITCODE
'@ | Set-Content $launch
    $output=Join-Path $temporary 'result'
    $run=Run $launch @('-Output',$output)
    if ($run.code -ne 7) { Write-Host $run.output }
    Check 'worker exit code is preserved, including failure' ($run.code -eq 7)
    $result=Get-Content (Join-Path $output 'result.json') -Raw | ConvertFrom-Json
    Check 'real visible probe window stays on the private non-input desktop' ($result.desktop -like 'AgwintermWsl-*' -and $result.inputDesktop -eq $inputBefore -and $run.output.Contains('PRIVATE_WINDOW_PROBE_PASSED'))
    Check 'input desktop is unchanged after the run' ([WslUiDesktop]::InputDesktop -eq $inputBefore)
    $probe=Get-Content (Join-Path $temporary 'probe.json') -Raw | ConvertFrom-Json
    Check 'quotes, non-ASCII and switches survive JSON as data' ($probe.distro -ceq 'quoted '' ; $(never code) текст' -and $probe.native)
    Check 'kill-on-close job reaps a remaining child' ($null -eq (Get-Process -Id $probe.child -ErrorAction SilentlyContinue))
    Check 'transcript is exported on test failure' ((Get-Content (Join-Path $output 'test.log') -Raw).Contains('PRIVATE_WINDOW_PROBE_PASSED'))
    # Every WSL entry point routes; no test body is allowed on the input desktop.
    @'
param([string]$Test,[hashtable]$TestParameters)
@{test=$Test; parameters=$TestParameters} | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot '../../route.json')
exit 9
'@ | Set-Content (Join-Path $integration 'wsl-ui.ps1')
    foreach ($name in @('WslControl','WslBenchmark','WslZmxDependency','WslZmxSurvival','WslZmxIdentity')) {
        $route=Run (Join-Path $integration 'sandbox-ui-entry.ps1') @('-Test',$name)
        # Calling a routing helper alone returns an object; the caller owns exit.
        $record=Get-Content (Join-Path $temporary 'route.json') -Raw | ConvertFrom-Json
        Check "$name entry chooses the private desktop runner" ($record.test -eq $name)
    }
    @'
$env:AGWINTERM_WSL_UI_DESKTOP='AgwintermWsl-forged'
& "$PSScriptRoot/tests/integration/sandbox-ui-entry.ps1" -Test WslControl
'@ | Set-Content (Join-Path $temporary 'forged.ps1')
    Check 'marker alone cannot allow UI on the input desktop' ((Run (Join-Path $temporary 'forged.ps1') @()).code -ne 0)
    Copy-Item "$PSScriptRoot/wsl-ui.ps1" -Destination $integration
    @'
$ErrorActionPreference='Stop'
$child=Start-Process (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 120') -WindowStyle Hidden -PassThru
$child.Id | Set-Content (Join-Path $PSScriptRoot '../../timeout-child.txt')
Start-Sleep -Seconds 120
'@ | Set-Content (Join-Path $integration 'wsl-hook-bench.ps1')
    $timeout=Run $runner @('-Test','WslBenchmark','-TimeoutSeconds','10','-OutputDirectory',(Join-Path $temporary 'timeout'))
    Check 'timeout fails rather than hanging or retrying on input desktop' ($timeout.code -ne 0 -and $timeout.output.Contains('timed out'))
    $childId=[int](Get-Content (Join-Path $temporary 'timeout-child.txt'))
    Check 'timeout reaps the test process tree' ($null -eq (Get-Process -Id $childId -ErrorAction SilentlyContinue))
    Check 'unknown tests fail before creating a desktop worker' ((Run $runner @('-Test','NotRegistered')).code -ne 0)
    @'
& "$PSScriptRoot/tests/integration/wsl-ui.ps1" -Test WslControl -TestParameters @{Unsupported='refuse'} -OutputDirectory "$PSScriptRoot/invalid"
'@ | Set-Content (Join-Path $temporary 'invalid.ps1')
    Check 'unknown parameters fail before creating a desktop worker' ((Run (Join-Path $temporary 'invalid.ps1') @()).code -ne 0 -and -not (Test-Path (Join-Path $temporary 'invalid')))
    Write-Host "$checks private desktop checks passed"
} finally {
    $full=[IO.Path]::GetFullPath($temporary)
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if (-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture cleanup path' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
exit 0
