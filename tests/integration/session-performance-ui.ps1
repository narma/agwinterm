param(
    [Parameter(Mandatory)][string]$Exe,
    [ValidateRange(1,10)][int]$Pairs=5,
    [ValidateRange(0,9)][int]$PairOffset=0,
    [ValidateSet('single-owner')][string]$LatencyCandidate='single-owner',
    [ValidateRange(1,100000)][int]$LatencySamples=2050,
    [ValidateSet('idle','ascii','unicode','tui','history','fixed-output')][string]$LatencyWorkload='idle',
    [ValidateSet('marker-burst','single-key')][string]$LatencyInputPattern='marker-burst',
    [ValidateSet('in-process','server')][string]$LatencyHost='server',
    [switch]$AllCandidates,
    [switch]$RevisionComparison
)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test SessionPerformance -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
if($RevisionComparison -and $AllCandidates){throw 'Choose revision comparison or architecture comparison, not both.'}
$baseline=Join-Path (Split-Path $Exe) 'baseline/Agwinterm.Win32.exe'
if (-not (Test-Path -LiteralPath $baseline)) { throw 'Paired measurements require the pinned baseline package under app/baseline.' }
# Both variants run serially in the same prepared VM. Each child invocation owns
# a fresh app/host job and private namespace; its normal cleanup completes first.
for($index=1;$index -le $Pairs;$index++) {
    $pair=$PairOffset+$index
    $variants=if($RevisionComparison){@('before','after')}elseif($AllCandidates){@('legacy','single-owner')}else{@('legacy',$LatencyCandidate)}
    if($AllCandidates){$variants=@(for($i=0;$i -lt $variants.Count;$i++){$variants[($i+$pair-1)%$variants.Count]})}
    elseif($pair % 2 -eq 0){[array]::Reverse($variants)}
    foreach($variant in $variants) {
        $variantExe=if($variant -in @('legacy','before')){$baseline}else{$Exe}
        $presentation=if($RevisionComparison){$LatencyCandidate}else{$variant}
        $label=if($RevisionComparison){$variant}else{''}
        "Pair $pair : $variant / $LatencyWorkload"
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$PSScriptRoot/hud-ui.ps1" -Exe $variantExe `
            -Suite TerminalLatency -EmulatorCore ghostty -LatencyHost $LatencyHost `
            -LatencySamples $LatencySamples -LatencyWorkload $LatencyWorkload -LatencyPresentation $presentation -LatencyVariant $label -LatencyInputPattern $LatencyInputPattern
        if($LASTEXITCODE -ne 0) { throw "Paired measurement failed: pair=$pair variant=$variant exit=$LASTEXITCODE" }
    }
}
