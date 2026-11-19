param([string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslAgentConversationRename -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
& "$PSScriptRoot/agent-conversation-rename-cases.ps1" -Distro $Distro
