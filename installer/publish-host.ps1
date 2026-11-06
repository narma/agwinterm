param([Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src/Agwinterm.Host/Agwinterm.Host.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none "-p:Version=$Version" -o $Destination
if ($LASTEXITCODE -ne 0) { throw 'Session host publish failed' }
