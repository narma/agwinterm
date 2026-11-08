# Inspect actual rendered pixels, including blank cells and shape gaps. Runs in the owned VM.
$decorationColors=@(@(231,41,83),@(37,191,71),@(61,89,229),@(199,67,213),@(239,149,47),@(23,213,163))
$sequence=$esc+'[0m'+$esc+'[2J'+$esc+'[H'
for($i=0;$i -lt 5;$i++) {
    $rgb=$decorationColors[$i] -join ':'
    $sequence+=$esc+"[$(4+$i*2);3H"+$esc+"[4:$($i+1);58:2::$($rgb)m"+(' '*24)+$esc+'[0m'
}
$sequence+=$esc+'[16;3H'+$esc+'[53;38:2::23:213:163m'+(' '*24)+$esc+'[0m'
$null=Rpc 'session.write' @{text=$sequence} $session
Start-Sleep -Milliseconds 500
$decRect=[HudOwnedJob+RECT]::new(); [void][HudOwnedJob]::GetClientRect($hwnd,[ref]$decRect)
$decImage=[Drawing.Bitmap]::new($decRect.right,$decRect.bottom)
$decGraphics=[Drawing.Graphics]::FromImage($decImage)
try {
    $dc=$decGraphics.GetHdc()
    try{[void][HudOwnedJob]::PrintWindow($hwnd,$dc,3)}finally{$decGraphics.ReleaseHdc($dc)}
    $decImage.Save((Join-Path $artifact 'ghostty-decorations.png'))
    $samples=@(for($i=0;$i -lt 6;$i++){,[Collections.Generic.List[Drawing.Point]]::new()})
    for($y=0;$y -lt $decImage.Height;$y++){for($x=0;$x -lt $decImage.Width;$x++){
        $pixel=$decImage.GetPixel($x,$y)
        for($i=0;$i -lt 6;$i++){
            $rgb=$decorationColors[$i]
            if([Math]::Abs([int]$pixel.R-$rgb[0]) -le 45 -and [Math]::Abs([int]$pixel.G-$rgb[1]) -le 45 -and [Math]::Abs([int]$pixel.B-$rgb[2]) -le 45){
                $samples[$i].Add([Drawing.Point]::new($x,$y))
            }
        }
    }}
    $names=@('single','double','curly','dotted','dashed','overline')
    $coverage=@()
    for($i=0;$i -lt 6;$i++){
        $xs=@($samples[$i] | ForEach-Object X | Sort-Object -Unique)
        $ys=@($samples[$i] | ForEach-Object Y | Sort-Object -Unique)
        Check "Ghostty $($names[$i]) decoration has its own color on blank cells" ($xs.Count -gt 25 -and $ys.Count -gt 0) "pixels=$($samples[$i].Count) rows=$($ys.Count) columns=$($xs.Count)"
        if($xs.Count -eq 0){$coverage+=0;continue}
        $coverage+=$xs.Count / [double]($xs[-1]-$xs[0]+1)
        if($i -eq 1){ Check 'Ghostty double underline has separated lines' (($ys[-1]-$ys[0]+1) -gt $ys.Count) }
        if($i -eq 2){
            $centers=@($samples[$i] | Group-Object X | ForEach-Object {($_.Group | Measure-Object Y -Average).Average})
            $range=($centers | Measure-Object -Minimum -Maximum)
            Check 'Ghostty curly underline changes height across columns' ($ys.Count -ge 2 -and $range.Maximum-$range.Minimum -gt 0.4) "rows=$($ys.Count) height variation=$($range.Maximum-$range.Minimum)"
        }
        if($i -in @(3,4)){Check "Ghostty $($names[$i]) underline has visible gaps" ($coverage[$i] -lt 0.9)}
    }
    Check 'Ghostty dashed strokes are longer than dots' ($coverage[4] -gt $coverage[3])
} finally {$decGraphics.Dispose();$decImage.Dispose()}
$null=Rpc 'session.write' @{text=($esc+'[0m'+$esc+'[2J'+$esc+'[H')} $session
