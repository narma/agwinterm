param([string]$GateExe = "$PSScriptRoot/../WebViewHost.Gate/bin/Release/net11.0-windows/win-x64/WebViewHost.Gate.exe")
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WebViewGate -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
& $GateExe
exit $LASTEXITCODE
