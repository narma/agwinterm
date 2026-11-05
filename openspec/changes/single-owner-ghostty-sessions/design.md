# Design

## Context

See proposal.md for motivation. Inspected baseline: PR #35, commit `83d82b1c55501bac97e5995d50910df59a8e7bf3` on `codex/ghostty-vt-foundation`. If implementation starts from a newer head, record and benchmark that exact head again.

Observed seams:

| Area | Current implementation | Required change |
| --- | --- | --- |
| Ownership | `PtyHostServer` creates `TerminalSession`; `ServerSession` creates another core and feeds raw output | Hosted client owns no Ghostty terminal; owner publishes presentation |
| Public session interface | `ISession.Emulator`, `SyncRoot`, `MutateLocked`, `Inject` expose mutable state | Semantic commands and immutable presentation/query results |
| Rendering | `Program.Render.cs` casts to `GhosttyTerminalCore`, captures rows under session lock | Render an owner-independent frame without IPC or terminal lock on paint |
| Input | `Program.GhosttyInput.cs` translates Windows input, encodes through UI core and writes bytes | Retain Windows translation/IME locally; encode at owner |
| Graphics | `Program.GhosttyImages.cs` associates timers with core objects | Session/attachment/image-generation identities; owner computes animation state |
| Reads | Input/selection, UIA, control text/output and history consumers read emulator directly | Versioned viewport cache plus bounded owner queries |
| Attachment | `ptyhost.proto` v2 seeds native snapshot followed by raw output | Negotiate presentation transport; retain old contract in compatibility adapter |
| Detached effects | Host answers terminal queries only when UI is absent | Owner always answers; UI receives policy-controlled effects |

The existing render benchmark measures Render/EndDraw, not key-to-visible-echo. Headless VT benchmarks do not measure IPC or UI scheduling. Existing htop checks establish rendering correctness, not latency or single-owner performance.

## Goals / Non-Goals

**Goals:** one live Ghostty terminal for each new session in either mode; reusable session semantics without local IPC overhead; reduced duplicated history and parsing; lower expected hosted input latency with measured non-regression in both modes; preserve existing user-visible terminal and lifecycle behavior.

**Non-Goals:** moving fonts/Direct2D to host, pixel streaming, shared native pointers between processes, Rust host rewrite, removing legacy cores, changing Linux/remote daemon ownership, simultaneous multiple attached viewers, or claiming survival of serverless sessions after UI exit. Temporary immutable search/capture objects are allowed; a second continuously fed terminal is not.

## Decisions

### 1. One owner, two adapters, one UI contract

Introduce a session-owned terminal service around the existing Ghostty implementation. It owns feed, resize, encoding, query responses, history, tracked positions, graphics metadata and persistence. A local adapter invokes that service directly in-process. A hosted adapter submits commands and receives immutable results over named pipes. Avoid a fake local server or serialization in the serverless path.

Split UI-facing responsibilities into presentation access, semantic input/commands, and asynchronous bounded queries. Names can be finalized during implementation; do not make a remote object implement `ITerminalCore` by issuing per-cell RPC. Paint takes the latest complete immutable frame. Local capture preserves the existing native begin-under-lock/end-outside-lock pattern; retain reusable buffers and avoid per-frame full-history copying.

```
Hosted:    ConPTY <-> [.NET host: one Ghostty + owner service]
                                  | frames/effects   ^ input/queries
                           [UI: bounded cache + Direct2D]

Serverless: ConPTY <-> [UI process: one Ghostty + owner service]
                                      | direct local adapter
                              [bounded frame view + Direct2D]
```

This is a change to state ownership, independent of the host implementation language. Keeping two cores and merely reducing scrollback limits would leave duplicate parsing and mode divergence. Moving ownership to UI only cannot preserve state across an unexpected UI crash.

### 2. Versioned, bounded presentation transport

Extend protobuf capability negotiation with an explicitly versioned presentation contract. Preserve v2 raw-stream handling for old peers; never reinterpret raw VT as framed messages. New hosted sessions require the negotiated path and must not silently fall back to a replica on error. Legacy host upgrades use a separate namespace/protocol endpoint when necessary so new and surviving old hosts can coexist.

Every message includes session incarnation/creation ticket, attachment epoch and appropriate request/frame sequence. Frame metadata includes viewport revision, dimensions, terminal generation, base frame sequence, cursor and visibility/style, alternate-screen state, palette/default colors, grapheme-aware cells, links, scroll/history metadata, stable position handles and image placements. Full *viewport* frames are recovery units; they are not native full-history snapshots.

