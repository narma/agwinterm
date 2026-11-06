param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference='Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test HostOwnerContracts -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$directory=Split-Path ([IO.Path]::GetFullPath($Exe))
& (Join-Path $directory 'ghostty-server-tests/Agwinterm.GhosttyServerHarness.exe') $directory
if($LASTEXITCODE -ne 0){throw "Host-owner contract harness failed: $LASTEXITCODE"}
