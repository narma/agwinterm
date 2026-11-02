# Full-window Render() timings, including EndDraw, via the existing AGWINTERM_PERF log.
# Run only through sandbox-ui.ps1 -Suite TerminalPerformance -Background.
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
        foreach($scenario in 'ascii','sparse','dense') {
            $payload=[Text.StringBuilder]::new()
            [void]$payload.Append($esc+'[0m'+$esc+'[2J')
            for($row=1;$row-le $rows;$row++) {
                [void]$payload.Append($esc+"[${row};1H")
                $line=('build output abcdef 0123456789 '*$cols).Substring(0,$cols-1)
                if($scenario-eq 'sparse' -and $row-le 3) {
                    # Six solo glyphs in the entire viewport, representative of occasional status icons.
                    $line='🔴 中 '+$line.Substring(6)
                } elseif($scenario-eq 'dense') {
                    $line=('🔴中🔵界'*[int][Math]::Floor(($cols-1)/8)) -join ''
                }
                [void]$payload.Append($line)
            }
            $null=Rpc 'session.write' @{text=$payload.ToString()} $session
            Start-Sleep -Milliseconds 300
            PaintFrames 30
            $offset=@(Get-Content -LiteralPath $env:AGWINTERM_PERF).Count
            PaintFrames 200
            $lines=@(Get-Content -LiteralPath $env:AGWINTERM_PERF | Select-Object -Skip $offset)
            $samples=@($lines | ForEach-Object {
                if($_-match '^frame render=([0-9.,]+)ms') {
                    [double]::Parse($Matches[1].Replace(',','.'),[Globalization.CultureInfo]::InvariantCulture)
                }
            })
            Check "$scenario timing samples (ligatures=$ligatures)" ($samples.Count-ge 200) "count=$($samples.Count)"
            if($samples.Count-lt 200){throw 'Missing render samples'}
            $sorted=@($samples|Sort-Object)
            $result=[ordered]@{scenario=$scenario;ligatures=$ligatures;cols=$cols;rows=$rows;count=$samples.Count;
                medianMs=$sorted[[int][Math]::Floor($sorted.Count*0.5)];p95Ms=$sorted[[int][Math]::Floor($sorted.Count*0.95)];
                meanMs=($samples|Measure-Object -Average).Average;samplesMs=$samples}
            $results+=$result
            "$scenario ligatures=$ligatures median=$($result.medianMs)ms p95=$($result.p95Ms)ms"
        }
    }
    $results|ConvertTo-Json -Depth 5|Set-Content (Join-Path $artifact 'terminal-performance.json')
} finally {
    $null=Rpc 'session.write' @{text=($esc+'[0m'+$esc+'[?25h'+$esc+'[?1049l')} $session
}