Use named pipes initially; reuse protobuf for control metadata and bounded bulk row/image payloads. No per-cell request/response. Separate latency-sensitive input/control from presentation and long query/image payloads to avoid head-of-line blocking. Apply the repository's current-user pipe access policy and validate frame lengths/counts, coordinates, handles and generations before allocation/use. No process-local native pointer crosses IPC.

Keep at most one in-flight and one replaceable pending presentation update per attachment, with byte caps derived and recorded during the transport prototype. A delta always names its base; when a base is no longer known, regenerate a full viewport frame. Do not simply discard an intermediate delta and apply later deltas to the wrong base. A stalled receiver times out/detaches; it never holds the feed lock during a pipe write. Capture under short owner synchronization, serialize/send outside it. Frame scheduling respects synchronized-output holds and existing timeout behavior; coalescing never delays urgent eligible updates by a fixed 16 ms timer.

Hidden panes stop regular frame publication but keep owner processing and lightweight state/effect updates. On visibility or viewport change, publish a fresh viewport. The UI retains visible viewport plus a bounded overscan/prefetch window, not full scrollback. Initial attach and resize use an atomic capture/subscription boundary; epoch changes discard late frames and query results. Final output generation must be available before exposing settled process exit.

Image payloads are referenced by session/attachment/image ID and generation, transferred once per required generation through a bounded bulk channel. Placements reference only available payloads or wait in a bounded pending frame. Eviction/re-request and reconnect invalidate stale IDs. Preserve current image budgets, layers, animation deadlines and Unicode placeholders. Animation ownership lives in the owner; GPU textures and raster/font caches remain UI-local. Start with owned buffers; shared memory is a measured follow-up only if copies prevent passing the performance gate.

### 3. Input fast path and ordering

Windows key translation, dead-key state, IME composition/candidate positioning, shortcut interception and physical-key target tracking remain in UI. Send semantic key press/repeat/release, committed text, paste, focus and mouse events to the owner, including translated text, physical key/modifiers, consumed modifiers and Win32 input information. The owner chooses Kitty/legacy/DEC 9001 encoding from authoritative modes and writes directly to PTY. Never ask host to return encoded bytes to UI for a second trip. Raw byte writes used by control APIs remain an explicit distinct operation; they must not be re-encoded.

The transport accepts input without waiting for a frame or per-key response. This retains one outbound IPC hop, just as today's byte write does. Use ordered sequences per attachment, bounded queues, explicit rejection on saturation, and no replay of uncertain writes after reconnect. Large paste is chunked; preserve byte/event ordering and the bracketed-paste boundary rather than overtaking it with later keys. Coalesce pointer motion only when semantics allow; never discard key releases, button transitions or focus transitions. Preserve broadcast targets and read-only checks per target.

Owner scheduling serializes feed and mode-dependent input at defined boundaries. Limit each output ingestion turn using the existing 256 KiB/4 ms budget as an initial upper bound, then tune against tail-latency results. Pending input receives service between turns; a long native operation is not preemptible, so measure its worst-case lock hold. Search/history conversion and serialization operate on bounded immutable captures outside the critical path. Terminal responses always come from owner, even with UI attached, and share the ordered PTY writer without waiting for UI policy effects.

Expected benefit: today's echoed output is parsed by host, copied to UI, parsed again under the UI session lock, then captured for painting. The new path removes the second parse and UI lock contention. This does not guarantee lower latency: capture/serialization, owner contention and frame scheduling can erase the gain. No speculative local echo is introduced to make timing look faster.

### 4. Queries, UI effects and persistence

Move search, tracked selection/anchors, copy extraction, history export, command marks, control readback and mutations to semantic owner operations. Keep immediate selection highlight from cached frame coordinates; authoritative extraction uses generation/anchor handles. Scroll requests carry a viewport revision; prefetch is bounded and cancellable. Search uses existing snapshot ownership/cancellation, with result limits and stale-generation rejection. UIA may answer current viewport reads from immutable cache; larger reads execute off the main UI thread with bounded timeout and consistent generation. Never block paint on an RPC.

Audit every `ISession.Emulator`, concrete Ghostty cast, `SyncRoot`, `MutateLocked`, `Inject`, `SnapshotRow` and history consumer. Cover Program.Render/Input/GhosttyInput/GhosttyImages/ControlHost, Uia.Text, SurfaceText, persistence and session lifecycle. UI-only status overlays/errors remain presentation data, not a second VT parser. Preserve display-injection/control semantics by routing their terminal mutations to owner; audit and document any former replica-only behavior before changing it.

