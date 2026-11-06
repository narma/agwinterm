param(
    [Parameter(Mandatory)][string]$Exe,
    [ValidateRange(1,10)][int]$Pairs=5,
    [ValidateRange(1,2048)][int]$MiB=128,
    [ValidateSet('ownership','trace')][string]$Comparison='ownership',
    [ValidateSet('in-process','server')][string]$LatencyHost='server',
    [ValidateSet('legacy','single-owner')][string]$LatencyPresentation='legacy',
    [ValidateSet('single-owner')][string]$LatencyCandidate='single-owner',
    [switch]$AllCandidates,
    [switch]$RevisionComparison
)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test SessionBulk -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
if($RevisionComparison -and ($AllCandidates -or $Comparison -ne 'ownership')){throw 'Revision comparison requires ownership mode without AllCandidates.'}
$baseline=Join-Path (Split-Path $Exe) 'baseline/Agwinterm.Win32.exe'
if($Comparison -eq 'ownership' -and -not (Test-Path -LiteralPath $baseline)){throw 'Ownership comparison requires a pinned baseline package.'}
for($pair=1;$pair -le $Pairs;$pair++) {
    $variants=if($RevisionComparison){@('before','after')}elseif($Comparison -eq 'ownership'){
        if($AllCandidates){@('legacy','single-owner')}else{@('legacy',$LatencyCandidate)}
    }else{@('off','counters','full')}
    if($AllCandidates -and $Comparison -eq 'ownership'){
        $variants=@(for($i=0;$i -lt $variants.Count;$i++){$variants[($i+$pair-1)%$variants.Count]})
    }
    elseif($pair % 2 -eq 0){[array]::Reverse($variants)}
    foreach($variant in $variants) {
        $variantExe=if($variant -eq 'before' -or ($Comparison -eq 'ownership' -and $variant -eq 'legacy')){$baseline}else{$Exe}
        $presentation=if($RevisionComparison){$LatencyCandidate}elseif($Comparison -eq 'ownership'){$variant}else{$LatencyPresentation}
        $traceMode=if($RevisionComparison -or $Comparison -eq 'ownership'){'off'}else{$variant}
        $label=if($RevisionComparison){$variant}else{''}
        "Pair $pair/$Pairs : $presentation / trace=$traceMode / $MiB MiB"
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$PSScriptRoot/hud-ui.ps1" -Exe $variantExe `
            -Suite TerminalLatency -EmulatorCore ghostty -LatencyHost $LatencyHost -LatencyWorkload fixed-bulk `
            -LatencyBulkMiB $MiB -LatencyPresentation $presentation -LatencyVariant $label -LatencyTraceMode $traceMode
        if($LASTEXITCODE -ne 0){throw "Bulk measurement failed: pair=$pair variant=$variant exit=$LASTEXITCODE"}
    }
}
