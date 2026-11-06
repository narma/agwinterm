param(
    [Parameter(Mandatory)][string]$BaselineDirectory,
    [string]$CandidateDirectory,
    [switch]$NoiseStudy,
    [switch]$MixedPairs,
    [string]$BaselineVariant,
    [string]$CandidateVariant,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
if (-not $NoiseStudy -and -not $CandidateDirectory -and -not $MixedPairs) { throw 'Specify a candidate, -MixedPairs or -NoiseStudy' }
function Percentile([double[]]$Values,[double]$P) {
    $sorted = @($Values | Sort-Object)
    if (-not $sorted.Count) { throw 'No percentile samples' }
    $sorted[[Math]::Max(0,[Math]::Ceiling($P*$sorted.Count)-1)]
}
function Mean([double[]]$Values) { ($Values | Measure-Object -Average).Average }
function Confidence([double[]]$Values) {
    # Two-sided 95% Student-t interval over independent paired RUNS, not keystrokes.
    if ($Values.Count -lt 5) { return $null }
    $mean = Mean $Values
    $squares = @($Values | ForEach-Object { ($_-$mean)*($_-$mean) })
    $sd = [Math]::Sqrt(($squares | Measure-Object -Sum).Sum/($Values.Count-1))
    # Conservative df bucket: never use a smaller critical value than the actual df.
    $df = $Values.Count-1
    $t = if ($df -lt 5) {2.776445} elseif ($df -lt 10) {2.570582} elseif ($df -lt 20) {2.228139} elseif ($df -lt 30) {2.085963} else {2.042273}
    $margin = $t*$sd/[Math]::Sqrt($Values.Count)
    @{mean=$mean;lower=$mean-$margin;upper=$mean+$margin;runs=$Values.Count;method='paired run Student-t 95%, conservative df bucket'}
}
function ReadRuns([string]$Directory) {
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter terminal-latency.json -Recurse) {
        $data = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $inputPattern=if($data.inputPattern){$data.inputPattern}else{'marker-burst'}
        $samples = @($data.samples | Where-Object id -gt $data.warmupSamples)
        if (-not $samples.Count) { throw "No post-warmup samples: $($file.FullName)" }
        $latencies = [ordered]@{}
        foreach ($metric in 'inputMs','presentMs','injectionMs') {
            foreach ($p in 50,95,99) { $latencies["$metric-p$p"] = Percentile @($samples.$metric) ($p/100.0) }
        }
        # Supplemental whole-burst observations retain the original Enter gates.
        # They expose any blocking work during marker priming instead of moving T0.
        if($samples[0].PSObject.Properties.Name -contains 'burstInjectionMs') {
            foreach($metric in 'preambleInjectionMs','burstInjectionMs') {
                foreach($p in 50,95,99){$latencies["$metric-p$p"]=Percentile @($samples.$metric) ($p/100.0)}
            }
        }
        # Determine host/UI identities from their actual stage events, excluding the echo child.
        $traceDirectory = Join-Path $file.DirectoryName 'latency'
        $ownerPids = @()
        foreach ($trace in Get-ChildItem -LiteralPath $traceDirectory -Filter 'trace-*.csv') {
            if (Select-String -LiteralPath $trace.FullName -Pattern '^2,\d+,\d+,\d+$' -Quiet) {
                $ownerPids += [int]($trace.BaseName.Substring(6))
            }
        }
        $processIds = @(@([int]$data.uiPid)+$ownerPids | Sort-Object -Unique)
        $expectedProcesses = if ($data.hostMode -eq 'server') {2} else {1}
        if ($processIds.Count -ne $expectedProcesses) { throw "Unexpected owner/UI process topology: $($file.FullName)" }
        $cpuMs = 0.0; $cpuUpperMs = 0.0; $privateBytes = 0.0; $nativeBytes = 0.0; $feedBytes = 0.0; $metricsComplete = $true
        $pageBytes=0.0; $imageBytes=0.0; $pageMetricsComplete=$true
        $start = [long]$samples[0].ticks[0]; $end = [long]$samples[-1].ticks[8]
        foreach ($processId in $processIds) {
            $metricPath = Join-Path $traceDirectory "metrics-$processId.csv"
            if (-not (Test-Path $metricPath)) { $metricsComplete = $false; continue }
            $allRows = @(Import-Csv -LiteralPath $metricPath)
            $rows = @($allRows | Where-Object { [long]$_.timestamp -ge $start -and [long]$_.timestamp -le $end })
            if ($rows.Count -lt 2) { $metricsComplete = $false; continue }
            $cpuMs += ([double]$rows[-1].cpuTicks-[double]$rows[0].cpuTicks)/10000.0
            $left = @($allRows | Where-Object { [long]$_.timestamp -le $start } | Select-Object -Last 1)
            $right = @($allRows | Where-Object { [long]$_.timestamp -ge $end } | Select-Object -First 1)
            if ($left.Count -ne 1 -or $right.Count -ne 1) { $metricsComplete = $false }
            else { $cpuUpperMs += ([double]$right[0].cpuTicks-[double]$left[0].cpuTicks)/10000.0 }
            $privateBytes += Mean @($rows | ForEach-Object { [double]$_.privateBytes })
            if ($rows[0].nativeCurrent -eq '') { $metricsComplete = $false }
            $nativeBytes += Mean @($rows | ForEach-Object { [double]$_.nativeCurrent })
            if ($null -eq $rows[0].nativePageResident -or $rows[0].nativePageResident -eq '') { $pageMetricsComplete=$false }
            else {
                $pageBytes += Mean @($rows | ForEach-Object { [double]$_.nativePageResident })
                $imageBytes += Mean @($rows | ForEach-Object { [double]$_.nativeImageBytes })
            }
            $feedBytes += [double]$rows[-1].ownerFeedBytes-[double]$rows[0].ownerFeedBytes
        }
        $workBytes = [double]$data.producerBytesPerEvent * $samples.Count
        $activeSeconds = ($samples | ForEach-Object { ([long]$_.ticks[5]-[long]$_.ticks[4])/[double]$data.frequency } | Measure-Object -Sum).Sum
        [pscustomobject]@{path=$file.FullName;utc=$data.utc;key="$($data.hostMode)/$($data.workload)/$inputPattern";inputPattern=$inputPattern;
            mode=$data.hostMode;workload=$data.workload;presentation=$(if($data.measurementVariant){$data.measurementVariant}else{$data.presentation});journalCheckpoints=$data.journalCheckpoints;count=$samples.Count;hashes=$data.binaryHashes;grid=$data.grid;
            latencies=$latencies;metricsComplete=$metricsComplete;combinedCpuMs=$cpuMs;combinedCpuUpperMs=$cpuUpperMs;combinedPrivateBytes=$privateBytes;
            fixedWorkBytes=$workBytes;activeOutputBytesPerSecond=$(if ($workBytes -gt 0 -and $activeSeconds -gt 0) {$workBytes/$activeSeconds} else {0});
            nativeCurrentBytes=$nativeBytes;nativePageResidentBytes=$pageBytes;nativeImageBytes=$imageBytes;pageMetricsComplete=$pageMetricsComplete;
            ownerFeedBytes=$feedBytes;intervalSeconds=($end-$start)/[double]$data.frequency}
    }
}
$beforeRuns = @(ReadRuns $BaselineDirectory | Sort-Object utc)
$afterRuns = if ($NoiseStudy -or $MixedPairs) { @() } else { @(ReadRuns $CandidateDirectory | Sort-Object utc) }
if ($MixedPairs) {
    if($BaselineVariant -or $CandidateVariant){
        if(-not $BaselineVariant -or -not $CandidateVariant){throw 'Specify both comparison variants'}
        function Variant($run){if($run.presentation -eq 'ui-owner-journal'){"$($run.presentation)/$($run.journalCheckpoints)"}else{$run.presentation}}
        $allRuns=$beforeRuns
        $afterRuns=@($allRuns | Where-Object {(Variant $_) -eq $CandidateVariant})
        $beforeRuns=@($allRuns | Where-Object {(Variant $_) -eq $BaselineVariant})
    }else{
        $afterRuns=@($beforeRuns | Where-Object presentation -ne 'legacy')
        $beforeRuns=@($beforeRuns | Where-Object presentation -eq 'legacy')
    }
}
$comparisons = @()
$resourceComparisons = @()
foreach ($group in $beforeRuns | Group-Object key) {
    $before = @($group.Group)
    if ($NoiseStudy) {
        $after = @(for ($i=1;$i -lt $before.Count;$i+=2) { $before[$i] })
        $before = @(for ($i=0;$i -lt $before.Count;$i+=2) { $before[$i] })
    } else { $after = @($afterRuns | Where-Object key -eq $group.Name) }
    if ($before.Count -ne $after.Count) { throw "Unpaired runs in $($group.Name)" }
    $valid = $before.Count -ge 5 -and ($before | ForEach-Object { $_.count } | Measure-Object -Sum).Sum -ge 10000 -and ($after | ForEach-Object { $_.count } | Measure-Object -Sum).Sum -ge 10000
    foreach ($side in @(@{runs=$before},@{runs=$after})) {
        # JSON property order is not binary identity (PowerShell hashtable order
        # varies between guest processes). Compare canonical name/hash pairs.
        $hashSignatures = @($side.runs | ForEach-Object {
            ($_.hashes.PSObject.Properties | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join "`n"
        } | Sort-Object -Unique)
        if ($hashSignatures.Count -ne 1) { throw "Changed binaries within one comparison side: $($group.Name)" }
    }
    foreach ($run in @($before)+@($after)) {
        if ($run.grid.cols -ne $before[0].grid.cols -or $run.grid.rows -ne $before[0].grid.rows -or
            $run.grid.cellWidth -ne $before[0].grid.cellWidth -or $run.grid.cellHeight -ne $before[0].grid.cellHeight) {
            throw "Changed grid/font metrics: $($group.Name)"
        }
    }
    foreach ($metric in 'inputMs-p50','inputMs-p95','inputMs-p99','presentMs-p50','presentMs-p95','presentMs-p99') {
        $differences = @(for ($i=0;$i -lt $before.Count;$i++) { $after[$i].latencies[$metric]-$before[$i].latencies[$metric] })
        $ci = Confidence $differences
        $baseline = Mean @($before | ForEach-Object { $_.latencies[$metric] })
        $absolute = if ($metric.EndsWith('p99')) {1.0} else {0.5}
        $tolerance = [Math]::Max($absolute,0.05*$baseline)
        $outcome = if (-not $valid -or $null -eq $ci) {'inconclusive'} elseif ($ci.upper -le $tolerance) {'pass'} elseif ($ci.lower -gt $tolerance) {'fail'} else {'inconclusive'}
        $comparisons += @{workload=$group.Name;metric=$metric;baselineMs=$baseline;toleranceMs=$tolerance;differenceMs=$ci;outcome=$outcome}
    }
    foreach ($metric in 'combinedPrivateBytes','fixedWorkCpu','activeOutputBytesPerSecond') {
        $eligible = $valid -and @(@($before)+@($after) | Where-Object { -not $_.metricsComplete }).Count -eq 0
        $ratios = @(for ($i=0;$i -lt $before.Count;$i++) {
            if ($metric -eq 'fixedWorkCpu') {
                if ($before[$i].fixedWorkBytes -le 0 -or $after[$i].fixedWorkBytes -ne $before[$i].fixedWorkBytes -or $before[$i].combinedCpuMs -le 0) { $eligible=$false; continue }
                # Conservative sampling bound: candidate enclosing CPU / baseline enclosed CPU.
                $after[$i].combinedCpuUpperMs/$before[$i].combinedCpuMs
            } else {
                if ($before[$i].$metric -le 0) { $eligible=$false; continue }
                $after[$i].$metric/$before[$i].$metric
            }
        })
        $ci = Confidence $ratios
        $minimum = $metric -eq 'activeOutputBytesPerSecond'
        $limit = if ($minimum) {0.95} else {1.05}
        $outcome = if (-not $eligible -or $null -eq $ci) {'inconclusive'}
            elseif ($minimum -and $ci.lower -ge $limit -or -not $minimum -and $ci.upper -le $limit) {'pass'}
            elseif ($metric -eq 'fixedWorkCpu') {'inconclusive'} # Upper bounds alone cannot prove a regression.
            elseif ($minimum -and $ci.upper -lt $limit -or -not $minimum -and $ci.lower -gt $limit) {'fail'} else {'inconclusive'}
        if($metric -eq 'combinedPrivateBytes') { $limit=$null; $outcome='informational' }
        $resourceComparisons += @{workload=$group.Name;metric=$metric;ratio=$ci;limit=$limit;outcome=$outcome}
    }
}
if (-not $comparisons.Count) { throw 'No paired latency reports found' }
@{noiseStudy=[bool]$NoiseStudy;endpoint='application EndDraw, not physical display';
    cpuScope='Combined owner and UI; fixed-output CPU uses conservative enclosing/enclosed sample bounds. Other workloads remain descriptive.';
    throughputScope='Fixed producer bytes / sum of T4-to-T5 intervals, excluding observer pacing; echo child CPU excluded.';
    memoryPolicy='User priority update 2026-11-05: modest bounded growth is a tradeoff, not an automatic +5% failure. Report exact overhead; leaks/unbounded growth still block rollout.';
    baselineRuns=$beforeRuns;candidateRuns=$afterRuns;latencyGates=$comparisons;resourceGates=$resourceComparisons;
    otherGates='History-memory savings, sustained throughput, interaction and bounded-memory soak require their dedicated runs.'} |
    ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $Output
$comparisons | Select-Object workload,metric,baselineMs,toleranceMs,outcome | Format-Table
