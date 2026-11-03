# Run HUD suites sequentially in one boot, with a fresh PowerShell process per
# suite. Different worktrees still get different VMs. Stop on the first failure.
param([Parameter(Mandatory)][string]$WorkDirectory, [string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
if (-not (& "$PSScriptRoot/hyperv-ui-context.ps1")) { throw 'HUD batching requires a verified Hyper-V guest.' }
$plan = Get-Content -LiteralPath (Join-Path $WorkDirectory 'run.json') -Raw | ConvertFrom-Json
if ($plan.test -ne 'Hud' -or -not $plan.hudSuites) { throw 'Expected a HUD batch plan.' }
$logRoot = if ($ResultsDirectory) { Join-Path $ResultsDirectory 'hud-batch' } else { Join-Path $WorkDirectory 'artifacts/hud-batch' }
$null = New-Item -ItemType Directory -Path $logRoot -Force
$outcomes = @()
$code = 2
try {
    foreach ($suite in $plan.hudSuites) {
        $index = $outcomes.Count + 1
        $stdout = Join-Path $logRoot "$index-stdout.log"
        $stderr = Join-Path $logRoot "$index-stderr.log"
        $timer = [Diagnostics.Stopwatch]::StartNew()
        Write-Host "HUD batch $index/$($plan.hudSuites.Count): $suite"
        # Pass data as arguments, never interpolate suite names into shell code.
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile','-File',"$PSScriptRoot/sandbox-ui-dispatch.ps1",'-WorkDirectory',$WorkDirectory,'-SuiteOverride',$suite)) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            # Copy streams concurrently: suites can produce enough output to fill
            # either pipe. Logs remain on disk even if the VM hits its deadline.
            $outFile = [IO.File]::Create($stdout)
            $errFile = [IO.File]::Create($stderr)
            try {
                $outCopy = $process.StandardOutput.BaseStream.CopyToAsync($outFile)
                $errCopy = $process.StandardError.BaseStream.CopyToAsync($errFile)
                $process.WaitForExit()
                $outCopy.GetAwaiter().GetResult()
                $errCopy.GetAwaiter().GetResult()
            } finally { $outFile.Dispose(); $errFile.Dispose() }
            $code = $process.ExitCode
        } finally { $process.Dispose() }
        $timer.Stop()
        $outcomes += @{suite=$suite; exitCode=$code; elapsedSeconds=[Math]::Round($timer.Elapsed.TotalSeconds,2)}
        Get-Content -LiteralPath $stdout | Write-Output
        Get-Content -LiteralPath $stderr | ForEach-Object { [Console]::Error.WriteLine($_) }
        if ($code -ne 0) { break }
    }
} finally {
    @{requested=@($plan.hudSuites); completed=@($outcomes); exitCode=$code} | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $logRoot 'summary.json') -Encoding utf8
}
exit $code
