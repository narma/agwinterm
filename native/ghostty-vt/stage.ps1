param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$cache = Join-Path $repo 'artifacts/ghostty-release-native'
$names = @('ghostty-vt.dll','agwinterm_ghostty.dll','ghostty-vt.provenance.json','ghostty-NOTICES.txt')
# Release jobs reuse only a complete package for these exact source inputs.
$inputs = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [IO.Path]::GetRelativePath($PSScriptRoot,$_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
}) -join "`n"
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($inputs)))
$manifestPath = Join-Path $cache 'package.json'
function Copy-Package {
    $null = New-Item -ItemType Directory -Force -Path $Destination
    foreach ($name in $names) {
        $from = Join-Path $cache $name
        $to = Join-Path ([IO.Path]::GetFullPath($Destination)) $name
        if ([IO.Path]::GetFullPath($from) -ne $to) { Copy-Item -LiteralPath $from -Destination $to -Force }
    }
}
if (Test-Path -LiteralPath $manifestPath) {
    $cached = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $valid = $cached.inputs -eq $fingerprint
    foreach ($name in $names) {
        $file = Join-Path $cache $name
        if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $cached.files.$name) { $valid = $false }
    }
    if ($valid) { Copy-Package; Write-Host 'Reused verified Ghostty release package'; return }
}
& "$PSScriptRoot/build.ps1"
$source = Join-Path $repo 'artifacts/ghostty-source'
$build = Join-Path $repo 'artifacts/ghostty-build'
$pin = Get-Content "$PSScriptRoot/pin.json" -Raw | ConvertFrom-Json
$zig = Join-Path $repo "artifacts/toolchains/zig-x86_64-windows-$($pin.zigVersion)/zig.exe"
Push-Location $source
try {
    & $zig build lib-vt-packages -Demit-lib-vt=true -Dtarget=x86_64-windows-gnu --prefix $build
    if ($LASTEXITCODE -ne 0) { throw 'Ghostty dependency inventory failed' }
} finally { Pop-Location }
$inventory = Get-Content (Join-Path $build 'lib-vt-packages.json') -Raw | ConvertFrom-Json
$notice = [Text.StringBuilder]::new()
[void]$notice.AppendLine("libghostty-vt $($pin.revision) and compiled dependencies")
[void]$notice.AppendLine((Get-Content (Join-Path $source 'deps/libghostty-vt-third-party.json') -Raw))
$files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($package in $inventory.packages | Where-Object { $_.linked_paths.Count -gt 0 }) {
    $path = if (-not $package.hash) { $source }
        elseif ($package.hash.StartsWith('pkg/')) { Join-Path $source $package.hash }
        else { Join-Path (Join-Path $source 'zig-pkg') $package.hash }
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing dependency source: $path" }
    $licenses = @(Get-ChildItem -LiteralPath $path -File | Where-Object Name -Match '^(LICENSE|COPYING|NOTICE)')
    foreach ($child in @('licenses','vendor')) {
        $directory = Join-Path $path $child
        if (Test-Path -LiteralPath $directory) {
            $licenses += Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object Name -Match '^(LICENSE|COPYING|NOTICE)'
        }
    }
    if ($licenses.Count -eq 0 -and -not $package.hash.StartsWith('pkg/')) { throw "Missing dependency license: $path" }
    foreach ($license in $licenses) { [void]$files.Add($license.FullName) }
}
# Zig's package filter omits uucode's additional notices. Keep verified copies
# from the same pinned upstream archive, including its Unicode data license.
Get-ChildItem "$PSScriptRoot/notices" -File | Where-Object Name -Like 'uucode-*' | ForEach-Object { [void]$files.Add($_.FullName) }
[void]$files.Add((Join-Path (Split-Path $zig) 'LICENSE'))
foreach ($file in $files | Sort-Object) {
    [void]$notice.AppendLine("`n--- $([IO.Path]::GetRelativePath($repo,$file)) ---")
    [void]$notice.AppendLine([IO.File]::ReadAllText($file))
}
$null = New-Item -ItemType Directory -Force -Path $cache
foreach ($name in @('ghostty-vt.dll','agwinterm_ghostty.dll','ghostty-vt.provenance.json')) {
    Copy-Item -LiteralPath (Join-Path $build "bin/$name") -Destination $cache -Force
}
[IO.File]::WriteAllText((Join-Path $cache 'ghostty-NOTICES.txt'),$notice.ToString())
$hashes = @{}
foreach ($name in $names) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $cache $name)).Hash }
@{inputs=$fingerprint;files=$hashes} | ConvertTo-Json | Set-Content -LiteralPath $manifestPath
Copy-Package
