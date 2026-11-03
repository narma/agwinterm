#Requires -Version 7.0
# Host-free lifecycle fixtures. No Hyper-V module, disk mount or UI is invoked.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/hyperv-ui-host.ps1"
$unmountImplementation = (Get-Item Function:\Dismount-HyperVUiDataDisk).ScriptBlock
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('agwinterm-hyperv-fixture-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
$script:checks = 0
function Check([string]$Name, [bool]$Ok) {
    if (-not $Ok) { throw "FAIL $Name" }
    $script:checks++
    Write-Host "PASS $Name"
}
function Refuses([string]$Name, [scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Check $Name ($message -like $Pattern)
}
function Get-VHD { param($Path) [pscustomobject]@{VhdType=$script:baseType; Attached=$false; DiskNumber=42} }
function New-VHD {
    param($Path,$ParentPath,[switch]$Differencing,[switch]$Dynamic,$SizeBytes)
    Set-Content -LiteralPath $Path -Value 'fixture'
}
function New-VM {
    param($Name,$Generation,$MemoryStartupBytes,$VHDPath,$Path)
    $script:owned = [pscustomobject]@{Id=[guid]::NewGuid(); Name=$Name; State='Off'; Path=$Path; MemoryStartup=$MemoryStartupBytes; MemoryAssigned=0; Notes=''}
    $script:created += $script:owned.Id
    $script:vms[$script:owned.Id] = $script:owned
    return $script:owned
}
function Get-VMNetworkAdapter { param($VM) $VM }
function Remove-VMNetworkAdapter { param([Parameter(ValueFromPipeline)]$VM) process { $script:networkRemoved = $true } }
function Set-VM { param($VM,[switch]$StaticMemory,$MemoryStartupBytes,$AutomaticStartAction,$AutomaticStopAction,$CheckpointType,$Notes) $VM.Notes=$Notes }
function Set-VMProcessor { param($VM,$Count,$Reserve,$Maximum,$RelativeWeight) $script:cpuPolicy=@{Count=$Count; Reserve=$Reserve; Maximum=$Maximum; Weight=$RelativeWeight} }
function Set-VMFirmware { param($VM,$EnableSecureBoot,$SecureBootTemplate) }
function Set-VMKeyProtector { param($VM,[switch]$NewLocalKeyProtector) }
function Enable-VMTPM { param($VM) }
function Set-VMVideo { param($VM,$HorizontalResolution,$VerticalResolution,$ResolutionType) }
function Add-VMHardDiskDrive { param($VM,$Path,$ControllerType) }
function Get-VM { param($Id) if ($Id) { return $script:vms[$Id] }; return $script:vms.Values }
function Get-HyperVUiHostMemory { [pscustomobject]@{FreeMB=50000; TotalMB=64000} }
function Start-VM {
    param($VM)
    if ($script:mode -eq 'startFailure') { throw 'simulated start failure' }
    $VM.State = 'Running'
    $mount = Join-Path $VM.Path 'mount'
    $run = Get-Content (Join-Path $mount 'hyperv-run.json') -Raw | ConvertFrom-Json
    'partial log' | Set-Content (Join-Path $mount 'results/bootstrap.log')
    if ($script:mode -in @('timeout','stopFailure')) { return }
    if ($script:mode -ne 'missing') {
        $code = switch ($script:mode) { 'failure' {1} 'setup' {2} 'invalid' {'0'} default {0} }
        $id = if ($script:mode -eq 'stale') { [guid]::NewGuid().ToString() } else { $run.runId }
        @{runId=$id; exitCode=$code; error=$null} | ConvertTo-Json | Set-Content (Join-Path $mount 'results/result.json')
    }
    $VM.State = 'Off'
}
function Stop-VM {
    param($VM,[switch]$TurnOff,[switch]$Force)
    $script:stopped += $VM.Id
    if ($script:mode -eq 'stopFailure') { throw 'simulated stop failure' }
    $VM.State = 'Off'
}
function Remove-VM {
    param($VM,[switch]$Force)
    $script:removed += $VM.Id
    $script:vms.Remove($VM.Id)
}
function Mount-HyperVUiDataDisk {
    param($Path,$AccessPath,[switch]$Initialize,[switch]$ReadOnly)
    if ($ReadOnly -and $script:mode -eq 'exportFailure') { throw 'simulated export failure' }
    if ($ReadOnly) { Check 'export mounts disk only after its VM is off' ($script:owned.State -eq 'Off') }
    $null = New-Item -ItemType Directory -Path $AccessPath -Force
    $script:mounted = $true
}
function Dismount-VHD { param($Path) $script:mounted = $false }
function Dismount-HyperVUiDataDisk { param($Path,$AccessPath) Dismount-VHD -Path $Path }
function New-Case([string]$Mode) {
    $script:mode = $Mode
    $path = Join-Path $temporary ([guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path (Join-Path $path 'input'), (Join-Path $path 'results')
    'payload' | Set-Content (Join-Path $path 'input/data.txt')
    return $path
}
try {
    $script:baseType = 'Dynamic'
    $script:created = @(); $script:stopped = @(); $script:removed = @(); $script:vms = @{}
    $foreign = [guid]::NewGuid()
    $script:vms[$foreign] = [pscustomobject]@{Id=$foreign; Name='unrelated-user-vm'; State='Running'}
    $base = Join-Path $temporary 'base.vhdx'
    'immutable base' | Set-Content $base
    $baseHash = (Get-FileHash $base).Hash
    foreach ($case in @(@{mode='success'; code=0},@{mode='failure'; code=1},@{mode='setup'; code=2})) {
        $path = New-Case $case.mode
        $result = Invoke-HyperVUiRun -BaseVhd $base -OutputDirectory $path -TimeoutSeconds 0
        Check "$($case.mode) preserves exit code" ($result.exitCode -eq $case.code)
        $metrics = Get-Content "$path/host-timing.json" -Raw | ConvertFrom-Json
        Check 'host timings distinguish VM work from queue/start and cleanup' ($metrics.provisioningSeconds -ge 0 -and $null -ne $metrics.guestLifecycleSeconds -and $metrics.totalSeconds -ge $metrics.exportAndCleanupSeconds)
        Check 'test VM CPU has a cap and reduced scheduling weight' ($script:cpuPolicy.Count -eq 2 -and $script:cpuPolicy.Maximum -eq 50 -and $script:cpuPolicy.Weight -eq 50 -and $script:cpuPolicy.Reserve -eq 0)
        Check "$($case.mode) exports evidence and removes owned VM files" ((Test-Path "$path/results/bootstrap.log") -and -not (Test-Path "$path/vm") -and $script:networkRemoved)
    }
    foreach ($case in @(
        @{mode='stale'; message='*stale*'}, @{mode='invalid'; message='*Invalid*'}, @{mode='missing'; message='*without a completed*'},
        @{mode='timeout'; message='*timed out*'}, @{mode='startFailure'; message='*start failure*'}
    )) {
        $path = New-Case $case.mode
        Refuses "$($case.mode) cannot count as success" { Invoke-HyperVUiRun -BaseVhd $base -OutputDirectory $path -TimeoutSeconds 0 } $case.message
        Check "$($case.mode) cleans the owned VM and disks" (-not (Test-Path "$path/vm") -and -not $script:mounted)
        if ($case.mode -eq 'timeout') { Check 'timeout preserves partial guest evidence' (Test-Path "$path/results/bootstrap.log") }
    }
    foreach ($case in @('exportFailure','stopFailure')) {
        $path = New-Case $case
        Refuses "$case is reported" { Invoke-HyperVUiRun -BaseVhd $base -OutputDirectory $path -TimeoutSeconds 0 } '*failure*'
        Check "$case retains owned disks for recovery" ((Test-Path "$path/vm/payload.vhdx") -and $script:vms.ContainsKey($script:owned.Id))
    }
    $path = New-Case 'success'
    $null = Invoke-HyperVUiRun -BaseVhd $base -OutputDirectory $path -KeepVM
    Check 'KeepVM retains a stopped VM with exported results' ($script:owned.State -eq 'Off' -and (Test-Path "$path/vm/windows.vhdx") -and (Test-Path "$path/results/result.json"))
    Refuses 'an existing owned directory is never reused' { Invoke-HyperVUiRun -BaseVhd $base -OutputDirectory $path } '*already exists*'
    $script:baseType = 'Differencing'
    Refuses 'a checkpoint chain is not accepted as a base image' { Invoke-HyperVUiRun -BaseVhd $base -OutputDirectory (New-Case 'base') } '*standalone*'
    Check 'all runs use distinct VM identities' (@($script:created | Select-Object -Unique).Count -eq $script:created.Count)
    Check 'foreign VM is never stopped or removed' ($script:vms[$foreign].State -eq 'Running' -and $foreign -notin $script:stopped -and $foreign -notin $script:removed)
    Check 'base disk contents remain unchanged' ((Get-FileHash $base).Hash -eq $baseHash)
    function Get-Partition {
        param($DiskNumber)
        [pscustomobject]@{PartitionNumber=1; AccessPaths=@('C:\owned fixture\')}
        [pscustomobject]@{PartitionNumber=2; AccessPaths=@('C:\unrelated\')}
    }
    function Remove-PartitionAccessPath {
        param($DiskNumber,$PartitionNumber,$AccessPath)
        $script:removedMounts += $AccessPath
        if ($script:failUnmount) { throw 'simulated mount-point removal failure' }
    }
    $script:removedMounts = @(); $script:failUnmount = $false; $script:mounted = $true
    & $unmountImplementation -Path $base -AccessPath 'C:\owned fixture'
    Check 'disk cleanup removes only its own mount point before detaching' ($script:removedMounts.Count -eq 1 -and $script:removedMounts[0] -eq 'C:\owned fixture\' -and -not $script:mounted)
    $script:failUnmount = $true; $script:mounted = $true
    Refuses 'mount-point cleanup errors remain failures' { & $unmountImplementation -Path $base -AccessPath 'C:\owned fixture' } '*mount-point*'
    Check 'disk is detached even if mount-point cleanup fails' (-not $script:mounted)
    $previousMarker = $env:AGWINTERM_HYPERV_UI
    $previousId = $env:AGWINTERM_HYPERV_RUN_ID
    try {
        $env:AGWINTERM_HYPERV_UI = '1'
        $env:AGWINTERM_HYPERV_RUN_ID = [guid]::NewGuid().ToString()
        Refuses 'environment markers alone never authorize host UI work' { & "$PSScriptRoot/hyperv-ui-context.ps1" } '*does not match*'
    } finally { $env:AGWINTERM_HYPERV_UI = $previousMarker; $env:AGWINTERM_HYPERV_RUN_ID = $previousId }
    # Actual payload staging needs no elevation, Hyper-V feature or Sandbox client.
    $fixtures = Join-Path $temporary 'binaries'
    $null = New-Item -ItemType Directory -Path $fixtures
    foreach ($file in @('Agwinterm.Win32.exe','agwintermctl.exe','pwsh.exe')) { 'fixture' | Set-Content (Join-Path $fixtures $file) }
    $path = Join-Path $temporary 'staging'
    & "$PSScriptRoot/hyperv-ui.ps1" -PrepareOnly -Test Win32Control -Exe "$fixtures/Agwinterm.Win32.exe" -CtlExe "$fixtures/agwintermctl.exe" -PowerShellHome $fixtures -OutputDirectory $path
    $plan = Get-Content "$path/input/run.json" -Raw | ConvertFrom-Json
    Check 'Hyper-V stages the shared test registry and guest guard' ($plan.test -eq 'Win32Control' -and (Test-Path "$path/input/tests/integration/hyperv-ui-identity.cs"))
    $stageMetrics = Get-Content "$path/staging-timing.json" -Raw | ConvertFrom-Json
    Check 'staging records payload size and elapsed time for optimization' ($stageMetrics.payloadBytes -gt 0 -and $stageMetrics.payloadFiles -gt 0 -and $stageMetrics.elapsedSeconds -ge 0)
    Refuses 'Hyper-V refuses WSL without touching a VM' { & "$PSScriptRoot/hyperv-ui.ps1" -Test WslControl -PrepareOnly } '*WSL tests*'
    $batchPath = Join-Path $temporary 'batch-staging'
    & "$PSScriptRoot/hyperv-ui.ps1" -PrepareOnly -Suite Quick,Navigation -Exe "$fixtures/Agwinterm.Win32.exe" -CtlExe "$fixtures/agwintermctl.exe" -PowerShellHome $fixtures -OutputDirectory $batchPath
    $batchPlan = Get-Content "$batchPath/input/run.json" -Raw | ConvertFrom-Json
    Check 'multiple HUD suites share one staged payload in request order' ($batchPlan.hudSuites.Count -eq 2 -and $batchPlan.hudSuites[0] -eq 'Quick' -and $batchPlan.hudSuites[1] -eq 'Navigation')
    Check 'Hyper-V uses its private payload disk without a second runtime copy' $batchPlan.hypervDirectPayload
    $runtimeFixture = Join-Path $temporary 'dotnet'
    foreach ($directory in @('host/fxr/10.0.1','shared/Microsoft.NETCore.App/10.0.1','shared/Microsoft.WindowsDesktop.App/10.0.1','shared/Microsoft.NETCore.App/9.0.1')) {
        $null = New-Item -ItemType Directory -Path (Join-Path $runtimeFixture $directory) -Force
        'runtime fixture' | Set-Content (Join-Path $runtimeFixture "$directory/library.dll")
    }
    'dotnet fixture' | Set-Content "$runtimeFixture/dotnet.exe"
    '{"runtimeOptions":{"framework":{"name":"Microsoft.WindowsDesktop.App","version":"10.0.0"}}}' | Set-Content "$fixtures/Agwinterm.Win32.runtimeconfig.json"
    $trimmedPath = Join-Path $temporary 'trimmed-staging'
    & "$PSScriptRoot/hyperv-ui.ps1" -PrepareOnly -Suite Quick -Exe "$fixtures/Agwinterm.Win32.exe" -CtlExe "$fixtures/agwintermctl.exe" -PowerShellHome $fixtures -DotnetHome $runtimeFixture -OutputDirectory $trimmedPath
    Check 'runtime trimming retains desktop and underlying core runtime patches' ((Test-Path "$trimmedPath/input/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.1/library.dll") -and (Test-Path "$trimmedPath/input/dotnet/shared/Microsoft.NETCore.App/10.0.1/library.dll"))
    Check 'runtime trimming excludes unrelated installed major versions' (-not (Test-Path "$trimmedPath/input/dotnet/shared/Microsoft.NETCore.App/9.0.1"))
    Refuses 'batching cannot silently discard a standalone test' { & "$PSScriptRoot/hyperv-ui.ps1" -PrepareOnly -Test Win32Control -Suite Quick,Navigation } '*Multiple suites*'
    Refuses 'oversized memory requests fail before staging' { & "$PSScriptRoot/hyperv-ui.ps1" -PrepareOnly -MemoryInMB 16384 } '*MemoryBudget*'
    $memory = [pscustomobject]@{FreeMB=50000; TotalMB=64000}
    $activeVm = [pscustomobject]@{MemoryStartup=4GB; MemoryAssigned=4GB; Notes=''}
    $capacity = Get-HyperVUiCapacity -ActiveVMs @($activeVm) -MemoryInMB 4096 -MaxParallel 2 -MemoryBudgetInMB 8192 -HostMemory $memory
    Check 'two standard VMs fit the shared budget' $capacity.Ready
    $capacity = Get-HyperVUiCapacity -ActiveVMs @($activeVm,$activeVm) -MemoryInMB 4096 -MaxParallel 2 -MemoryBudgetInMB 8192 -HostMemory $memory
    Check 'third concurrent VM waits instead of overcommitting' (-not $capacity.Ready)
    $capacity = Get-HyperVUiCapacity -ActiveVMs @($activeVm) -MemoryInMB 8192 -MaxParallel 4 -MemoryBudgetInMB 8192 -HostMemory $memory
    Check 'memory budget applies independently of slot count' (-not $capacity.Ready)
    $capacity = Get-HyperVUiCapacity -ActiveVMs @() -MemoryInMB 4096 -MaxParallel 2 -MemoryBudgetInMB 8192 -HostMemory ([pscustomobject]@{FreeMB=10000;TotalMB=64000})
    Check 'low free host memory defers even the first VM' (-not $capacity.Ready -and $capacity.ReserveMB -eq 8192)
    $activeVm.Notes = '{"schema":"agwinterm-ui-budget-v1","maxParallel":1,"memoryBudgetInMB":4096}'
    $capacity = Get-HyperVUiCapacity -ActiveVMs @($activeVm) -MemoryInMB 4096 -MaxParallel 4 -MemoryBudgetInMB 32768 -HostMemory $memory
    Check 'another worktree cannot loosen an active run budget' (-not $capacity.Ready -and $capacity.Limit -eq 1 -and $capacity.BudgetMB -eq 4096)
    function Test-AdmissionQueue {
        function Start-VM { param($VM) $script:admitted = $true }
        $savedVMs = $script:vms
        try {
            $script:vms = @{}
            $own = [pscustomobject]@{Id=[guid]::NewGuid();Name='own'}
            foreach ($index in 1..2) {
                $id = [guid]::NewGuid()
                $script:vms[$id] = [pscustomobject]@{Id=$id;Name="agwinterm-ui-$id";State='Running';MemoryStartup=4GB;MemoryAssigned=4GB;Notes=''}
            }
            $script:admitted = $false
            Refuses 'full queue has its own deadline and never starts a VM' { Start-HyperVUiWhenReady -VM $own -MemoryInMB 4096 -QueueTimeoutSeconds 0 } '*waiting for UI VM capacity*'
            Check 'queue timeout leaves other VMs running' (-not $script:admitted -and @($script:vms.Values | Where-Object State -eq Running).Count -eq 2)
            $script:vms.Remove($id)
            Start-HyperVUiWhenReady -VM $own -MemoryInMB 4096 -QueueTimeoutSeconds 0
            Check 'freeing one slot admits the waiting caller' $script:admitted
        } finally { $script:vms = $savedVMs }
    }
    Test-AdmissionQueue
    # Run the real batch coordinator against script-only probes, never UI.
    $batchFixture = Join-Path $temporary 'batch-worker'
    $null = New-Item -ItemType Directory -Path $batchFixture
    Copy-Item -LiteralPath "$PSScriptRoot/hyperv-ui-batch.ps1" -Destination $batchFixture
    'return $true' | Set-Content (Join-Path $batchFixture 'hyperv-ui-context.ps1')
    @'
param($WorkDirectory,$SuiteOverride)
[Console]::Out.WriteLine(('a' * 100000))
[Console]::Error.WriteLine(('b' * 100000))
if ($SuiteOverride -eq 'Failure') { exit 1 }
exit 0
'@ | Set-Content (Join-Path $batchFixture 'sandbox-ui-dispatch.ps1')
    foreach ($suites in @(@('Quick','Navigation'),@('Quick','Failure','Navigation'))) {
        @{test='Hud';hudSuites=$suites} | ConvertTo-Json | Set-Content (Join-Path $batchFixture 'run.json')
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $batchFixture 'hyperv-ui-batch.ps1') -WorkDirectory $batchFixture *> (Join-Path $temporary 'batch-probe.log')
        $probeExit = $LASTEXITCODE
        $summary = Get-Content (Join-Path $batchFixture 'artifacts/hud-batch/summary.json') -Raw | ConvertFrom-Json
        if ($suites -contains 'Failure') {
            Check 'batch fails fast and preserves the failing suite exit code' ($probeExit -eq 1 -and $summary.completed.Count -eq 2 -and $summary.completed[1].suite -eq 'Failure')
        } else {
            Check 'batch runs ordered suites and drains both output streams' ($probeExit -eq 0 -and $summary.completed.Count -eq 2 -and $summary.completed[1].suite -eq 'Navigation')
        }
    }
    Write-Host "$script:checks Hyper-V checks passed"
} finally {
    $full = [IO.Path]::GetFullPath($temporary)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture cleanup path' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
# GitHub's pwsh wrapper propagates LASTEXITCODE. The final child probe fails
# deliberately; only reaching here means every assertion and cleanup succeeded.
exit 0
