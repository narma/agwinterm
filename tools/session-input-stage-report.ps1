param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$reports=foreach($file in Get-ChildItem $Directory -Recurse -Filter terminal-latency.json) {
    $run=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json
    if($run.presentation -ne 'single-owner') { continue }
    $extra=@{}
    foreach($trace in Get-ChildItem $file.DirectoryName -Recurse -Filter 'trace-*.csv') {
        foreach($line in [IO.File]::ReadLines($trace.FullName)) {
            if($line -match '^(9|10),(\d+),(\d+),\d+$') {
                $key="$($Matches[2]):$($Matches[1])"
                if($extra.ContainsKey($key)) { throw "Duplicate transport stage $key" }
                $extra[$key]=[long]$Matches[3]
            }
        }
    }
    $samples=@($run.samples|Select-Object -Skip $run.warmupSamples)
    $stages=foreach($pair in @(@(1,9),@(9,10),@(10,2),@(2,4))) {
        $values=@(foreach($sample in $samples) {
            $ticks=foreach($stage in $pair) {
                if($stage -le 8) { $sample.ticks[$stage] }
                else {
                    $key="$($sample.id):$stage"
                    if(-not $extra.ContainsKey($key)) { throw "Missing transport stage $key" }
                    $extra[$key]
                }
            }
            if($ticks[1] -lt $ticks[0]) { throw 'Transport stage ordering failure' }
            1000.0*($ticks[1]-$ticks[0])/$run.frequency
        })|Sort-Object
        [pscustomobject]@{from=$pair[0];to=$pair[1];count=$values.Count;
            p50Ms=$values[[int][Math]::Ceiling($values.Count*.50)-1];
            p95Ms=$values[[int][Math]::Ceiling($values.Count*.95)-1];
            p99Ms=$values[[int][Math]::Ceiling($values.Count*.99)-1]}
    }
    [pscustomobject]@{run=$file.FullName;stages=@($stages)}
}
@($reports)|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $Output
$reports.stages|Format-Table
