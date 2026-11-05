$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('agwinterm-latency-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory $temporary)
$checks = 0
try {
    $valid = @('frequency,10000000,pid,1') + @(0..8 | ForEach-Object { "$_,1,$(1000+$_),$(if ($_ -ge 6) {42} else {0})" }) + @('lost,0')
    $path = Join-Path $temporary 'trace-1.csv'
    $valid | Set-Content $path
    $answer = & "$PSScriptRoot/terminal-latency-analyze.ps1" -TraceDirectory $temporary -SampleCount 1
    if ($answer.samples.Count -ne 1) { throw 'Valid trace rejected' }; $checks++
    $cases = @(
        @{name='missing event';lines=@($valid | Where-Object { $_ -notmatch '^4,' })},
        @{name='duplicate event';lines=@($valid)+@('4,1,1004,0')},
        @{name='queue overflow';lines=@($valid)+@('lost,1')},
        @{name='wrong frame';lines=@($valid | ForEach-Object { $_.Replace('8,1,1008,42','8,1,1008,43') })},
        @{name='clock mismatch';lines=@($valid)+@('frequency,1000,pid,2')},
        @{name='backwards clock';lines=@($valid | ForEach-Object { $_.Replace('5,1,1005,0','5,1,1,0') })}
    )
    foreach ($case in $cases) {
        $case.lines | Set-Content $path
        $rejected = $false
        try { $null = & "$PSScriptRoot/terminal-latency-analyze.ps1" -TraceDirectory $temporary -SampleCount 1 }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Accepted invalid trace: $($case.name)" }; $checks++
    }
    $valid | Set-Content $path
    $driver=Join-Path $temporary 'driver.csv'
    @('frequency,10000000,pid,2','11,1,900,0') | Set-Content $driver
    $answer=& "$PSScriptRoot/terminal-latency-analyze.ps1" -TraceDirectory $temporary -SampleCount 1
    if([Math]::Abs($answer.samples[0].burstInjectionMs-0.0108) -gt 1e-8 -or
       [Math]::Abs($answer.samples[0].preambleInjectionMs-0.01) -gt 1e-8){throw 'Incorrect supplemental burst timing'}
    $checks++
    @('frequency,10000000,pid,2','11,1,1001,0') | Set-Content $driver
    $rejected=$false
    try{$null=& "$PSScriptRoot/terminal-latency-analyze.ps1" -TraceDirectory $temporary -SampleCount 1}catch{$rejected=$true}
    if(-not $rejected){throw 'Accepted burst start after Enter'}; $checks++
    "$checks latency analyzer checks passed"
} finally {
    Remove-Item -LiteralPath (Join-Path $temporary 'trace-1.csv') -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $temporary 'driver.csv') -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temporary
}
