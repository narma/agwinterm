# Single-owner UI consumer migration checklist

This inventory is implementation tracking, not a claim of completed migration.
Hosted sessions render a viewport from their owner and use separate input, query and effect channels.
Every row below must be migrated and exercised locally and hosted before rollout.

| Consumer | Existing source | Replacement operation | Required checks | Status |
|---|---|---|---|---|
| Text, cursor, palette, dynamic background | Program.Render.cs, Program.Presentation.cs | Immutable frame lease with resolved cells and metadata | Ghostty visuals, cursor/blink, theme, synchronized output, B/D timing | Local and hosted visual suites passed. Navigation verified cursor shape, blink and DECSCUSR overrides in both modes. Final B/D timings remain open. |
| Key translation and IME | Program.cs, Program.GhosttyInput.cs, Program.WndProc.cs | Windows translation in UI; semantic key/committed text at owner | Repeat/release, AltGr, dead keys, surrogate/IME, broadcast/read-only | Both modes passed routed Ghostty input and real Pinyin commit/preedit cases. |
| Mouse, wheel, focus, paste | Program.Input.cs, Program.WndProc.cs | Owner semantic mouse/focus/paste encoding with current modes | Mode changes under output, SGR pixels, protected paste, focus | Native cell/pixel encoding and unfocused hosted wheel delivery passed, including a scrolled sibling. |
| Scroll and viewport | Program.Input.cs, Program.Sessions.cs, Dashboard.cs | Versioned viewport request and immutable cached dimensions/history metadata | Hidden panes, scroll during output, resize/reflow, stale responses | Hosted resize recovered the final marker after 256 keys with viewport credit held. Hidden and resumed animation passed. |
| Selection, links, copy, command marks | Program.Input.cs, Program.ControlHost.cs | Owner tracked anchors, bounded extraction and generation-tagged marks | Wide cells, graphemes, wraps, alt screen, concurrent output | Owner anchors survived resize. Select-all included concurrent output from a single generation. Both reflow fixtures passed. Drag fallback still needs coverage. |
| Search | Program.Input.cs, Program.ControlHost.cs | Cancellable owner snapshot worker with bounded results and generation fences | Unicode folding, stale/closed session, input latency during search | Bounded and cancelled queries, Unicode and stale results were exercised. Over 40,000 rows, 250 simultaneous searches raised decoded-viewport echo p95 from 32.56 to 83.56 ms. |
| Accessibility and control text | Uia.Text.cs, SurfaceText, ControlServer.cs, ISession.SnapshotRow/SnapshotCursor | Cached consistent viewport; bounded asynchronous history query | Screen-reader ranges, styles, command output, full history | Both accessibility suites passed. Remote styled-history recovery preserved a dense 2,000-cell row. |
| Persistence and history restore | Program.Services.cs, BufferPersist | Owner export/import with existing attributed format | Fidelity, saved history seeding, no command replay on adoption | Installed 0.20.19 and preview migrations passed with split/window IDs, explicit RGB, Unicode, retry and two restarts. |
| Display injection, clear/reset, theme, history limit | Program.Services.cs, Program.Sessions.cs, ISession.Inject/MutateLocked | Explicit owner commands; UI-only errors as literal presentation | Control side effects, existing replica-only differences, save/restore | Control and UI suites covered queued display writes, clear/reset and theme changes. |
| Bell, clipboard, notifications, progress, authenticated completion | PaneHost, Program.Sessions.cs, WSL title filter | Reliable ordered effects plus durable metadata; owner terminal replies | Policy, exactly-once replies, detached effects, overflow/reconnect | Clipboard policy, title/cwd under stalled credit and durable progress after reattach passed. A host-free pipe check coalesced 4,096 metadata changes behind 256 ordered effects. |
| Graphics | Program.GhosttyImages.cs, Program.Render.cs | Bounded image generations and session GPU cache; owner animation deadlines | Layers, links, placeholders, limits, hidden/visible, reconnect | Hosted animation stops publishing when hidden or static. Contract teardown released every counted native resource. Multi-session soak remains open. |
| Adoption and errors | Program.WslLive.cs, SessionBackends, ServerSession | Negotiated attachment identity and literal error frame | Surviving PID, exact transport, no VT replay, legacy exception | Real Mac and WSL/ZMX recovery passed with the same workload incarnation, including host loss and partial-split refusal. |

Audit patterns: `.Emulator`, `GhosttyTerminalCore`, `SyncRoot`, `MutateLocked`,
`Inject`, `SnapshotRow`, `SnapshotCursor`, `SurfaceText`, `BufferPersist` across
Win32, Pty and accessibility code. Configuration/core-factory references are
backend selection, not proof of a second terminal; verify live counters as well.
No row is complete until its remaining direct mutable UI accesses are removed
from the new path and its regression checks pass.
