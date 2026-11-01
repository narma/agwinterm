param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
Add-Type -Path "$PSScriptRoot/wsl-ui-desktop.cs"
$run = Get-Content -LiteralPath $Plan -Raw | ConvertFrom-Json -AsHashtable
if (-not [WslUiDesktop]::IsPrivate($run.desktop)) { throw 'WSL worker is not on its private non-input desktop; refusing UI work.' }
$env:AGWINTERM_WSL_UI_DESKTOP = $run.desktop
$env:GITHUB_ACTIONS = ''
$env:CI = ''
$env:AGWINTERM_SANDBOX_UI = ''
$definitions = Import-PowerShellDataFile "$PSScriptRoot/sandbox-ui-tests.psd1"
$definition = $definitions[$run.test]
if (-not $definition.Wsl) { throw 'The private desktop worker only accepts registered WSL tests.' }
$code = 1
$transcribing = $false
try {
    $null = Start-Transcript -LiteralPath (Join-Path $run.output 'test.log')
    $transcribing = $true
    $parameters = $run.parameters
    foreach ($key in $parameters.Keys) {
        if ($key -notin $definition.Options) { throw "Unsupported WSL test parameter: $key" }
    }
    $global:LASTEXITCODE = 0
    & (Join-Path $run.repo $definition.Path) @parameters
    $succeeded = $?
    $code = $LASTEXITCODE
    if (-not $succeeded -and $code -eq 0) { $code = 1 }
} catch {
    Write-Error $_ -ErrorAction Continue
} finally {
    if ($transcribing) { $null = Stop-Transcript }
    @{test=$run.test; exitCode=$code; desktop=[WslUiDesktop]::CurrentDesktop; inputDesktop=[WslUiDesktop]::InputDesktop} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run.output 'result.json')
}
exit $code