Forward bell, notifications, progress, title/cwd, clipboard-write and authenticated completion effects with ordered IDs and attachment fencing. Owner answers terminal queries once and persists authenticated completion once. UI retains clipboard/notification policy. Match existing detached behavior: do not replay ephemeral clipboard/bell/notification actions on attach; publish current durable metadata such as title/progress. Reliable effect delivery is separate from replaceable frames; on bounded effect-queue overflow fail/detach explicitly rather than losing events silently. No terminal content or clipboard payload is placed into performance logs.

Hosted history persistence reads owner state; in-process persistence calls the same service directly. Existing on-disk attributed history format and no-command-replay rules remain unchanged. Native snapshots may still be used internally for bounded search/persistence work, but are not restored as a live UI terminal.

### 5. Performance experiment and rollout gates

Before functional changes, produce baseline artifacts for A = current hosted host+replica and B = current serverless single core. Compare C = new hosted single owner against A, and D = new local adapter against B. Do not compare C to B as proof of a hosted regression/improvement. Pin executable/native-library hashes, Ghostty revision, .NET configuration, ConPTY, VM image/resources, font/grid/history limits, graphics configuration and workload seeds.

Add a small compiled test child using existing build tooling: raw input echo with unique event IDs and deterministic output markers. Drive ordinary Windows key messages through routed UI automation; do not substitute `session.send` for the physical input pipeline. Record monotonic timestamps (QPC/Stopwatch frequency validated across processes in the same guest):

- T0: synthetic input injection, T1: UI event receipt.
- T2: host event accepted, T3: encoding/write submission completed; neither proves child consumption.
- T4: child observes event, T5: owner parses its matching echo marker.
- T6: frame containing that marker is ready, T7: UI receives/applies it, T8: that frame's EndDraw/presentation submission completes.

Measure T1-T4 for input delivery, T1-T8 for application input-to-present, T0-T8 for injection pipeline, and each stage for diagnosis. Record sequence IDs in side-channel diagnostics and in a deterministic echo fixture; verify the presented frame actually contains the matching marker. EndDraw is not proof of photons or compositor display time: use available compositor/present telemetry when supported and label unsupported paths. VM results establish controlled software-path latency, not physical keyboard-to-photon latency. No arbitrary sleep or forced repaint is allowed in measured echo flow.

Test idle interactive typing, burst/repeated keys, IME commits, paste plus subsequent input, mode-switching TUI, continuous ASCII/Unicode logs, htop-like updates, resize/reflow, scrolling/search during output, Kitty static/animated images, and 1/8/32 sessions (visible, hidden and detached mixes). Saturation tests use a foreground echo pane plus other busy panes. Include new attachment with large history and image caches; test both cold startup and warmed steady state separately. WSL and real remote cases are supplemental; do not attribute network/daemon scheduling to Windows IPC without the stage timings.

For each core workload use at least five paired alternating A/C or B/D runs, with warmup and at least 10,000 echo samples across runs for p99 estimates. Run sequentially on the same worker VM allocation, not concurrently; predeclare invalid-run criteria (fixture failure, missing samples, changed configuration), preserve all results and report run-level confidence intervals rather than treating correlated keystrokes as independent runs. First run baseline-vs-baseline to establish noise; if noise cannot resolve the gates, improve measurement or increase runs, never report an inconclusive run as passing.

Proposed acceptance tolerances, fixed before candidate measurements:

| Metric | Gate |
| --- | --- |
| T1-T4 and T1-T8 p50/p95/p99 | Upper 95% confidence bound of paired candidate-minus-baseline must be <= max(0.5 ms, 5% of baseline percentile) for p50/p95 and <= max(1 ms, 5%) for p99, in each primary workload and each mode |
| Expected hosted improvement | Engineering target: >=10% lower T1-T8 p95 under sustained output/contention, with confidence interval below zero; report unmet target explicitly even if non-regression passes |
| Sustained output throughput | Lower 95% bound of candidate/baseline >=0.95; no missing bytes, invalid frames or unbounded queue growth |
| CPU at fixed completed workload | Upper 95% bound of candidate/baseline <=1.05, report combined host+UI CPU separately from child/conhost; idle absolute deltas reported |
| Render/scroll/search latency | p95/p99 within the same latency tolerances; slower interaction cannot be hidden by faster typing |
| Memory | Report combined host+UI private bytes at matched contents, absolute/relative overhead, history savings and transient peaks. Per the user's explicit priority update on 2026-11-05, modest bounded memory growth is an acceptable tradeoff for latency and overall performance; the original +5% cutoff is no longer a rollout blocker. Leaks and unbounded growth remain blockers. No replacement numeric allowance has been invented. |
| Long run | >=60 minutes sustained output with bounded history, resize/search/attach churn; no persistent growth beyond documented caches, no input loss or stalled ingestion |

