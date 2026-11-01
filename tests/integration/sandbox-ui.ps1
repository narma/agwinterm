#Requires -Version 7.0
param(
    [string]$Exe = "$PSScriptRoot/../../src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe",
    [string]$CtlExe = "$PSScriptRoot/../../src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe",
    [ValidateSet('Hud','HudAdvanced','Recent','WorkingSets','Windows','Presentation','Quick','Navigation','Picker','Ask','Accessibility','Command','Menu')]
    [string]$Suite = 'Navigation',
    [string]$Test = 'Hud',
    [hashtable]$TestParameters = @{},
    [string]$WebView2Home,
    [string]$PythonHome,
    [string]$PowerShellHome = $PSHOME,
    [string]$DotnetHome = "$env:ProgramFiles/dotnet",
    [string]$OutputDirectory = "$PSScriptRoot/../../artifacts/sandbox/$((Get-Date).ToString('yyyyMMddTHHmmss'))-$([guid]::NewGuid().ToString('N').Substring(0,8))",
    [ValidateRange(1,86400)][int]$TimeoutSeconds = 900,
    [ValidateRange(2048,65536)][int]$MemoryInMB = 4096,
    [switch]$DisableVGpu,
    [switch]$Background,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows Sandbox UI tests require a Windows host.' }
$definitions = Import-PowerShellDataFile "$PSScriptRoot/sandbox-ui-tests.psd1"
if (-not $definitions.ContainsKey($Test)) { throw "Unknown Sandbox UI test: $Test. Available: $($definitions.Keys -join ', ')" }
$definition = $definitions[$Test]
if ($definition.Wsl) { throw "$Test requires a configured Linux distribution. Windows Sandbox does not inherit host WSL distributions; refusing to open test windows on the host." }
foreach ($key in $TestParameters.Keys) {
    if ($key -notin @($definition.Options) + @($definition.Inputs) + @($definition.OptionalInputs) + @($definition.Outputs)) { throw "Unsupported $Test parameter: $key" }
    if ($TestParameters[$key] -isnot [string] -and $TestParameters[$key] -isnot [bool] -and $TestParameters[$key] -isnot [int]) { throw "Unsupported value for $Test parameter: $key" }
}
if ($Test -eq 'Hud' -and $TestParameters.ContainsKey('Suite')) { $Suite = $TestParameters.Suite }
$label = if ($Test -eq 'Hud') { $Suite } else { $Test }
$requiresPython = $definition.Python -or ($Test -eq 'WebOverlayHost' -and ($TestParameters.Cookbook -or $TestParameters.CookbookOnly))
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$inputFiles = @{}
foreach ($key in @($definition.Inputs) + @($definition.OptionalInputs)) {
    if (-not $key -or ($key -in $definition.OptionalInputs -and -not $TestParameters.ContainsKey($key))) { continue }
    $source = if ($TestParameters.ContainsKey($key)) { $TestParameters[$key] } else {
        Join-Path $repo ('installer/Output/' + $(if ($key -eq 'Installer') { 'agwinterm-setup-0.20.14.exe' } else { 'agwinterm-portable-0.20.14-win-x64.exe' }))
    }
    $inputFiles[$key] = (Resolve-Path -LiteralPath $source).Path
}
if ($definition.Web) {
    if (-not $WebView2Home) {
        foreach ($root in @("${env:ProgramFiles(x86)}/Microsoft/EdgeWebView/Application", "$env:ProgramFiles/Microsoft/EdgeWebView/Application")) {
            if (Test-Path -LiteralPath $root) {
                $candidate = Get-ChildItem -LiteralPath $root -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'msedgewebview2.exe') } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($candidate) { $WebView2Home = $candidate.FullName; break }
            }
        }
    }
    if (-not $WebView2Home -or -not (Test-Path -LiteralPath (Join-Path $WebView2Home 'msedgewebview2.exe'))) { throw "$Test requires a local WebView2 runtime; pass -WebView2Home. No runtime is downloaded and there is no host fallback." }
    $WebView2Home = (Resolve-Path -LiteralPath $WebView2Home).Path
}
if ($requiresPython) {
    if (-not $PythonHome) {
        $pythonExe = & py -3 -c 'import sys; print(sys.executable)'
        if ($LASTEXITCODE -ne 0) { throw 'Cannot locate Python 3; pass -PythonHome.' }
        $PythonHome = Split-Path $pythonExe
    }
    $PythonHome = (Resolve-Path -LiteralPath $PythonHome).Path
    if (-not (Test-Path -LiteralPath (Join-Path $PythonHome 'python.exe'))) { throw 'PythonHome must contain python.exe and its standard library.' }
}

