param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test HostOwnerSearchLoad -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$dir=Split-Path ([IO.Path]::GetFullPath($Exe))
$evidence=Join-Path $PSScriptRoot '../../.revmux/owner-search'
$null=New-Item -Type Directory -Force $evidence
& (Join-Path $dir 'ghostty-server-tests/Agwinterm.GhosttyServerHarness.exe') --search-load $dir (Join-Path $evidence 'samples.csv')
if($LASTEXITCODE){throw "Owner search load failed: $LASTEXITCODE"}
