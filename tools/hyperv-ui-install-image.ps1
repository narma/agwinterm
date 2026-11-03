#Requires -Version 7.0
param(
    [Parameter(Mandatory)][guid]$VMId,
    [Parameter(Mandatory)][string]$IsoPath,
    [Parameter(Mandatory)][string]$StateDirectory,
    [Parameter(Mandatory)][string]$BaseDestination,
    [ValidateRange(0,100)][int]$ImageIndex = 0,
    [switch]$ResumeAppliedImage
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../tests/integration/hyperv-ui-host.ps1"
Assert-HyperVUiHost
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
$null = New-Item -ItemType Directory -Path $StateDirectory -Force
function Write-ImageState([string]$Phase, [string]$Detail = '') {
    @{phase=$Phase; detail=$Detail; utc=[DateTime]::UtcNow.ToString('o'); vmId=$VMId.ToString()} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StateDirectory 'install-state.json')
    Write-Host "$Phase $Detail"
}
$isoMounted = $false
$diskMounted = $false
$session = $null
try {
    $vm = Get-VM -Id $VMId
    if ($vm.State -ne 'Off' -or $vm.Generation -ne 2) { throw 'The installation VM must be off and generation 2. Existing running installations are never interrupted.' }
    $drives = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 0 -and $_.ControllerType -eq 'SCSI' })
    if ($drives.Count -ne 1) { throw 'Cannot identify the unique OS disk at SCSI 0:0.' }
    $diskPath = $drives[0].Path
    if ((Get-VHD -Path $diskPath).VhdType -ne 'Dynamic') { throw 'Expected a new standalone dynamic OS VHDX.' }
    $IsoPath = (Resolve-Path -LiteralPath $IsoPath).Path
    $iso = Get-DiskImage -ImagePath $IsoPath
    if (-not $iso.Attached) { $iso = Mount-DiskImage -ImagePath $IsoPath -PassThru; $isoMounted = $true }
    $isoDrive = ($iso | Get-Volume).DriveLetter
    $wim = "${isoDrive}:\sources\install.wim"
    if (-not (Test-Path -LiteralPath $wim)) { $wim = "${isoDrive}:\sources\install.esd" }
    $images = @(Get-WindowsImage -ImagePath $wim)
    $images | Select-Object ImageIndex,ImageName | ConvertTo-Json | Set-Content (Join-Path $StateDirectory 'iso-images.json')
    if (-not $ImageIndex) {
        $pro = @($images | Where-Object { $_.ImageName -eq 'Windows 11 Pro' -or $_.ImageName -eq 'Windows 11 Профессиональная' })
        if ($pro.Count -ne 1) { throw 'Cannot select Windows 11 Pro unambiguously. Review iso-images.json and pass -ImageIndex.' }
        $ImageIndex = $pro[0].ImageIndex
    }
    $selected = Get-WindowsImage -ImagePath $wim -Index $ImageIndex
    if ($selected.Architecture -notin @(9,'x64','amd64')) { throw 'An x64 Windows image is required.' }
    Write-ImageState 'preparing-disk' $selected.ImageName
    $mounted = Mount-VHD -Path $diskPath -NoDriveLetter -PassThru
    $diskMounted = $true
    $disk = Get-Disk -Number $mounted.DiskNumber
    if ($disk.IsBoot -or $disk.IsSystem) { throw 'Refusing to modify a host system disk.' }
    if ($ResumeAppliedImage) {
        $efiPartitions = @(Get-Partition -DiskNumber $disk.Number | Where-Object { ($_ | Get-Volume -ErrorAction SilentlyContinue).FileSystemLabel -eq 'AGW_EFI' })
        $osPartitions = @(Get-Partition -DiskNumber $disk.Number | Where-Object { ($_ | Get-Volume -ErrorAction SilentlyContinue).FileSystemLabel -eq 'AGW_WINDOWS' })
        if ($efiPartitions.Count -ne 1 -or $osPartitions.Count -ne 1) { throw 'Resume requires the unique installer-owned AGW_EFI and AGW_WINDOWS partitions.' }
        $efi = $efiPartitions[0]; $windows = $osPartitions[0]
        foreach ($partition in @($efi,$windows)) {
            if (-not $partition.DriveLetter) { Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -AssignDriveLetter }
        }
        $efi = Get-Partition -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber
        $windows = Get-Partition -DiskNumber $disk.Number -PartitionNumber $windows.PartitionNumber
    } else {
        if ($disk.PartitionStyle -ne 'RAW') { throw 'Refusing to format an OS disk with existing partitions.' }
        $null = Initialize-Disk -Number $disk.Number -PartitionStyle GPT
        $efi = New-Partition -DiskNumber $disk.Number -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -AssignDriveLetter
        $null = $efi | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'AGW_EFI' -Confirm:$false -Force
        $null = New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}'
        $windows = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
        $null = $windows | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'AGW_WINDOWS' -Confirm:$false -Force
    }
    if (-not $efi.DriveLetter -or -not $windows.DriveLetter) { throw 'Guest partitions did not receive drive letters.' }
    $windowsRoot = "$($windows.DriveLetter):\"
    $efiRoot = "$($efi.DriveLetter):\"
    if ($ResumeAppliedImage -and (Test-Path (Join-Path $windowsRoot 'Windows/Panther/setupact.log'))) {
        throw 'ResumeAppliedImage is only for pre-boot deployment failures. Windows setup has already started; retain this disk for diagnosis and create a clean installation disk.'
    }
    if (-not $ResumeAppliedImage) {
        Write-ImageState 'applying-windows' $selected.ImageName
        & dism.exe /English /Apply-Image "/ImageFile:$wim" "/Index:$ImageIndex" "/ApplyDir:$windowsRoot" /CheckIntegrity *> (Join-Path $StateDirectory 'dism-apply.log')
        if ($LASTEXITCODE -ne 0) { throw "DISM image deployment failed: $LASTEXITCODE. See dism-apply.log." }
    }
    if (-not (Test-Path (Join-Path $windowsRoot 'Windows/System32/config/BCD-Template'))) { throw 'No deployed Windows boot template found.' }
    Write-ImageState 'writing-boot-files' "Windows=$windowsRoot EFI=$efiRoot"
    & bcdboot.exe (Join-Path $windowsRoot 'Windows') /s $efiRoot /f UEFI /c /v *> (Join-Path $StateDirectory 'bcdboot.log')
    if ($LASTEXITCODE -ne 0) {
        # Some serviced host builds fail BCDBoot's store-template import with
        # STATUS_OBJECT_NAME_COLLISION. Build a minimal guest-only UEFI store
        # explicitly; every BCDEdit invocation is bound to that offline file.
        Write-ImageState 'writing-boot-files' 'Creating an explicit offline guest BCD store'
        $bootRoot = Join-Path $efiRoot 'EFI/Microsoft/Boot'
        $fallbackRoot = Join-Path $efiRoot 'EFI/Boot'
        $null = New-Item -ItemType Directory -Path $bootRoot,$fallbackRoot -Force
        Copy-Item -Path (Join-Path $windowsRoot 'Windows/Boot/EFI/*') -Destination $bootRoot -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $windowsRoot 'Windows/Boot/EFI/bootmgfw.efi') -Destination (Join-Path $fallbackRoot 'bootx64.efi') -Force
        $storePath = Join-Path $bootRoot ('BCD-' + [guid]::NewGuid().ToString('N'))
        function Invoke-GuestBcd([string[]]$BcdArguments) {
            $output = & bcdedit.exe @BcdArguments 2>&1
            $output | Out-File -LiteralPath (Join-Path $StateDirectory 'guest-bcd.log') -Append
            if ($LASTEXITCODE -ne 0) { throw "Guest BCDEdit failed ($LASTEXITCODE): $($output -join ' ')" }
            return ($output -join ' ')
        }
        $null = Invoke-GuestBcd @('/createstore',$storePath)
        $null = Invoke-GuestBcd @('/store',$storePath,'/create','{bootmgr}','/d','Windows Boot Manager')
        $created = Invoke-GuestBcd @('/store',$storePath,'/create','/d','Windows 11 UI tests','/application','osloader')
        $loader = [regex]::Match($created, '\{[0-9a-fA-F-]{36}\}').Value
        if (-not $loader) { throw 'Guest BCDEdit returned no OS loader ID.' }
        # BCDEdit rewrites partition=<mounted-letter> to a native-host VHD path.
        # Locate the Windows partition inside the VM instead of referencing its host file.
        foreach ($setting in @(
            @($loader,'device','locate=\Windows\system32\winload.efi'),
            @($loader,'osdevice','locate=\Windows'),
            @($loader,'path','\Windows\system32\winload.efi'),
            @($loader,'systemroot','\Windows'),
            @('{bootmgr}','device','boot'),
            @('{bootmgr}','path','\EFI\Microsoft\Boot\bootmgfw.efi')
        )) { $null = Invoke-GuestBcd (@('/store',$storePath,'/set') + $setting) }
        $null = Invoke-GuestBcd @('/store',$storePath,'/default',$loader)
        $null = Invoke-GuestBcd @('/store',$storePath,'/displayorder',$loader,'/addlast')
        $null = Invoke-GuestBcd @('/store',$storePath,'/timeout','0')
        # A newly created BCD store needs the system-store flags for specialize.
        . "$PSScriptRoot/hyperv-ui-offline-registry.ps1"
        $systemStore = $storePath + '-system'
        [AgwintermOfflineHive]::MarkSystemBcd($storePath, $systemStore)
        Move-Item -LiteralPath $systemStore -Destination (Join-Path $bootRoot 'BCD') -Force
        Remove-Item -LiteralPath $storePath
    }
    $guestSetup = Join-Path $windowsRoot 'ProgramData/AgwintermUi'
    $null = New-Item -ItemType Directory -Path $guestSetup -Force
    Copy-Item -LiteralPath "$PSScriptRoot/hyperv-ui-prepare-guest.ps1" -Destination (Join-Path $guestSetup 'prepare-guest.ps1')
    Copy-Item -LiteralPath "$PSScriptRoot/hyperv-ui-first-logon.ps1" -Destination (Join-Path $guestSetup 'first-logon.ps1')
    Copy-Item -LiteralPath "$PSScriptRoot/../tests/integration/hyperv-ui-bootstrap.ps1" -Destination (Join-Path $guestSetup 'source-bootstrap.ps1')
    $secret = [guid]::NewGuid().ToString('N') + '!aA9'
    $credential = [Management.Automation.PSCredential]::new('AgwintermSetup', (ConvertTo-SecureString $secret -AsPlainText -Force))
    # Recovery credential is protected by Windows DPAPI for this administrator.
    # It is never printed or put into command-line arguments.
    $credential | Export-Clixml -LiteralPath (Join-Path $StateDirectory 'setup-credential.clixml')
    $escapedPassword = [Security.SecurityElement]::Escape($secret)
    $answer = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><ComputerName>AGW-UI-IMAGE</ComputerName><TimeZone>Russian Standard Time</TimeZone></component>
    <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <RunSynchronous><RunSynchronousCommand wcm:action="add"><Order>1</Order><Path>reg.exe add HKLM\SYSTEM\CurrentControlSet\Control\BitLocker /v PreventDeviceEncryption /t REG_DWORD /d 1 /f</Path></RunSynchronousCommand></RunSynchronous>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><InputLocale>0409:00000409</InputLocale><SystemLocale>ru-RU</SystemLocale><UILanguage>ru-RU</UILanguage><UserLocale>ru-RU</UserLocale></component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <OOBE><HideEULAPage>true</HideEULAPage><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC></OOBE>
      <UserAccounts><LocalAccounts><LocalAccount wcm:action="add"><Name>AgwintermSetup</Name><DisplayName>Agwinterm image setup</DisplayName><Group>Administrators</Group><Password><Value>$escapedPassword</Value><PlainText>true</PlainText></Password></LocalAccount></LocalAccounts></UserAccounts>
      <AutoLogon><Username>AgwintermSetup</Username><Domain>.</Domain><Password><Value>$escapedPassword</Value><PlainText>true</PlainText></Password><Enabled>true</Enabled><LogonCount>2</LogonCount></AutoLogon>
      <FirstLogonCommands><SynchronousCommand wcm:action="add"><Order>1</Order><Description>Prepare isolated UI test image</Description><CommandLine>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\AgwintermUi\first-logon.ps1</CommandLine></SynchronousCommand></FirstLogonCommands>
    </component>
  </settings>
