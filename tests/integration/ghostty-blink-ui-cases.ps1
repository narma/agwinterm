# Capture several idle phases: no output, cursor timer deliberately much slower than text blinking.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BlinkPixels {
    public static int[] Count(IntPtr data,int stride,int width,int height) {
        int[] colors={0xe72953,0x25bf47,0x3d59e5}, counts=new int[3];
        byte[] row=new byte[width*4];
        for(int y=0;y<height;y++) {
            Marshal.Copy(IntPtr.Add(data,y*stride),row,0,row.Length);
            for(int x=0;x<width;x++) for(int c=0;c<colors.Length;c++) {
                int target=colors[c], offset=x*4;
                if(Math.Abs(row[offset]-(target&255))<=8 && Math.Abs(row[offset+1]-((target>>8)&255))<=8 &&
                   Math.Abs(row[offset+2]-((target>>16)&255))<=8) counts[c]++;
            }
        }
        return counts;
    }
}
'@
function Read-BlinkPixels([string]$name) {
    $rect=[HudOwnedJob+RECT]::new(); [void][HudOwnedJob]::GetClientRect($hwnd,[ref]$rect)
    $bitmap=[Drawing.Bitmap]::new($rect.right,$rect.bottom); $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc=$graphics.GetHdc()
        try {[void][HudOwnedJob]::PrintWindow($hwnd,$dc,3)}finally{$graphics.ReleaseHdc($dc)}
        $bitmap.Save((Join-Path $artifact ($name+'.png')))
        $data=$bitmap.LockBits([Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try { return ,([BlinkPixels]::Count($data.Scan0,$data.Stride,$bitmap.Width,$bitmap.Height)) }
        finally {$bitmap.UnlockBits($data)}
    } finally {$graphics.Dispose();$bitmap.Dispose()}
}
$oldCursorBlink=Rpc 'config.get' @{key='cursor-blink'}
$oldCursorInterval=Rpc 'config.get' @{key='cursor-blink-ms'}
try {
    $null=Rpc 'config.set' @{key='cursor-blink';value='false'}
    $null=Rpc 'config.set' @{key='cursor-blink-ms';value='10000'}
    $paint=$esc+'[0m'+$esc+'[2J'+$esc+'[H'+$esc+'[?25l'+$esc+'[4;3H'+
        $esc+'[5;4:3;53;38:2::231:41:83;48:2::61:89:229mBLINK '+('M'*12)+' 中é'+
        $esc+'[5;3H'+(' '*24)+$esc+'[0m'+$esc+'[7;3H'+$esc+'[38:2::37:191:71mSTEADY '+('M'*16)+$esc+'[0m'
    $null=Rpc 'session.write' @{text=$paint} $session
    Start-Sleep -Milliseconds 150
    $samples=@(for($i=0;$i -lt 10;$i++){Read-BlinkPixels "blink-idle-$i";Start-Sleep -Milliseconds 150})
    $reds=@($samples|ForEach-Object {$_[0]}); $greens=@($samples|ForEach-Object {$_[1]}); $blues=@($samples|ForEach-Object {$_[2]})
    Check 'Blink hides and restores glyphs and blank-cell decorations while idle' (($reds|Measure-Object -Minimum).Minimum -eq 0 -and ($reds|Measure-Object -Maximum).Maximum -gt 80) "foreground samples=$($reds -join ',')"
    Check 'Blink leaves nonblinking text visible' (($greens|Measure-Object -Minimum).Minimum -gt 80)
    Check 'Blink never erases cell backgrounds' (($blues|Measure-Object -Minimum).Minimum -gt 1000)
    Check 'Blink does not remove text from terminal queries' (([string](Rpc 'session.text' @{} $session)).Contains('BLINK'))
    # Clear blink with SGR 25, keeping the underline/overline, foreground and background.
    $null=Rpc 'session.write' @{text=($esc+'[5;4:3;53;38:2::231:41:83;48:2::61:89:229m'+$esc+'[25m'+$esc+'[4;3H'+('M'*24)+$esc+'[5;3H'+(' '*24)+$esc+'[0m')} $session
    Start-Sleep -Milliseconds 150
    $steady=@(for($i=0;$i -lt 7;$i++){Read-BlinkPixels "blink-reset-$i";Start-Sleep -Milliseconds 180})
    Check 'SGR 25 keeps decorated text steady across multiple blink periods' ((@($steady|ForEach-Object {$_[0]})|Measure-Object -Minimum).Minimum -gt 80)
} finally {
    $null=Rpc 'config.set' @{key='cursor-blink';value=[string]$oldCursorBlink}
    $null=Rpc 'config.set' @{key='cursor-blink-ms';value=[string]$oldCursorInterval}
    $null=Rpc 'session.write' @{text=($esc+'[0m'+$esc+'[?25h'+$esc+'[2J'+$esc+'[H')} $session
}
