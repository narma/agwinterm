# Export numeric outcomes only. Raw logs, screenshots and state stay in the run directory.
param(
    [string]$InputDirectory = "$PSScriptRoot/../.revmux",
    [string]$OutputDirectory = "$PSScriptRoot/../artifacts/native-ui"
)
$ErrorActionPreference = 'Stop'
$runs = @()
$index = Join-Path $OutputDirectory 'index.json'
# A repeated export with no runs must not leave an older success report behind.
if (Test-Path -LiteralPath $index) { Remove-Item -LiteralPath $index }
if (-not (Test-Path -LiteralPath $InputDirectory)) { return }
foreach ($directory in Get-ChildItem -LiteralPath $InputDirectory -Directory) {
    if ($directory.Name -notmatch '^[a-z]+-ui-\d{8}T\d{6}-[a-f0-9]{6}$') { continue }
    $log = Join-Path $directory.FullName 'transcript.log'
    $summary = if (Test-Path -LiteralPath $log) {
        [regex]::Matches([IO.File]::ReadAllText($log),
            '(?m)^([A-Za-z]+)-ui: (\d+) checks, (\d+) failed; cleanup complete: (True|False);') |
            Select-Object -Last 1
    }
    $runs += [ordered]@{
        run = $directory.Name
        checks = if ($summary) { [int]$summary.Groups[2].Value } else { $null }
        failed = if ($summary) { [int]$summary.Groups[3].Value } else { $null }
        cleanupComplete = if ($summary) { $summary.Groups[4].Value -eq 'True' } else { $null }
    }
}
if ($runs.Count -gt 0) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    [ordered]@{
        schemaVersion = 1
        commit = $env:GITHUB_SHA
        workflowRun = $env:GITHUB_RUN_ID
        workflowAttempt = $env:GITHUB_RUN_ATTEMPT
        runs = $runs
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $index -Encoding utf8
}