</unattend>
"@
    $panther = Join-Path $windowsRoot 'Windows/Panther'
    $null = New-Item -ItemType Directory -Path $panther -Force
    [IO.File]::WriteAllText((Join-Path $panther 'unattend.xml'), $answer)
    $secret = $null; $answer = $null; $escapedPassword = $null
    Dismount-VHD -Path $diskPath
    $diskMounted = $false
    if ($isoMounted) { Dismount-DiskImage -ImagePath $IsoPath; $isoMounted = $false }
    # Offline image setup needs no network; no Windows viewer is opened.
    Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter
    Set-VMFirmware -VM $vm -FirstBootDevice $drives[0]
    Get-VMDvdDrive -VM $vm | Set-VMDvdDrive -Path $null
    Start-VM -VM $vm | Out-Null
    Write-ImageState 'waiting-for-guest' 'Unattended Windows setup and first interactive logon'
    $deadline = [DateTime]::UtcNow.AddMinutes(30)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Get-VM -Id $vm.Id).State -eq 'Off') { throw 'The installation VM powered off before Windows setup completed. Inspect its boot screen and Windows/Panther logs; the image is retained.' }
        try {
            if (-not $session -or $session.State -ne 'Opened') {
                if ($session) { Remove-PSSession $session; $session = $null }
                $session = New-PSSession -VMId $vm.Id -Credential $credential -ErrorAction Stop
            }
            $status = Invoke-Command -Session $session -ScriptBlock {
                $path = 'C:\ProgramData\AgwintermUi\image-configured.json'
                if (-not (Test-Path $path)) { return @{phase='setup'} }
                $configured = Get-Content $path -Raw | ConvertFrom-Json
                if (-not $configured.configured) { return @{phase='failed'; error=$configured.error} }
                $loggedOn = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq 'AgwintermUi' })
                if (-not $loggedOn.Count) { return @{phase='awaiting-test-logon'} }
                $volume = Get-BitLockerVolume -MountPoint C:
                if ($volume.VolumeStatus -ne 'FullyDecrypted') { return @{phase='failed'; error='The guest OS volume is encrypted.'} }
                return @{phase='ready'; sessionId=$loggedOn[0].SessionId}
            }
            if ($status.phase -eq 'failed') { throw "Guest provisioning failed: $($status.error)" }
            if ($status.phase -eq 'ready') { $ready = $true; break }
        } catch {
            if ($_.Exception.Message -like 'Guest provisioning failed:*') { throw }
            # Connection loss during the intentional first-logon reboot is normal.
        }
        Start-Sleep -Seconds 10
    }
    if (-not $ready) { throw 'Timed out preparing the Windows guest. VM retained; setup-credential.clixml supports administrator recovery.' }
    Write-ImageState 'sealing' 'Verified automatic test-user logon and unencrypted OS disk'
    Invoke-Command -Session $session -ScriptBlock {
        Disable-LocalUser -Name AgwintermSetup
        & shutdown.exe /s /t 0
    }
    Remove-PSSession $session; $session = $null
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ((Get-VM -Id $vm.Id).State -ne 'Off' -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Seconds 2 }
    & "$PSScriptRoot/hyperv-ui-seal-image.ps1" -VMName $vm.Name -Destination $BaseDestination
    Remove-Item -LiteralPath (Join-Path $StateDirectory 'setup-credential.clixml')
    Write-ImageState 'ready' $BaseDestination
} catch {
    Write-ImageState 'failed' $_.ToString()
    throw
} finally {
    if ($session) { Remove-PSSession $session }
    if ($diskMounted) { Dismount-VHD -Path $diskPath }
    if ($isoMounted) { Dismount-DiskImage -ImagePath $IsoPath }
}
