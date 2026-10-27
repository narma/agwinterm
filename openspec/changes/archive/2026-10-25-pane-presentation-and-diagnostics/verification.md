# C8 verification

Verified locally on 2026-10-25. No release, live installation, update or user configuration change was performed.

- Final Release solution build passed (`artifacts/c8-build.log`); the full build reports the two existing xUnit analyzer warnings.
- Full managed regression passed **476 Core + 1438 Pty** (`artifacts/c8-regression.log`). New tests cover Unicode/line/color/shape bounds, invalid saved values, optional serialization, strict CLI/raw arguments, both system-theme choices/fallback and version match/different/unknown. Existing offline VersionReport tests remain green.
- Final owned native presentation acceptance passed **21 checks** (`artifacts/c8-presentation.log`, `.revmux/presentation-ui-20261025T235459-56cad2`). Covers sibling isolation, status semantics, refusal before mutation, OSC background precedence, explicit ANSI probe, swaps, clear, process restart, recent recovery, effective keymap overrides/unmaps/leader, reserved behavior, manual/system/unset theme choices, reload diagnostics, executable metadata and actual CLI calls.
- Full Win32 control integration passed on repeat (`artifacts/c8-win32.log`). The initial run had one quick-terminal capture-file timeout (`artifacts/c8-win32-first.log`); the repeat passed that check and the full suite. This is recorded as an intermittent test result, not a proved root-cause diagnosis. The final renderer adjustment only affects decorated panes and was checked again by the dedicated native suite.
- Native screenshots were inspected: colored pane with Unicode watermark and diamond indicator, explicit red ANSI background above the solid fallback, and light terminal/chrome with a recovered pane retaining its background and square indicator. Local test output was recorded for the final run above and `.revmux/presentation-ui-20261025T235034-ee8a70`.
- Actual version metadata reports the development executable as Portable, running/on-disk 1.0.0 with available metadata and match. Pure tests cover missing evidence and different versions; no real installation was replaced to test diagnostics.

## Contract audit and limits

PaneAppearanceTests and PresentationRequestTests cover validation and additive state. The native fixture covers identity, render/clear, status invariance and restoration. SystemThemeChoice tests exercise both OS choices without changing the user's registry; native acceptance verifies the same theme application recolors terminal/chrome. Reload shares native config-set validation and application, and surfaces file diagnostics. Keymap readback copies effective dictionaries and separately describes reserved keys. Version metadata explicitly describes the file at the running executable path, not all installed copies.

An initial reload fixture appended a setting to a file without a final newline, combining two values. The fixture now inserts a line break before its private edits. Earlier PrintWindow captures were partial; final captures wait for layout and force a fresh size before inspection. Neither issue is hidden as passing evidence.

The broader UIA/cursor/hover limitations recorded for C6 remain. The last complete WSL run belongs to C7; C8 adds no new WSL launch/conversion path. Native per-pane appearance and restore were exercised with owned Windows sessions. Excluded upstream features in the roadmap remain excluded.

Historical local acceptance record: the exact tested application commit was not recorded.
Raw logs and screenshots remain local and are intentionally not versioned; the paths above
identify original runs, not a permanent archive available after cloning. These checks were
not rerun as part of repository cleanup.
