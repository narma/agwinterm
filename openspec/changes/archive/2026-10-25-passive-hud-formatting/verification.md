# C4 verification — 2026-10-25

Implemented `passive-hud-formatting` after archived C1/C2/C3.

- Release solution build passed (two pre-existing xUnit analyzer warnings).
- Full managed regression: 460 Core and 1365 Pty tests passed.
- Native advanced HUD: 23 checks passed at default desktop DPI and again with 96-DPI override; geometry readback matches painted pixels. Existing HUD series: 51 checks passed.
- Full Win32 control-host integration passed. Actual WSL integration including native hooks passed; a UTF-8 Markdown file with spaces was read through explicit wslpath conversion.
- Visually inspected native Markdown screenshots in regular and 640x480 windows: Unicode headings, emphasis, code and aligned table columns. Height clipping in small windows is intentional and retains the existing 80-percent cap; text remains passive.
- Raw and actual CLI checks cover invalid options, file conflicts, readback, anchor swap/removal, background expiry, invalid-update deadline retention and replacement by hidden/program overlays. Managed checks cover strict UTF-8/file limits, Markdown bounds and validation.

Evidence: ignored `artifacts/c4-build.log`, `c4-regression.log`, `c4-hud-ui.log`, `c4-hud-96.log`, `c4-hud-regression.log`, `c4-wsl.log`, `c4-win32-regression.log`; private screenshots under `.revmux/hudadvanced-ui-20261025T224621-fbbf2e`.

Limits: DPI override exercises native scaling without moving the user's windows across physical monitors. Markdown is the documented bounded subset, not CommonMark/HTML. UI-thread congestion may delay expiry, never advance it. No release or live installation performed; unrelated ConPTY edits are excluded from this stage.

Post-archive audit: corrected generic CLI integer validation to allow fractional HUD font-size as specified, while retaining dashboard integer validation. All 16 HudPresentationTests passed (artifacts/c4-font-fix.log).
