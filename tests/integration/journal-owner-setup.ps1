$journalGateName='Local\agwinterm-owner-'+[guid]::NewGuid().ToString('N')
$journalGate=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::AutoReset,$journalGateName)
$journalReply=Join-Path $artifact 'background-reply.txt'
$journalPidFile=Join-Path $artifact 'child-pid.txt'
$journalScript=Join-Path $artifact 'background-query.ps1'
@"
[Console]::OutputEncoding=[Text.UTF8Encoding]::new(`$false)
Set-Content -LiteralPath '$journalPidFile' -Value `$PID
`$gate=[Threading.EventWaitHandle]::OpenExisting('$journalGateName')
`$esc=[string][char]27
[Console]::Write((`$esc+'[?9001l'+`$esc+'[2J'+`$esc+'[HOWNER_READY'))
[void]`$gate.WaitOne()
[Console]::Write((`$esc+'[5n'))
`$reply='';`$timer=[Diagnostics.Stopwatch]::StartNew()
while(`$timer.ElapsedMilliseconds -lt 15000 -and -not `$reply.EndsWith('n')){
 if([Console]::KeyAvailable){`$reply += [Console]::ReadKey(`$true).KeyChar}else{Start-Sleep -Milliseconds 10}
}
Set-Content -LiteralPath '$journalReply' -Value `$reply.Replace(`$esc,'ESC')
[Console]::Write((`$esc+'[2;1HBACKGROUND_REPLY'))
[void]`$gate.WaitOne()
exit 0
"@ | Set-Content $journalScript
@{default='Journal';profiles=@(@{name='Journal';command='powershell.exe';args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$journalScript);cwd=$artifact})} |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $appDir 'profiles.json')
@('session-host = server','emulator-core = ghostty','cursor-blink = false') | Add-Content (Join-Path $appDir 'agwinterm.conf')
