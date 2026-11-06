param([Parameter(Mandatory)][string]$Exe,[ValidateSet(1,8,32)][int]$Sessions=1,
      [ValidateRange(1,20)][int]$Minutes=20)
$ErrorActionPreference='Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test HostOwnerSoak -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$directory=Split-Path ([IO.Path]::GetFullPath($Exe))
$evidence=Join-Path $PSScriptRoot '../../.revmux/owner-soak'
$null=New-Item -Type Directory -Force -Path $evidence
& (Join-Path $directory 'ghostty-server-tests/Agwinterm.GhosttyServerHarness.exe') --soak $directory $Sessions $Minutes (Join-Path $evidence "samples-$Sessions.csv")
if($LASTEXITCODE -ne 0){throw "Owner soak failed: $LASTEXITCODE"}
