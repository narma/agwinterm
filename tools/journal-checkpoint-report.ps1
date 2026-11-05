param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$runs=@(foreach($file in Get-ChildItem -LiteralPath $Directory -Recurse -Filter terminal-latency.json){
    $run=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if($run.presentation -ne 'ui-owner-journal'){continue}
    $trace=Join-Path $file.DirectoryName 'latency'
    $stages=@(foreach($timing in Get-ChildItem -LiteralPath $trace -Filter 'timings-*.csv'){
        $last=@{}
        Import-Csv -LiteralPath $timing.FullName | ForEach-Object {if([int]$_.stage -in 40,42,43,50,51,52,53,54,55){$last[[int]$_.stage]=$_}}
        foreach($stage in @($last.Keys | Sort-Object)){
            $row=$last[$stage];$count=[long]$row.count
            $scale=if($stage -in 52,55){1.0}else{1000.0/[double]$run.frequency}
            $unit=if($stage -in 52,55){'bytes'}else{'ms'}
            $seen=0L;$p95=$null
            for($bucket=0;$bucket -lt 64;$bucket++){
                $seen += [long]$row."log2_$bucket"
                if($seen -ge [Math]::Ceiling($count*0.95)){$p95=([Math]::Pow(2,$bucket+1)-1)*$scale;break}
            }
            [pscustomobject]@{process=$timing.BaseName;stage=$stage;count=$count;unit=$unit;
                mean=[double]$row.total/$count*$scale;maximum=[double]$row.maximum*$scale;p95HistogramUpperBound=$p95}
        }
    })
    $memory=@(foreach($metrics in Get-ChildItem -LiteralPath $trace -Filter 'metrics-*.csv'){
        $rows=@(Import-Csv -LiteralPath $metrics.FullName)
        [pscustomobject]@{process=$metrics.BaseName;peakPrivateBytes=($rows.privateBytes | ForEach-Object {[long]$_} | Measure-Object -Maximum).Maximum;
            peakOwners=($rows.owners | ForEach-Object {[long]$_} | Measure-Object -Maximum).Maximum;
            peakReplicas=($rows.replicas | ForEach-Object {[long]$_} | Measure-Object -Maximum).Maximum}
    })
    [pscustomobject]@{source=$file.FullName;checkpoints=$run.journalCheckpoints;stages=$stages;processMemory=$memory}
})
@{schema=1;stageMeaning=@{'40'='input queue residence through writer lock acquisition';'42'='writer session-lock acquisition';'43'='writer session-lock hold';'50'='capture acquisition including in-flight paint wait';'51'='native capture lock hold';'52'='snapshot bytes';'53'='upload and input-cut publication wait';'54'='broker commit lock hold';'55'='retained journal high-water bytes'};
    caveat='Cumulative sampled diagnostic histograms; p95 is a log2 bucket upper bound, not an exact percentile. Process memory peaks include the echo fixture and must not be summed as simultaneous UI+broker peak.';runs=$runs} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output
$runs | ForEach-Object {$_.stages} | Format-Table process,stage,count,unit,mean,maximum,p95HistogramUpperBound
