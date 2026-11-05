Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class JournalWindowProbe {
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
}
'@
function JournalWait([scriptblock]$condition){for($i=0;$i -lt 100;$i++){if(& $condition){return $true};Start-Sleep -Milliseconds 100};return $false}
Check 'journal child becomes ready' (JournalWait { ([string](Rpc 'session.text' @{} $session)).Contains('OWNER_READY') })
$originalChild=[Diagnostics.Process]::GetProcessById([int](Get-Content $journalPidFile -Raw))
$window=[string](@((Rpc 'window.list').windows | Where-Object open)[0].id)
$null=Rpc 'window.close' @{} $window
Check 'last window becomes invisible while owner remains alive' (JournalWait { -not [JournalWindowProbe]::IsWindowVisible($hwnd) -and -not $proc.HasExited })
Start-Sleep -Milliseconds 200
$framesBefore=@(Get-Content (Join-Path $artifact 'frames.log') | Where-Object {$_ -like 'frame *'}).Count
[void]$journalGate.Set()
Check 'background parser answers terminal state query' (JournalWait { Test-Path $journalReply })
Check 'background query has exactly one correct reply' ((Get-Content $journalReply -Raw).Trim() -eq 'ESC[0n')
Check 'hidden owner continues parsing output' (JournalWait { ([string](Rpc 'session.text' @{} $session)).Contains('BACKGROUND_REPLY') })
Start-Sleep -Milliseconds 300
$framesAfter=@(Get-Content (Join-Path $artifact 'frames.log') | Where-Object {$_ -like 'frame *'}).Count
Check 'background output causes zero rendered frames' ($framesAfter -eq $framesBefore)
$reopen=[HudOwnedJob]::new()
try {
    $reopen.Start([IO.Path]::GetFullPath($Exe),"--app-id $appId --pipe $pipe --no-restore",$root)
    $reopenCode=$reopen.WaitExit(15000)
    Check 'ordinary launch hands back to the existing owner' ($reopenCode -eq 0) "exit=$reopenCode"
    Check 'reopen shows the same native window and terminal' (JournalWait { [JournalWindowProbe]::IsWindowVisible($hwnd) -and ([string](Rpc 'session.text' @{} $session)).Contains('BACKGROUND_REPLY') })
} finally {$reopen.Finish()}
$recovery=[HudOwnedJob]::new()
try {
    $recoveryTimer=[Diagnostics.Stopwatch]::StartNew()
    $proc.Kill();$proc.WaitForExit()
    Check 'owner crash leaves the shell alive in its broker' (-not $originalChild.HasExited)
    $recovery.Start([IO.Path]::GetFullPath($Exe),"--app-id $appId --pipe $pipe",$root)
    $restored=JournalWait { try { ([string](Rpc 'session.text' @{} $session)).Contains('BACKGROUND_REPLY') } catch { $false } }
    Check 'new owner restores the original shell and journal after crash' ($restored -and -not $originalChild.HasExited)
    @{recoveryMilliseconds=$recoveryTimer.Elapsed.TotalMilliseconds;oldOwnerPid=$job.Pid;newOwnerPid=$recovery.Pid;shellPid=$originalChild.Id} |
        ConvertTo-Json | Set-Content (Join-Path $artifact 'journal-recovery.json')
    $null=Rpc 'session.close' @{} $session
    Check 'explicit session close terminates its original shell' (JournalWait { $originalChild.HasExited })
} finally {$recovery.Finish();$originalChild.Dispose()}
