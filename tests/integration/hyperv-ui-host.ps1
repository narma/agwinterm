# Hyper-V operations kept separate so lifecycle and failure paths can be tested
# with host-free command doubles. Never select or stop VMs by a shared name.
function Assert-HyperVUiHost {
    if (-not $IsWindows) { throw 'Hyper-V UI tests require Windows.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run the Hyper-V UI runner from an elevated PowerShell 7 terminal (disk mounting requires administrator rights).'
    }
    Import-Module Hyper-V -ErrorAction Stop
}

function Mount-HyperVUiDataDisk([string]$Path, [string]$AccessPath, [switch]$Initialize, [switch]$ReadOnly, [string]$Label = 'AGWINTERM_UI') {
    $mounted = Mount-VHD -Path $Path -NoDriveLetter -ReadOnly:$ReadOnly -PassThru
    try {
        if ($Initialize) {
            $disk = Initialize-Disk -Number $mounted.DiskNumber -PartitionStyle GPT -PassThru
            $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize
            $null = $partition | Format-Volume -FileSystem NTFS -NewFileSystemLabel $Label -Confirm:$false -Force
        } else {
            $partitions = @(Get-Partition -DiskNumber $mounted.DiskNumber | Where-Object { $_.Type -eq 'Basic' })
            if ($partitions.Count -ne 1) { throw 'Expected one data partition on the owned UI payload disk.' }
            $partition = $partitions[0]
        }
        $null = New-Item -ItemType Directory -Path $AccessPath -Force
        Add-PartitionAccessPath -DiskNumber $mounted.DiskNumber -PartitionNumber $partition.PartitionNumber -AccessPath ($AccessPath.TrimEnd('\') + '\')
    } catch {
        Dismount-VHD -Path $Path
        throw
    }
}

function Dismount-HyperVUiDataDisk([string]$Path, [string]$AccessPath) {
    try {
        $disk = Get-VHD -Path $Path
        $mount = $AccessPath.TrimEnd('\') + '\'
        foreach ($partition in Get-Partition -DiskNumber $disk.DiskNumber) {
            if ($partition.AccessPaths -contains $mount) {
                Remove-PartitionAccessPath -DiskNumber $disk.DiskNumber -PartitionNumber $partition.PartitionNumber -AccessPath $mount
            }
        }
    } finally { Dismount-VHD -Path $Path }
}

function Read-HyperVUiResult([string]$Results, [string]$RunId) {
    $path = Join-Path $Results 'result.json'
    if (-not (Test-Path -LiteralPath $path)) { throw "VM stopped without a completed UI result. Inspect $Results (including bootstrap.log)." }
    $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($result.runId -cne $RunId -or ($result.exitCode -isnot [long] -and $result.exitCode -isnot [int]) -or $result.exitCode -notin @(0,1,2)) {
        throw "Invalid or stale Hyper-V UI result: $path"
    }
    return $result
}

function Get-HyperVUiHostMemory {
    $os = Get-CimInstance Win32_OperatingSystem
    [pscustomobject]@{FreeMB=[long]($os.FreePhysicalMemory / 1024); TotalMB=[long]($os.TotalVisibleMemorySize / 1024)}
}

function Get-HyperVUiCapacity {
    param([object[]]$ActiveVMs, [int]$MemoryInMB, [int]$MaxParallel, [int]$MemoryBudgetInMB, $HostMemory)
    $limit = $MaxParallel
    $budget = $MemoryBudgetInMB
    $usedMB = 0L
    foreach ($active in $ActiveVMs) {
        $usedMB += [long]([Math]::Max($active.MemoryStartup, $active.MemoryAssigned) / 1MB)
        # Respect the tighter policy of already running sessions, including runs
        # from another worktree. Older versions without notes still count.
        if ($active.Notes) {
            try {
                $policy = $active.Notes | ConvertFrom-Json -ErrorAction Stop
                if ($policy.schema -eq 'agwinterm-ui-budget-v1') {
                    if ($policy.maxParallel -ge 1) { $limit = [Math]::Min($limit, $policy.maxParallel) }
                    if ($policy.memoryBudgetInMB -ge 2048) { $budget = [Math]::Min($budget, $policy.memoryBudgetInMB) }
                }
            } catch { } # Unknown metadata never removes the VM from accounting.
        }
    }
    $reserveMB = [long][Math]::Max(4096, [Math]::Min(8192, $HostMemory.TotalMB / 4))
    [pscustomobject]@{
        Ready=($ActiveVMs.Count -lt $limit -and $usedMB + $MemoryInMB -le $budget -and $HostMemory.FreeMB - $MemoryInMB -ge $reserveMB)
        Active=$ActiveVMs.Count; Limit=$limit; UsedMB=$usedMB; BudgetMB=$budget; ReserveMB=$reserveMB
    }
}

function Start-HyperVUiWhenReady {
    param($VM, [int]$MemoryInMB, [int]$MaxParallel = 2, [int]$MemoryBudgetInMB = 8192, [int]$QueueTimeoutSeconds = 1800)
    if ($MemoryInMB -gt $MemoryBudgetInMB) { throw 'Requested VM memory exceeds the UI memory budget.' }
    # Global across worktrees and Windows sessions. Only admission/start is
    # serialized; the actual tests execute concurrently in independent guests.
    $mutex = [Threading.Mutex]::new($false, 'Global\agwinterm-hyperv-ui-admission-v1')
    $deadline = [DateTime]::UtcNow.AddSeconds($QueueTimeoutSeconds)
    $announced = $false
    try {
        do {
            $locked = $false
            try {
                try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
                if ($locked) {
                    $active = @(Get-VM | Where-Object {
                        $_.Id -ne $VM.Id -and $_.State -ne 'Off' -and
                        $_.Name -match '^agwinterm-ui-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                    })
                    $capacity = Get-HyperVUiCapacity -ActiveVMs $active -MemoryInMB $MemoryInMB -MaxParallel $MaxParallel -MemoryBudgetInMB $MemoryBudgetInMB -HostMemory (Get-HyperVUiHostMemory)
                    if ($capacity.Ready) { Start-VM -VM $VM | Out-Null; return }
                }
            } finally { if ($locked) { $mutex.ReleaseMutex() } }
            if (-not $announced) {
                Write-Host "Waiting for UI VM capacity (up to $MaxParallel VMs / $MemoryBudgetInMB MiB, with memory left for the host)."
                $announced = $true
            }
            if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for UI VM capacity after $QueueTimeoutSeconds seconds; no test VM was started." }
            Start-Sleep -Milliseconds 1000
        } while ($true)
    } finally { $mutex.Dispose() }
}

function Invoke-HyperVUiRun {
    param(
        [Parameter(Mandatory)][string]$BaseVhd,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [int]$TimeoutSeconds = 900,
        [int]$MemoryInMB = 4096,
        [int]$ProcessorCount = 2,
        [int]$CpuMaximum = 50,
        [int]$MaxParallel = 2,
        [int]$MemoryBudgetInMB = 8192,
        [int]$QueueTimeoutSeconds = 1800,
        [switch]$KeepVM
    )
    $BaseVhd = (Resolve-Path -LiteralPath $BaseVhd).Path
    $base = Get-VHD -Path $BaseVhd
    if ($base.VhdType -notin @('Dynamic','Fixed') -or $base.Attached) {
        throw 'BaseVhd must be a detached, standalone VHDX from a shut-down prepared generation-2 Windows VM.'
    }
    if ([IO.Path]::GetExtension($BaseVhd) -ne '.vhdx') { throw 'BaseVhd must be a VHDX file.' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $ownedRoot = Join-Path $OutputDirectory 'vm'
    if (Test-Path -LiteralPath $ownedRoot) { throw "VM directory already exists: $ownedRoot" }
    $null = New-Item -ItemType Directory -Path $ownedRoot
    $runId = [guid]::NewGuid().ToString()
    $name = 'agwinterm-ui-' + $runId
    $osDisk = Join-Path $ownedRoot 'windows.vhdx'
    $dataDisk = Join-Path $ownedRoot 'payload.vhdx'
    $mountPath = Join-Path $ownedRoot 'mount'
    $results = Join-Path $OutputDirectory 'results'
    $vm = $null
    $mounted = $false
    $dataReady = $false
    $timedOut = $false
    $exported = $false
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $timings = @{startedUtc=[DateTime]::UtcNow.ToString('o'); provisioningSeconds=$null; admissionAndStartSeconds=$null; guestLifecycleSeconds=$null}
    $admissionAt = $null
    $startedAt = $null
    try {
        $null = New-VHD -Path $osDisk -ParentPath $BaseVhd -Differencing
        $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes ($MemoryInMB * 1MB) -VHDPath $osDisk -Path $ownedRoot
        @{schema=1; runId=$runId; vmId=$vm.Id.ToString(); vmName=$name; baseVhd=$BaseVhd; output=$OutputDirectory;
            resources=@{memoryInMB=$MemoryInMB; processors=$ProcessorCount; cpuMaximum=$CpuMaximum; maxParallel=$MaxParallel; memoryBudgetInMB=$MemoryBudgetInMB}} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'hyperv-run.json') -Encoding utf8
        # No network, viewer, enhanced session, clipboard sharing or host folder mapping.
        Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter
        $policy = @{schema='agwinterm-ui-budget-v1'; maxParallel=$MaxParallel; memoryBudgetInMB=$MemoryBudgetInMB} | ConvertTo-Json -Compress
        Set-VM -VM $vm -StaticMemory -MemoryStartupBytes ($MemoryInMB * 1MB) -AutomaticStartAction Nothing -AutomaticStopAction TurnOff -CheckpointType Disabled -Notes $policy
        Set-VMProcessor -VM $vm -Count $ProcessorCount -Reserve 0 -Maximum $CpuMaximum -RelativeWeight 50
        Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
        Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
        Enable-VMTPM -VM $vm
        Set-VMVideo -VM $vm -HorizontalResolution 1920 -VerticalResolution 1080 -ResolutionType Single
        $inputSize = (Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'input') -File -Recurse | Measure-Object Length -Sum).Sum
        $null = New-VHD -Path $dataDisk -Dynamic -SizeBytes ([long][Math]::Ceiling(($inputSize + 4GB) / 1GB) * 1GB)
        Mount-HyperVUiDataDisk -Path $dataDisk -AccessPath $mountPath -Initialize
        $mounted = $true
        Copy-Item -LiteralPath (Join-Path $OutputDirectory 'input') -Destination $mountPath -Recurse
        Copy-Item -LiteralPath (Join-Path $OutputDirectory 'hyperv-run.json') -Destination (Join-Path $mountPath 'hyperv-run.json')
        $null = New-Item -ItemType Directory -Path (Join-Path $mountPath 'results')
        Dismount-HyperVUiDataDisk -Path $dataDisk -AccessPath $mountPath
        $mounted = $false
        $dataReady = $true
        Add-VMHardDiskDrive -VM $vm -Path $dataDisk -ControllerType SCSI
        Write-Host "Prepared isolated UI VM $name. Results: $results"
        $admissionAt = $timer.Elapsed.TotalSeconds
        $timings.provisioningSeconds = [Math]::Round($admissionAt,3)
        Start-HyperVUiWhenReady -VM $vm -MemoryInMB $MemoryInMB -MaxParallel $MaxParallel -MemoryBudgetInMB $MemoryBudgetInMB -QueueTimeoutSeconds $QueueTimeoutSeconds
        $startedAt = $timer.Elapsed.TotalSeconds
        $timings.admissionAndStartSeconds = [Math]::Round($startedAt - $admissionAt,3)
        Write-Host "Started UI VM $name ($MemoryInMB MiB, $ProcessorCount vCPUs capped at $CpuMaximum percent each)."
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ((Get-VM -Id $vm.Id).State -ne 'Off') {
            if ([DateTime]::UtcNow -ge $deadline) { $timedOut = $true; break }
            Start-Sleep -Milliseconds 500
        }
    } finally {
        $cleanupAt = $timer.Elapsed.TotalSeconds
        if ($null -ne $startedAt) { $timings.guestLifecycleSeconds = [Math]::Round($cleanupAt - $startedAt,3) }
        elseif ($null -ne $admissionAt) { $timings.admissionAndStartSeconds = [Math]::Round($cleanupAt - $admissionAt,3) }
        # Do not touch disks until this exact VM is stopped; failed cleanup leaves
        # the manifest and owned files available for deliberate recovery.
        try {
            if ($vm -and (Get-VM -Id $vm.Id).State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
            if ($vm -and (Get-VM -Id $vm.Id).State -ne 'Off') { throw "Cannot stop owned VM $name; disks retained at $ownedRoot" }
            if ($mounted) { Dismount-HyperVUiDataDisk -Path $dataDisk -AccessPath $mountPath; $mounted = $false }
            if ($dataReady) {
                Mount-HyperVUiDataDisk -Path $dataDisk -AccessPath $mountPath -ReadOnly
                $mounted = $true
                try {
                    $null = New-Item -ItemType Directory -Path $results -Force
                    if (-not ('HyperVUiTransfer' -as [type])) { Add-Type -Path "$PSScriptRoot/hyperv-ui-transfer.cs" }
                    # The stopped guest controls this filesystem. Never follow its
                    # junctions/symlinks into host files while exporting as admin.
                    [HyperVUiTransfer]::ValidateTree((Join-Path $mountPath 'results'))
                    foreach ($item in Get-ChildItem -LiteralPath (Join-Path $mountPath 'results') -Force) {
                        Copy-Item -LiteralPath $item.FullName -Destination $results -Recurse -Force
                    }
                    $exported = $true
                } finally { Dismount-HyperVUiDataDisk -Path $dataDisk -AccessPath $mountPath; $mounted = $false }
            }
            if ($KeepVM -or ($dataReady -and -not $exported)) {
                Write-Host "Retained stopped VM $name and disks: $ownedRoot"
            } else {
                if ($vm) { Remove-VM -VM $vm -Force }
                $resolved = [IO.Path]::GetFullPath($ownedRoot)
                $prefix = $OutputDirectory.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
                if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -ne 'vm') {
                    throw 'Refusing cleanup outside the owned run directory.'
                }
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        } catch {
            Write-Warning "Hyper-V cleanup/export failed; recover only the VM ID in hyperv-run.json. Files retained at $ownedRoot. $_"
            throw
        } finally {
            $timings.exportAndCleanupSeconds = [Math]::Round($timer.Elapsed.TotalSeconds - $cleanupAt,3)
            $timings.totalSeconds = [Math]::Round($timer.Elapsed.TotalSeconds,3)
            $timings.timedOut = $timedOut
            $timings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'host-timing.json') -Encoding utf8 -ErrorAction Continue
        }
    }
    if ($timedOut) { throw "Hyper-V UI run timed out after $TimeoutSeconds seconds. Partial evidence: $results" }
    Read-HyperVUiResult -Results $results -RunId $runId
}