function Copy-Directory([string]$Source, [string]$Destination) {
    $null = New-Item -ItemType Directory -Path $Destination -Force
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse
    }
}

$Exe = (Resolve-Path -LiteralPath $Exe).Path
$CtlExe = (Resolve-Path -LiteralPath $CtlExe).Path
$PowerShellHome = (Resolve-Path -LiteralPath $PowerShellHome).Path
if (-not (Test-Path -LiteralPath (Join-Path $PowerShellHome 'pwsh.exe'))) {
    throw 'PowerShellHome must contain a portable Windows x64 PowerShell 7 installation.'
}
$needsRuntime = $false
foreach ($binary in @($Exe, $CtlExe)) {
    $runtimeConfig = [IO.Path]::ChangeExtension($binary, '.runtimeconfig.json')
    if (Test-Path -LiteralPath $runtimeConfig) {
        $options = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions
        if ($options.framework -or $options.frameworks) { $needsRuntime = $true }
    }
}
if ($needsRuntime) {
    $DotnetHome = (Resolve-Path -LiteralPath $DotnetHome).Path
    foreach ($part in @('dotnet.exe','host','shared')) {
        if (-not (Test-Path -LiteralPath (Join-Path $DotnetHome $part))) {
            throw "Framework-dependent binaries require a Windows .NET installation: missing $DotnetHome/$part"
        }
    }
}
$sandboxCli = $null
$viewerCommand = $null
$sandbox = if (-not $PrepareOnly) {
    # Store-serviced clients expose wsb.exe; the legacy launcher can exit successfully
    # without starting a guest on these systems. Keep it for older Windows hosts.
    $sandboxCli = Get-Command wsb.exe -ErrorAction SilentlyContinue
    $command = if ($sandboxCli) { $sandboxCli } else { Get-Command WindowsSandbox.exe -ErrorAction SilentlyContinue }
    if (-not $command) {
        throw 'Enable Windows Sandbox (Containers-DisposableClientVM) in Windows Features, restart if required, and finish installing the Sandbox client from Start/Store. Use -PrepareOnly to stage without the client.'
    }
    if ($Background) {
        if (-not $sandboxCli) { throw 'Background UI tests require the modern Windows Sandbox client (wsb.exe).' }
        $viewerCommand = Get-Command WindowsSandboxRemoteSession.exe -ErrorAction SilentlyContinue
        if (-not $viewerCommand) { throw 'Background UI tests require the WindowsSandboxRemoteSession.exe execution alias.' }
    }
    $command.Source
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw "Output directory already exists: $OutputDirectory" }
$sources = @((Split-Path $Exe), (Split-Path $CtlExe), $PowerShellHome, $PSScriptRoot)
foreach ($part in @('tests/ui','tests/fixtures','tests/conformance')) { $sources += Join-Path $repo $part }
if ($definition.Web) { $sources += $WebView2Home }
if ($requiresPython) { $sources += $PythonHome }
if ($needsRuntime) { $sources += $DotnetHome }
foreach ($source in $sources) {
    $prefix = [IO.Path]::GetFullPath($source).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if ($OutputDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory must be outside staged source folders: $source"
    }
}

