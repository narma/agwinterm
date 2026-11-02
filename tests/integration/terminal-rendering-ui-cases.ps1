# Runs through hud-ui.ps1's isolated app and background Sandbox routing.
function RenderPixels([string]$name) {
    Start-Sleep -Milliseconds 300
    $oldDpi=[HudOwnedJob]::SetThreadDpiAwarenessContext([IntPtr](-4))
    try {
        $rc=[HudOwnedJob+RECT]::new();[void][HudOwnedJob]::GetClientRect($hwnd,[ref]$rc)
        $bitmap=[Drawing.Bitmap]::new($rc.right,$rc.bottom);$g=[Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc=$g.GetHdc()
            try {if(-not [HudOwnedJob]::PrintWindow($hwnd,$dc,3)){throw 'Render capture failed'}} finally {$g.ReleaseHdc($dc)}
            $bitmap.Save((Join-Path $artifact "$name.png"))
            $bits=$bitmap.LockBits([Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $pixels=[int[]]::new($bitmap.Width*$bitmap.Height)
                [Runtime.InteropServices.Marshal]::Copy($bits.Scan0,$pixels,0,$pixels.Length)
                return ,$pixels
            } finally {$bitmap.UnlockBits($bits)}
        } finally {$g.Dispose();$bitmap.Dispose()}
    } finally {[void][HudOwnedJob]::SetThreadDpiAwarenessContext($oldDpi)}
}
$esc=[string][char]27
$null=Rpc 'config.set' @{key='cursor-blink';value='false'}
$null=Rpc 'session.write' @{text=($esc+'[?1049h'+$esc+'[?25l')} $session
try {
    foreach($ligatures in 'true','false') {
        $null=Rpc 'config.set' @{key='ligatures';value=$ligatures}
        $clear=$esc+'[0m'+$esc+'[2J'+$esc+'[H'
        $null=Rpc 'session.write' @{text=$clear} $session
        $blank=RenderPixels "blank-$ligatures"
        $null=Rpc 'session.write' @{text=($esc+'[H🔴 🔵')} $session
        $colored=RenderPixels "emoji-$ligatures"
        $red=0;$blue=0
        for($i=0;$i-lt $colored.Length;$i++) {
            if($colored[$i]-eq $blank[$i]){continue}
            $r=($colored[$i]-shr 16)-band 255;$g=($colored[$i]-shr 8)-band 255;$b=$colored[$i]-band 255
            if($r-gt 120 -and $r-gt $g+40 -and $r-gt $b+40){$red++}
            if($b-gt 120 -and $b-gt $r+40 -and $b-gt $g+20){$blue++}
        }
        Check "emoji retain red and blue colour (ligatures=$ligatures)" ($red-gt 10 -and $blue-gt 10) "red=$red blue=$blue"
        # Spaces isolate the decoration from glyph ink: SGR 9 must add a visible line.
        $null=Rpc 'session.write' @{text=($clear+$esc+'[38;2;18;90;159m'+$esc+'[9m'+(' '*20)+$esc+'[29m')} $session
        $struck=RenderPixels "strikethrough-$ligatures"
        $line=0
        for($i=0;$i-lt $struck.Length;$i++) {
            if($struck[$i]-ne $blank[$i] -and ($struck[$i]-band 0xffffff)-eq 0x125a9f){$line++}
        }
        Check "SGR 9 draws strikethrough (ligatures=$ligatures)" ($line-gt 50) "line pixels=$line"
    }
} finally {
    $null=Rpc 'session.write' @{text=($esc+'[0m'+$esc+'[?25h'+$esc+'[?1049l')} $session
}
