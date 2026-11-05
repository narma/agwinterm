param([switch]$SkipDownload, [string]$SourceDirectory, [switch]$PrepareOnly, [switch]$TestSearch, [switch]$BuildSearchTests)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$pin = Get-Content (Join-Path $PSScriptRoot 'pin.json') -Raw | ConvertFrom-Json
$artifacts = Join-Path $repo 'artifacts'
$toolchains = Join-Path $artifacts 'toolchains'
$source = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory) } else { Join-Path $artifacts 'ghostty-source' }
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
$freshClone = -not (Test-Path -LiteralPath (Join-Path $source '.git'))
if ($freshClone) {
    if ($SkipDownload) { throw 'Pinned Ghostty source is missing' }
    & git clone --filter=blob:none --no-checkout https://github.com/ghostty-org/ghostty.git $source
    CheckExit 'Ghostty clone'
}
if (-not $freshClone -and (& git -C $source status --porcelain).Count -gt 0) { throw 'Ghostty source has local changes; refusing to overwrite them' }
& git -C $source cat-file -e "$($pin.revision)^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
    if ($SkipDownload) { throw 'Pinned Ghostty revision is missing; run without -SkipDownload to fetch it' }
    & git -C $source fetch origin $pin.revision
    CheckExit 'Pinned Ghostty fetch'
}
if ($freshClone -or (& git -C $source rev-parse HEAD) -ne $pin.revision) {
    & git -C $source checkout --detach $pin.revision
    CheckExit 'Pinned Ghostty checkout'
}
if ($PrepareOnly) { Write-Output "Prepared pinned Ghostty source: $source"; return }
Push-Location $source
$extensionPath = Join-Path $source 'src/terminal/c/agwinterm_history.zig'
$libraryPath = Join-Path $source 'src/lib_vt.zig'
$librarySource = [IO.File]::ReadAllBytes($libraryPath)
$searchExtension = Join-Path $source 'src/terminal/c/agwinterm_search.zig'
$foldExtension = Join-Path $source 'src/terminal/search/case_fold.zig'
$searchPatch = Join-Path $PSScriptRoot 'patches/unicode-search.patch'
$patchApplied = $false
try {
    & git apply --check $searchPatch
    CheckExit 'Check Unicode search patch'
    & git apply $searchPatch
    CheckExit 'Apply Unicode search patch'
    $patchApplied = $true
    Copy-Item (Join-Path $PSScriptRoot 'case_fold.zig') $foldExtension
    Copy-Item (Join-Path $PSScriptRoot 'search.zig') $searchExtension
    Copy-Item (Join-Path $PSScriptRoot 'history.zig') $extensionPath
    [IO.File]::AppendAllText($libraryPath, "`ncomptime { if (@import(`"terminal_options`").c_abi) @export(&@import(`"terminal/c/agwinterm_history.zig`").prepend, .{ .name = `"agwinterm_ghostty_prepend_history`" }); }`n")
    [IO.File]::AppendAllText($libraryPath, "`ncomptime { if (@import(`"terminal_options`").c_abi) @export(&@import(`"terminal/c/agwinterm_history.zig`").metadata, .{ .name = `"agwinterm_ghostty_metadata`" }); }`n")
    [IO.File]::AppendAllText($libraryPath, "`ncomptime { if (@import(`"terminal_options`").c_abi) @export(&@import(`"terminal/c/agwinterm_search.zig`").unicodeNeedle, .{ .name = `"agwinterm_ghostty_search_unicode`" }); }`n")
    [IO.File]::AppendAllText($libraryPath, "`ncomptime { if (@import(`"terminal_options`").c_abi) @export(&@import(`"terminal/c/agwinterm_search.zig`").tick, .{ .name = `"agwinterm_ghostty_search_tick`" }); }`n")
    [IO.File]::AppendAllText($libraryPath, "`ncomptime { if (@import(`"terminal_options`").c_abi) @export(&@import(`"terminal/c/agwinterm_search.zig`").matches, .{ .name = `"agwinterm_ghostty_search_matches`" }); }`n")
    $phaseTimer = [Diagnostics.Stopwatch]::StartNew()
    & $zig build -Demit-lib-vt=true -Doptimize=ReleaseFast "-Dtarget=$($pin.target)" --prefix $output -j4 --summary all
    CheckExit 'Ghostty build'
    Write-Output ("Ghostty ReleaseFast build: {0:N2}s" -f $phaseTimer.Elapsed.TotalSeconds)
    if ($TestSearch -or $BuildSearchTests) {
        $phaseTimer.Restart()
        $searchOutput = Join-Path $output 'search-tests'
        # Upstream's C API test binary includes all Zig module tests too. Build it
        # once in library-only mode, avoiding app dependencies and duplicate builds.
        & $zig build test-lib-vt-build -Demit-lib-vt=true -Demit-test-exe=true -Dtest-filter=search -Doptimize=Debug "-Dtarget=$($pin.target)" --prefix $searchOutput -j1 --summary all
        CheckExit 'Ghostty search test build'
        Write-Output ("Ghostty Debug search test build: {0:N2}s" -f $phaseTimer.Elapsed.TotalSeconds)
        if ($TestSearch) {
            $phaseTimer.Restart()
            # Run directly: a restored compiler cache must not reuse a test-run result.
            & (Join-Path $searchOutput 'bin/ghostty-vt-c-test.exe')
            CheckExit 'Ghostty search tests'
            Write-Output ("Ghostty Debug search test execution: {0:N2}s" -f $phaseTimer.Elapsed.TotalSeconds)
        }
    }
} finally {
    [IO.File]::WriteAllBytes($libraryPath, $librarySource)
    if (Test-Path -LiteralPath $extensionPath) { Remove-Item -LiteralPath $extensionPath }
    if (Test-Path -LiteralPath $searchExtension) { Remove-Item -LiteralPath $searchExtension }
    if (Test-Path -LiteralPath $foldExtension) { Remove-Item -LiteralPath $foldExtension }
    if ($patchApplied) { & git apply --reverse $searchPatch; CheckExit 'Restore upstream search sources' }
    Pop-Location
}
$bin = Join-Path $output 'bin'
# The shim uses Zig's bundled MinGW headers and Windows' inbox CRT, avoiding
# a Visual Studio/VC redistributable installation. Both DLLs expose the x64 C ABI.
$phaseTimer.Restart()
& $zig cc -target x86_64-windows-gnu -O2 -Wall -Wextra -Werror -shared `
    -I (Join-Path $source 'include') (Join-Path $PSScriptRoot 'agwvt.c') `
    (Join-Path $output 'lib/ghostty-vt.lib') -lole32 -lwindowscodecs -luuid -o (Join-Path $bin 'agwinterm_ghostty.dll')
CheckExit 'Ghostty adapter build'
Write-Output ("Ghostty C adapter build: {0:N2}s" -f $phaseTimer.Elapsed.TotalSeconds)
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
    searchPatchSha256 = (Get-FileHash $searchPatch -Algorithm SHA256).Hash
    searchExtensionSha256 = (Get-FileHash (Join-Path $PSScriptRoot 'search.zig') -Algorithm SHA256).Hash
    caseFoldSourceSha256 = (Get-FileHash (Join-Path $PSScriptRoot 'case_fold.zig') -Algorithm SHA256).Hash
    utc = [DateTimeOffset]::UtcNow.ToString('O'); binaries = @($hashes)
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'provenance.json')
Copy-Item (Join-Path $output 'provenance.json') (Join-Path $bin 'ghostty-vt.provenance.json') -Force
Write-Output "Built Ghostty and adapter: $bin"
