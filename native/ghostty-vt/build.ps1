param([switch]$SkipDownload)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$pin = Get-Content (Join-Path $PSScriptRoot 'pin.json') -Raw | ConvertFrom-Json
$artifacts = Join-Path $repo 'artifacts'
$toolchains = Join-Path $artifacts 'toolchains'
$source = Join-Path $artifacts 'ghostty-source'
$output = Join-Path $artifacts 'ghostty-build'
$archive = Join-Path $toolchains "zig-$($pin.zigVersion).zip"
$zig = Join-Path $toolchains "zig-x86_64-windows-$($pin.zigVersion)/zig.exe"
function CheckExit([string]$operation) { if ($LASTEXITCODE -ne 0) { throw "$operation failed: $LASTEXITCODE" } }
New-Item -ItemType Directory -Force $toolchains | Out-Null
if (-not (Test-Path -LiteralPath $zig)) {
    if ($SkipDownload) { throw 'Pinned Zig toolchain is missing' }
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $pin.zigWindowsUrl -OutFile $archive }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $pin.zigWindowsSha256) { throw 'Zig archive SHA-256 mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath $toolchains -Force
}
if ((& $zig version) -ne $pin.zigVersion) { throw 'Incorrect Zig version' }
if (-not (Test-Path -LiteralPath (Join-Path $source '.git'))) {
    if ($SkipDownload) { throw 'Pinned Ghostty source is missing' }
    & git clone --filter=blob:none --no-checkout https://github.com/ghostty-org/ghostty.git $source
    CheckExit 'Ghostty clone'
}
if ((& git -C $source status --porcelain).Count -gt 0) { throw 'Ghostty source has local changes; refusing to overwrite them' }
if ((& git -C $source rev-parse HEAD) -ne $pin.revision) {
    & git -C $source checkout --detach $pin.revision
    CheckExit 'Pinned Ghostty checkout'
}
Push-Location $source
$extensionPath = Join-Path $source 'src/terminal/c/agwinterm_history.zig'
$libraryPath = Join-Path $source 'src/lib_vt.zig'
$librarySource = [IO.File]::ReadAllBytes($libraryPath)
try {
    Copy-Item (Join-Path $PSScriptRoot 'history.zig') $extensionPath
    [IO.File]::AppendAllText($libraryPath, "`ncomptime { @export(&@import(`"terminal/c/agwinterm_history.zig`").prepend, .{ .name = `"agwinterm_ghostty_prepend_history`" }); }`n")
    & $zig build -Demit-lib-vt=true -Doptimize=ReleaseFast "-Dtarget=$($pin.target)" --prefix $output -j4
    CheckExit 'Ghostty build'
} finally {
    [IO.File]::WriteAllBytes($libraryPath, $librarySource)
    if (Test-Path -LiteralPath $extensionPath) { Remove-Item -LiteralPath $extensionPath }
    Pop-Location
}
$bin = Join-Path $output 'bin'
# The shim uses Zig's bundled MinGW headers and Windows' inbox CRT, avoiding
# a Visual Studio/VC redistributable installation. Both DLLs expose the x64 C ABI.
& $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -shared `
    -I (Join-Path $source 'include') (Join-Path $PSScriptRoot 'agwvt.c') `
    (Join-Path $output 'lib/ghostty-vt.lib') -lole32 -lwindowscodecs -luuid -o (Join-Path $bin 'agwinterm_ghostty.dll')
CheckExit 'Ghostty adapter build'
$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $source 'LICENSE') (Join-Path $licenses 'ghostty.txt')
Copy-Item (Join-Path (Split-Path $zig) 'LICENSE') (Join-Path $licenses 'zig.txt')
$hashes = @('ghostty-vt.dll','agwinterm_ghostty.dll') | ForEach-Object {
    [ordered]@{ file = $_; sha256 = (Get-FileHash (Join-Path $bin $_) -Algorithm SHA256).Hash }
}
[ordered]@{
    upstream = $pin.revision; zig = $pin.zigVersion; zigArchiveSha256 = $pin.zigWindowsSha256
    upstreamTarget = $pin.target; adapterTarget = 'x86_64-windows-gnu'; abi = $pin.abi
    adapterSourceSha256 = (Get-FileHash (Join-Path $PSScriptRoot 'agwvt.c') -Algorithm SHA256).Hash
    adapterHeaderSha256 = (Get-FileHash (Join-Path $PSScriptRoot 'agwvt.h') -Algorithm SHA256).Hash
    historyExtensionSha256 = (Get-FileHash (Join-Path $PSScriptRoot 'history.zig') -Algorithm SHA256).Hash
    utc = [DateTimeOffset]::UtcNow.ToString('O'); binaries = @($hashes)
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'provenance.json')
Copy-Item (Join-Path $output 'provenance.json') (Join-Path $bin 'ghostty-vt.provenance.json') -Force
Write-Output "Built Ghostty and adapter: $bin"
