# Only load Win32 code when a Hyper-V guest claims an active run.
if ($env:AGWINTERM_HYPERV_UI -ne '1') { return $false }
if (-not ('HyperVUiIdentity' -as [type])) { Add-Type -Path "$PSScriptRoot/hyperv-ui-identity.cs" }
if (-not [HyperVUiIdentity]::IsGuest()) {
    throw 'Hyper-V UI context does not match a prepared VM and its unlocked interactive desktop.'
}
return $true
