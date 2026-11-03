# Host-free fixtures: every dispatched test is replaced by an argument probe.
# No real application, viewer or UI entry point is launched by these checks.
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('agwinterm-ui-routing-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
$checks = 0
function Check([string]$Name, [bool]$Ok) {
    $script:checks++
    if (-not $Ok) { throw "FAIL $Name" }
    Write-Host "PASS $Name"
}
function Run-Probe([string]$Script, [string[]]$Arguments, [hashtable]$Environment) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile','-File',$Script) + $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) { $process.Kill($true); throw 'Probe timed out' }
        $output = $outputTask.GetAwaiter().GetResult() + $errorTask.GetAwaiter().GetResult()
        @{code=$process.ExitCode; output=$output}
    } finally { $process.Dispose() }
}
try {
    $largeProbe = Join-Path $temporary 'large-output.ps1'
    '[Console]::Out.WriteLine((''a'' * 100000)); [Console]::Error.WriteLine((''b'' * 100000)); exit 0' | Set-Content $largeProbe
    $large = Run-Probe $largeProbe @() @{}
    Check 'simultaneous large stdout and stderr do not block probe completion' ($large.code -eq 0 -and $large.output.Length -ge 200000)
    $gateSource = Get-Content (Join-Path $root 'tests/WebViewHost.Gate/Program.cs') -Raw
    Check 'C# gate routes before creating its first window' ($gateSource.IndexOf('if (RouteLocalRun()') -lt $gateSource.IndexOf('_window = CreateWindowExW'))
    $definitions = Import-PowerShellDataFile "$PSScriptRoot/sandbox-ui-tests.psd1"
    $entryPaths = @($definitions.Values | ForEach-Object { $_.Path })
    foreach ($directory in @('tests/integration','tools')) {
        foreach ($file in Get-ChildItem (Join-Path $root $directory) -Filter '*.ps1') {
            if ($file.Name -like '*.tests.ps1' -or $file.Name -like '*-cases.ps1' -or $file.Name -eq 'web-overlay-parity-content.ps1') { continue }
            $source = Get-Content -LiteralPath $file.FullName -Raw
            if ($source -match 'Start-Process\s+(?:-FilePath\s+)?\$[Ee]xe' -and $source -match 'Agwinterm.Win32') {
                Check "$($file.Name) is registered or a host-free extraction check" (("$directory/$($file.Name)" -in $entryPaths) -or $file.Name -eq 'portable-tools.ps1')
            }
        }
    }
    $integration = Join-Path $temporary 'tests/integration'
    $null = New-Item -ItemType Directory -Path $integration -Force
    Copy-Item "$PSScriptRoot/sandbox-ui-tests.psd1", "$PSScriptRoot/sandbox-ui-dispatch.ps1" -Destination $integration
    $guest = @{USERNAME='WDAGUtilityAccount'; AGWINTERM_SANDBOX_UI='1'; GITHUB_ACTIONS=''; CI='true'; SANDBOX_DISPATCH_PROBE=(Join-Path $temporary 'probe.json')}
    foreach ($name in $definitions.Keys) {
        $definition = $definitions[$name]
        $source = Get-Content -LiteralPath (Join-Path $root $definition.Path) -Raw
        $guard = $source.IndexOf("-Test $name -Parameters " + '$PSBoundParameters')
        $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$null, [ref]$null)
        Check "$name fails closed and redirects before any setup" ($guard -ge 0 -and $ast.EndBlock.Statements[0].Extent.Text -eq '$ErrorActionPreference = ''Stop''' -and $ast.EndBlock.Statements[1].Extent.Text.Contains('sandbox-ui-entry.ps1'))
        if ($definition.Wsl) { continue }
        $path = Join-Path $temporary $definition.Path
        $null = New-Item -ItemType Directory -Path (Split-Path $path) -Force
        @'
param([string]$Exe,[string]$Ctl,[string]$GateExe,[switch]$Strict,[string]$Suite,[int]$Dpi,[string]$Only,[int]$Cycles,[int]$Runs,[string]$Installer,[string]$Portable,[string]$Spec,[string]$Output,[switch]$Cookbook,[switch]$CookbookOnly,[switch]$NavigationOnly,[switch]$ReviewOnly)
@{exe=$Exe;ctl=$Ctl;strict=[bool]$Strict;suite=$Suite;dpi=$Dpi;only=$Only;cookbook=[bool]$Cookbook;output=$Output;spec=$Spec} | ConvertTo-Json | Set-Content -LiteralPath $env:SANDBOX_DISPATCH_PROBE
exit 0
'@ | Set-Content -LiteralPath $path
        $parameters = @{}
        if ($name -eq 'ProgramOverlay') { $parameters.Dpi=144 }
        if ($name -eq 'RestoreRoundtrip') { $parameters.Only='replay ; $(not-code) — текст' }
        if ($name -eq 'WebOverlayHost') { $parameters.Cookbook=$true }
        $inputs = @($definition.Inputs | Where-Object { $_ })
        if ($name -eq 'Conformance') { $inputs += 'Spec' }
        @{test=$name; parameters=$parameters; inputs=$inputs; outputs=@($definition.Outputs | Where-Object { $_ }); suite='Navigation'; exeName='Agwinterm.Win32.exe'} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $temporary 'run.json')
        $result = Run-Probe (Join-Path $integration 'sandbox-ui-dispatch.ps1') @('-WorkDirectory',$temporary) $guest
        Check "$name dispatch succeeds" ($result.code -eq 0)
        $probe = Get-Content -LiteralPath $guest.SANDBOX_DISPATCH_PROBE -Raw | ConvertFrom-Json
        Check "$name gets staged binaries and strictness" ((-not $definition.Exe -or $probe.exe.StartsWith($temporary)) -and (-not $definition.Ctl -or $probe.ctl.StartsWith($temporary)) -and (-not $definition.Strict -or $probe.strict))
        if ($name -eq 'ProgramOverlay') { Check 'DPI survives JSON dispatch' ($probe.dpi -eq 144) }
        if ($name -eq 'RestoreRoundtrip') { Check 'strings are data, including quotes and shell syntax' ($probe.only -ceq $parameters.Only) }
        if ($name -eq 'WebOverlayHost') { Check 'switches survive JSON dispatch' $probe.cookbook }
        if ($name -eq 'Conformance') { Check 'custom contract uses the guest input path' ($probe.spec -eq (Join-Path $temporary 'inputs/Spec')) }
        if ($definition.Outputs) { Check "$name writes reports inside guest artifacts" ($probe.output -eq (Join-Path $temporary "artifacts/$name-Output.json")) }
    }
    @{test='NotAnEntryPoint'; parameters=@{}} | ConvertTo-Json | Set-Content (Join-Path $temporary 'run.json')
    Check 'unknown dispatch refuses to run a script' ((Run-Probe (Join-Path $integration 'sandbox-ui-dispatch.ps1') @('-WorkDirectory',$temporary) $guest).code -ne 0)
    # Exercise the actual host-generated plan, not only hand-built dispatch data.
    $binaries = Join-Path $temporary 'binaries'
    $fakePowerShell = Join-Path $temporary 'powershell'
    $null = New-Item -ItemType Directory -Path $binaries,$fakePowerShell
    foreach ($file in @('app.exe','ctl.exe')) { 'fixture' | Set-Content (Join-Path $binaries $file) }
    'fixture' | Set-Content (Join-Path $fakePowerShell 'pwsh.exe')
    foreach ($testName in @('ControlCli','WebStartupBaseline','Conformance')) {
        $output = Join-Path $temporary "host-plan-$testName"
        $parameters = @{}
        if ($testName -eq 'WebStartupBaseline') { $parameters = @{Installer=(Join-Path $binaries 'app.exe');Portable=(Join-Path $binaries 'ctl.exe')} }
        if ($testName -eq 'Conformance') { $parameters.Spec = Join-Path $root 'tests/conformance/control-api.json' }
        & "$PSScriptRoot/sandbox-ui.ps1" -Test $testName -TestParameters $parameters -Exe (Join-Path $binaries 'app.exe') -CtlExe (Join-Path $binaries 'ctl.exe') -PowerShellHome $fakePowerShell -OutputDirectory $output -PrepareOnly
        $payload = Join-Path $output 'input'
        Check "$testName stages an alternate app filename for canonical-path runners" ((Get-FileHash (Join-Path $payload 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe')).Hash -eq (Get-FileHash (Join-Path $binaries 'app.exe')).Hash)
        $path = Join-Path $payload $definitions[$testName].Path
        @'
param([string]$Exe,[string]$Ctl,[switch]$Strict,[string]$Spec,[string]$Output,[int]$Runs,[string]$Installer,[string]$Portable)
@{exe=$Exe;spec=$Spec;output=$Output} | ConvertTo-Json | Set-Content -LiteralPath $env:SANDBOX_DISPATCH_PROBE
exit 0
'@ | Set-Content -LiteralPath $path
        $result = Run-Probe (Join-Path $payload 'tests/integration/sandbox-ui-dispatch.ps1') @('-WorkDirectory',$payload) $guest
        Check "$testName host-generated plan dispatches successfully" ($result.code -eq 0)
        $probe = Get-Content -LiteralPath $guest.SANDBOX_DISPATCH_PROBE -Raw | ConvertFrom-Json
        if ($testName -eq 'Conformance') { Check 'host stages the exact custom contract' ((Get-FileHash $probe.spec).Hash -eq (Get-FileHash $parameters.Spec).Hash) }
    }
    $entry = Join-Path $temporary 'entry'
    $null = New-Item -ItemType Directory -Path $entry
    Copy-Item "$PSScriptRoot/sandbox-ui-entry.ps1" -Destination $entry
    @'
param([string]$Test,[switch]$Background,[hashtable]$TestParameters,[string]$Exe,[string]$CtlExe)
if ($env:SANDBOX_PROVISION_FAIL -eq '1') { throw 'simulated provisioning failure' }
@{test=$Test;background=[bool]$Background;parameters=$TestParameters;exe=$Exe} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:SANDBOX_DISPATCH_PROBE
exit 7
'@ | Set-Content (Join-Path $entry 'sandbox-ui.ps1')
    @'
param([string]$Exe,[switch]$Cookbook)
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WebOverlayHost -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
'direct-body' | Set-Content -LiteralPath $env:SANDBOX_DISPATCH_PROBE
exit 0
'@ | Set-Content (Join-Path $entry 'target.ps1')
    foreach ($case in @(
        @{name='local'; user='developer'; marker=''; github=''; ci=''; routed=$true},
        @{name='CI flag alone'; user='developer'; marker=''; github=''; ci='true'; routed=$true},
        @{name='guest marker alone'; user='developer'; marker='1'; github=''; ci=''; routed=$true},
        @{name='guest'; user='WDAGUtilityAccount'; marker='1'; github=''; ci='true'; routed=$false},
        @{name='GitHub CI'; user='runner'; marker=''; github='true'; ci='true'; routed=$false}
    )) {
        $environment = @{AGWINTERM_UI_BACKEND='Sandbox';USERNAME=$case.user; AGWINTERM_SANDBOX_UI=$case.marker; GITHUB_ACTIONS=$case.github; CI=$case.ci; SANDBOX_DISPATCH_PROBE=(Join-Path $temporary 'entry-probe.json')}
        $result = Run-Probe (Join-Path $entry 'target.ps1') @('-Exe','C:\build with spaces\app.exe','-Cookbook') $environment
        Check "$($case.name) chooses the correct execution environment" ($result.code -eq $(if ($case.routed) {7} else {0}))
        if ($case.routed) {
            $probe = Get-Content -LiteralPath $environment.SANDBOX_DISPATCH_PROBE -Raw | ConvertFrom-Json
            Check "$($case.name) forwards parameters and forces Background" ($probe.background -and $probe.parameters.Cookbook -and $probe.exe -eq 'C:\build with spaces\app.exe')
        }
    }
    $failureEnvironment = @{AGWINTERM_UI_BACKEND='Sandbox';USERNAME='developer'; AGWINTERM_SANDBOX_UI=''; GITHUB_ACTIONS=''; CI=''; SANDBOX_PROVISION_FAIL='1'; SANDBOX_DISPATCH_PROBE=(Join-Path $temporary 'failure-probe.json')}
    $result = Run-Probe (Join-Path $entry 'target.ps1') @() $failureEnvironment
    Check 'provisioning failure never falls through to the host UI body' ($result.code -eq 1 -and -not (Test-Path $failureEnvironment.SANDBOX_DISPATCH_PROBE))
    $local = @{AGWINTERM_UI_BACKEND='Sandbox';USERNAME='developer'; AGWINTERM_SANDBOX_UI=''; GITHUB_ACTIONS=''; CI=''; SANDBOX_DISPATCH_PROBE=(Join-Path $temporary 'hud-default.json')}
    $null = Run-Probe (Join-Path $entry 'sandbox-ui-entry.ps1') @('-Test','Hud') $local
    $probe = Get-Content -LiteralPath $local.SANDBOX_DISPATCH_PROBE -Raw | ConvertFrom-Json
    Check 'direct HUD entry retains its Hud default instead of wrapper Navigation default' ($probe.parameters.Suite -eq 'Hud')
    @'
param([string]$Test,[hashtable]$TestParameters,[string]$Exe,[string]$CtlExe)
@{backend='HyperV';test=$Test;parameters=$TestParameters;exe=$Exe} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:SANDBOX_DISPATCH_PROBE
exit 8
'@ | Set-Content (Join-Path $entry 'hyperv-ui.ps1')
    $local.AGWINTERM_UI_BACKEND = ''
    $result = Run-Probe (Join-Path $entry 'target.ps1') @() $local
    Check 'local Windows UI defaults to Hyper-V without environment setup' ($result.code -eq 8)
    $local.AGWINTERM_UI_BACKEND = 'HyperV'
    $result = Run-Probe (Join-Path $entry 'target.ps1') @('-Exe','C:\worktree B\app.exe','-Cookbook') $local
    $probe = Get-Content -LiteralPath $local.SANDBOX_DISPATCH_PROBE -Raw | ConvertFrom-Json
    Check 'Hyper-V selection preserves parameters and the child exit code' ($result.code -eq 8 -and $probe.backend -eq 'HyperV' -and $probe.parameters.Cookbook -and $probe.exe -eq 'C:\worktree B\app.exe')
    $local.AGWINTERM_UI_BACKEND = 'NotABackend'
    $result = Run-Probe (Join-Path $entry 'target.ps1') @() $local
    Check 'unknown backend fails closed before host UI' ($result.code -eq 1 -and $result.output -match 'Unknown UI backend')
    Write-Host "$checks routing checks passed"
} finally {
    $full = [IO.Path]::GetFullPath($temporary)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture cleanup path' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
exit 0
