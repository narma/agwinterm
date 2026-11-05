# T0 is Enter injection, T1 receipt, T2 owner acceptance, T3 write completion,
# T4 child receipt, T5 parsed echo, T6 frame capture, T7 frame application,
# T8 successful EndDraw. Baseline capture/application are in the UI and coincide.
# No repaint or sleep is inserted between input and its matching presentation.
$trace = $env:AGWINTERM_LATENCY_TRACE
Add-Type @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
public sealed class EchoTraceTail : IDisposable {
    readonly string path;
    readonly AutoResetEvent changed = new AutoResetEvent(false);
    readonly FileSystemWatcher watcher;
    readonly StringBuilder pending = new StringBuilder();
    readonly byte[] buffer = new byte[4096];
    FileStream stream;
    public EchoTraceTail(string directory, string name) {
        path = Path.Combine(directory,name);
        watcher = new FileSystemWatcher(directory,name);
        watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
        watcher.Changed += (_,e) => Signal();
        watcher.Created += (_,e) => Signal();
        watcher.EnableRaisingEvents = true;
    }
    public void WaitFor(int id) {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 10000) {
            if (stream == null && File.Exists(path)) stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            bool found = false;
            if (stream != null) {
                int n;
                while ((n=stream.Read(buffer,0,buffer.Length)) > 0) {
                    for (int i=0;i<n;i++) {
                        if (buffer[i] != 10) { pending.Append((char)buffer[i]); continue; }
                        string line=pending.ToString().TrimEnd('\r'); pending.Clear();
                        if (line.StartsWith("8,"+id+",")) found=true;
                        if (line.StartsWith("lost,") && line != "lost,0") throw new Exception(line);
                    }
                }
            }
            if (found) return;
            // Some Windows filesystems defer change notifications until close.
            // This observation-only timeout neither wakes the app nor delays T8.
            changed.WaitOne((int)Math.Min(10,Math.Max(1,10000-deadline.ElapsedMilliseconds)));
        }
        throw new Exception("Missing presented echo marker "+id+" in "+path+" at offset "+(stream == null ? -1 : stream.Position));
    }
    void Signal() { try { changed.Set(); } catch (ObjectDisposedException) { } }
    public void Dispose() { watcher.Dispose(); stream?.Dispose(); changed.Dispose(); }
}
'@
$readyDeadline = [DateTime]::UtcNow.AddSeconds(120)
while ([string](Rpc 'session.text' @{} $session) -notlike '*ECHO_READY*') {
    if ([DateTime]::UtcNow -gt $readyDeadline) { throw 'Echo child did not become ready' }
    Start-Sleep -Milliseconds 50
}
# Exclude the existing startup toast from steady-state runs.
Start-Sleep -Seconds 7
$uiPath = Join-Path $trace "trace-$($job.Pid).csv"
# The writer initializes lazily on first instrumented input.
$driver = [IO.StreamWriter]::new((Join-Path $trace 'driver.csv'))
$driver.AutoFlush = $true
$driver.WriteLine("frequency,$([Diagnostics.Stopwatch]::Frequency),pid,$PID")
$tail = [EchoTraceTail]::new($trace,"trace-$($job.Pid).csv")
try {
    foreach ($id in 1..$LatencySamples) {
        $marker = '~AGL{0:X8}~' -f $id
        $burstStart = [Diagnostics.Stopwatch]::GetTimestamp()
        foreach ($character in $(if($LatencyInputPattern -eq 'marker-burst'){$marker.ToCharArray()}else{@()})) {
            [void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr][int]$character,[IntPtr]1)
        }
        $t0 = [Diagnostics.Stopwatch]::GetTimestamp()
        [void][HudOwnedJob]::SendMessageW($hwnd,0x100,[IntPtr]13,[IntPtr]0x001c0001)
        [void][HudOwnedJob]::SendMessageW($hwnd,0x102,[IntPtr]13,[IntPtr]0x001c0001)
        [void][HudOwnedJob]::SendMessageW($hwnd,0x101,[IntPtr]13,[IntPtr]0xc01c0001L)
        $driver.WriteLine("0,$id,$t0,0")
        $driver.WriteLine("11,$id,$burstStart,0")
        $tail.WaitFor($id)
    }
} finally {
    $driver.Dispose()
    $tail.Dispose()
}
# Flush polling is outside all measured input-to-present intervals.
Start-Sleep -Milliseconds 300
$analysis = & "$PSScriptRoot/terminal-latency-analyze.ps1" -TraceDirectory $trace -SampleCount $LatencySamples
$samples = $analysis.samples; $frequencies = @($analysis.frequency)
$binaryHashes = [ordered]@{}
foreach ($name in 'Agwinterm.Win32.dll','Agwinterm.Core.dll','Agwinterm.Pty.dll','ghostty-vt.dll','agwinterm_ghostty.dll','echo-fixture/Agwinterm.EchoFixture.dll','echo-fixture/Agwinterm.Core.dll','echo-fixture/Agwinterm.Pty.dll') {
    $binaryHashes[$name] = (Get-FileHash (Join-Path (Split-Path $Exe) $name) -Algorithm SHA256).Hash
}
@{hostMode=$LatencyHost;workload=$LatencyWorkload;count=$samples.Count;warmupSamples=[Math]::Min(50,[int]($samples.Count/10));
    presentation=$LatencyPresentation;inputPattern=$LatencyInputPattern;
    journalCheckpoints=$JournalCheckpoints;
    producerBytesPerEvent=$(if ($LatencyWorkload -eq 'fixed-output') {262144} else {0});
    binaryHashes=$binaryHashes;grid=(Rpc 'session.metrics' @{} $session);uiPid=$job.Pid;utc=[DateTime]::UtcNow.ToString('O');
    frequency=$frequencies[0];endpoint='EndDraw, not physical display';samples=$samples} |
    ConvertTo-Json -Depth 7 | Set-Content (Join-Path $artifact 'terminal-latency.json')
Check 'Every echo has one correlated T0-T8 trace and a presented marker' ($samples.Count -eq $LatencySamples)
