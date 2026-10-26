# C6 verification

Verified locally on 2026-10-25. No release or live installation was performed.

- Release solution build passed (`artifacts/c6-build.log`).
- Managed regression passed: **464 Core and 1408 Pty** (`artifacts/c6-regression.log`).
- Native working-set acceptance passed **24 checks**, including exact split IDs after ordered batch movement, duplicate alias refusal, partial close/history, collapsed flagging, retained focus membership, visible navigation, actual CLI and process restart. Evidence: `.revmux/workingsets-ui-20261025T232227-f281e3`, `artifacts/c6-working-sets-ui.log`.
- Plain/flat/tree screenshots were inspected. The capture fixture resizes its private window before screenshots to force a complete rendered frame; earlier captures omitted unchanged glyphs.
- Full Win32 control integration passed (`artifacts/c6-win32.log`).
- Full Ubuntu WSL integration passed (`artifacts/c6-wsl.log`), including three new C6 checks for anchored creation, Linux cwd with spaces and stable-ID movement. Existing C0–C5 WSL checks also passed.

## Broader UI regression limitations

The Navigation suite was run but did not pass as a whole. The original fixture completed its workspace, empty-workspace, keymap and ordering checks, then failed three cursor-image comparisons and stopped when its private hover window could not be positioned beneath the real pointer (`.revmux/navigation-ui-20261025T232045-2e4ad5`: 98 checks, four reported failures). A trial of process-thread DPI awareness produced additional inconsistent keyboard checks and did not repair capture/hover; that fixture change was reverted. The second run is retained in `artifacts/c6-navigation.log`; it is not passing evidence. These failures are not asserted to be proven pre-existing. C6-specific visible ordering is independently covered by the passing owned WorkingSets fixture and pure projection tests.

The Menu suite stopped at discovery: the .NET UIA client exposed no MenuBar/items (`artifacts/c6-menu.log`). C3 already records this desktop's process-wide UIA proxy limitation. Menu model expectations pass in Core tests; no claim is made that the broader UIA/hover/cursor suites are green or repaired by C6.

## Contract audit

WorkingSetsTests covers migration, invalid membership/layout and visible projection. WorkingSetRequestTests covers typed/raw requests, bounds, conflicting targets and CLI misuse. The owned native fixture covers hidden members, anchor conflicts, duplicate aliases, ordered successful members, per-target partial outcomes and recent history, three layouts, focus/filter separation, selection, deletion and restart. Windows/WSL actual creation and movement use the existing launch paths. Strict OpenSpec validation and whitespace checks are required before the separate C6 commit.
