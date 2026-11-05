param(
    [Parameter(Mandatory)][string]$Exe,
    [ValidateRange(1,10)][int]$Pairs=5,
    [ValidateRange(1,200)][int]$LatencySamples=200
)
$ErrorActionPreference='Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test JournalMeasurements -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$baseline=Join-Path (Split-Path $Exe) 'baseline/Agwinterm.Win32.exe'
if(-not (Test-Path -LiteralPath $baseline)){throw 'Journal comparison requires the pinned baseline.'}
& "$PSScriptRoot/journal-contracts.ps1" -Exe $Exe
if($LASTEXITCODE -ne 0){throw 'Journal contracts failed before measurement'}
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$PSScriptRoot/hud-ui.ps1" -Exe $Exe -Suite JournalOwner -EmulatorCore ghostty
if($LASTEXITCODE -ne 0){throw 'Journal owner lifetime failed before measurement'}
# Keep each no-checkpoint fixed-output run below the deliberate 64 MiB cap.
# Rotate variant order between pairs to avoid consistently favoring the last run.
$variants=@('legacy','single-owner','ui-owner-journal/off','ui-owner-journal/on')
for($pair=0;$pair -lt $Pairs;$pair++){
    for($i=0;$i -lt $variants.Count;$i++){
        $variant=$variants[($i+$pair)%$variants.Count]
        $presentation=$variant.Split('/')[0]
        $checkpoints=if($variant.EndsWith('/off')){'off'}else{'on'}
        $variantExe=if($variant -eq 'legacy'){$baseline}else{$Exe}
        "Pair $($pair+1)/$Pairs : $variant / fixed-output"
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$PSScriptRoot/hud-ui.ps1" -Exe $variantExe `
            -Suite TerminalLatency -EmulatorCore ghostty -LatencyHost server -LatencyWorkload fixed-output `
            -LatencySamples $LatencySamples -LatencyPresentation $presentation -LatencyInputPattern single-key -JournalCheckpoints $checkpoints
        if($LASTEXITCODE -ne 0){throw "Journal comparison failed: pair=$pair variant=$variant exit=$LASTEXITCODE"}
    }
}
