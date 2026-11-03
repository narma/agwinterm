#Requires -Version 5.1
#Requires -RunAsAdministrator
param(
    [ValidatePattern('^[a-zA-Z][a-zA-Z0-9_-]{0,19}$')][string]$UserName = 'AgwintermUi',
    [Security.SecureString]$Password,
    [switch]$ReconfigureExisting,
    [string]$Bootstrap = "$PSScriptRoot/../tests/integration/hyperv-ui-bootstrap.ps1"
)
$ErrorActionPreference = 'Stop'
$machine = Get-CimInstance Win32_ComputerSystem
if ($machine.Manufacturer -ne 'Microsoft Corporation' -or $machine.Model -ne 'Virtual Machine') {
    throw 'Run this script INSIDE a disposable Hyper-V Windows image, never on the development host.'
}
if ($machine.PartOfDomain) { throw 'Use a dedicated, non-domain-joined test image.' }
$Bootstrap = (Resolve-Path -LiteralPath $Bootstrap).Path
$user = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
if ($user -and (-not $ReconfigureExisting -or $user.Description -ne 'Disposable Agwinterm Hyper-V UI test account')) { throw "Account $UserName already exists; only an explicitly selected disposable test account may be reconfigured." }
if (-not $Password) { $Password = Read-Host 'Password for the disposable local UI test account' -AsSecureString }
if ($user) { Set-LocalUser -Name $UserName -Password $Password -PasswordNeverExpires $true; Enable-LocalUser -Name $UserName }
else { $user = New-LocalUser -Name $UserName -Password $Password -PasswordNeverExpires -AccountNeverExpires -Description 'Disposable Agwinterm Hyper-V UI test account' }
# The bootstrap sets the per-run registry guard and shuts down the disposable VM.
if (-not (Get-LocalGroupMember -SID 'S-1-5-32-544' | Where-Object { $_.SID -eq $user.SID })) { Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $user }
$root = 'C:\ProgramData\AgwintermUi'
$null = New-Item -ItemType Directory -Path $root -Force
Copy-Item -LiteralPath $Bootstrap -Destination (Join-Path $root 'bootstrap.ps1') -Force
$key = 'HKLM:\SOFTWARE\Agwinterm\UiTests'
$null = New-Item -Path $key -Force
New-ItemProperty -LiteralPath $key -Name Schema -Value 1 -PropertyType DWord -Force | Out-Null
Set-ItemProperty -LiteralPath $key -Name UserSid -Value $user.SID.Value
Set-ItemProperty -LiteralPath $key -Name ActiveRunId -Value ''
Set-ItemProperty -LiteralPath $key -Name ActiveVmName -Value ''
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\ProgramData\AgwintermUi\bootstrap.ps1'
$principal = New-ScheduledTaskPrincipal -UserId $user.SID.Value -LogonType Interactive -RunLevel Highest
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user.SID.Value
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Days 2) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'Agwinterm UI bootstrap' -Action $action -Principal $principal -Trigger $trigger -Settings $settings -Force | Out-Null
# Windows built-in autologon stores the dedicated credential in the image.
# Never reuse a personal/domain password; no credential is exported to the host.
$winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$credential = [Management.Automation.PSCredential]::new($UserName, $Password)
Set-ItemProperty -LiteralPath $winlogon -Name DefaultUserName -Value $UserName
Set-ItemProperty -LiteralPath $winlogon -Name DefaultDomainName -Value $env:COMPUTERNAME
Set-ItemProperty -LiteralPath $winlogon -Name DefaultPassword -Value $credential.GetNetworkCredential().Password
Set-ItemProperty -LiteralPath $winlogon -Name AutoAdminLogon -Value '1'
Remove-ItemProperty -LiteralPath $winlogon -Name AutoLogonCount -ErrorAction SilentlyContinue
$policy = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
New-ItemProperty -LiteralPath $policy -Name InactivityTimeoutSecs -Value 0 -PropertyType DWord -Force | Out-Null
$personalization = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization'
$null = New-Item -Path $personalization -Force
New-ItemProperty -LiteralPath $personalization -Name NoLockScreen -Value 1 -PropertyType DWord -Force | Out-Null
& powercfg.exe /change monitor-timeout-ac 0
if ($LASTEXITCODE -ne 0) { throw 'Cannot disable guest display timeout.' }
& powercfg.exe /change standby-timeout-ac 0
if ($LASTEXITCODE -ne 0) { throw 'Cannot disable guest sleep.' }
& powercfg.exe /hibernate off
if ($LASTEXITCODE -ne 0) { throw 'Cannot disable guest hibernation.' }
Set-Service vmickvpexchange -StartupType Automatic
Start-Service vmickvpexchange
Write-Host 'Prepared. Restart, verify automatic console logon, finish first-logon prompts, disable the test account screen saver, then shut Windows down fully.'
Write-Host 'The image contains the disposable autologon password. Keep it private. Do not enable BitLocker/device encryption: each clone has a new vTPM.'
