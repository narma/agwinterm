# Run with an immutable candidate package containing baseline/ and echo-fixture/.
# Every guest executes serially at the installed worker's default resource limits.
param([Parameter(Mandatory)][string]$Exe,[Parameter(Mandatory)][string]$Directory,
    [ValidateSet('latency','bulk','soak','trace')][string[]]$Phases=@('latency','bulk','soak','trace'),
    [ValidateSet('server','in-process')][string[]]$Modes=@('server','in-process'),
    [ValidateSet('idle','fixed-output','ascii','unicode','history','tui')][string[]]$Workloads=@('idle','fixed-output'),
    [ValidateSet('single-key','marker-burst')][string]$InputPattern='single-key',
    [switch]$DiscardStaging)
$ErrorActionPreference='Stop'
$root=Join-Path $PSScriptRoot '..'
$worker=Join-Path $root 'tests/integration/hyperv-ui.ps1'
$Exe=(Resolve-Path -LiteralPath $Exe).Path
$Directory=[IO.Path]::GetFullPath($Directory)
$null=New-Item -Type Directory $Directory -Force
$package=Split-Path $Exe
$manifest=foreach($variant in '.', 'baseline'){
    foreach($name in 'Agwinterm.Win32.dll','Agwinterm.Pty.dll','Agwinterm.Core.dll','ghostty-vt.dll','agwinterm_ghostty.dll',
        'echo-fixture/Agwinterm.EchoFixture.dll','echo-fixture/Agwinterm.Core.dll','echo-fixture/Agwinterm.Pty.dll'){
        $hash=Get-FileHash -LiteralPath (Join-Path $package "$variant/$name")
        [ordered]@{variant=$variant;file=$name;sha256=$hash.Hash}
    }
}
$pin=Join-Path $Directory 'packages.json'
$json=ConvertTo-Json -InputObject $manifest
if(Test-Path $pin){
    if((Get-Content -LiteralPath $pin -Raw).Trim() -ne $json.Trim()){throw 'Measurement directory belongs to another build.'}
}else{$json|Set-Content -LiteralPath $pin}
function Run([string]$test,[hashtable]$settings,[string]$path){
    # Let a running guest finish and preserve its raw samples when the build
    # is superseded. Remove STOP to resume the same pinned study.
    if(Test-Path -LiteralPath (Join-Path $Directory 'STOP')){throw "Study stopped before $path"}
    $result=Join-Path $path 'results/result.json'
    if(Test-Path -LiteralPath $result){
        if((Get-Content -LiteralPath $result -Raw|ConvertFrom-Json).exitCode -eq 0){return}
        throw "Previously failed measurement at $path. Keep its evidence and retry in a fresh directory."
    }
    & $worker -Test $test -Exe $Exe -TestParameters $settings -TimeoutSeconds 1800 -OutputDirectory $path | Out-Host
    if($LASTEXITCODE){throw "$test failed in $path"}
    if($DiscardStaging){
        # The guest input is a copy of the pinned packages and runtimes.
        # Keep the run plan, guest results and raw traces.
        $run=(Resolve-Path -LiteralPath $path).Path
        $stage=(Resolve-Path -LiteralPath (Join-Path $run 'input')).Path
        if(-not $run.StartsWith($Directory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
            [IO.Directory]::GetParent($stage).FullName -ne $run -or [IO.Path]::GetFileName($stage) -ne 'input' -or
            (Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint){
            throw "Unexpected staging path $stage"
        }
        Copy-Item -LiteralPath (Join-Path $stage 'run.json') -Destination (Join-Path $run 'guest-plan.json')
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
foreach($phase in $Phases){
    if($phase -eq 'soak'){
        $files=foreach($sessions in 1,8,32){
            $path=Join-Path $Directory "soak/$sessions"
            Run 'HostOwnerSoak' @{Sessions=$sessions;Minutes=20} $path
            Join-Path $path "results/evidence/owner-soak/samples-$sessions.csv"
        }
        & "$PSScriptRoot/host-owner-soak-report.ps1" -Samples $files -Output (Join-Path $Directory 'soak/report.json')
        continue
    }
    foreach($mode in $Modes){
        if($phase -eq 'latency'){
            foreach($workload in $Workloads){
                $path=Join-Path $Directory "$phase/$mode-$workload-$InputPattern"
                # Avoid the worker's 30 minute ceiling for legacy flood runs.
                # Both variants in each pair always run in the same VM.
                $offset=0
                foreach($pairs in 3,2){
                    Run 'SessionPerformance' @{Pairs=$pairs;PairOffset=$offset;LatencySamples=2050;LatencyHost=$mode;
                        LatencyWorkload=$workload;LatencyInputPattern=$InputPattern} (Join-Path $path "pairs-$offset")
                    $offset+=$pairs
                }
                & "$PSScriptRoot/session-performance-report.ps1" -BaselineDirectory $path -MixedPairs -BaselineVariant legacy -CandidateVariant single-owner -Output (Join-Path $path 'report.json')
            }
        }else{
            $comparison=if($phase -eq 'bulk'){'ownership'}else{'trace'}
            $path=Join-Path $Directory "$phase/$mode"
            Run 'SessionBulk' @{Pairs=5;MiB=128;LatencyHost=$mode;LatencyPresentation='single-owner';Comparison=$comparison} $path
            & "$PSScriptRoot/session-bulk-report.ps1" -Directory $path -Comparison $comparison -Output (Join-Path $path 'report.json')
        }
    }
}
