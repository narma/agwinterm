param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
function Percentile($values,$p) {
    $sorted=@($values|Sort-Object)
    $sorted[[Math]::Max(0,[Math]::Ceiling($sorted.Count*$p)-1)]
}
$runs=@(foreach($file in Get-ChildItem $Directory -Recurse -Filter terminal-latency.json) {
    $run=Get-Content $file.FullName -Raw|ConvertFrom-Json
    $samples=@($run.samples|Where-Object id -gt $run.warmupSamples)
    $trace=Join-Path $file.DirectoryName 'latency'
    $segments=@(foreach($pair in @(@(1,4),@(4,5),@(5,6),@(6,7),@(7,8))) {
        $values=@($samples|ForEach-Object {1000.0*($_.ticks[$pair[1]]-$_.ticks[$pair[0]])/$run.frequency})
        @{segment="T$($pair[0])-T$($pair[1])";p50Ms=(Percentile $values .5);p95Ms=(Percentile $values .95);p99Ms=(Percentile $values .99)}
    })
    $processes=@(foreach($metrics in Get-ChildItem $trace -Filter 'metrics-*.csv') {
        $rows=@(Import-Csv $metrics.FullName)
        $pidText=$metrics.BaseName.Substring(8)
        $role=if([int]$pidText -eq $run.uiPid){'ui'}elseif(Select-String (Join-Path $trace "trace-$pidText.csv") -Pattern '^2,\d+,\d+,\d+$' -Quiet){'host'}else{'fixture'}
        $last=@{}
        $timing=Join-Path $trace "timings-$pidText.csv"
        if(Test-Path $timing){Import-Csv $timing|ForEach-Object {$last[[int]$_.stage]=$_}}
        $stages=@(foreach($stage in 20,21,23,24,30,31,40,42,43,50,51,53,54) {
            if(-not $last.ContainsKey($stage)){continue}
            $row=$last[$stage]
            $scale=if($stage -eq 23){1.0}else{1000.0/$run.frequency}
            @{stage=$stage;count=[long]$row.count;total=[double]$row.total*$scale;maximum=[double]$row.maximum*$scale;unit=$(if($stage -eq 23){'bytes'}else{'elapsed-ms'})}
        })
        @{pid=$pidText;role=$role;cpuSeconds=([double]$rows[-1].cpuTicks-[double]$rows[0].cpuTicks)/1e7;
          allocatedBytes=[double]$rows[-1].managedAllocated-[double]$rows[0].managedAllocated;stages=$stages}
    })
    @{source=$file.FullName;presentation=$run.presentation;checkpoints=$run.journalCheckpoints;segments=$segments;processes=$processes}
})
@{schema=1;caveat='Diagnostic whole-process sampled intervals include startup/warmup; not the fixed-work CPU acceptance interval. Stage elapsed times overlap and are not CPU attribution. T4-T5 includes producer output generation and transport/feed, T5-T6 waits for frame capture. Stage 23 counts output bytes, not elapsed ticks.';runs=$runs}|
    ConvertTo-Json -Depth 10|Set-Content $Output
$runs|ForEach-Object {"$($_.presentation)/$($_.checkpoints)"; $_.processes|Select-Object role,cpuSeconds,allocatedBytes|Format-Table; $_.segments|ForEach-Object {[pscustomobject]$_}|Format-Table}
