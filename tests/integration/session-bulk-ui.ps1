param(
    [Parameter(Mandatory)][string]$Exe,
    [ValidateRange(1,10)][int]$Pairs=5,
    [ValidateRange(1,2048)][int]$MiB=128,
    [ValidateSet('ownership','trace')][string]$Comparison='ownership',
    [ValidateSet('in-process','server')][string]$LatencyHost='server',
    [ValidateSet('legacy','single-owner')][string]$LatencyPresentation='legacy'
)
$ErrorActionPreference='Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test SessionBulk -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$baseline=Join-Path (Split-Path $Exe) 'baseline/Agwinterm.Win32.exe'
if($Comparison -eq 'ownership' -and -not (Test-Path -LiteralPath $baseline)){throw 'Ownership comparison requires a pinned baseline package.'}
for($pair=1;$pair -le $Pairs;$pair++) {
    $variants=if($Comparison -eq 'ownership'){@('legacy','single-owner')}else{@('off','counters','full')}
    foreach($variant in $variants) {
        $variantExe=if($Comparison -eq 'ownership' -and $variant -eq 'legacy'){$baseline}else{$Exe}
        $presentation=if($Comparison -eq 'ownership'){$variant}else{$LatencyPresentation}
        $traceMode=if($Comparison -eq 'ownership'){'off'}else{$variant}
        "Pair $pair/$Pairs : $presentation / trace=$traceMode / $MiB MiB"
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$PSScriptRoot/hud-ui.ps1" -Exe $variantExe `
            -Suite TerminalLatency -EmulatorCore ghostty -LatencyHost $LatencyHost -LatencyWorkload fixed-bulk `
            -LatencyBulkMiB $MiB -LatencyPresentation $presentation -LatencyTraceMode $traceMode
        if($LASTEXITCODE -ne 0){throw "Bulk measurement failed: pair=$pair variant=$variant exit=$LASTEXITCODE"}
    }
}
