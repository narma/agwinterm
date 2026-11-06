# Fixed-byte work for independent CPU/memory/throughput and tracing-overhead checks.
# No latency claim: completion is observed through normal cached text readback.
$readyDeadline=[DateTime]::UtcNow.AddSeconds(120)
while([string](Rpc 'session.text' @{} $session) -notlike '*BULK_READY*') {
    if([DateTime]::UtcNow -gt $readyDeadline){throw 'Bulk producer did not become ready'}
    Start-Sleep -Milliseconds 50
}
Start-Sleep -Seconds 7
$appName=[IO.Path]::GetFileNameWithoutExtension($Exe)
$processIds=@($job.MemberIds() | Where-Object {
    $process=[Diagnostics.Process]::GetProcessById($_)
    try { $process.ProcessName -eq $appName } finally { $process.Dispose() }
} | Sort-Object)
$expected=if($LatencyHost -eq 'server'){2}else{1}
if($processIds.Count -ne $expected -or $job.Pid -notin $processIds){throw "Expected $expected owned UI/host processes; found $($processIds.Count)"}
function Read-BulkResources {
    $cpu=0.0; $private=0L; $working=0L
    foreach($processId in $processIds) {
        $process=[Diagnostics.Process]::GetProcessById($processId)
        try {
            $process.Refresh()
            $cpu+=$process.TotalProcessorTime.TotalMilliseconds
            $private+=$process.PrivateMemorySize64; $working+=$process.WorkingSet64
        } finally { $process.Dispose() }
    }
    [pscustomobject]@{ticks=[Diagnostics.Stopwatch]::GetTimestamp();cpuMs=$cpu;privateBytes=$private;workingSetBytes=$working}
}
$samples=[Collections.Generic.List[object]]::new()
$start=Read-BulkResources; $samples.Add($start)
[void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]13,[IntPtr]0x001c0001)
[void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr]13,[IntPtr]0x001c0001)
[void][HudOwnedJob]::SendMessageW($hwnd,0x101,[IntPtr]13,[IntPtr]0xc01c0001L)
$deadline=[DateTime]::UtcNow.AddMinutes(10)
do {
    Start-Sleep -Milliseconds 100
    $previous=$samples[$samples.Count-1]
    $done=[string](Rpc 'session.text' @{} $session) -like '*BULK_DONE*'
    $samples.Add((Read-BulkResources))
    if([DateTime]::UtcNow -gt $deadline){throw 'Bulk output did not complete'}
} while(-not $done)
$end=$samples[$samples.Count-1]
$elapsed=($end.ticks-$start.ticks)/[double][Diagnostics.Stopwatch]::Frequency
$hashes=[ordered]@{}
foreach($name in 'Agwinterm.Win32.dll','Agwinterm.Core.dll','Agwinterm.Pty.dll','ghostty-vt.dll','agwinterm_ghostty.dll','echo-fixture/Agwinterm.EchoFixture.dll','echo-fixture/Agwinterm.Core.dll') {
    $hashes[$name]=(Get-FileHash -LiteralPath (Join-Path (Split-Path $Exe) $name) -Algorithm SHA256).Hash
}
@{schema=1;hostMode=$LatencyHost;presentation=$LatencyPresentation;measurementVariant=$LatencyVariant;traceMode=$LatencyTraceMode;
  bytes=[long]$LatencyBulkMiB*1MB;elapsedSeconds=$elapsed;bytesPerSecond=([long]$LatencyBulkMiB*1MB/$elapsed);
  cpuLowerMs=($previous.cpuMs-$start.cpuMs);cpuUpperMs=($end.cpuMs-$start.cpuMs);
  meanPrivateBytes=($samples.privateBytes|Measure-Object -Average).Average;peakPrivateBytes=($samples.privateBytes|Measure-Object -Maximum).Maximum;
  processIds=$processIds;binaryHashes=$hashes;grid=(Rpc 'session.metrics' @{} $session);frequency=[Diagnostics.Stopwatch]::Frequency;samples=$samples.ToArray();
  endpoint='cached BULK_DONE readback, sampled every 100 ms; not an input latency or physical-display measurement';utc=[DateTime]::UtcNow.ToString('O')} |
    ConvertTo-Json -Depth 7 | Set-Content (Join-Path $artifact 'session-bulk.json')
Check 'Fixed-byte producer completes with owned process resource samples' ($done -and $end.cpuMs -ge $start.cpuMs)
