$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/ui-evidence-tests'))
$scratch = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
$inputPath = Join-Path $scratch 'input'
$outputPath = Join-Path $scratch 'output'
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
try {
    foreach ($run in @('ask-ui-20261025T223104-5502c4', 'menu-ui-20261025T232750-b20623',
            'navigation-ui-20261025T232242-b5ddff', 'not-a-test-run')) {
        New-Item -ItemType Directory -Force (Join-Path $inputPath $run) | Out-Null
    }
    $ask = Join-Path $inputPath 'ask-ui-20261025T223104-5502c4'
    @'
PRIVATE: user=alice computer=workstation path=C:\Users\alice secret=do-not-export
PASS an assertion with private details
Ask-ui: 30 checks, 0 failed; cleanup complete: True; artifacts C:\Users\alice\checkout
'@ | Set-Content (Join-Path $ask 'transcript.log')
    'not-an-image-private-payload' | Set-Content (Join-Path $ask 'capture.png')
    'private-state' | Set-Content (Join-Path $ask 'lease.json')
    'Menu-ui: 3 checks, 4 failed; cleanup complete: False; artifacts private-path' |
        Set-Content (Join-Path $inputPath 'menu-ui-20261025T232750-b20623/transcript.log')
    'interrupted before summary; secret=do-not-export' |
        Set-Content (Join-Path $inputPath 'navigation-ui-20261025T232242-b5ddff/transcript.log')
    'Ask-ui: 999 checks, 0 failed; cleanup complete: True;' |
        Set-Content (Join-Path $inputPath 'not-a-test-run/transcript.log')
    & "$PSScriptRoot/export-ui-evidence.ps1" -InputDirectory $inputPath -OutputDirectory $outputPath
    $json = Get-Content (Join-Path $outputPath 'index.json') -Raw
    $report = $json | ConvertFrom-Json
    Assert ($report.runs.Count -eq 3) 'Only fixture run directories should be exported.'
    $passed = $report.runs | Where-Object run -like 'ask-*'
    Assert ($passed.checks -eq 30 -and $passed.failed -eq 0 -and $passed.cleanupComplete) 'Passing summary was not preserved.'
    $failed = $report.runs | Where-Object run -like 'menu-*'
    Assert ($failed.checks -eq 3 -and $failed.failed -eq 4 -and -not $failed.cleanupComplete) 'Fixture failure counts/cleanup must be preserved.'
    $incomplete = $report.runs | Where-Object run -like 'navigation-*'
    Assert ($null -eq $incomplete.checks -and $null -eq $incomplete.failed -and $null -eq $incomplete.cleanupComplete) 'Interrupted runs must not become successes.'
    Assert ($json -notmatch 'alice|workstation|secret|private|C:') 'Private text leaked into the export.'
    Assert (@(Get-ChildItem $outputPath -Recurse -File).Count -eq 1) 'Raw logs, PNGs or state were copied.'
    & "$PSScriptRoot/export-ui-evidence.ps1" -InputDirectory (Join-Path $scratch 'missing') -OutputDirectory $outputPath
    Assert (-not (Test-Path (Join-Path $outputPath 'index.json'))) 'Absent input left stale evidence.'
    $empty = New-Item -ItemType Directory (Join-Path $scratch 'empty')
    & "$PSScriptRoot/export-ui-evidence.ps1" -InputDirectory $empty.FullName -OutputDirectory $outputPath
    Assert (-not (Test-Path (Join-Path $outputPath 'index.json'))) 'Empty input should produce no report.'
    'UI evidence export: 8 checks passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the evidence-test scratch directory.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
