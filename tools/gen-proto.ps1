# Regenerate the protobuf handlers from proto/*.proto. Generated code is COMMITTED so CI and dev
# builds never need protoc/python — rerun this after a schema change. C#: protoc's csharp plugin.
#
# The C++ (nanopb) handlers belong to agliteterm now, which lives in its own repository and commits
# its own src/proto. This script used to write them into lite/src/proto here; that directory left
# with the product, so the step silently recreated an orphan tree of C++ nobody builds.
#
# Regenerate the sibling C++ bindings by passing -AgliteTermRoot with its checkout path.
param(
    # Or $env:AGLITETERM_ROOT. Omitted = C# only, and the script says so rather than
    # leaving you to assume the C++ was refreshed.
    [string]$AgliteTermRoot = $env:AGLITETERM_ROOT
)
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

# C++ (nanopb) -> <agliteterm>/src/proto/ (nanopb's generator shells out to protoc: put ours on PATH)
$liteOut = $null
if ($AgliteTermRoot) {
    if (-not (Test-Path (Join-Path $AgliteTermRoot 'src\main.cpp'))) {
        throw "AgliteTermRoot does not look like an agliteterm checkout: $AgliteTermRoot"
    }
    $env:PATH = (Join-Path $PSScriptRoot 'protoc\bin') + ';' + $env:PATH
    $nanopb = Join-Path $PSScriptRoot 'nanopb'
    $liteOut = Join-Path $AgliteTermRoot 'src\proto'
    New-Item -ItemType Directory -Force $liteOut | Out-Null
    python (Join-Path $nanopb 'generator\nanopb_generator.py') -I $proto -D $liteOut (Join-Path $proto 'ptyhost.proto')
    if ($LASTEXITCODE -ne 0) { throw 'nanopb generation failed' }
    Copy-Item (Join-Path $nanopb 'pb.h'), (Join-Path $nanopb 'pb_encode.*'), (Join-Path $nanopb 'pb_decode.*'), (Join-Path $nanopb 'pb_common.*') $liteOut -Force
}

if ($liteOut) { "generated: C++ nanopb ($liteOut) - commit it in that repository too" }
else { "SKIPPED the C++ handlers: agliteterm has its own repository. Pass -AgliteTermRoot <path> to regenerate them, or they keep the OLD schema." }