$mutex = [Threading.Mutex]::new($false, 'Local\agwinterm-sandbox-ui')
$locked = $false
$backgroundViewer = $null
try {
    if (-not $PrepareOnly) {
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw 'Another Sandbox UI run is active.' }
        if (Get-Process -Name WindowsSandbox,WindowsSandboxClient,WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) {
            throw 'Close the existing Windows Sandbox before starting a UI run.'
        }
        if ($sandboxCli) {
            $running = & $sandbox list --raw
            if ($LASTEXITCODE -ne 0) { throw 'Cannot list running Windows Sandbox environments.' }
            if (($running | ConvertFrom-Json).WindowsSandboxEnvironments.Count -gt 0) {
                throw 'Close the existing Windows Sandbox before starting a UI run.'
            }
        }
    }
    $payload = Join-Path $OutputDirectory 'input'
    $results = Join-Path $OutputDirectory 'results'
    Write-Host "Preparing $label payload in $OutputDirectory"
    $null = New-Item -ItemType Directory -Path $payload, $results
    Copy-Directory (Split-Path $Exe) (Join-Path $payload 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64')
    $ctlDirectory = Join-Path $payload 'src/Agwinterm.Ctl/bin/Release/net10.0-windows'
    Copy-Directory (Split-Path $CtlExe) $ctlDirectory
    if ((Split-Path $CtlExe -Leaf) -ne 'agwintermctl.exe') {
        Copy-Item -LiteralPath $CtlExe -Destination (Join-Path $ctlDirectory 'agwintermctl.exe')
    }
    Copy-Directory $PSScriptRoot (Join-Path $payload 'tests/integration')
    foreach ($part in @('tests/ui','tests/fixtures','tests/conformance')) {
        $source = Join-Path $repo $part
        if (Test-Path -LiteralPath $source) { Copy-Directory $source (Join-Path $payload $part) }
    }
    if ($definition.Path.StartsWith('tools/')) {
        $null = New-Item -ItemType Directory -Path (Join-Path $payload 'tools')
        Copy-Item -LiteralPath (Join-Path $repo $definition.Path) -Destination (Join-Path $payload $definition.Path)
    }
    if ($inputFiles.Count) {
        $null = New-Item -ItemType Directory -Path (Join-Path $payload 'inputs')
        foreach ($key in $inputFiles.Keys) { Copy-Item -LiteralPath $inputFiles[$key] -Destination (Join-Path $payload "inputs/$key") }
    }
    if ($definition.Web) { Copy-Directory $WebView2Home (Join-Path $payload 'webview2') }
    if ($requiresPython) {
        $pythonDestination = Join-Path $payload 'python'
        $null = New-Item -ItemType Directory -Path $pythonDestination
        foreach ($file in Get-ChildItem -LiteralPath $PythonHome -File | Where-Object { $_.Extension -in @('.exe','.dll','.zip','._pth') }) {
            Copy-Item -LiteralPath $file.FullName -Destination $pythonDestination
        }
        if (Test-Path -LiteralPath (Join-Path $PythonHome 'DLLs')) { Copy-Directory (Join-Path $PythonHome 'DLLs') (Join-Path $pythonDestination 'DLLs') }
        $library = Join-Path $pythonDestination 'Lib'
        $null = New-Item -ItemType Directory -Path $library
        if (Test-Path -LiteralPath (Join-Path $PythonHome 'Lib')) {
            foreach ($item in Get-ChildItem -LiteralPath (Join-Path $PythonHome 'Lib') | Where-Object { $_.Name -notin @('site-packages','__pycache__') }) {
                Copy-Item -LiteralPath $item.FullName -Destination $library -Recurse
            }
        }
    }
    Copy-Directory $PowerShellHome (Join-Path $payload 'powershell')
    if ($needsRuntime) {
        $runtimeDirectory = Join-Path $payload 'dotnet'
        $null = New-Item -ItemType Directory -Path $runtimeDirectory
        Copy-Item -LiteralPath (Join-Path $DotnetHome 'dotnet.exe') -Destination $runtimeDirectory
        foreach ($part in @('host','shared')) {
            Copy-Directory (Join-Path $DotnetHome $part) (Join-Path $runtimeDirectory $part)
        }
    }
    $parameters = @{}
    foreach ($key in $TestParameters.Keys) { if ($key -in $definition.Options) { $parameters[$key] = $TestParameters[$key] } }
    @{test=$Test; parameters=$parameters; inputs=@($inputFiles.Keys); outputs=@($definition.Outputs | Where-Object { $_ }); suite=$Suite; exeName=(Split-Path $Exe -Leaf)} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $payload 'run.json') -Encoding utf8
    $inputXml = [Security.SecurityElement]::Escape($payload)
    $resultsXml = [Security.SecurityElement]::Escape($results)
    $vgpu = if ($DisableVGpu) { 'Disable' } else { 'Enable' }
    $configuration = Join-Path $OutputDirectory 'ui-tests.wsb'
    @"
