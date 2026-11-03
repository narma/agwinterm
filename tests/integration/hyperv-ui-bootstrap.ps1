# Installed in the template; runs in Windows PowerShell 5.1 at interactive logon.
# Protocol version 1. No network, PowerShell Direct credentials or host shares.
$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\AgwintermUi'
$key = 'HKLM:\SOFTWARE\Agwinterm\UiTests'
$data = $null
$run = $null
$transcribing = $false
try {
    $machine = Get-CimInstance Win32_ComputerSystem
    if ($machine.Manufacturer -ne 'Microsoft Corporation' -or $machine.Model -ne 'Virtual Machine') { throw 'Bootstrap requires a Hyper-V guest.' }
    $image = Get-ItemProperty -LiteralPath $key
    if ($image.Schema -ne 1 -or $image.UserSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Not the prepared image test account.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $volumes = @(Get-Volume | Where-Object { $_.FileSystemLabel -eq 'AGWINTERM_UI' -and $_.DriveLetter })
        if ($volumes.Count -gt 1) { throw 'Ambiguous UI payload disks.' }
        if ($volumes.Count -eq 1) { $data = "$($volumes[0].DriveLetter):\"; break }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    # Opening the template itself has no payload and must not shut it down.
    if (-not $data) { return }
    $results = Join-Path $data 'results'
    $null = New-Item -ItemType Directory -Path $results -Force
    Start-Transcript -LiteralPath (Join-Path $results 'bootstrap.log') -Force | Out-Null
    $transcribing = $true
    $run = Get-Content -LiteralPath (Join-Path $data 'hyperv-run.json') -Raw | ConvertFrom-Json
    $id = [guid]::Parse($run.runId)
    if ($run.schema -ne 1 -or $id -eq [guid]::Empty -or $run.vmName -cne ('agwinterm-ui-' + $id.ToString())) { throw 'Invalid UI run manifest.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $vmName = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters' -ErrorAction SilentlyContinue).VirtualMachineName
        if ($vmName -ceq $run.vmName) { break }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($vmName -cne $run.vmName) { throw 'Payload belongs to a different VM, or Hyper-V Data Exchange is not running.' }
    $sessionId = (Get-Process -Id $PID).SessionId
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $shell = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sessionId })
        if ($sessionId -gt 0 -and $shell.Count) { break }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($sessionId -eq 0 -or -not $shell.Count) { throw 'Interactive test account shell did not become ready.' }
    Set-ItemProperty -LiteralPath $key -Name ActiveRunId -Value $id.ToString()
    Set-ItemProperty -LiteralPath $key -Name ActiveVmName -Value $run.vmName
    $env:AGWINTERM_HYPERV_UI = '1'
    $env:AGWINTERM_HYPERV_RUN_ID = $id.ToString()
    $env:AGWINTERM_SANDBOX_UI = ''
    $env:GITHUB_ACTIONS = ''
    $inputPath = Join-Path $data 'input'
    & (Join-Path $inputPath 'powershell/pwsh.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $inputPath 'tests/integration/sandbox-ui-guest.ps1') -InputDirectory $inputPath -ResultsDirectory $results
    if ($LASTEXITCODE -ne 0) { throw "Guest runner process failed: $LASTEXITCODE" }
    $resultPath = Join-Path $results 'result.json'
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.exitCode -notin @(0,1,2)) { throw 'Guest did not produce a valid result.' }
    $result | Add-Member -NotePropertyName runId -NotePropertyValue $id.ToString() -Force
    $result | ConvertTo-Json | Set-Content -LiteralPath ($resultPath + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($resultPath + '.tmp') -Destination $resultPath -Force
} catch {
    if ($data) {
        $null = New-Item -ItemType Directory -Path (Join-Path $data 'results') -Force
        @{runId=$run.runId; exitCode=2; error=$_.ToString()} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $data 'results/result.json') -Encoding UTF8
    } else {
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $root 'bootstrap-error.log')
    }
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($data) {
        # Flush the payload filesystem by shutting down normally. The host waits
        # for Off before mounting it; on a hang it powers off only this run's VM.
        & "$env:SystemRoot\System32\shutdown.exe" /s /t 0 /f
    }
}
