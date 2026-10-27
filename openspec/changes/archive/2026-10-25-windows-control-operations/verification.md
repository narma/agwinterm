# C7 verification

Verified locally on 2026-10-25 without changing the live installation or monitor settings.

- Release solution build passed; two existing xUnit analyzer warnings (`artifacts/c7-build.log`). Always build the solution's x64 native output used by integration fixtures.
- Full managed regression passed **467 Core + 1422 Pty** (`artifacts/c7-regression.log`), including physical negative-origin/oversized placement and strict CLI/raw argument validation.
- Native window acceptance passed **20 checks** (`artifacts/c7-windows.log`, `.revmux/windows-ui-20261025T233659-9188ec`). Covers minimized creation preserving actual foreground HWND and library active identity, nonactivating restore, unknown/quick/closed targets, actual monitor geometry, refusal without movement, minimized placement, fullscreen idempotence, navigation/wrap/one-open refusal, Windows Reveal and actual CLI calls.
- Full Ubuntu WSL integration passed (`artifacts/c7-wsl.log`). Added path-only Reveal validates the converted existing directory with spaces, then repeats while a Windows session is selected to prove it uses the target WSL profile and does not select its owner. Existing C0–C6 checks remained green.
- The UI Reveal and API share directory validation and Explorer launch; automated acceptance uses path-only to avoid opening Explorer in the user's desktop. Explorer launch success describes process acceptance, not proof of a visible selected item.

## Contract audit and limits

Each native-window-control scenario maps to NativeWindowRequestTests, WindowPlacementTests or the owned Windows fixture; WSL Reveal has real distribution/user conversion coverage. Monitor enumeration/placement ran on the available display. Negative work-area and oversized geometry are pure tests; no claim is made of physically testing hotplug or mixed-DPI movement across multiple monitors. Native replies read actual OS state rather than echoing requested coordinates.

An initial native test caught an unsupported event-kind name after creation. It now emits the existing C1 tree invalidation with operation info, avoiding a post-mutation error. Existing UI fullscreen entry points retain their default behavior; new control paths use the nonactivating variant. The broader UIA/cursor/hover limitations recorded in C6 remain outside this change.

Historical local acceptance record: the exact tested application commit was not recorded.
Raw logs and screenshots remain local and are intentionally not versioned; the paths above
identify original runs, not a permanent archive available after cloning. These checks were
not rerun as part of repository cleanup.
