# Single-owner UI consumer migration checklist

This inventory is implementation tracking, not a claim of completed migration.
Hosted sessions render a viewport from their owner and use separate input, query and effect channels.
Every row below must be migrated and exercised locally and hosted before rollout.

| Consumer | Existing source | Replacement operation | Required checks | Status |
|---|---|---|---|---|
| Text, cursor, palette, dynamic background | Program.Render.cs, Program.Presentation.cs | Immutable frame lease with resolved cells and metadata | Ghostty visuals, cursor/blink, theme, synchronized output, B/D timing | Local/hosted text source migrated; routed local validation pending |
| Key translation and IME | Program.cs, Program.GhosttyInput.cs, Program.WndProc.cs | Windows translation in UI; semantic key/committed text at owner | Repeat/release, AltGr, dead keys, surrogate/IME, broadcast/read-only | Local/hosted dispatch implemented; full regression checks pending |
| Mouse, wheel, focus, paste | Program.Input.cs, Program.WndProc.cs | Owner semantic mouse/focus/paste encoding with current modes | Mode changes under output, SGR pixels, protected paste, focus | Semantic dispatch implemented; current-mode coordinates and validation pending |
| Scroll and viewport | Program.Input.cs, Program.Sessions.cs, Dashboard.cs | Versioned viewport request and immutable cached dimensions/history metadata | Hidden panes, scroll during output, resize/reflow, stale responses | Fenced resize/scroll prototype; visibility consumers and validation pending |
| Selection, links, copy, command marks | Program.Input.cs, Program.ControlHost.cs | Owner tracked anchors, bounded extraction and generation-tagged marks | Wide cells, graphemes, wraps, alt screen, concurrent output | Owner anchors and immutable copy text implemented; reflow covered in VM. Full-history select-all with concurrent output and drag fallback need final coverage. |
| Search | Program.Input.cs, Program.ControlHost.cs | Cancellable owner snapshot worker with bounded results and generation fences | Unicode folding, stale/closed session, input latency during search | Owner snapshot search shipped in candidate; bounded results, stale queries and Unicode exercised. Large-history latency gate pending. |
| Accessibility and control text | Uia.Text.cs, SurfaceText, ControlServer.cs, ISession.SnapshotRow/SnapshotCursor | Cached consistent viewport; bounded asynchronous history query | Screen-reader ranges, styles, command output, full history | Owner history, styled cells, UIA snapshot and off-UI reads implemented. Full UIA regression pending. |
| Persistence and history restore | Program.Services.cs, BufferPersist | Owner export/import with existing attributed format | Fidelity, saved history seeding, no command replay on adoption | Chunked attributed import/export implemented. Installed-version and preview upgrade gates pending. |
| Display injection, clear/reset, theme, history limit | Program.Services.cs, Program.Sessions.cs, ISession.Inject/MutateLocked | Explicit owner commands; UI-only errors as literal presentation | Control side effects, existing replica-only differences, save/restore | Owner commands implemented; queued-write visibility documented. Final UI validation pending. |
| Bell, clipboard, notifications, progress, authenticated completion | PaneHost, Program.Sessions.cs, WSL title filter | Reliable ordered effects plus durable metadata; owner terminal replies | Policy, exactly-once replies, detached effects, overflow/reconnect | Ordered effects and role/progress seed implemented. Policy and reconnect integration gates pending. |
| Graphics | Program.GhosttyImages.cs, Program.Render.cs | Bounded image generations and session GPU cache; owner animation deadlines | Layers, links, placeholders, limits, hidden/visible, reconnect | Image transfer, control images and animation deadlines implemented. Hidden/visible churn and resource gates pending. |
| Adoption and errors | Program.WslLive.cs, SessionBackends, ServerSession | Negotiated attachment identity and literal error frame | Surviving PID, exact transport, no VT replay, legacy exception | Experimental Windows path only |

Audit patterns: `.Emulator`, `GhosttyTerminalCore`, `SyncRoot`, `MutateLocked`,
`Inject`, `SnapshotRow`, `SnapshotCursor`, `SurfaceText`, `BufferPersist` across
Win32, Pty and accessibility code. Configuration/core-factory references are
backend selection, not proof of a second terminal; verify live counters as well.
No row is complete until its remaining direct mutable UI accesses are removed
from the new path and its regression checks pass.
