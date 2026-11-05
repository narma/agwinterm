param(
    [Parameter(Mandatory)][string]$Exe,
    [ValidateRange(1,10)][int]$Pairs=5,
    [ValidateRange(1,100000)][int]$LatencySamples=2050,
    [ValidateSet('idle','ascii','unicode','tui','history','fixed-output')][string]$LatencyWorkload='idle',
    [ValidateSet('marker-burst','single-key')][string]$LatencyInputPattern='marker-burst',
    [ValidateSet('in-process','server')][string]$LatencyHost='server'
)
$ErrorActionPreference='Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test SessionPerformance -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$baseline=Join-Path (Split-Path $Exe) 'baseline/Agwinterm.Win32.exe'
if (-not (Test-Path -LiteralPath $baseline)) { throw 'Paired measurements require the pinned baseline package under app/baseline.' }
# Both variants run serially in the same prepared VM. Each child invocation owns
# a fresh app/host job and private namespace; its normal cleanup completes first.
for($pair=1;$pair -le $Pairs;$pair++) {
    foreach($variant in 'legacy','single-owner') {
        $variantExe=if($variant -eq 'legacy'){$baseline}else{$Exe}
        "Pair $pair/$Pairs : $variant / $LatencyWorkload"
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$PSScriptRoot/hud-ui.ps1" -Exe $variantExe `
            -Suite TerminalLatency -EmulatorCore ghostty -LatencyHost $LatencyHost `
            -LatencySamples $LatencySamples -LatencyWorkload $LatencyWorkload -LatencyPresentation $variant -LatencyInputPattern $LatencyInputPattern
        if($LASTEXITCODE -ne 0) { throw "Paired measurement failed: pair=$pair variant=$variant exit=$LASTEXITCODE" }
    }
}
