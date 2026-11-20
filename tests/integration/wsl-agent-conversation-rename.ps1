param([string]$Distro = 'Ubuntu', [string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslAgentConversationRename -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$binaries = @{}
if ($PackageDirectory) {
    $binaries.Exe = (Resolve-Path (Join-Path $PackageDirectory 'Agwinterm.Win32.exe')).Path
    $binaries.Ctl = (Resolve-Path (Join-Path $PackageDirectory 'agwintermctl.exe')).Path
}
& "$PSScriptRoot/agent-conversation-rename-cases.ps1" -Distro $Distro @binaries
