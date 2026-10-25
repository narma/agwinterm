# C2 verification

Date: 2026-10-25. Prerequisites: C0 `5970e69`, C1 `0997759`.

## Executed checks

- Release solution build succeeded.
- Full managed suite: 437 Core and 1333 Pty passed, none skipped (`artifacts/c2-regression.log`). Targeted overlay/CLI suite also passed: 8 Core and 205 Pty before the additional rejection cases were included in the full suite.
- `tests/integration/program-overlay-controls.ps1 -Dpi 96` and `-Dpi 144`: passed, using disposable app identity, pipe, profiles and data directory.
- `tests/integration/wsl-control.ps1 -NativeHooks`: passed on Ubuntu x64 with disposable Linux HOME, including C0/C1 checks and C2 explicit cwd with spaces, background selection preservation, hide and follow.
- `tests/integration/win32-control.ps1 -Strict -Exe <Release app>`: all passed (`artifacts/c2-win32-regression-stable.log`), including concurrent overlay block results, resize/exit races, slot ownership, copy/text, pane promotion and native mouse-coordinate geometry.
- `openspec validate program-overlay-controls --strict`: passed; main-spec sync verified before archive.
- Native PrintWindow capture `artifacts/c2-overlay-144.png` inspected: centered floating panel, requested dark-blue background, readable terminal text and unaffected underlying terminal. Capture uses the calling thread's per-monitor DPI context to avoid clipping a physical-pixel bitmap with virtualized window dimensions.

## Normative scenario audit

| Requirement | Evidence |
| --- | --- |
| Hide/show/toggle preserve lifetime | C2 Windows acceptance retains IDs, buffers and exited --wait panels; a hidden --block program remains pending until its own exit 9 |
| Hidden ordinary input and explicit-ID access | Actual active input lands in the underlying shell; explicit hidden overlay input/text retains its own buffer and Windows cwd |
| Owner stability and removal | Hidden pane overlay follows swap, show uses its new side, owner removal disposes it, removed ID refuses; session teardown removes session overlay |
| Effective versus requested visibility | Background open, hidden selected session, shown pane and session-cover occlusion checked through overlayDetails |
| Independent geometry | Core tests cover mixed DIP/percent at 96/144/192 DPI, partial changes, percent reset, full reset and zero/tiny bounds; native runs at 96/144 verify pixel conversion, small-window clamp and subsequent growth |
| Shared draw/grid/hit geometry | Shared geometry helper drives layout/readback; hidden session overlays regrid on resize; full existing native mouse and zoom regression passed |
| Cwd/follow/color | Windows cwd with spaces read from hidden program; invalid cwd preserves old program; actual WSL cwd/follow checks; color parsing/refusal tests and native visual inspection |
| Legacy results and teardown | Existing full Win32 suite plus hidden --wait/window-wide result and --block tests; original pane resize/refusal wording retained |
| Events | Actual no-op show/hide sequence emits one owned transition; resize emits only changed requested geometry through C1 |
| Validation before mutation | CLI invalid dimension/color exits 2 without connection; raw invalid replacement leaves fake slot intact; typed/unknown/duplicate/incompatible arguments covered |

## Resolved findings

- Preserve the legacy bare-resize full-region reset while partial C2 resize retains omitted axes.
- Preserve the existing no-overlay resize refusal text; a general no-overlay response broke the resize/exit race contract test.
- Readback must agree with hidden/background terminal grids too, so regrid occupied session overlays independently of visibility.
- The pre-existing injected Ctrl+Shift+R regression case intermittently lost modifier state. Its private profile now binds F10 to the same native reopen action and delivers it synchronously to the test window. The test still verifies original session ID/context restoration through keyboard dispatch. User ConPTY edits in that file are excluded from this commit.
- An invalid-argument test previously sent size-percent on close while asserting missing-target wording. It now supplies size only to resize, and a separate test explicitly verifies incompatible close arguments.

## Limits

Physical monitor migration was not available; pure geometry covers multiple DPIs and native runs use the existing DPI override seam. WSL runtime coverage is Ubuntu x64. Overlays remain ephemeral, pane slots remain full-pane, and no release or live installation was performed. Logs and screenshots are local ignored artifacts; test scripts and assertions are committed.
