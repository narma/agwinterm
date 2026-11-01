# Runs in a fresh guest PowerShell process; configuration carries data, never code.
param([Parameter(Mandatory)][string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
try {
    if ($env:AGWINTERM_SANDBOX_UI -ne '1' -or $env:USERNAME -ne 'WDAGUtilityAccount') { throw 'UI dispatch requires Windows Sandbox.' }
    $run = Get-Content -LiteralPath (Join-Path $WorkDirectory 'run.json') -Raw | ConvertFrom-Json -AsHashtable
    $tests = Import-PowerShellDataFile "$PSScriptRoot/sandbox-ui-tests.psd1"
    $name = if ($run.test) { $run.test } else { 'Hud' }
    if (-not $tests.ContainsKey($name)) { throw "Unknown Sandbox UI test: $name" }
    $definition = $tests[$name]
    if ($definition.Wsl) { throw 'WSL tests need a separately configured Linux environment; no host fallback is permitted.' }
    $arguments = @{}
    foreach ($key in $run.parameters.Keys) {
        if ($key -notin $definition.Options) { throw "Unsupported $name parameter: $key" }
        $arguments[$key] = $run.parameters[$key]
    }
    $appDirectory = Join-Path $WorkDirectory 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64'
    $exe = Join-Path $appDirectory $run.exeName
    $ctl = Join-Path $WorkDirectory 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
    $env:AGWINTERMCTL = $ctl
    $null = New-Item -ItemType Directory -Path (Join-Path $WorkDirectory 'artifacts') -Force
    $env:PATH = (Split-Path $ctl) + ';' + $env:PATH
    if ($definition.Exe) { $arguments.Exe = $exe }
    if ($definition.Ctl) { $arguments.Ctl = $ctl }
    if ($definition.Strict) { $arguments.Strict = $true }
    if ($name -eq 'Hud') { $arguments.Suite = $run.suite }
    foreach ($key in $run.inputs) {
        if ($key -notin @($definition.Inputs) + @($definition.OptionalInputs)) { throw "Unsupported input: $key" }
        $arguments[$key] = Join-Path $WorkDirectory "inputs/$key"
    }
    foreach ($key in $run.outputs) {
        if ($key -notin $definition.Outputs) { throw "Unsupported output: $key" }
        $arguments[$key] = Join-Path $WorkDirectory "artifacts/$name-$key.json"
    }
    $global:LASTEXITCODE = 0
    & (Join-Path $WorkDirectory $definition.Path) @arguments
    if (-not $?) { exit 1 }
    exit $LASTEXITCODE
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
