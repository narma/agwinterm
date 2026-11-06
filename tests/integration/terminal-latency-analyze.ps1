param([Parameter(Mandatory)][string]$TraceDirectory,[ValidateRange(1,100000)][int]$SampleCount)
$ErrorActionPreference = 'Stop'
$events = @{}; $frames = @{}; $frequencies = @(); $burstStarts=@{}
foreach ($file in Get-ChildItem $TraceDirectory -Filter '*.csv' | Where-Object { $_.Name -eq 'driver.csv' -or $_.Name -like 'trace-*.csv' }) {
    $stream = [IO.FileStream]::new($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream)
    try { while ($null -ne ($line = $reader.ReadLine())) {
        if ($line -match '^frequency,(\d+),pid,(\d+)$') { $frequencies += [long]$Matches[1]; continue }
        if ($line -match '^lost,([1-9]\d*)') { throw "Trace loss in $($file.Name)" }
        if($line -match '^11,(\d+),(\d+),0$') {
            $id=[int]$Matches[1]
            if($file.Name -ne 'driver.csv' -or $burstStarts.ContainsKey($id)){throw 'Duplicate or misplaced burst-start stage'}
            $burstStarts[$id]=[long]$Matches[2]; continue
        }
        if ($line -match '^([0-8]),(\d+),(\d+),(\d+)$') {
            $key = "$($Matches[2]):$($Matches[1])"
            if ($events.ContainsKey($key)) { throw "Duplicate stage $key" }
            $events[$key] = [long]$Matches[3]
            $frames[$key] = [long]$Matches[4]
        }
    } } finally { $reader.Dispose() }
}
if (@($frequencies | Sort-Object -Unique).Count -ne 1 -or $frequencies[0] -le 0) { throw 'Cross-process clock frequencies differ or are missing' }
if ($events.Count -ne 9 * $SampleCount) { throw 'Missing or unexpected trace events' }
if($burstStarts.Count -ne 0 -and $burstStarts.Count -ne $SampleCount){throw 'Incomplete burst-start observations'}
$samples = foreach ($id in 1..$SampleCount) {
    $ticks = foreach ($stage in 0..8) {
        if (-not $events.ContainsKey("${id}:$stage")) { throw "Missing T$stage for echo $id" }
        $events["${id}:$stage"]
    }
    # T3 submission may complete after the child reads; do not invent a causal T3<T4 constraint.
    if ($frames["${id}:6"] -le 0 -or $frames["${id}:6"] -ne $frames["${id}:7"] -or $frames["${id}:7"] -ne $frames["${id}:8"]) {
        throw "Echo $id was not matched to a single submitted frame"
    }
    foreach ($pair in @(@(0,1),@(1,2),@(2,3),@(2,4),@(4,5),@(5,6),@(6,7),@(7,8))) {
        if ($ticks[$pair[0]] -gt $ticks[$pair[1]]) { throw "Clock/ordering failure for echo $id" }
    }
    $sample=[ordered]@{id=$id;ticks=$ticks;inputMs=1000.0*($ticks[4]-$ticks[1])/$frequencies[0];
        presentMs=1000.0*($ticks[8]-$ticks[1])/$frequencies[0];injectionMs=1000.0*($ticks[8]-$ticks[0])/$frequencies[0]}
    if($burstStarts.Count -gt 0) {
        if(-not $burstStarts.ContainsKey($id) -or $burstStarts[$id] -gt $ticks[0]){throw 'Invalid burst-start ordering'}
        $sample.preambleInjectionMs=1000.0*($ticks[0]-$burstStarts[$id])/$frequencies[0]
        $sample.burstInjectionMs=1000.0*($ticks[8]-$burstStarts[$id])/$frequencies[0]
    }
    [pscustomobject]$sample
}
[pscustomobject]@{frequency=$frequencies[0];samples=@($samples)}
