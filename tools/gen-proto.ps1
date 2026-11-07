# Regenerate the committed C# protobuf bindings for the Windows product.
# Normal builds use the generated files and do not require protoc.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$protoc = Join-Path $PSScriptRoot 'protoc\bin\protoc.exe'
$proto = Join-Path $root 'proto'

# C# ptyhost -> src/Agwinterm.Pty/Proto/ ; persist -> src/Agwinterm.Core/Persist/
$csOut = Join-Path $root 'src\Agwinterm.Pty\Proto'
New-Item -ItemType Directory -Force $csOut | Out-Null
& $protoc -I $proto --csharp_out=$csOut (Join-Path $proto 'ptyhost.proto')
if ($LASTEXITCODE -ne 0) { throw 'protoc csharp (ptyhost) failed' }
$csCore = Join-Path $root 'src\Agwinterm.Core\Persist'
New-Item -ItemType Directory -Force $csCore | Out-Null
& $protoc -I $proto --csharp_out=$csCore (Join-Path $proto 'persist.proto')
if ($LASTEXITCODE -ne 0) { throw 'protoc csharp (persist) failed' }
