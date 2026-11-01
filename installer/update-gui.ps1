# Update the installed GUI without replacing the running Rust pty-host or ConPTY.
# Close the GUI normally when prompted; server-hosted sessions survive the restart.
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\agwinterm'),
    [ValidateRange(1, 3600)][int]$WaitSeconds = 120,
    [switch]$NoLaunch,
    [switch]$IncludeCli
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$exe = Join-Path $InstallDir 'Agwinterm.Win32.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Installed GUI not found: $exe. Run the full installer first, or specify -InstallDir." }
if (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'agwinterm-ptyhost.exe'))) {
    throw 'This update requires an existing installation with the Rust pty-host.'
}
$dotnet = if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') { 'C:\Program Files\dotnet\dotnet.exe' } else { 'dotnet' }
$iss = Get-Content (Join-Path $PSScriptRoot 'agwinterm.iss') -Raw
if ($iss -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw 'AppVersion not found' }
$version = $Matches[1]
# A fresh folder avoids stale publish files and keeps the previous backup available.
$run = Join-Path $root ('artifacts\gui-update\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $run 'publish'
$backup = Join-Path $run 'backup'
New-Item -ItemType Directory -Path $stage, $backup -Force | Out-Null
Write-Host "Building GUI $version..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'src\Agwinterm.Win32\Agwinterm.Win32.csproj') `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$version" -o $stage
if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed; installed files were not changed.' }
if ($IncludeCli) {
    & $dotnet publish (Join-Path $root 'src\Agwinterm.Ctl\Agwinterm.Ctl.csproj') `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$version" -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed; installed files were not changed.' }
}

# Publish currently includes ConPTY. Never replace it while a host uses it. Keep this
# exclusion explicit, including host/core/hooks/CLI in case publish gains more payloads.
$protected = @('conpty.dll', 'OpenConsole.exe', 'agwinterm_core.dll')
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
    $_.Name -notin $protected -and $_.Name -notlike 'agwinterm-ptyhost*' -and
    $_.Name -notlike 'agwinterm-hook*' -and ($IncludeCli -or $_.Name -notlike 'agwintermctl*')
})
$changes = @(foreach ($file in $files) {
    $relative = $file.FullName.Substring($stage.Length + 1)
    $target = Join-Path $InstallDir $relative
    $exists = Test-Path -LiteralPath $target
    if (-not $exists -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
        [pscustomobject]@{ Source = $file.FullName; Target = $target; Relative = $relative; Existed = $exists }
    }
})

Write-Host 'Close the Agwinterm GUI normally. The Rust pty-host can keep running.' -ForegroundColor Yellow
Write-Host "Waiting up to $WaitSeconds seconds; no process will be killed."
$deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
do {
    # A C# --pty-host uses the same exe: refuse to update it while it runs too.
    $running = @(Get-Process -Name 'Agwinterm.Win32' -ErrorAction SilentlyContinue | Where-Object {
        try { [string]::Equals($_.Path, $exe, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    if ($running.Count -eq 0) { break }
    if ([DateTime]::UtcNow -ge $deadline) { throw 'GUI (or C# pty-host) is still running. Installed files were not changed.' }
    Start-Sleep -Milliseconds 500
} while ($true)

# Check ALL changed existing files before modifying anything. A legacy C# host or
# another reader may still have managed/runtime DLLs locked. Never terminate it.
foreach ($change in $changes) {
    if ($change.Existed) {
        try {
            $stream = [IO.File]::Open($change.Target, 'Open', 'ReadWrite', 'None')
            $stream.Dispose()
        } catch { throw "Cannot update $($change.Target). It may still be in use. No files were changed. $($_.Exception.Message)" }
        $saved = Join-Path $backup $change.Relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $saved) -Force | Out-Null
        Copy-Item -LiteralPath $change.Target -Destination $saved
    }
}

$applied = [Collections.Generic.List[object]]::new()
try {
    foreach ($change in $changes) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $change.Target) -Force | Out-Null
        $applied.Add($change) # Include a partially failed copy in rollback.
        Copy-Item -LiteralPath $change.Source -Destination $change.Target -Force
        if ((Get-FileHash -LiteralPath $change.Source).Hash -ne (Get-FileHash -LiteralPath $change.Target).Hash) {
            throw "Verification failed: $($change.Target)"
        }
    }
} catch {
    $failure = $_
    $rollbackErrors = @()
    foreach ($change in $applied) {
        try {
            if ($change.Existed) { Copy-Item -LiteralPath (Join-Path $backup $change.Relative) -Destination $change.Target -Force }
            elseif (Test-Path -LiteralPath $change.Target) { Remove-Item -LiteralPath $change.Target -Force }
        } catch { $rollbackErrors += $_.Exception.Message }
    }
    if ($rollbackErrors.Count) { throw "Update failed: $failure. Rollback incomplete: $($rollbackErrors -join '; '). Backup: $backup" }
    throw "Update failed; previous files restored. $failure"
}
Write-Host "GUI $version updated: $($changes.Count) files. Rust host and ConPTY were not replaced." -ForegroundColor Green
Write-Host "Backup: $backup"
if (-not $NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $InstallDir }
