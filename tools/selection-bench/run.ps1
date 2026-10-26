param(
    [ValidatePattern('^[0-9a-fA-F]{7,40}$')][string]$Baseline = '8924812',
    [string]$Output,
    [string]$Scenario
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = @(& git -C $repo show "${Baseline}:src/Agwinterm.Core/ContentSelection.cs")
if ($LASTEXITCODE -ne 0) { throw "Cannot read selection baseline $Baseline" }
$obj = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
($source -join "`n").Replace('ContentSelection', 'BaselineContentSelection') |
    Set-Content -LiteralPath (Join-Path $obj 'SelectionBaseline.cs') -Encoding utf8
if (-not $Output) { $Output = Join-Path $repo 'artifacts/selection-bench/results.json' }
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force (Split-Path -Parent $Output) | Out-Null
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    Write-Output "Baseline commit: $Baseline"
    $runArgs = @($Output)
    if ($Scenario) { $runArgs += $Scenario }
    & dotnet run --project (Join-Path $PSScriptRoot 'selection-bench.csproj') -c Release -- @runArgs
    if ($LASTEXITCODE -ne 0) { throw 'Selection benchmark failed' }
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
