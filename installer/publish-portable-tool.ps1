param(
    [Parameter(Mandatory)][ValidateSet('host', 'cli')][string]$Tool,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$iss = Get-Content (Join-Path $PSScriptRoot 'agwinterm.iss') -Raw
if ($iss -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw 'AppVersion not found in agwinterm.iss' }
$version = $Matches[1]
if ($Tool -eq 'host') {
    & (Join-Path $PSScriptRoot 'publish-host.ps1') -Destination $Destination -Version $version
} else {
    $dotnet = if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') { 'C:\Program Files\dotnet\dotnet.exe' } else { 'dotnet' }
    & $dotnet publish (Join-Path $root 'src/Agwinterm.Ctl/Agwinterm.Ctl.csproj') `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none "-p:Version=$version" -o $Destination
    if ($LASTEXITCODE -ne 0) { throw 'portable CLI publish failed' }
}
