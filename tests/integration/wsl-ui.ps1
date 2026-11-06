# Use host WSL, with every test-owned Windows process on a private desktop.
param(
    [Parameter(Mandatory)][ValidateSet('WslControl','WslBenchmark','WslTuiRendering','WslZmxDependency','WslZmxSurvival','WslZmxIdentity','WslAgtermRemote','WslOriginExport')][string]$Test,
    [hashtable]$TestParameters = @{},
    [string]$OutputDirectory,
    [ValidateRange(1,7200)][int]$TimeoutSeconds = 1200
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$definition = (Import-PowerShellDataFile "$PSScriptRoot/sandbox-ui-tests.psd1")[$Test]
foreach ($key in $TestParameters.Keys) {
    if ($key -notin $definition.Options) { throw "Unsupported WSL test parameter: $key" }
    if ($TestParameters[$key] -isnot [string] -and $TestParameters[$key] -isnot [bool] -and $TestParameters[$key] -isnot [int]) { throw "Unsupported value for WSL test parameter: $key" }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts/wsl-ui/' + (Get-Date -Format 'yyyyMMddTHHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'WSL UI output directory must be new.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
if (-not ('WslUiDesktop' -as [type])) { Add-Type -Path "$PSScriptRoot/wsl-ui-desktop.cs" }
$desktop = 'AgwintermWsl-' + [guid]::NewGuid().ToString('N')
$plan = Join-Path $OutputDirectory 'run.json'
@{test=$Test; parameters=$TestParameters; repo=$repo; output=$OutputDirectory; desktop=$desktop} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $plan
$worker = "$PSScriptRoot/wsl-ui-worker.ps1"
$bootstrapLog = Join-Path $OutputDirectory 'worker.log'
$command = "& '" + $worker.Replace("'", "''") + "' -Plan '" + $plan.Replace("'", "''") + "' *> '" + $bootstrapLog.Replace("'", "''") + "'; exit " + '$LASTEXITCODE'
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$owner = $null
try {
    $owner = [WslUiDesktop]::Start((Join-Path $PSHOME 'pwsh.exe'),$encoded,$repo,$desktop)
    Write-Host "$Test running on private desktop $desktop; logs: $OutputDirectory"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $owner.HasExited) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "WSL UI test timed out; terminating its private process tree. Logs: $OutputDirectory" }
        Start-Sleep -Milliseconds 250
    }
    $resultFile = Join-Path $OutputDirectory 'result.json'
    if (-not (Test-Path -LiteralPath $resultFile)) {
        if (Test-Path -LiteralPath $bootstrapLog) { Get-Content -LiteralPath $bootstrapLog | Out-Host }
        throw "WSL worker exited without a result ($($owner.ExitCode)). Logs: $OutputDirectory"
    }
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    if ($result.desktop -ne $desktop -or $result.inputDesktop -eq $desktop) { throw 'WSL UI desktop isolation check failed.' }
    if (Test-Path (Join-Path $OutputDirectory 'test.log')) { Get-Content (Join-Path $OutputDirectory 'test.log') | Out-Host }
    $code = [int]$result.exitCode
    if ($owner.ExitCode -ne 0) { $code = [int]$owner.ExitCode }
} finally {
    # Kill-on-close also reaps app processes left behind by a failing test.
    if ($owner) { $owner.Dispose() }
}
exit $code
