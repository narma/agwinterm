param(
    [Parameter(Mandatory)][string]$Directory,
    [ValidateSet('ownership','trace')][string]$Comparison='ownership',
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('legacy','single-owner','ui-owner-journal')][string]$BaselineVariant='legacy'
)
$ErrorActionPreference='Stop'
function Confidence([double[]]$Values) {
    if($Values.Count -lt 5){return $null}
    $mean=($Values|Measure-Object -Average).Average
    $variance=($Values|ForEach-Object {($_-$mean)*($_-$mean)}|Measure-Object -Sum).Sum/($Values.Count-1)
    $df=$Values.Count-1
    $t=if($df -lt 5){2.776445}elseif($df -lt 10){2.570582}elseif($df -lt 20){2.228139}elseif($df -lt 30){2.085963}else{2.042273}
    $margin=$t*[Math]::Sqrt($variance/$Values.Count)
    @{mean=$mean;lower=$mean-$margin;upper=$mean+$margin;runs=$Values.Count;method='paired run Student-t 95%, conservative df bucket'}
}
$runs=@(Get-ChildItem -LiteralPath $Directory -Filter session-bulk.json -Recurse | ForEach-Object {
    $data=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json
    if($data.schema -ne 1 -or $data.bytes -le 0 -or $data.elapsedSeconds -le 0 -or $data.cpuLowerMs -le 0 -or
        $data.cpuUpperMs -lt $data.cpuLowerMs){throw "Invalid completed bulk run: $($_.FullName)"}
    $data|Add-Member -NotePropertyName sourcePath -NotePropertyValue $_.FullName -PassThru
} | Sort-Object utc)
if(-not $runs.Count){throw 'No completed bulk runs'}
$comparisons=@()
$groups=$runs|Group-Object {if($Comparison -eq 'ownership'){"$($_.hostMode)/$($_.traceMode)"}else{"$($_.hostMode)/$($_.presentation)"}}
foreach($group in $groups) {
    $before=@($group.Group|Where-Object {if($Comparison -eq 'ownership'){$_.presentation -eq $BaselineVariant}else{$_.traceMode -eq 'off'}})
    $variants=if($Comparison -eq 'ownership'){@($group.Group.presentation | Where-Object {$_ -ne $BaselineVariant} | Sort-Object -Unique)}else{@('counters','full')}
    foreach($variant in $variants) {
        $after=@($group.Group|Where-Object {if($Comparison -eq 'ownership'){$_.presentation -eq $variant}else{$_.traceMode -eq $variant}})
        if($before.Count -eq 0 -or $before.Count -ne $after.Count){throw "Unpaired bulk runs: $($group.Name)/$variant"}
        foreach($side in @(@{runs=$before},@{runs=$after})) {
            $signatures=@($side.runs|ForEach-Object {
                ($_.binaryHashes.PSObject.Properties|Sort-Object Name|ForEach-Object {"$($_.Name)=$($_.Value)"}) -join "`n"
            }|Sort-Object -Unique)
            if($signatures.Count -ne 1){throw 'Binaries changed within a comparison side'}
        }
        foreach($run in @($before)+@($after)) {
            if($run.bytes -ne $before[0].bytes -or $run.grid.cols -ne $before[0].grid.cols -or $run.grid.rows -ne $before[0].grid.rows -or
                $run.grid.cellWidth -ne $before[0].grid.cellWidth -or $run.grid.cellHeight -ne $before[0].grid.cellHeight){throw 'Bulk workload or grid changed'}
        }
        foreach($metric in 'cpuUpperRatio','cpuLowerRatio','throughputRatio','privateBytesRatio') {
            $ratios=@(for($i=0;$i -lt $before.Count;$i++) {
                switch($metric) {
                    cpuUpperRatio {$after[$i].cpuUpperMs/$before[$i].cpuLowerMs}
                    cpuLowerRatio {$after[$i].cpuLowerMs/$before[$i].cpuUpperMs}
                    throughputRatio {$after[$i].bytesPerSecond/$before[$i].bytesPerSecond}
                    privateBytesRatio {$after[$i].meanPrivateBytes/$before[$i].meanPrivateBytes}
                }
            })
            $ci=Confidence $ratios
            $outcome=if($Comparison -eq 'trace' -or $metric -eq 'privateBytesRatio'){'informational'}
                elseif($null -eq $ci){'inconclusive'}
                elseif($metric -eq 'cpuUpperRatio'){if($ci.upper -le 1.05){'pass'}else{'inconclusive'}}
                elseif($metric -eq 'cpuLowerRatio'){if($ci.lower -gt 1.05){'fail'}else{'inconclusive'}}
                else{if($ci.lower -ge .95){'pass'}elseif($ci.upper -lt .95){'fail'}else{'inconclusive'}}
            $comparisons+=@{group=$group.Name;variant=$variant;metric=$metric;ratio=$ci;perPair=$ratios;outcome=$outcome}
        }
    }
}
@{comparison=$Comparison;baselineVariant=$BaselineVariant;runs=$runs;comparisons=$comparisons;cpuScope='UI plus owner, excludes child and conhost';
  endpoint='cached completion readback; 100 ms observation interval; no latency claim';memoryPolicy='informational bounded overhead; growth/leaks require separate soak'} |
    ConvertTo-Json -Depth 9|Set-Content -LiteralPath $Output
$comparisons|Select-Object group,variant,metric,outcome|Format-Table
