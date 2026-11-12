# Build the PORTABLE agwinterm: one self-contained single-file exe, no installer needed.
# Output: installer\Output\agwinterm-portable-<ver>-win-x64.exe
# Themes, icon, CLI and WSL helpers are embedded. Tools are extracted lazily to a
# content-addressed directory under %LOCALAPPDATA%\agwinterm\portable-tools.
param([string]$PrebuiltToolsDirectory)
$ErrorActionPreference = "Stop"
$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root   = Split-Path -Parent $here
$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }
$stage  = Join-Path $here "stage-portable"
$outDir = Join-Path $here "Output"

# version = the installer's AppVersion define (single source of truth)
$iss = Get-Content (Join-Path $here "agwinterm.iss") -Raw
if ($iss -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in agwinterm.iss" }
$ver = $Matches[1]

Write-Host "== clean stage ==" -ForegroundColor Cyan
# Verify the cleanup target before removing a previous build.
$stage = [IO.Path]::GetFullPath($stage)
if ($stage -ne [IO.Path]::GetFullPath((Join-Path $root 'installer/stage-portable'))) { throw 'Unexpected portable stage path' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, $outDir | Out-Null

$tools = Join-Path $stage 'tools'
New-Item -ItemType Directory -Path $tools | Out-Null
& (Join-Path $root 'native/ghostty-vt/stage.ps1') -Destination $tools
# Resolve the application's pinned ConPTY package using MSBuild, not a separately
# hard-coded NuGet version. Its host must remain beside the extracted DLL.
$nativePayload = Join-Path $stage 'native-payload'
& $dotnet publish (Join-Path $root 'src/Agwinterm.Win32/Agwinterm.Win32.csproj') -c Release -r win-x64 --self-contained false -o $nativePayload
if ($LASTEXITCODE -ne 0) { throw 'portable native payload publish failed' }
Copy-Item (Join-Path $nativePayload 'conpty.dll') $tools
Copy-Item (Join-Path $nativePayload 'x64/OpenConsole.exe') $tools
if ($PrebuiltToolsDirectory) {
    # CI supplies artifacts built from this same checkout, never a cross-commit cache.
    foreach ($name in @('Agwinterm.Host.exe', 'agwintermctl.exe')) {
        Copy-Item -LiteralPath (Join-Path $PrebuiltToolsDirectory $name) -Destination $tools -ErrorAction Stop
    }
} else {
    & (Join-Path $here 'publish-portable-tool.ps1') -Tool host -Destination $tools
    & (Join-Path $here 'publish-portable-tool.ps1') -Tool cli -Destination $tools
}
& cargo build --locked --release --manifest-path (Join-Path $root 'native/Cargo.toml') -p agwinterm-hook
if ($LASTEXITCODE -ne 0) { throw 'portable Windows hook build failed' }
& (Join-Path $root 'tools/stage-wsl-hooks.ps1') -Destination $tools
$archive = Join-Path $stage 'tools.zip'
$toolFiles = @('Agwinterm.Host.exe', 'agwintermctl.exe', 'agwinterm-hook.exe', 'agwinterm-hook-linux-x64', 'agwinterm-hook-linux-arm64',
  'ghostty-vt.dll', 'agwinterm_ghostty.dll', 'ghostty-vt.provenance.json', 'ghostty-NOTICES.txt', 'conpty.dll', 'OpenConsole.exe') |
  ForEach-Object { Join-Path $tools $_ }
Compress-Archive -LiteralPath $toolFiles -DestinationPath $archive

Write-Host "== publish single-file portable exe (v$ver) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Agwinterm.Win32\Agwinterm.Win32.csproj") `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:SatelliteResourceLanguages=en `
  -p:DebugType=none `
  -p:Version=$ver `
  -p:SeparateHostPayload=true "-p:PortableToolsArchive=$archive" `
  -o $stage
if ($LASTEXITCODE -ne 0) { throw "portable publish failed" }

$exe = Join-Path $stage "Agwinterm.Win32.exe"
if (-not (Test-Path $exe)) { throw "publish produced no Agwinterm.Win32.exe" }

# Ship ONLY the exe — themes/assets land next to it in the stage as loose copies, but the
# embedded fallbacks make the bare exe self-sufficient.
$dst = Join-Path $outDir "agwinterm-portable-$ver-win-x64.exe"
Copy-Item $exe $dst -Force
Write-Host ("== done: {0} ({1:N1} MB) ==" -f $dst, ((Get-Item $dst).Length/1MB)) -ForegroundColor Green
