# Called before any UI, clipboard, process or configuration work in an entry point.
param([Parameter(Mandatory)][string]$Test, [System.Collections.IDictionary]$Parameters = @{})
$ErrorActionPreference = 'Stop'
$inGuest = $env:AGWINTERM_SANDBOX_UI -eq '1' -and $env:USERNAME -eq 'WDAGUtilityAccount'
$inCi = $env:GITHUB_ACTIONS -eq 'true' -and $env:CI -eq 'true'
if ($inGuest -or $inCi) { return $false }
if ($Test -in @('WslControl','WslBenchmark','WslZmxDependency','WslZmxSurvival','WslZmxIdentity','WslAgtermRemote') -and $env:AGWINTERM_WSL_UI_DESKTOP) {
    if (-not ('WslUiDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/wsl-ui-desktop.cs" }
    if (-not [WslUiDesktop]::IsPrivate($env:AGWINTERM_WSL_UI_DESKTOP)) { throw 'WSL desktop marker does not match a private non-input desktop.' }
    return $false
}
$options = @{ Test=$Test; Background=$true; TestParameters=@{} }
if ($Test -eq 'Hud' -and 'Suite' -notin $Parameters.Keys) { $options.TestParameters.Suite = 'Hud' }
foreach ($key in $Parameters.Keys) {
    switch ($key) {
        'Exe' { $options.Exe = $Parameters[$key] }
        'Ctl' { $options.CtlExe = $Parameters[$key] }
        'GateExe' { $options.GateExe = $Parameters[$key] }
        'TokenOwner' { }
        'Strict' { }
        default {
            $value = $Parameters[$key]
            if ($value -is [Management.Automation.SwitchParameter]) { $value = [bool]$value }
            $options.TestParameters[$key] = $value
        }
    }
}
Write-Host "Routing local $Test UI tests to the background test runner."
try {
    if ($Test -in @('WslControl','WslBenchmark','WslZmxDependency','WslZmxSurvival','WslZmxIdentity','WslAgtermRemote')) {
        & "$PSScriptRoot/wsl-ui.ps1" -Test $Test -TestParameters $options.TestParameters | Out-Host
    } else {
        & "$PSScriptRoot/sandbox-ui.ps1" @options | Out-Host
    }
    $global:LASTEXITCODE = $LASTEXITCODE
} catch {
    # A failing child script must never make a caller with ErrorActionPreference
    # Continue fall through to its UI body on the host desktop.
    Write-Error $_ -ErrorAction Continue
    $global:LASTEXITCODE = 1
}
return $true
