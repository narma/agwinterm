# Design

## Context

See proposal.md for motivation and the C2 roadmap. P5 deliberately distinguishes the session overlay from full-pane slots; its result and replacement differences remain binding. `OverlaySlot` owns Term, SizePercent, Wait, Exited and LastResult. `SurfaceOf`, `ActiveSurface`, `CoverRect`, `RegridSession` and `RegridCover` are shared native seams. The control host currently splits session and pane handling, and forwards synchronous string replies. C1 adds owned lifecycle publication and asynchronous observation without changing ordinary commands.

## Goals / Non-Goals

**Goals:** keep visibility orthogonal to lifetime; make rendering, input and readback agree; validate all options before replacement; maintain existing wire response shapes.

**Non-Goals:** floating pane overlays, persisted overlays, HTML, new shell execution semantics, HUD formatting or release/install work.

## Decisions

1. Extend each slot with Hidden plus session-only DIP dimensions. A hidden slot retains Term and remains discoverable by exact ID. Change shared surface routing instead of sprinkling command-specific input fallbacks. Session selection respects Hidden; pane overlays under a session cover remain occupied but effectively occluded. Visibility changes resolve/check/mutate in one queued UI operation.
2. Introduce a typed overlay options value at the Pty control boundary with strict action-specific validation, and a host-free geometry helper in Core. Preserve the legacy host overload via a default forwarding adapter when no new options are requested, so existing fake/Lite hosts refuse unsupported extensions rather than pretending support. Windows implements the extended seam and keeps legacy calls flowing through it.
3. Store percent as the common fallback and nullable DIP overrides. Partial resize keeps unspecified axes; explicit percent resets both DIP overrides before applying new overrides; full clears all three. This offers mixed axes without changing existing percent semantics. The geometry helper takes bounds and DPI and returns one centered clamped rectangle used by rendering, PTY resizing and readback.
4. Add overlayDetails to session snapshots and tree without removing legacy fields. Requested hidden and effective visible are separate because background sessions and a higher cover can conceal an otherwise shown slot. Geometry is client pixels; requested DIP/percent remain readable for automation.
5. Reuse target-profile launch/cwd normalization and existing terminal color primitives. Validate cwd/color/dimensions before calling replacement. Follow occurs after successful creation and selects owner/session through existing navigation; it does not force foreground activation. The color belongs to the overlay pane, never a global theme mutation.
6. Publish hidden/shown/resized only after actual state changes with existing C1 ownership. Keep process exits and block sources independent of visibility; teardown always examines occupied slots, not visible surfaces.

## Risks / Trade-offs

- Hidden overlays accidentally retaining key capture → exercise ordinary active input, close chords, mouse routing and explicit-ID access; keep lifetime discovery separate from SurfaceOf.
- Geometry divergence between draw and hit-test → one pure helper and native geometry readback tests at multiple DPIs and tiny bounds.
- Replacement destroys a valid overlay before option validation → parse and validate before the host mutation; verify invalid input leaves ID/process unchanged.
- WSL cwd incorrectly normalized as Windows → use target profile, test paths with spaces through the installed native bridge.
- Input-driven integration flakes when suites overlap → serialize desktop acceptance and use private-window messages with fixture key bindings where possible.

## Migration Plan

No persisted format migration: overlays remain ephemeral. Complete CLI/wire/core tests, native acceptance and WSL launch tests; update control/agent/overlay docs. Sync and archive OpenSpec, commit C2 separately, then proceed to C3 under its own change. Reverting C2 removes only additive options/readback; legacy behavior remains covered by regression tests.
