param(
    [Parameter(Mandatory)][string]$Codex,
    [Parameter(Mandatory)][string]$Claude,
    [string]$Exe,
    [string]$Ctl
)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test AgentConversationRename -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
& "$PSScriptRoot/agent-conversation-rename-cases.ps1" @PSBoundParameters
