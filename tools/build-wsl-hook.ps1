# Builds a static Linux x64 hook. Requires Rust in WSL with x86_64-unknown-linux-musl installed.
param([string]$Distro = 'Ubuntu')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$linuxRepo = (& wsl -d $Distro --exec wslpath -u $repo).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot access repository in WSL' }
& wsl -d $Distro --exec sh -c 'export PATH="$HOME/.cargo/bin:$PATH"; export RUSTFLAGS="-C linker=rust-lld"; cargo build --locked --release --manifest-path "$1/native/Cargo.toml" -p agwinterm-hook --target x86_64-unknown-linux-musl --target-dir "$1/artifacts/native-linux"' build $linuxRepo
if ($LASTEXITCODE -ne 0) { throw 'Linux hook build failed (requires Rust target x86_64-unknown-linux-musl)' }
$destination = Join-Path $repo 'artifacts/wsl-hook'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'artifacts/native-linux/x86_64-unknown-linux-musl/release/agwinterm-hook') -Destination (Join-Path $destination 'agwinterm-hook-linux-x64') -Force
