# C3 verification

Verified locally on 2026-10-25 against the Release output in bin/x64/Release.

- Release solution build passed. The full rebuild reports only the two pre-existing xUnit analyzer warnings; the incremental native build has zero warnings.
- Full managed regression: **455 Core + 1350 Pty passed** (`artifacts/c3-regression.log`). Includes form/schema bounds, typed answers, exact IDs, first-completion retention, CLI refusal/cancellation/transport uncertainty, raw host routing and existing picker behavior.
- Native ask acceptance: **30 checks passed** (`artifacts/c3-ask-ui.log`), including text/multiline/single/multiple/other values, Back/Next retention, logical Latin/Cyrillic Alt characters, all cancellation reasons, shared modal guards, no terminal input leakage, actual CLI no-block/result/cancel, active-window changes and owner close.
- Native picker acceptance: **39 checks passed** (`artifacts/c3-picker-ui.log`), including original behavior plus duplicate-label exact selected ID, status marker rendering and accessible selected-row text.
- Direct Windows MSAA checks passed for descriptive edit/other names, multi-selection state and picker status. The existing process-wide UIA proxy limitation remains: a .NET UIA client did not expose the standard control patterns here. No claim of Narrator/NVDA certification or process-wide UIA repair.
- Native compact layout checks passed with 16 fields and 8 buttons at 96 and 192 DPI. Default desktop rendering is 144 DPI. Full uncropped screenshots were inspected: `native-ask-multi.png`, `native-ask-192-compact.png` and `native-picker-status.png`. Original captures remain in the ignored local `.revmux/ask-ui-*` and `.revmux/picker-ui-*` runs. Native fixture capture now enters/restores per-monitor DPI awareness to avoid clipped evidence.
- Linux bridge: x64 and ARM64 musl Release builds passed (`artifacts/c3-wsl-build.log`); **12 Linux hook tests passed** (`artifacts/c3-linux-tests.log`).
- Full Ubuntu WSL integration passed (`artifacts/c3-wsl.log`). Actual terminal Ctrl+C returns 130 and releases the known ask slot; no-block returns an ID usable by the Windows CLI. Existing C0/C1/C2 path, hook, watch/wait, cancellation and restart checks still passed.
- Full existing Win32 control integration passed (`artifacts/c3-win32-regression.log`), including overlays, background targets, history/reopen, input and geometry.
- Strict OpenSpec validation and `git diff --check` passed.

## Contract audit

Picker requirements map to PickerPresentationTests, AskCliTests.PickMetadataSurvivesCliSerialization and picker-ui-cases. Form parsing/types/buttons map to NativeAskTests and AskDispatchTests. CLI exact-ID outcomes and uncertain transport map to AskCliTests and actual Windows/WSL runs. Native names/state, cancellation, ownership and geometry map to ask-ui-cases and the direct MSAA probe. Retention caps and first-completion races are host-free registry tests.

The owner-close test exposed Windows destroying owned windows before the owner's WM_DESTROY. All library-window close paths now settle asks before DestroyWindow, preserving owner-closed rather than accidentally reporting closed. Initial test-only failures also identified cross-process SetWindowText/GetWindowText restrictions and a PowerShell property-count expression; the fixture now uses synchronous owned HWND messages and explicit collection counting.

## Limits

No release, installed runtime or live user configuration changed. GUI tests use private app identities and owned process jobs, no global input or clipboard changes. DPI acceptance includes synthetic WM_DPICHANGED geometry, not physically moving a window between monitors. Keyboard acceptance injects logical WM_CHAR/WM_SYSCHAR into owned controls, not a global keyboard-layout switch. WSL runtime coverage is Ubuntu; ARM64 is build coverage. Extremely long native labels can be visually clipped while remaining available through native accessibility.

Historical local acceptance record: the exact tested application commit was not recorded.
Raw logs and screenshots remain local and are intentionally not versioned; the paths above
identify original runs, not a permanent archive available after cloning. These checks were
not rerun as part of repository cleanup.
