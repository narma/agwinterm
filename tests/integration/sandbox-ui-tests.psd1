@{
    Hud = @{ Path='tests/integration/hud-ui.ps1'; Exe=$true; Strict=$true; Options=@('Suite') }
    Win32Control = @{ Path='tests/integration/win32-control.ps1'; Exe=$true; Strict=$true; Options=@() }
    Conformance = @{ Path='tests/conformance/conformance.ps1'; Exe=$true; Strict=$true; Options=@(); OptionalInputs=@('Spec') }
    RestoreRoundtrip = @{ Path='tests/integration/restore-roundtrip.ps1'; Exe=$true; Strict=$true; Options=@('Only') }
    ControlCli = @{ Path='tests/integration/control-cli-reliability.ps1'; Options=@() }
    ControlEvents = @{ Path='tests/integration/control-events-and-wait.ps1'; Options=@() }
    SplitRetention = @{ Path='tests/integration/split-retention.ps1'; Options=@() }
    ProgramOverlay = @{ Path='tests/integration/program-overlay-controls.ps1'; Options=@('Dpi') }
    WebOverlayHost = @{ Path='tests/integration/web-overlay-host.ps1'; Exe=$true; Ctl=$true; Web=$true; Options=@('Cookbook','CookbookOnly','NavigationOnly') }
    WebOverlayParity = @{ Path='tests/integration/web-overlay-parity.ps1'; Exe=$true; Ctl=$true; Web=$true; Python=$true; Options=@('ReviewOnly') }
    WebViewGate = @{ Path='tests/integration/webview-gate.ps1'; Gate=$true; Web=$true; Options=@() }
    ProfileMemory = @{ Path='tools/profile-memory.ps1'; Exe=$true; Ctl=$true; Options=@('Cycles') }
    WebPageMeasurements = @{ Path='tools/web-page-measurements.ps1'; Exe=$true; Ctl=$true; Web=$true; Options=@(); Outputs=@('Output') }
    WslControl = @{ Path='tests/integration/wsl-control.ps1'; Wsl=$true; Options=@('Distro','NativeHooks','LinuxUser','NoPython','Portable') }
    WslBenchmark = @{ Path='tests/integration/wsl-hook-bench.ps1'; Wsl=$true; Options=@() }
    WslZmxDependency = @{ Path='tests/integration/wsl-zmx-dependency.ps1'; Wsl=$true; Options=@('Distro','BuildDirectory') }
    WslZmxSurvival = @{ Path='tests/integration/wsl-zmx-survival.ps1'; Wsl=$true; Options=@('Distro','BuildDirectory','DiagnosticKeepalive','LifetimeGuard','ProfileLive','LifecycleControls','PeerDistro','PeerUser','PeerExecutable') }
    WslAgtermRemote = @{ Path='tests/integration/wsl-agterm-remote.ps1'; Wsl=$true; Options=@('Distro','LinuxUser','Destination','AgentSocket','OriginSocket','Bridge','TerminalFixture','FaultBridge','PassiveOnly') }
    WslOriginExport = @{ Path='tests/integration/wsl-origin-export.ps1'; Wsl=$true; Options=@('Distro','LinuxUser','BuildDirectory','Destination','AgentSocket','MacAgentSocket','MacApp','PeerHost','ManualQuestion','ManualJob') }
    WslZmxIdentity = @{ Path='tests/integration/wsl-zmx-identity.ps1'; Wsl=$true; Options=@('Distro','BuildDirectory') }
    WebStartupBaseline = @{ Path='tools/web-startup-baseline.ps1'; Exe=$true; Options=@('Runs'); OptionalInputs=@('Installer','Portable'); Outputs=@('Output') }
}
