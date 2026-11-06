#Requires -Version 7.0
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][string]$BaseVhd,
    [Parameter(Mandatory)][ValidatePattern('^S-1-5-21-(\d+-){3}\d+$')][string]$UserSid,
    [string]$StatusPath,
    [switch]$Update
)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgwintermUiHost'
$configPath = Join-Path $root 'host.json'
$pwsh = Join-Path $env:ProgramFiles 'PowerShell/7/pwsh.exe'
if (-not (Test-Path $pwsh)) { throw 'The installed PowerShell 7 in Program Files is required.' }
Import-Module Hyper-V
$BaseVhd = (Resolve-Path -LiteralPath $BaseVhd).Path
$image = Get-VHD -Path $BaseVhd
if ($image.Attached -or $image.VhdType -notin @('Dynamic','Fixed') -or [IO.Path]::GetExtension($BaseVhd) -ne '.vhdx') { throw 'Use the detached, prepared standalone VHDX.' }
function Write-Status([string]$Phase, [string]$Detail='') {
    Write-Host "$Phase $Detail"
    if ($StatusPath) { @{phase=$Phase;detail=$Detail;utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $StatusPath }
}
function Protect-Directory([string]$Path,[bool]$UserRead) {
    $null = New-Item -ItemType Directory -Path $Path -Force
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow')) }
    if ($UserRead) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($UserSid),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')) }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
try {
    if (Test-Path $root) {
        if (-not $Update -or -not (Test-Path $configPath)) { throw 'UI host directory already exists. Use -Update only for this installer-owned host.' }
        if ((Get-Acl $root).GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') { throw 'Existing UI host has an unexpected owner.' }
        $old = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($old.schema -ne 1 -or $old.userSid -ne $UserSid) { throw 'Existing UI host belongs to a different account or version.' }
        foreach ($slot in 0,1) {
            $slotPath = Join-Path $root "jobs/slot-$slot"
            if (-not (Test-Path -LiteralPath $slotPath)) { continue }
            foreach ($job in Get-ChildItem -LiteralPath $slotPath -Directory -ErrorAction Stop) {
                # A worker can retain a failed job for recovery indefinitely.
                # Leave its files and registered VM in place during an update.
                $marker = Join-Path $job.FullName 'recovery-required.json'
                if (Test-Path -LiteralPath $marker) {
                    $run = Get-Content -LiteralPath (Join-Path $job.FullName 'run/hyperv-run.json') -Raw | ConvertFrom-Json
                    $vm = Get-VM -Id ([guid]$run.vmId) -ErrorAction Stop
                    if ($vm.Name -eq "agwinterm-ui-$($run.runId)" -and $vm.State -eq 'Off') { continue }
                }
                throw "Wait until UI host job $($job.Name) in slot $slot has finished before updating."
            }
        }
        foreach ($slot in 0,1) { Stop-ScheduledTask -TaskPath '\AgwintermUi\' -TaskName "Slot$slot" -ErrorAction SilentlyContinue }
    }
    Write-Status 'installing' 'Protecting the fixed host runner and image'
    Protect-Directory $root $true
    $bin = Join-Path $root 'bin'
    Protect-Directory $bin $true
    Protect-Directory (Join-Path $root 'jobs') $false
    foreach ($name in @('hyperv-ui-host.ps1','hyperv-ui-worker.ps1','hyperv-ui-transfer.cs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../tests/integration/$name") -Destination (Join-Path $bin $name) -Force
    }
    $destination = Join-Path $root 'windows.vhdx'
    if (-not (Test-Path $destination)) { Copy-Item -LiteralPath $BaseVhd -Destination $destination }
    elseif ((Get-FileHash $destination).Hash -ne (Get-FileHash $BaseVhd).Hash) { throw 'The installed base differs. Preserve it and use a deliberate image migration instead of overwriting it.' }
    (Get-Item $destination).IsReadOnly = $true
    $config = @{schema=1;userSid=$UserSid;pipePrefix="agwinterm-ui-$UserSid";baseVhd=$destination;sourceBaseVhd=$BaseVhd;
        cpuMaximum=$(if ([Environment]::ProcessorCount -ge 16){100}else{50});installedUtc=[DateTime]::UtcNow.ToString('o')}
    $config | ConvertTo-Json | Set-Content $configPath
    Get-ChildItem $bin -File | ForEach-Object { @{name=$_.Name;sha256=(Get-FileHash $_.FullName).Hash} } | ConvertTo-Json | Set-Content (Join-Path $root 'installed-files.json')
    $scheduler = New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    $sddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$UserSid)"
    try { $folder = $scheduler.GetFolder('\AgwintermUi') } catch { $folder = $scheduler.GetFolder('\').CreateFolder('AgwintermUi',$sddl) }
    $folder.SetSecurityDescriptor($sddl,0)
    foreach ($slot in 0,1) {
        $task = $scheduler.NewTask(0)
        $task.RegistrationInfo.Description = 'Agwinterm isolated UI VM worker. Protected host code; guest-only payload execution.'
        $task.Principal.UserId = 'SYSTEM'
        $task.Principal.LogonType = 5
        $task.Principal.RunLevel = 1
        $task.Settings.MultipleInstances = 2 # IgnoreNew: one worker per slot.
        $task.Settings.DisallowStartIfOnBatteries = $false
        $task.Settings.StopIfGoingOnBatteries = $false
        $task.Settings.ExecutionTimeLimit = 'PT0S'
        $task.Settings.AllowDemandStart = $true
        $task.Settings.Enabled = $true
        $action = $task.Actions.Create(0)
        $action.Path = $pwsh
        $action.Arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+(Join-Path $bin 'hyperv-ui-worker.ps1')+'" -Configuration "'+$configPath+'" -Slot '+$slot
        $action.WorkingDirectory = $bin
        $null = $folder.RegisterTaskDefinition("Slot$slot",$task,6,'SYSTEM',$null,5,$sddl)
    }
    Write-Status 'ready' $configPath
} catch { Write-Status 'failed' $_.ToString(); throw }
