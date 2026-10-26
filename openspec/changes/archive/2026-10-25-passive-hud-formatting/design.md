# Design

## Context

See proposal.md for motivation and upstream reference. SessionHuds validates and serializes immutable HudSpec; Program.Hud draws with DirectWrite and owns one existing window timer for spinners. HUD state is session-local and shares the session program-overlay slot. C1 lifecycle events and C2 geometry already provide stable observation and native pixel conventions.

## Goals / Non-Goals

Keep one native presentation pipeline for measurement, drawing and readback. Bound text/layout work and preserve all terminal/input paths. No HTML, interactive links, shell renderer, generated body files, persistence or third-party Markdown dependency. Markdown is a documented useful subset, not a CommonMark conformance claim.

## Decisions

- Extend HudSpec with optional validated settings and additive readback fields. Resolve pane words/IDs once in the queued session operation and store anchorPaneId separately from the original selector. Session-target routing remains strict; a pane ID passed as the session target still refuses. Rendering and snapshots find the pinned main/split pane by ID, so swaps cannot retarget it.
- Use the existing 80 ms HUD timer for both spinner invalidation and expiry. Each successful open/update writes the current session's monotonic deadline; tick reads current state rather than scheduling per-HUD callbacks. Clearing/replacing clears that deadline, so stale queued ticks cannot close a newer HUD. Timer runs for background timed panels and stops when no spinner/deadline remains. Anchor removal is settled with layout reconciliation, before the next readback/render.
- Compute a disposable native layout for any session without selecting it, using the same content/pane boxes and text metrics for rendering and tree/reply projection. Only selected unobscured sessions draw. Readback geometry converts the shared DIP box to client pixels and reports visibility independently of retained state.
- Sticky affects edge margins and the width cap only; center retains the ordinary cap. Frameless keeps opaque fill and horizontal padding but reduces vertical padding and skips the border. Font size uses DIP and can change on update because DirectWrite has no upstream creation-only terminal font constraint. Background retention on update remains unchanged.
- Core parses Markdown into bounded plain text and style ranges, without shell escaping or HTML parsing. Handle ATX headings, flat list prefixes, quote/rule presentation, fenced and inline code, bold/italic, and pipe tables. Tables render bounded monospaced rows with separators and a distinct header; no interactive cell layout. Unsupported constructs remain literal. At most 4096 scalars/128 lines and 8 columns/32 rows per table bound parsing and layout independently of regex backtracking.
- DirectWrite applies weight/style/size/monospace ranges and wraps within the anchor box. Cache parsed content with the immutable spec, not native device resources; renderer-created layouts are disposed after use. The ordinary plain renderer keeps its default look.
- CLI --file is mutually exclusive with a positional message. Read a maximum 64 KiB using strict UTF-8 before connecting, then run the same plain/Markdown validator. Existing explicit wslpath conversion applies; raw wire never opens a path. Avoid a server read because the caller's filesystem and trust context can differ.
- Extend only the HUD row of the C1 lifecycle contract with anchorPaneId and expired. No message bodies enter events. Refusals preserve both current state and deadline.

## Risks / Trade-offs

- Per-session geometry needs background layout metrics → reuse existing native content/pane layout inputs without SetActive; test two sessions with different splits.
- A pane can disappear during teardown → resolve and reconcile on the owner UI thread; retain its ID for close events before clearing state.
- Formatted content can exceed visible height → clip to the existing 80 percent height bound; document no scrolling or clickable content rather than stealing terminal input.
- Small windows and Unicode fallback fonts → use actual DirectWrite metrics for wrapping, inspect screenshots and test client-pixel bounds at multiple DPIs.
- Expiry while UI work is busy → monotonic deadlines prevent early expiry; dispatch at the next owner timer turn without promising hard real-time scheduling.

## Migration Plan

No persisted state changes. Land validation/parser/CLI tests, then native lifetime/geometry/rendering and isolated HUD acceptance. Run existing control/HUD regressions and real WSL file-path acceptance. Sync the new capability and the precise C1 requirement delta, archive and commit C4 independently. Do not release or install.
