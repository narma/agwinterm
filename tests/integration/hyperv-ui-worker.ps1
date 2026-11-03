# Installed into an administrator-owned directory; never run a checkout as SYSTEM.
#Requires -Version 7.0
#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$Configuration, [ValidateRange(0,1)][int]$Slot)
$ErrorActionPreference = 'Stop'
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18') { throw 'Run the installed scheduled task, not this script directly.' }
$config = Get-Content -LiteralPath $Configuration -Raw | ConvertFrom-Json
if ($config.schema -ne 1) { throw 'Unsupported UI host configuration.' }
. "$PSScriptRoot/hyperv-ui-host.ps1"
Assert-HyperVUiHost
Add-Type -Path "$PSScriptRoot/hyperv-ui-transfer.cs"
$slotRoot = Join-Path (Split-Path $Configuration) "jobs/slot-$Slot"
$null = New-Item -ItemType Directory -Path $slotRoot -Force

function Remove-OwnedJob([string]$Directory) {
    $full = [IO.Path]::GetFullPath($Directory)
    if (-not $full.StartsWith($slotRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $full -Leaf) -notmatch '^[0-9a-f]{32}$') { throw 'Invalid worker cleanup path.' }
    # The host removes run/vm only after successful export and cleanup. Its
    # presence also covers an interrupted worker: never discard unexported data.
    $recovery = Join-Path $full 'recovery-required.json'
    if ((Test-Path -LiteralPath $recovery) -or (Test-Path -LiteralPath (Join-Path $full 'run/vm'))) {
        if (-not (Test-Path -LiteralPath $recovery)) {
            @{schema=1;reason='VM files retained or worker interrupted; administrator recovery required';utc=[DateTime]::UtcNow.ToString('o')} |
                ConvertTo-Json | Set-Content -LiteralPath $recovery
        }
        Write-Warning "Preserving UI job for administrator recovery: $full"
        return
    }
    $manifest = Join-Path $full 'run/hyperv-run.json'
    if (Test-Path $manifest) {
        $run = Get-Content $manifest -Raw | ConvertFrom-Json
        $vm = Get-VM -Id ([guid]$run.vmId) -ErrorAction SilentlyContinue
        if ($vm) {
            if ($run.schema -ne 1 -or $vm.Name -cne ('agwinterm-ui-'+([guid]$run.runId).ToString()) -or $run.output -ne (Join-Path $full 'run')) { throw 'Owned VM identity mismatch.' }
            if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
            Remove-VM -VM $vm -Force
        }
    }
    foreach ($disk in Get-ChildItem $full -Recurse -Filter '*.vhdx' -File) {
        if ((Get-VHD -Path $disk.FullName).Attached) { throw 'Retaining a job with an attached disk.' }
    }
    Remove-Item -LiteralPath $full -Recurse -Force
}
# Each slot is a separate IgnoreNew scheduled task. Recover only its own jobs
# after a host restart or task crash. Jobs with VM files or recovery markers
# remain untouched until an administrator recovers them.
foreach ($old in Get-ChildItem $slotRoot -Directory) { Remove-OwnedJob $old.FullName }

$security = [IO.Pipes.PipeSecurity]::new()
$security.SetAccessRuleProtection($true,$false)
$security.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
$security.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-2'),'FullControl','Deny'))
foreach ($sid in @('S-1-5-18','S-1-5-32-544')) { $security.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow')) }
$security.AddAccessRule([IO.Pipes.PipeAccessRule]::new([Security.Principal.SecurityIdentifier]::new($config.userSid),'ReadWrite','Allow'))
while ($true) {
    $pipe = [IO.Pipes.NamedPipeServerStreamAcl]::Create("$($config.pipePrefix)-$Slot",'InOut',1,'Byte',([IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::FirstPipeInstance),65536,65536,$security)
    $job = $null
    try {
        if (-not $pipe.WaitForConnectionAsync().Wait(60000)) { break }
        $request = [HyperVUiTransfer]::ReceiveText($pipe,30) | ConvertFrom-Json
        if ($request.schema -ne 1 -or $request.action -ne 'Run' -or $request.timeoutSeconds -isnot [long] -and $request.timeoutSeconds -isnot [int] -or $request.timeoutSeconds -lt 30 -or $request.timeoutSeconds -gt 1800) { throw 'Invalid UI worker request.' }
        $job = Join-Path $slotRoot ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $job
        $archive = Join-Path $job 'input.zip'
        [HyperVUiTransfer]::SendText($pipe,'{"ready":true}')
        [HyperVUiTransfer]::ReceiveFile($pipe,$archive)
        $runRoot = Join-Path $job 'run'
        [HyperVUiTransfer]::Extract($archive,(Join-Path $runRoot 'input'))
        Remove-Item -LiteralPath $archive
        # Only protected code executes on the host. Every uploaded byte is data
        # copied to an isolated guest with no network or host folder mappings.
        $failure = $null
        try {
            $result = Invoke-HyperVUiRun -BaseVhd $config.baseVhd -OutputDirectory $runRoot -TimeoutSeconds $request.timeoutSeconds -MemoryInMB 4096 -ProcessorCount 2 -CpuMaximum $config.cpuMaximum -MaxParallel 2 -MemoryBudgetInMB 8192
        } catch { $failure=$_.ToString(); $result=@{exitCode=2;error=$failure} }
        $export = Join-Path $job 'export'
        $null = New-Item -ItemType Directory -Path $export
        foreach ($name in @('results','host-timing.json','hyperv-run.json')) {
            $source = Join-Path $runRoot $name
            if (Test-Path $source) { Copy-Item -LiteralPath $source -Destination $export -Recurse }
        }
        $result | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $export 'worker-result.json')
        $response = Join-Path $job 'results.zip'
        [HyperVUiTransfer]::Pack($export,$response)
        [HyperVUiTransfer]::SendText($pipe,'{"completed":true}')
        [HyperVUiTransfer]::SendFile($pipe,$response)
    } catch {
        $_ | Out-String | Add-Content (Join-Path (Split-Path $Configuration) "worker-$Slot.log")
        try { [HyperVUiTransfer]::SendText($pipe,(@{error=$_.ToString()} | ConvertTo-Json -Compress)) } catch {}
    } finally {
        $pipe.Dispose()
        if ($job) { Remove-OwnedJob $job }
    }
}
