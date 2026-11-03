#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$VMName,
    [Parameter(Mandatory)][string]$Destination,
    [switch]$Compact
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../tests/integration/hyperv-ui-host.ps1"
Assert-HyperVUiHost
$vm = Get-VM -Name $VMName
if ($vm.State -ne 'Off' -or $vm.Generation -ne 2) { throw 'Shut down the generation-2 image VM from inside Windows first.' }
if (@(Get-VMSnapshot -VM $vm).Count) { throw 'Merge/remove template checkpoints before sealing the base image.' }
$drive = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 0 })
if ($drive.Count -ne 1) { throw 'Expected the Windows disk at SCSI 0:0.' }
$source = Get-VHD -Path $drive[0].Path
if ($source.Attached -or $source.VhdType -notin @('Fixed','Dynamic')) { throw 'Image disk must be detached and standalone.' }
$Destination = [IO.Path]::GetFullPath($Destination)
if ([IO.Path]::GetExtension($Destination) -ne '.vhdx') { throw 'Destination must end in .vhdx.' }
if ((Test-Path -LiteralPath $Destination) -or (Test-Path -LiteralPath ($Destination + '.json'))) { throw 'Destination already exists; sealed images are never overwritten.' }
$null = New-Item -ItemType Directory -Path (Split-Path $Destination) -Force
Copy-Item -LiteralPath $source.Path -Destination $Destination
$beforeBytes = (Get-Item -LiteralPath $Destination).Length
if ($Compact -and $source.VhdType -eq 'Dynamic') {
    $null = Mount-VHD -Path $Destination -ReadOnly -NoDriveLetter
    try { Optimize-VHD -Path $Destination -Mode Full }
    finally { Dismount-VHD -Path $Destination }
}
(Get-Item -LiteralPath $Destination).IsReadOnly = $true
@{schema=1; sourceVmId=$vm.Id.ToString(); sealedUtc=[DateTime]::UtcNow.ToString('o'); sha256=(Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash;
    compacted=[bool]$Compact;bytesBeforeCompaction=$beforeBytes;bytes=(Get-Item -LiteralPath $Destination).Length} |
    ConvertTo-Json | Set-Content -LiteralPath ($Destination + '.json') -Encoding utf8
Write-Host "Sealed immutable base: $Destination"
Write-Host 'Run a Quick UI suite against this image to validate automatic logon and desktop readiness before parallel use.'
