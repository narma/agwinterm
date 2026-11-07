# Read-BlinkPixels is shared with the preceding blink cases.
try {
    $hidden=$esc+'[0m'+$esc+'[2J'+$esc+'[H'+$esc+'[?25l'+$esc+'[4;3H'+
        $esc+'[8;4:3;53;38:2::231:41:83;48:2::61:89:229mCONCEALED 中é'+
        $esc+'[5;3H'+(' '*24)+$esc+'[0m'+$esc+'[7;3H'+$esc+'[38:2::37:191:71mSTEADY'+$esc+'[0m'
    $null=Rpc 'session.write' @{text=$hidden} $session
    Start-Sleep -Milliseconds 250
    $pixels=Read-BlinkPixels 'conceal-hidden'
    Check 'Conceal hides glyphs, graphemes and blank-cell decorations' ($pixels[0] -eq 0)
    Check 'Conceal preserves backgrounds and neighboring text' ($pixels[1] -gt 80 -and $pixels[2] -gt 1000)
    Check 'Concealed content remains available to text queries' (([string](Rpc 'session.text' @{} $session)).Contains('CONCEALED'))
    # Matching styles must not allow a visible run to absorb concealed text.
    $null=Rpc 'session.write' @{text=($esc+'[4;3H'+$esc+'[0;38:2::231:41:83mVISIBLE'+$esc+'[8mHIDDEN'+$esc+'[0m')} $session
    Start-Sleep -Milliseconds 250
    $mixed=Read-BlinkPixels 'conceal-mixed'
    $null=Rpc 'session.write' @{text=($esc+'[4;10H'+$esc+'[0m      ')} $session
    Start-Sleep -Milliseconds 250
    $cleared=Read-BlinkPixels 'conceal-cleared'
    Check 'Visible text runs never absorb concealed neighbors' ($mixed[0] -gt 80 -and $mixed[0] -eq $cleared[0])
    $null=Rpc 'session.write' @{text=($esc+'[4;3H'+$esc+'[8;4:3;53;38:2::231:41:83m'+$esc+'[28m'+('M'*24)+$esc+'[0m')} $session
    Start-Sleep -Milliseconds 250
    $visible=Read-BlinkPixels 'conceal-reset'
    Check 'SGR 28 restores visible decorated text' ($visible[0] -gt $cleared[0])
} finally {
    $null=Rpc 'session.write' @{text=($esc+'[0m'+$esc+'[?25h'+$esc+'[2J'+$esc+'[H')} $session
}
