# Design

## Context

See proposal.md. Pane already owns an optional DefaultBackground used by overlays; session owns image watermark settings. PaneState is additive and shared by restore/recent recovery. ThemeFollowSystem/ThemeLight/ThemeDark already exist and ApplySystemTheme updates chrome. Keymap stores resolved default-plus-user dictionaries. Native config reload shares ReloadConfigApplying with config set, but diagnostics and keymap readback are not exposed. VersionReport intentionally retains its offline two-line CLI contract.

## Goals / Non-Goals

Persist appearance on stable pane identity, expose effective runtime data and reuse existing native render/config paths. No HTML, live install/update, global registry writes, status-priority changes or change to legacy session image commands.

## Decisions

- Introduce an immutable validated PaneAppearance value with optional color/text/statusColor/statusShape, opacity 0..100 (default 15), and hideImage. Store it as an optional PaneState field, preserving default serialization. Text is at most 512 Unicode scalars and 12 lines; colors are #RRGGBB; shapes circle/square/diamond. Invalid persisted values drop to inheritance. Explicit clear removes the complete value.
- Add session appearance get/set/clear through an optional presentation host. Set replaces the full local value so callers can remove individual fields by omission. Target primary/split pane by exact ID or unique prefix within the selected window. Readback includes overrides, effective fallback background and inherited session image. No auxiliary appearance persistence.
- Fill local solid background before inherited image and clipped DirectWrite text, then draw terminal cells. Skip fills for default-color cells of decorated panes; explicit cell backgrounds/inverse and OSC dynamic background retain precedence. Text is passive, not HUD or terminal output. hideImage suppresses the inherited session image only for that pane.
- Native sidebar aggregate status selects its normal winning status, then uses the first pane in pane order contributing that status for color/shape. Keep blink/priority/notification/wait semantics. This is appearance of the sidebar status indicator, not replacement of every decorative circle in palettes.
- Keep existing theme config and add a pure selection helper: disabled follow uses Theme; unset selected side also falls back to Theme instead of retaining stale previous appearance. No registry mutation is required to test both choices.
- Add keymap list and config reload using the routed native host, plus app version for running/on-disk diagnostics. Keymap readback returns normal and leader dictionaries, reserved F1/F6 behavior and parser diagnostics. Existing command list stays unchanged.
- Reload retains the shared native application path. Extract existing strict config-set validation for reuse in diagnostic scanning; report unknown/malformed/invalid entries and values normalized by the parser. Return changed keys, notes and effective theme; native UI uses the same notes. Do not rewrite the config file during reload.
- app version compares running informational version with FileVersionInfo.ProductVersion at Environment.ProcessPath. Report source path and existing update channel, availability reason and match/different/unknown. This is the on-disk executable backing this process, not a claim to discover every installed copy. Existing offline version command is unchanged.

## Risks / Trade-offs

- Default cell fills can hide native watermarks → explicit default-color tests and screenshots; preserve explicit ANSI colors and overlay rendering.
- Malformed saved appearance → validate on restore/recent, never propagate invalid drawing values.
- Version resources absent in portable/dev builds → unknown comparison, never infer match from absence.
- Config parsing intentionally clamps some values → expose normalization diagnostics while retaining existing live application semantics.

## Migration Plan

Additive optional state only. Verify pure bounds/theme/version semantics, strict raw/CLI routes, owned native rendering and swap/restart/recent recovery, effective keymaps and config reload. Sync both capabilities, archive C8 and commit separately. Update the roadmap without erasing C6's recorded broader UI regression limitations.
