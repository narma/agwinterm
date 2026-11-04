# Full-window Render() timings, including EndDraw, via the existing AGWINTERM_PERF log.
# Run through the installed Hyper-V worker (or explicit Sandbox routing).
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TerminalPerfPaint {
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
}
'@
function PaintFrames([int]$count) {
    for($i=0;$i-lt $count;$i++) {
        # RDW_INVALIDATE | RDW_UPDATENOW: synchronously repaint the owned test window.
        if(-not [TerminalPerfPaint]::RedrawWindow($hwnd,[IntPtr]::Zero,[IntPtr]::Zero,0x101)) {
            throw 'Cannot repaint benchmark window'
        }
    }
}
$esc=[string][char]27
$null=Rpc 'config.set' @{key='cursor-blink';value='false'}
$null=Rpc 'config.set' @{key='font-family';value='Consolas'}
$null=Rpc 'config.set' @{key='font-size';value='14'}
$null=Rpc 'session.write' @{text=($esc+'[?1049h'+$esc+'[?25l')} $session
$metrics=Rpc 'session.metrics' @{} $session
$cols=[int]$metrics.cols;$rows=[int]$metrics.rows
if($cols-lt 40 -or $rows-lt 10){throw "Unexpected benchmark grid: ${cols}x${rows}"}
$results=@()
try {
    foreach($ligatures in 'true','false') {
        $null=Rpc 'config.set' @{key='ligatures';value=$ligatures}
        foreach($scenario in 'ascii','mixed-0','mixed-4','mixed-16','mixed-64','dense') {
            $emojiCount = if ($scenario.StartsWith('mixed-')) { [int]$scenario.Substring(6) } else { 0 }
            $payload=[Text.StringBuilder]::new()
            [void]$payload.Append($esc+'[0m'+$esc+'[2J')
            for($row=1;$row-le $rows;$row++) {
                [void]$payload.Append($esc+"[${row};1H")
                $line=('build output abcdef 0123456789 '*$cols).Substring(0,$cols-1)
                if($scenario.StartsWith('mixed-')) {
                    # One screen mixes Latin and Russian text; emoji are added below at exact cells.
                    $line=('build output Сборка проекта: успешно abcdef Привет мир 0123456789 '*$cols).Substring(0,$cols-1)
                } elseif($scenario-eq 'dense') {
                    $line=('🔴中🔵界'*[int][Math]::Floor(($cols-1)/8)) -join ''
                }
                [void]$payload.Append($line)
            }
            $emoji = @('🔴','🔵','😀','🚀')
            for ($i=0; $i-lt $emojiCount; $i++) {
                $r=1+[int][Math]::Floor($i*$rows/$emojiCount); $c=5+($i%4)*20
                [void]$payload.Append($esc+"[${r};${c}H"+$emoji[$i%4])
            }
            $null=Rpc 'session.write' @{text=$payload.ToString()} $session
            Start-Sleep -Milliseconds 300
            if ($scenario.StartsWith('mixed-')) {
                $screen=[string](Rpc 'session.text' @{} $session)
                $actualEmoji=[regex]::Matches($screen,'🔴|🔵|😀|🚀').Count
                Check "$scenario contains Latin, Russian and exactly $emojiCount emoji" ($screen.Contains('build') -and $screen.Contains('Привет') -and $actualEmoji -eq $emojiCount) "actual=$actualEmoji"
            }
            PaintFrames 20
            $offset=@(Get-Content -LiteralPath $env:AGWINTERM_PERF).Count
            PaintFrames 100
            $lines=@(Get-Content -LiteralPath $env:AGWINTERM_PERF | Select-Object -Skip $offset)
            $samples=@($lines | ForEach-Object {
                if($_-match '^frame render=([0-9.,]+)ms') {
                    [double]::Parse($Matches[1].Replace(',','.'),[Globalization.CultureInfo]::InvariantCulture)
                }
            })
            Check "$scenario timing samples (ligatures=$ligatures)" ($samples.Count-ge 100) "count=$($samples.Count)"
            if($samples.Count-lt 100){throw 'Missing render samples'}
            $expectedCore = @{managed='TerminalEmulator';rust='RustTerminalCore';ghostty='GhosttyTerminalCore'}[$selectedCore]
            $coreNames = @($lines | ForEach-Object { if ($_ -match ' core=(\w+)') { $Matches[1] } } | Sort-Object -Unique)
            Check "$scenario uses requested $selectedCore core without fallback" ($coreNames.Count -eq 1 -and $coreNames[0] -eq $expectedCore) ($coreNames -join ',')
            if ($coreNames.Count -ne 1 -or $coreNames[0] -ne $expectedCore) { throw 'Benchmark core mismatch' }
            $sorted=@($samples|Sort-Object)
            $result=[ordered]@{scenario=$scenario;core=$coreNames[0];emojiCount=$emojiCount;ligatures=$ligatures;cols=$cols;rows=$rows;count=$samples.Count;
                medianMs=$sorted[[int][Math]::Floor($sorted.Count*0.5)];p95Ms=$sorted[[int][Math]::Floor($sorted.Count*0.95)];
                meanMs=($samples|Measure-Object -Average).Average;samplesMs=$samples}
            foreach ($field in 'bodyMs','endMs','soloMs','soloCount','rasterHits','rasterMisses') {
                $values=@($lines | ForEach-Object { if ($_ -match " $field=([0-9.,]+)") { [double]::Parse($Matches[1].Replace(',','.'),[Globalization.CultureInfo]::InvariantCulture) } } | Sort-Object)
                if ($values.Count) { $result[$field+'Median']=$values[[int][Math]::Floor($values.Count*0.5)] }
            }
            $results+=$result
            "$scenario ligatures=$ligatures median=$($result.medianMs)ms p95=$($result.p95Ms)ms"
        }
    }
    $results|ConvertTo-Json -Depth 5|Set-Content (Join-Path $artifact 'terminal-performance.json')
} finally {
    $null=Rpc 'session.write' @{text=($esc+'[0m'+$esc+'[?25h'+$esc+'[?1049l')} $session
}
