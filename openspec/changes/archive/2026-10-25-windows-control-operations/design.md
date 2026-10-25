# Design

## Context

See proposal.md. Existing IWindowHost posts ordinary operations and returns acknowledgement strings. Native StepWindow already follows open library order; ToggleFullscreen owns restore geometry. Boot unconditionally shows library windows. RevealActiveInExplorer uses the active pane, while the WSL helper captures the active profile; neither is suitable for a background explicit target without refactoring.

## Goals / Non-Goals

Expose native behavior through a small optional host interface and preserve existing wire replies. No quick-window lifecycle expansion, monitor configuration changes, remote desktop support or Explorer automation.

## Decisions

- Add an optional native-window control interface and strict request validator. New commands use queued UI execution for resolution and readback. Do not extend every Lite host with unsupported operations. Wire names: window.next/previous/minimize/restore/fullscreen/monitors/geometry, window.new with minimized, window.move with monitor; session.reveal with optional path-only diagnostics. CLI mirrors these verbs, with --monitor and --minimized additions.
- Resolve exact ID or unique prefix within open library windows. Next/previous use the same library order as StepWindow. Unknown/quick/ambiguous selectors refuse. Existing close/delete paths remain untouched.
- Pass a per-instance start-minimized flag through CreateWindowInstance to Boot and show with SW_SHOWMINNOACTIVE. Preserve Frontmost and avoid SetForegroundWindow. Restore uses SW_SHOWNOACTIVATE. Fullscreen delegates the existing toggle only when required.
- Enumerate monitors with EnumDisplayMonitors/GetMonitorInfo and expose device names plus primary/work rectangles. Accept exact device name or primary. A pure placement helper clamps the normal rectangle to the requested work area. Use WINDOWPLACEMENT for minimized/maximized restore geometry, converting workspace coordinates, and SetWindowPos with NOACTIVATE for normal windows. Fullscreen placement reuses the fullscreen exit/enter path. Read actual rectangle and DPI after application.
- Reveal snapshots the addressed pane cwd/profile on UI, then converts WSL paths off UI and validates existence before launching Explorer. Share the resulting path validation/launch helper with native Reveal. Return the converted path; --path-only performs the same validation without opening Explorer and provides deterministic Windows/WSL integration coverage.
- Emit existing C1 tree invalidations with operation info after successful mutation; do not claim a refused request changed state. Use actual results and explicit failure if a native call fails.

## Risks / Trade-offs

- Minimized geometry uses workspace coordinates → explicit conversions and negative work-area tests; never restore just to move.
- Display hotplug can invalidate a selected device → resolve again during the queued action, refusing before mutation.
- Windows may clamp geometry or deny foreground selection → report effective state and do not promise OS focus acquisition.
- WSL conversion can fail or be slow → use existing bounded conversion behavior off the UI thread and preserve clear error text.

## Migration Plan

Additive commands only; no persistent schema migration. Verify strict CLI/raw validation, owned native windows, pure monitor geometry and target-specific Windows/WSL paths. Record physical multi-monitor test availability explicitly. Sync/archive and commit C7 separately before beginning C8 implementation.
