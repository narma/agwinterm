param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src/Agwinterm.Win32/Agwinterm.Win32.csproj'
$dotnet = if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') { 'C:\Program Files\dotnet\dotnet.exe' } else { 'dotnet' }
# Resolve the app's pinned package without compiling/publishing the GUI just to
# copy two native files. GeneratePathProperty also respects custom NuGet roots.
& $dotnet restore $project -r win-x64 -p:SeparateHostPayload=true
if ($LASTEXITCODE -ne 0) { throw 'ConPTY package restore failed' }
$package = (& $dotnet msbuild $project -nologo -p:SeparateHostPayload=true -getProperty:PkgMicrosoft_Windows_Console_ConPTY) -join "`n"
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($package)) { throw 'ConPTY package path could not be resolved' }
$package = $package.Trim()
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item -LiteralPath (Join-Path $package 'runtimes/win-x64/native/conpty.dll') -Destination $Destination
Copy-Item -LiteralPath (Join-Path $package 'build/native/runtimes/x64/OpenConsole.exe') -Destination $Destination
