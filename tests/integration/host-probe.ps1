# Run the matching .NET harness out of process; never load product assemblies into PowerShell.
function Invoke-TestHostProbe {
    param([string]$Exe, [string]$Namespace,
        [ValidateSet('list','create-abandoned','shutdown-if-running')][string]$Operation)
    $probe = Join-Path (Split-Path $Exe) 'ghostty-server-tests/Agwinterm.GhosttyServerHarness.exe'
    if (-not (Test-Path -LiteralPath $probe)) {
        throw 'Publish Agwinterm.GhosttyServerHarness into the app directory under ghostty-server-tests before running this fixture.'
    }
    $output = & $probe --host-probe $Namespace $Operation
    if ($LASTEXITCODE -ne 0) { throw "Host probe failed ($Operation): $LASTEXITCODE" }
    if ($Operation -eq 'list') { $output | ConvertFrom-Json }
}