<Configuration>
  <MemoryInMB>$MemoryInMB</MemoryInMB>
  <vGPU>$vgpu</vGPU>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputXml</HostFolder><SandboxFolder>C:\AgwintermInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$resultsXml</HostFolder><SandboxFolder>C:\AgwintermResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\AgwintermInput\tests\integration\sandbox-ui-guest.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $configuration -Encoding utf8
    Write-Host "Sandbox configuration: $configuration"
    Write-Host "Results: $results"
    if ($PrepareOnly) { return }

    $closeInstruction = 'Close Windows Sandbox when finished inspecting the results.'
    if ($sandboxCli) {
        $started = & $sandbox start --config (Get-Content -LiteralPath $configuration -Raw) --raw
        if ($LASTEXITCODE -ne 0) { throw 'Windows Sandbox CLI could not start the UI environment.' }
        $sandboxId = ($started | ConvertFrom-Json).Id
        if (-not $sandboxId) { throw 'Windows Sandbox CLI did not return an environment id.' }
        $closeInstruction = "Stop this Windows Sandbox environment with: wsb stop --id $sandboxId"
        Write-Host $closeInstruction
        if ($Background) {
            try {
                $backgroundViewer = & "$PSScriptRoot/sandbox-ui-window.ps1" -Exe $viewerCommand.Source -SandboxId $sandboxId
            } catch {
                throw "Cannot connect background Windows Sandbox $sandboxId. $_ $closeInstruction"
            }
        } else {
            & $sandbox connect --id $sandboxId --raw
            if ($LASTEXITCODE -ne 0) { throw "Cannot open Windows Sandbox $sandboxId. $closeInstruction" }
        }
    } else {
        Start-Process -FilePath $sandbox -ArgumentList "`"$configuration`""
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $resultFile = Join-Path $results 'result.json'
    while (-not (Test-Path -LiteralPath $resultFile)) {
        if ($backgroundViewer -and $backgroundViewer.HasExited) {
            throw "Background Sandbox viewer exited with code $($backgroundViewer.ExitCode). $closeInstruction Evidence: $results"
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Sandbox did not return a result within $TimeoutSeconds seconds. It may still be running. $closeInstruction Evidence: $results"
        }
        Start-Sleep -Milliseconds 500
    }
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    if ($result.exitCode -notin @(0,1,2)) { throw "Invalid Sandbox result: $resultFile" }
    if ($result.exitCode -eq 0) {
        foreach ($key in $definition.Outputs) {
            if (-not $TestParameters.ContainsKey($key)) { continue }
            $source = Join-Path $results "artifacts/$Test-$key.json"
            if (-not (Test-Path -LiteralPath $source)) { throw "Missing $Test output: $source" }
            Copy-Item -LiteralPath $source -Destination ([IO.Path]::GetFullPath($TestParameters[$key])) -Force
        }
    }
    Write-Host "Sandbox $label completed with exit code $($result.exitCode). $($result.error)"
    Write-Host $closeInstruction
    exit $result.exitCode
} finally {
    if ($backgroundViewer) { $backgroundViewer.Dispose() }
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