These are engineering tolerances, not a claim that sub-millisecond regressions are literally zero. Change tolerances only with explicit review of baseline noise, never retrospectively to pass a slow candidate. If the improvement target is missed, retain the feature behind an explicit experimental switch until the bottleneck and result are reviewed; do not advertise faster input. Zero idle baselines use absolute CPU time with baseline noise bounds rather than division by zero. Measure private committed bytes/native allocations; summed working sets can double-count shared DLL pages. Keep instrumentation off for a paired overhead check and record trace loss, allocations, GC pauses, owner lock holds, queue age/depth, IPC bytes and skipped/resynced frames.

### 6. Compatibility and validation

New path requires a capable host. Existing .NET/Rust sessions remain attachable via an isolated legacy adapter, including replicas only for those old sessions. Diagnostics explicitly identify this exception; never count it as single-owner acceptance. New session creation uses the new host endpoint after rollout. Old clients attaching to a new host must negotiate a supported legacy stream or receive an explicit incompatibility error; no wire ambiguity. Drain old processes naturally without replay or forced termination. Removing that adapter is a later versioned migration.

Validate native instance counts and feed counters, including startup failures, reconnect, UI crash, teardown, search and snapshot captures. Tests distinguish one live terminal from short-lived read-only captures. Serverless tests assert no host launch and no named-pipe session traffic, including default-terminal handoff.

Run host-free protocol/fixture tests directly. Windows lifecycle, ConPTY, visual, input and latency suites use the installed Hyper-V worker (ordinary user, max two VMs/8 GiB); WSL suites use existing private-desktop routing. Do not perform tests on the user's desktop or set CI flags to bypass routing. Use compiled fixtures; no new runtime dependency. Real SSH fixture failures are reported as missing evidence, not replaced with synthetic claims.

## Risks / Trade-offs

- Larger frame traffic than raw VT -> changed-row batches, viewport-only publication, stable image IDs and measured IPC/copy costs.
- Owner contention moves input bottleneck -> short feed turns, capture outside locks, separate query workers and measured p99 lock/queue delay.
- Render-state APIs may expose mutable native lifetimes -> immutable owned frame contract, one capture consumer and concurrency tests; never share pointers across processes.
- Scroll/search/UIA become asynchronous -> versioned requests, bounded prefetch, cancellation and local viewport reads; explicit unavailable results.
- Slow UI can back up events or images -> separate bounded channels, replaceable frames, explicit disconnect on reliable-queue exhaustion.
- Current spec mentions Rust-only recovery -> preserve verified PR #35 dual-host behavior and record the existing documentation discrepancy; do not force host migration during recovery.
- .NET pipe lifetime bug class remains relevant -> preserve cancellation-before-disposal ownership rules, stress detach/write/exit races; Ghostty ownership alone does not fix transport bugs.
- Common adapter regresses serverless speed -> direct calls, no serialization/extra worker hop solely for abstraction, independent baseline gate.

## Migration Plan

1. Land measurement fixtures/counters and save A/B baselines before changing ownership.
2. Introduce semantic session contracts and local adapter while keeping current hosted path; migrate UI consumers and prove B/D parity.
3. Implement negotiated owner presentation and input transport behind an experimental setting; prototype text/input first and run an early latency gate before expanding graphics/history.
4. Complete owner queries, effects, persistence, images, UIA, input protocols and compatibility; remove live Ghostty allocation from the new hosted client.
5. Run correctness, fault injection, paired performance matrix and soak; inspect Greptile for the final implementation commit alongside local/CI checks.
6. Enable the new path for new .NET-hosted Ghostty sessions only after gates pass and the improvement result is reviewed. Retain serverless mode. Update effective-transport diagnostics and upgrade documentation.

Rollback selects the previous path for future sessions and uses the retained compatible client path for existing owners where negotiated. Never kill/recreate live shells to roll back. A binary downgrade that cannot attach must explicitly report incompatibility; use the compatible build to finish/drain those sessions. Baseline binaries and benchmark artifacts are retained for comparison.

## Open Questions

- Exact row encoding, buffer pool sizes and byte caps are selected by the bounded text/input transport prototype; ownership, ordering and acceptance gates do not depend on those tuning choices.
- Availability of compositor timing in the prepared VM determines whether T8 can be supplemented with display telemetry. Missing telemetry cannot be relabelled physical display latency.
