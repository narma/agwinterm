# One-time unattended image provisioning, inside the guest only.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\AgwintermUi'
Start-Transcript -LiteralPath (Join-Path $root 'image-setup.log') -Force | Out-Null
try {
    $password = ConvertTo-SecureString ([guid]::NewGuid().ToString('N') + '!aA9') -AsPlainText -Force
    & (Join-Path $root 'prepare-guest.ps1') -Password $password -Bootstrap (Join-Path $root 'source-bootstrap.ps1')
    @{configured=$true; utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $root 'image-configured.json')
} catch {
    @{configured=$false; error=$_.ToString()} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $root 'image-configured.json')
    throw
} finally { Stop-Transcript | Out-Null }
Restart-Computer -Force
