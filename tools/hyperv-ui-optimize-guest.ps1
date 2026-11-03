#Requires -Version 5.1
#Requires -RunAsAdministrator
param([switch]$SkipComponentCleanup)
# Apply only to a disposable prepared image, after retaining a baseline VHDX.
# Keep Windows UI, accessibility, fonts, servicing and security facilities intact.
$ErrorActionPreference = 'Stop'
$machine = Get-CimInstance Win32_ComputerSystem
$prepared = Get-ItemProperty 'HKLM:\SOFTWARE\Agwinterm\UiTests'
if ($machine.Manufacturer -ne 'Microsoft Corporation' -or $machine.Model -ne 'Virtual Machine' -or $machine.PartOfDomain -or $prepared.Schema -ne 1) { throw 'Optimization requires a prepared standalone Hyper-V test image.' }
$changes = [Collections.Generic.List[object]]::new()
$timer = [Diagnostics.Stopwatch]::StartNew()
$freeBefore = (Get-Volume -DriveLetter C).SizeRemaining
function Set-ImagePolicy([string]$Path, [string]$Name, [int]$Value) {
    $before = (Get-ItemProperty -LiteralPath $Path -ErrorAction SilentlyContinue).$Name
    $changes.Add(@{kind='registry';path=$Path;name=$Name;before=$before;after=$Value})
    if (-not (Test-Path -LiteralPath $Path)) { $null = New-Item -Path $Path -Force }
    New-ItemProperty -LiteralPath $Path -Name $Name -Value $Value -PropertyType DWord -Force | Out-Null
}
$search = Get-CimInstance Win32_Service -Filter "Name='WSearch'" -ErrorAction SilentlyContinue
if ($search) {
    $changes.Add(@{kind='service';name='WSearch';before=$search.StartMode;after='Disabled'})
    Stop-Service WSearch -Force
    Set-Service WSearch -StartupType Disabled
}
Set-ImagePolicy 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' 'AllowNewsAndInterests' 0
Set-ImagePolicy 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsConsumerFeatures' 1
Set-ImagePolicy 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' 'EnableFirstLogonAnimation' 0
$runKey = "Registry::HKEY_USERS\$($prepared.UserSid)\Software\Microsoft\Windows\CurrentVersion\Run"
$oneDrive = (Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue).OneDrive
if ($oneDrive) {
    $changes.Add(@{kind='registry';path=$runKey;name='OneDrive';before=$oneDrive;after=$null})
    Remove-ItemProperty -LiteralPath $runKey -Name OneDrive
}
if (-not $SkipComponentCleanup) {
    & dism.exe /Online /Cleanup-Image /StartComponentCleanup /NoRestart *> 'C:\ProgramData\AgwintermUi\component-cleanup.log'
    if ($LASTEXITCODE -notin @(0,3010)) { throw "Guest component cleanup failed: $LASTEXITCODE; inspect component-cleanup.log." }
    $changes.Add(@{kind='servicing';name='StartComponentCleanup';exitCode=$LASTEXITCODE})
}
$report = @{utc=[DateTime]::UtcNow.ToString('o');changes=@($changes.ToArray());accountSid=$prepared.UserSid;
    elapsedSeconds=[Math]::Round($timer.Elapsed.TotalSeconds,2);freeBytesBefore=$freeBefore;freeBytesAfter=(Get-Volume -DriveLetter C).SizeRemaining}
$path = 'C:\ProgramData\AgwintermUi\optimization-' + (Get-Date -Format yyyyMMddTHHmmss) + '.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding UTF8
$report
