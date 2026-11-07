# Included by the isolated input fixture, so replies are captured from the real child PTY.
$clipboardWritePolicy=[string](Rpc 'config.get' @{key='clipboard-write'})
$clipboardReadPolicy=[string](Rpc 'config.get' @{key='clipboard-read'})
$st=$esc+'\'
function ClipboardPacket([string]$metadata,[string]$data='') { return $esc+']5522;'+$metadata+$(if($data){';'+$data})+$st }
function ClipboardBase64([string]$text) { return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text)) }
function ClipboardWritePackets([string]$id,[hashtable]$items) {
    $result=ClipboardPacket "type=write:id=$id"
    foreach($mime in $items.Keys) { $result+=ClipboardPacket ('type=wdata:mime='+(ClipboardBase64 $mime)) ([string]$items[$mime]) }
    return $result+(ClipboardPacket 'type=wdata')
}
try {
    Check 'OSC clipboard reads default to disabled' ($clipboardReadPolicy -eq 'false')
    $null=Rpc 'config.set' @{key='clipboard-write';value='true'}
    $null=Rpc 'config.set' @{key='clipboard-read';value='false'}
    $plain='OSC 5522 русский 😀'; $html='<b>OSC 5522 русский 😀</b>'
    $clipboardPng='iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=='
    $packets=ClipboardWritePackets 'mime' @{'text/plain'=(ClipboardBase64 $plain);'text/html'=(ClipboardBase64 $html);'application/x-agwinterm-test'='AAECAAD/';'image/png'=$clipboardPng}
    InputCheck 'OSC 5522 write acknowledges the real Windows clipboard' (ClipboardPacket 'type=write:status=DONE:id=mime') {
        $null=Rpc 'session.write' @{text=$packets} $inputSession
    }
    Check 'OSC 5522 plain representation reaches Windows Unicode clipboard' (([string](Get-Clipboard)) -ceq $plain)
    Check 'OSC 5522 HTML representation reaches Windows HTML clipboard' ([GhosttySelectionWindow]::Html().Contains($html))
    InputCheck 'OSC 5522 read is denied by default' (ClipboardPacket 'type=read:status=EPERM:id=denied') {
        $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=read:id=denied' (ClipboardBase64 'text/plain'))} $inputSession
    }
    InputCheck 'Disabled reads also deny MIME listings' (ClipboardPacket 'type=read:status=EPERM:id=denylist') {
        $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=read:id=denylist' 'Lg==')} $inputSession
    }
    $null=Rpc 'config.set' @{key='clipboard-read';value='true'}
    foreach($entry in @(@('text/plain',(ClipboardBase64 $plain)),@('text/html',(ClipboardBase64 $html)),@('application/x-agwinterm-test','AAECAAD/'),@('image/png',$clipboardPng))) {
        $mime=$entry[0]; $data=$entry[1]; $encodedMime=ClipboardBase64 $mime
        $expected=(ClipboardPacket 'type=read:status=OK:id=read')+(ClipboardPacket "type=read:status=DATA:id=read:mime=$encodedMime" $data)+(ClipboardPacket 'type=read:status=DONE:id=read')
        InputCheck "OSC 5522 enabled read round-trips $mime" $expected {
            $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=read:id=read' $encodedMime)} $inputSession
        }
    }
    $listed=ClipboardBase64 "text/plain text/html image/png application/x-agwinterm-test`n"
    InputCheck 'OSC 5522 lists available MIME representations' ((ClipboardPacket 'type=read:status=OK:id=list')+(ClipboardPacket 'type=read:status=DATA:id=list:mime=Lg==' $listed)+(ClipboardPacket 'type=read:status=DONE:id=list')) {
        $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=read:id=list' 'Lg==')} $inputSession
    }
    $clipboardLocked=$false
    for($attempt=0;$attempt -lt 20 -and -not $clipboardLocked;$attempt++) {
        $clipboardLocked=[GhosttySelectionWindow]::OpenClipboard([IntPtr]::Zero)
        if(-not $clipboardLocked){Start-Sleep -Milliseconds 10}
    }
    if(-not $clipboardLocked){throw 'Cannot lock guest clipboard for busy-status test'}
    try {
        InputCheck 'Locked Windows clipboard returns EBUSY for writes' (ClipboardPacket 'type=write:status=EBUSY:id=busywrite') {
            $null=Rpc 'session.write' @{text=(ClipboardWritePackets 'busywrite' @{'text/plain'=(ClipboardBase64 'busy')})} $inputSession
        }
        InputCheck 'Locked Windows clipboard returns EBUSY for reads' (ClipboardPacket 'type=read:status=EBUSY:id=busyread') {
            $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=read:id=busyread' (ClipboardBase64 'text/plain'))} $inputSession
        }
    } finally { [void][GhosttySelectionWindow]::CloseClipboard() }
    $null=Rpc 'config.set' @{key='clipboard-write';value='false'}
    InputCheck 'OSC 5522 denied write reports EPERM' (ClipboardPacket 'type=write:status=EPERM:id=denywrite') {
        $null=Rpc 'session.write' @{text=(ClipboardWritePackets 'denywrite' @{'text/plain'=(ClipboardBase64 'forbidden')})} $inputSession
    }
    Check 'Denied OSC write preserves the old clipboard' (([string](Get-Clipboard)) -ceq $plain)
    $null=Rpc 'config.set' @{key='clipboard-write';value='true'}
    InputCheck 'Invalid text rejects the whole MIME transaction' (ClipboardPacket 'type=write:status=EINVAL:id=invalid') {
        $null=Rpc 'session.write' @{text=(ClipboardWritePackets 'invalid' @{'text/plain'='/w==';'text/html'=(ClipboardBase64 '<b>bad</b>')})} $inputSession
    }
    Check 'Rejected MIME transaction preserves all previous contents' (([string](Get-Clipboard)) -ceq $plain)
    InputCheck 'OSC 5522 primary selection is explicitly unsupported on Windows' (ClipboardPacket 'type=write:status=ENOSYS:id=primary') {
        $null=Rpc 'session.write' @{text=((ClipboardPacket 'type=write:id=primary:loc=primary')+(ClipboardPacket 'type=wdata'))} $inputSession
    }
    InputCheck 'OSC 5522 clear acknowledges completion' (ClipboardPacket 'type=write:status=DONE:id=clear') {
        $null=Rpc 'session.write' @{text=(ClipboardWritePackets 'clear' @{})} $inputSession
    }
    Check 'OSC 5522 clear removes the Windows text representation' ([string]::IsNullOrEmpty([string](Get-Clipboard)))
    InputCheck 'OSC 5522 empty representation is a successful write' (ClipboardPacket 'type=write:status=DONE:id=empty') {
        $null=Rpc 'session.write' @{text=(ClipboardWritePackets 'empty' @{'application/x-empty'=''})} $inputSession
    }
    InputCheck 'Empty representation remains available after writing' ((ClipboardPacket 'type=read:status=OK:id=emptylist')+(ClipboardPacket 'type=read:status=DATA:id=emptylist:mime=Lg==' (ClipboardBase64 "application/x-empty`n"))+(ClipboardPacket 'type=read:status=DONE:id=emptylist')) {
        $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=read:id=emptylist' 'Lg==')} $inputSession
    }
    $largeClipboard='x'*800000
    $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=write:id=large')} $inputSession
    for($offset=0;$offset -lt $largeClipboard.Length;$offset+=4096) {
        $chunk=$largeClipboard.Substring($offset,[Math]::Min(4096,$largeClipboard.Length-$offset))
        $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=wdata:mime=dGV4dC9wbGFpbg==' (ClipboardBase64 $chunk))} $inputSession
    }
    InputCheck 'Large MIME write preserves the previous 1-MiB clipboard allowance' (ClipboardPacket 'type=write:status=DONE:id=large') {
        $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=wdata')} $inputSession
    }
    Check 'Large clipboard reaches Windows without truncation' (([string](Get-Clipboard)) -ceq $largeClipboard)
    $beforeLargeRead=(InputBytes).Length
    $null=Rpc 'session.write' @{text=(ClipboardPacket 'type=read:id=large' (ClipboardBase64 'text/plain'))} $inputSession
    $largeDone=[Convert]::ToHexString([Text.Encoding]::UTF8.GetBytes((ClipboardPacket 'type=read:status=DONE:id=large')))
    for($attempt=0;$attempt -lt 200;$attempt++) {
        $largeReply=(InputBytes).Substring($beforeLargeRead)
        if($largeReply.Contains($largeDone)){break}; Start-Sleep -Milliseconds 50
    }
    $largeReplyText=[Text.Encoding]::UTF8.GetString([Convert]::FromHexString($largeReply))
    $readData=[Text.StringBuilder]::new()
    foreach($match in [regex]::Matches($largeReplyText,'type=read:status=DATA:id=large:mime=dGV4dC9wbGFpbg==;([A-Za-z0-9+/=]+)')) {
        [void]$readData.Append([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($match.Groups[1].Value)))
    }
    Check 'Large clipboard read survives base64 expansion and PTY response queuing' ($largeReply.Contains($largeDone) -and $readData.ToString() -ceq $largeClipboard)
} finally {
    $null=Rpc 'config.set' @{key='clipboard-write';value=$clipboardWritePolicy}
    $null=Rpc 'config.set' @{key='clipboard-read';value=$clipboardReadPolicy}
}
