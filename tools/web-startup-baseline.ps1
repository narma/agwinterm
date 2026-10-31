# Repeat before/after on the same desktop and power plan. Uses only a private app identity.
param(
    [string]$Exe = "$PSScriptRoot/../installer/stage/Agwinterm.Win32.exe",
    [string]$Installer = "$PSScriptRoot/../installer/Output/agwinterm-setup-0.20.14.exe",
    [string]$Portable = "$PSScriptRoot/../installer/Output/agwinterm-portable-0.20.14-win-x64.exe",
    [string]$Output = "$PSScriptRoot/../artifacts/webview2-before.json",
    [ValidateRange(20, 1000)][int]$Runs = 20
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$identity = 'agwinterm-web-baseline-' + [guid]::NewGuid().ToString('N')
$dataRoot = [Environment]::GetFolderPath('LocalApplicationData')
$data = [IO.Path]::GetFullPath((Join-Path $dataRoot $identity))
if ([IO.Path]::GetDirectoryName($data) -ne $dataRoot) { throw 'Invalid private data directory' }
New-Item -ItemType Directory -Path $data | Out-Null
@{ default = 'Baseline'; profiles = @(@{ name = 'Baseline'; command = 'cmd.exe'; args = @('/d','/q','/k','echo AGW_BASELINE_READY'); cwd = (Split-Path $Exe) }) } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $data 'profiles.json')
"session-host = in-process`nemulator-core = managed" | Set-Content (Join-Path $data 'agwinterm.conf')
Add-Type @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
public static class BaselinePipe {
    public static string Request(string name, string request) {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(100);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        writer.WriteLine(request);
        var reply = reader.ReadLineAsync();
        if (!reply.Wait(2000)) throw new TimeoutException("Control response timed out");
        return reply.Result;
    }
}
'@
$samples = @()
$process = $null
try {
    for ($run = 1; $run -le $Runs; $run++) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $Exe -ArgumentList @('--app-id',$identity,'--pipe',$identity,'--no-restore') -WindowStyle Hidden -PassThru
        $ready = $false
        do {
            try {
                $reply = [BaselinePipe]::Request($identity, '{"cmd":"session.text"}') | ConvertFrom-Json
                $ready = $reply.ok -and ($reply.result -match 'AGW_BASELINE_READY')
            } catch { if ($process.HasExited) { throw 'Baseline app exited before terminal readiness' } }
            if (-not $ready) { Start-Sleep -Milliseconds 10 }
        } while (-not $ready -and $watch.Elapsed.TotalSeconds -lt 20)
        if (-not $ready) { throw "Terminal readiness timed out: $($reply | ConvertTo-Json -Compress)" }
        $elapsed = $watch.Elapsed.TotalMilliseconds
        $all = @(Get-CimInstance Win32_Process)
        $ids = @([int]$process.Id)
        do {
            $children = @($all | Where-Object { $_.ParentProcessId -in $ids -and $_.ProcessId -notin $ids })
            $ids += @($children | ForEach-Object { [int]$_.ProcessId })
        } while ($children.Count)
        $browsers = @($all | Where-Object { $_.ProcessId -in $ids -and $_.Name -match 'msedge|chrome|webview' })
        $samples += [pscustomobject]@{ run=$run; terminalReadyMs=$elapsed; browserProcesses=$browsers.Count }
        [void][BaselinePipe]::Request($identity, '{"cmd":"window.close"}')
        if (-not $process.WaitForExit(10000)) { throw 'Private baseline instance did not exit' }
        $process = $null
        Write-Host "Run $run/$Runs : $([math]::Round($elapsed, 2)) ms"
    }
    $times = @($samples.terminalReadyMs | Sort-Object)
    $report = [ordered]@{
        timestampUtc=[DateTime]::UtcNow.ToString('o'); commit=(& git rev-parse HEAD)
        executable=$Exe; executableSha256=(Get-FileHash $Exe).Hash
        protocol='20+ sequential warm-cache starts; fixed cmd /d /q /k echo marker; in-process managed terminal; no restore; terminal.text polling at 10ms, connection timeout 100ms; timing includes process creation and control readback, not first paint'
        sdk=(& dotnet --version); os=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber)
        cpu=(Get-CimInstance Win32_Processor | Select-Object Name); gpu=(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
        powerPlan=(& powercfg /getactivescheme | Out-String).Trim()
        medianMs=($times[[int][math]::Floor(($Runs-1)/2)]+$times[[int][math]::Floor($Runs/2)])/2
        p95Ms=$times[[int][math]::Ceiling($Runs*0.95)-1]
        installerBytes=(Get-Item $Installer).Length; portableBytes=(Get-Item $Portable).Length
        applicationPayloadBytes=(Get-ChildItem (Split-Path $Exe) -Recurse -File | Measure-Object Length -Sum).Sum
        payloadNote='Published installer payload; excludes installer-created uninstaller and shared runtime installation.'
        samples=$samples
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output
    Write-Host "Report: $Output"
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    # Retain the private profile for inspection; never enumerate or kill other application instances.
    Write-Host "Private profile: $data"
}
