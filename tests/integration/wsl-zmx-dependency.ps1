param(
    [string]$Distro = 'Ubuntu',
    [Parameter(Mandatory)][string]$BuildDirectory
)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslZmxDependency -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
if ($BuildDirectory -notmatch '^/[^\r\n]+$' -or $BuildDirectory.StartsWith('/mnt/')) {
    throw 'BuildDirectory must be an absolute Linux filesystem path.'
}
# Upstream probe creates only a private /tmp/zmx-e2e-PID endpoint and test PTYs.
# Keep it on the same private Windows desktop route as subsequent Rust/WSL tests.
& wsl.exe -d $Distro --cd "$BuildDirectory/zmx" --exec env "ZIG_GLOBAL_CACHE_DIR=$BuildDirectory/zig-cache" timeout 300 "$BuildDirectory/zig-x86_64-linux-0.16.0/zig" build test
if ($LASTEXITCODE -ne 0) { throw "Pinned ZMX unit/publication tests failed: $LASTEXITCODE" }
Write-Host 'PASS pinned ZMX unit tests including atomic socket publication'
& wsl.exe -d $Distro --exec env -u ZMX_SESSION -u ZMX_SESSION_PREFIX -u ZMX_MANAGED -u ZMX_MANAGED_CLAIM -u ZMX_DIR timeout 90 python3 "$BuildDirectory/agterm/scripts/zmx-patches/check.py" "$BuildDirectory/zmx/zig-out/bin/zmx"
if ($LASTEXITCODE -ne 0) { throw "Pinned ZMX capability probe failed: $LASTEXITCODE" }
Write-Host 'PASS pinned managed-role, screen, acknowledged input and replay probe'
$existing = (& wsl.exe -d $Distro --exec wslpath -u (Join-Path $PSScriptRoot 'zmx/existing-fd-tests.py')).Trim()
& wsl.exe -d $Distro --exec timeout 30 python3 -B $existing "$BuildDirectory/zmx/zig-out/bin/zmx"
if ($LASTEXITCODE -ne 0) { throw 'Existing-connection attachment probe failed' }
$attest = (& wsl.exe -d $Distro --exec wslpath -u (Join-Path $PSScriptRoot 'zmx/attest.py')).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve capability evidence writer' }
& wsl.exe -d $Distro --exec python3 $attest $BuildDirectory
if ($LASTEXITCODE -ne 0) { throw 'Cannot persist capability evidence' }
