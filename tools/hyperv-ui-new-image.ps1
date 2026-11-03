#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$IsoPath,
    [Parameter(Mandatory)][string]$ImageDirectory,
    [string]$Name = ('agwinterm-ui-image-' + [guid]::NewGuid().ToString('N').Substring(0,8)),
    [ValidateRange(4096,65536)][int]$MemoryInMB = 4096,
    [ValidateRange(2,64)][int]$ProcessorCount = 2,
    [ValidateRange(64,512)][int]$DiskSizeGB = 80,
    [string]$SwitchName
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../tests/integration/hyperv-ui-host.ps1"
Assert-HyperVUiHost
$IsoPath = (Resolve-Path -LiteralPath $IsoPath).Path
if ([IO.Path]::GetExtension($IsoPath) -ne '.iso') { throw 'IsoPath must name a Windows x64 installation ISO.' }
$ImageDirectory = [IO.Path]::GetFullPath($ImageDirectory)
if (Test-Path -LiteralPath $ImageDirectory) { throw 'ImageDirectory must be a new directory; existing images are never overwritten.' }
if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { throw "VM already exists: $Name" }
$null = New-Item -ItemType Directory -Path $ImageDirectory
$disk = Join-Path $ImageDirectory 'windows-base.vhdx'
$options = @{Name=$Name; Generation=2; MemoryStartupBytes=($MemoryInMB * 1MB); NewVHDPath=$disk; NewVHDSizeBytes=($DiskSizeGB * 1GB); Path=$ImageDirectory}
if ($SwitchName) { $options.SwitchName = $SwitchName }
$vm = New-VM @options
# Retain an incomplete installation on error; never discard a user's OS setup.
@{vmId=$vm.Id.ToString(); vmName=$Name; baseVhd=$disk} | ConvertTo-Json | Set-Content (Join-Path $ImageDirectory 'image.json')
Set-VM -VM $vm -StaticMemory -MemoryStartupBytes ($MemoryInMB * 1MB) -AutomaticStartAction Nothing -AutomaticStopAction ShutDown -CheckpointType Disabled
Set-VMProcessor -VM $vm -Count $ProcessorCount
Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
Enable-VMTPM -VM $vm
Set-VMVideo -VM $vm -HorizontalResolution 1920 -VerticalResolution 1080 -ResolutionType Single
$dvd = Add-VMDvdDrive -VM $vm -Path $IsoPath -Passthru
Set-VMFirmware -VM $vm -FirstBootDevice $dvd
# A second disk supplies setup scripts without shares or extra installed tools.
$setupDisk = Join-Path $ImageDirectory 'setup.vhdx'
$mountPath = Join-Path $ImageDirectory 'setup-mount'
$null = New-VHD -Path $setupDisk -Dynamic -SizeBytes 128MB
Mount-HyperVUiDataDisk -Path $setupDisk -AccessPath $mountPath -Initialize -Label AGWINTERM_SETUP
try {
    Copy-Item -LiteralPath "$PSScriptRoot/hyperv-ui-prepare-guest.ps1" -Destination $mountPath
    Copy-Item -LiteralPath "$PSScriptRoot/../tests/integration/hyperv-ui-bootstrap.ps1" -Destination $mountPath
} finally { Dismount-HyperVUiDataDisk -Path $setupDisk -AccessPath $mountPath }
Add-VMHardDiskDrive -VM $vm -Path $setupDisk -ControllerType SCSI
Write-Host "Created $Name. Start it and open VMConnect manually to install Windows from the ISO."
Write-Host 'Inside the guest, use elevated Windows PowerShell to run: <setup-drive>:\hyperv-ui-prepare-guest.ps1 -Bootstrap <setup-drive>:\hyperv-ui-bootstrap.ps1'
Write-Host "After setup and clean shutdown, use -BaseVhd '$disk'. See docs/hyperv-ui-tests.md."
