function Get-HyperVUiHostConfiguration {
    $path = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AgwintermUiHost/host.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($config.schema -ne 1) { throw 'Unsupported installed Hyper-V UI host configuration.' }
    return $config
}

function Invoke-HyperVUiWorker {
    param($Configuration, [string]$OutputDirectory, [int]$TimeoutSeconds, [int]$QueueTimeoutSeconds)
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $Configuration.userSid) { throw 'This Windows account is not authorized for the installed UI host.' }
    if (-not ('HyperVUiTransfer' -as [type])) { Add-Type -Path "$PSScriptRoot/hyperv-ui-transfer.cs" }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $requestArchive = Join-Path $OutputDirectory 'worker-input.zip'
    $responseArchive = Join-Path $OutputDirectory 'worker-results.zip'
    $pipe = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $scheduler = New-Object -ComObject Schedule.Service
        try { $scheduler.Connect() }
        catch { throw "Cannot access Windows Task Scheduler from this process. If an automation sandbox blocks host IPC, run the same command outside that sandbox as the same ordinary Windows account; do not use RunAs or bypass the UI worker. $_" }
        $folder = $scheduler.GetFolder('\AgwintermUi')
        [HyperVUiTransfer]::Pack((Join-Path $OutputDirectory 'input'),$requestArchive)
        $deadline = [DateTime]::UtcNow.AddSeconds($QueueTimeoutSeconds)
        $nextStart = [DateTime]::MinValue
        Write-Host 'Waiting for an available Hyper-V UI worker (no administrator prompt).'
        while (-not $pipe) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for the installed UI workers.' }
            if ([DateTime]::UtcNow -ge $nextStart) {
                foreach ($slot in 0,1) { $null = $folder.GetTask("Slot$slot").Run($null) }
                $nextStart = [DateTime]::UtcNow.AddSeconds(10)
            }
            foreach ($slot in 0,1) {
                $candidate = [IO.Pipes.NamedPipeClientStream]::new('.',"$($Configuration.pipePrefix)-$slot",'InOut',[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Identification)
                try {
                    $candidate.Connect(250)
                    # Prevent a different local account from impersonating the
                    # installed worker by creating a pipe with the same name.
                    $owner = [IO.Pipes.PipesAclExtensions]::GetAccessControl($candidate).GetOwner([Security.Principal.SecurityIdentifier]).Value
                    if ($owner -ne 'S-1-5-18') { throw 'UI worker pipe is not owned by SYSTEM.' }
                    $pipe = $candidate
                    break
                } catch [TimeoutException] { $candidate.Dispose() }
                catch { $candidate.Dispose(); throw }
            }
        }
        [HyperVUiTransfer]::SendText($pipe,(@{schema=1;action='Run';timeoutSeconds=$TimeoutSeconds} | ConvertTo-Json -Compress))
        $ready = [HyperVUiTransfer]::ReceiveText($pipe,30) | ConvertFrom-Json
        if (-not $ready.ready) { throw "UI worker refused the request: $($ready.error)" }
        [HyperVUiTransfer]::SendFile($pipe,$requestArchive)
        $completed = [HyperVUiTransfer]::ReceiveText($pipe,($TimeoutSeconds+2100)) | ConvertFrom-Json
        if (-not $completed.completed) { throw "UI worker failed: $($completed.error)" }
        [HyperVUiTransfer]::ReceiveFile($pipe,$responseArchive)
        [HyperVUiTransfer]::Extract($responseArchive,$OutputDirectory)
        $result = Get-Content (Join-Path $OutputDirectory 'worker-result.json') -Raw | ConvertFrom-Json
        if ($result.exitCode -notin @(0,1,2)) { throw 'Invalid worker result.' }
        if ($result.exitCode -in @(0,1)) {
            $manifest = Get-Content (Join-Path $OutputDirectory 'hyperv-run.json') -Raw | ConvertFrom-Json
            $null = Read-HyperVUiResult -Results (Join-Path $OutputDirectory 'results') -RunId $manifest.runId
        }
        return $result
    } finally {
        if ($pipe) { $pipe.Dispose() }
        foreach ($path in @($requestArchive,$responseArchive)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
        @{elapsedSeconds=[Math]::Round($timer.Elapsed.TotalSeconds,3);transport='local-system-owned-pipe'} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'worker-timing.json')
    }
}
