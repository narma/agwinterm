param([string]$Distro = 'Ubuntu', [Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslZmxIdentity -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$scriptPath = (& wsl.exe -d $Distro --exec wslpath -u (Join-Path $PSScriptRoot 'zmx/identity-tests.py')).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve fixture path' }
& wsl.exe -d $Distro --exec timeout 60 python3 -B $scriptPath "$BuildDirectory/zmx/zig-out/bin/zmx"
if ($LASTEXITCODE -ne 0) { throw "Exact daemon identity/termination fixture failed: $LASTEXITCODE" }
$lifecyclePath = (& wsl.exe -d $Distro --exec wslpath -u (Join-Path $PSScriptRoot 'zmx/lifecycle-tests.py')).Trim()
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$native = Join-Path $repo 'artifacts/wsl-hook/agwinterm-hook-linux-x64'
$nativeLinux = (& wsl.exe -d $Distro --exec wslpath -u $native).Trim()
& wsl.exe -d $Distro --exec timeout 90 python3 -B $lifecyclePath "$BuildDirectory/zmx/zig-out/bin/zmx" $nativeLinux
if ($LASTEXITCODE -ne 0) { throw "Lifecycle fixture failed: $LASTEXITCODE" }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repo 'tests/Agwinterm.WslLiveHarness/Agwinterm.WslLiveHarness.csproj'
& dotnet build $project --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Windows live bridge harness build failed' }
Copy-Item -LiteralPath $native -Destination (Join-Path $repo 'tests/Agwinterm.WslLiveHarness/bin/Debug/net10.0-windows/agwinterm-hook-linux-x64')
& dotnet (Join-Path $repo 'tests/Agwinterm.WslLiveHarness/bin/Debug/net10.0-windows/Agwinterm.WslLiveHarness.dll') $Distro "$BuildDirectory/zmx/zig-out/bin/zmx" (Join-Path $repo 'native/target/release/agwinterm-ptyhost.exe') (Join-Path $PSScriptRoot 'zmx/terminal-workload.py')
if ($LASTEXITCODE -ne 0) { throw 'Windows live bridge fixture failed' }
