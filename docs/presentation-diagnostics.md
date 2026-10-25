# Pane appearance and runtime diagnostics

```
agwintermctl session appearance set --target PANE --color '#14283C' --text 'Build sandbox' --opacity 25 --status-color '#FFAA00' --status-shape diamond
agwintermctl session appearance get --target PANE --json
agwintermctl session appearance clear --target PANE
```

Set replaces the complete local appearance; omitted values inherit defaults. Clear removes
all overrides. Primary/split pane IDs (or unique prefixes) are resolved within `--window`;
auxiliary surfaces refuse. Changes follow pane IDs through swaps, restore and recent recovery.
No sibling or terminal status changes. Colors require #RRGGBB, opacity is 0..100 (default 15),
text is limited to 512 Unicode scalars and 12 lines, and shape is circle/square/diamond.
Text is clipped native background content, not terminal output or interactive HUD.

The solid color replaces the theme fallback; explicit ANSI cell colors and OSC background
remain authoritative. Session images are inherited unless `--hide-image` is set. Clearing
restores the current theme/session appearance. The native sidebar uses the style of the first
pane in pane order contributing the winning status; priority, notifications and waits are
unchanged. Other palette decorations retain their existing presentation.

Readback returns paneId, overrides, effectiveBackground (#RRGGBB), inheritedImage and
effectiveTheme. Wire command session.appearance uses op=get/set/clear and optional color,
text, opacity, status-color, status-shape, hide-image. Set is replacement, not a patch.

Existing `theme-follow-system`, `theme-light` and `theme-dark` settings remain supported.
When following is disabled, or the chosen side is unset, `theme` supplies the fallback.
The same native application updates terminal colors and derived chrome colors.

```
agwintermctl keymap list --json
agwintermctl config reload --json
agwintermctl app version --json
```

All three accept `--window` for the native host. `keymap list` exposes resolved normal and
leader bindings, reserved key behavior and parsing diagnostics; `command list` remains the
separate custom-command list. Reload applies the same live path as the UI, returning changed
keys, effectiveTheme and diagnostics for malformed/unknown/invalid or normalized entries.
It never rewrites the configuration file or executes commands. Existing new-session-only
settings retain that lifecycle. A diagnostic does not imply an all-or-nothing reload.

`app version` reports running, path, channel, onDisk, availability, comparison and source.
The source is the executable path backing the running process, including a portable executable;
it is not discovery of every installed copy. Missing files or unavailable resource metadata
produce comparison=unknown; available versions compare as match/different after removing build
metadata. This does not contact update servers, install or restart anything. The original
offline `agwintermctl version` command and its CLI/app report are unchanged.
