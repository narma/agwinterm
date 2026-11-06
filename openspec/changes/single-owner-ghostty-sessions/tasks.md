# Tasks

## Integration scope

On 2026-11-06 the maintainer approved merging the host-owner implementation
into its foundation branch using primary before/after measurements, migration
from both previous versions, Windows/WSL/Mac feature checks and a clean final
review. Open performance matrix and audit items below track follow-up work.
Confidence gates retain their measured outcomes. See
`docs/host-owner-completion.md` for evidence and the 32-session timeout.

## 1. Baselines and measurement fixtures

- [x] 1.1 Add opt-in live-terminal/feed/allocation counters that distinguish owners, legacy replicas and temporary captures; verify current hosted sessions report two live instances and serverless sessions one, with zero after disposal.
- [x] 1.2 Add a compiled deterministic echo child and correlated T0-T8 tracing through the existing Hyper-V entry point; verify marker-to-frame matching, cross-process clock frequency, dropped-sample detection and no forced repaint in timing runs.
- [ ] 1.3 Extend performance collection with combined host/UI CPU and private bytes, native allocations, GC/lock/queue timings and IPC volume; verify trace-disabled overhead comparisons and document that EndDraw is not physical display time.
- [ ] 1.4 Record pinned A/B baseline binaries, configuration, paired baseline-vs-baseline noise runs and raw samples for the design matrix; deliver a reproducible report with the predeclared gates before changing ownership.

## 2. Shared session contract and local adapter

- [x] 2.1 Introduce immutable viewport/frame, semantic input/command and cancellable query contracts alongside ISession; verify generation/lifetime behavior with host-free tests and document ownership/threading.
- [x] 2.2 Implement direct local owner adapter around existing TerminalSession/Ghostty capture; verify one live terminal, no session-host launch or transport, and no serialization on the local path.
- [ ] 2.3 Migrate text rendering and cursor/theme consumers to the frame contract; verify routed Ghostty visual tests and compare serverless render/echo timing against B before continuing.
- [x] 2.4 Migrate local keyboard/mouse/paste/focus encoding through semantic commands while keeping Windows translation and IME in UI; verify existing input/IME suites, repeat/release, AltGr/dead keys, broadcast and read-only behavior.
- [x] 2.5 Inventory remaining direct emulator access in selection/search/UIA/control/history/graphics; deliver a consumer checklist with explicit replacement operation and retain local regression tests as each consumer moves.

## 3. Hosted text and input prototype

- [x] 3.1 Extend proto negotiation and generated bindings with presentation/input versions, incarnation/epoch/sequence fields and bounded payload validation; verify malformed frames, unsupported peers and legacy v2 wire compatibility.
- [x] 3.2 Implement host viewport capture and background publication without pipe writes under the feed lock; verify delta bases, atomic attach, resize generations, final output and synchronized-output holds.
- [x] 3.3 Implement the bounded UI frame cache without constructing or feeding Ghostty; verify one host owner/zero UI live cores and identical grapheme/style/link/cursor readback on a text fixture.
- [x] 3.4 Implement ordered semantic input channel and owner encoding/PTY writer with explicit overflow/disconnect results; verify no duplicate response, no per-key round trip, no lost release and no input replay after connection loss.
- [x] 3.5 Add slow-reader and output-flood tests; verify bounded queues, input service between feed turns and recovery by a full viewport after dropped/superseded deltas.
- [x] 3.6 Run the early A/C text/input latency and throughput experiment; document chosen encoding, queue byte caps, lock budgets and whether improvement/non-regression gates pass before adding richer payloads.

## 4. History, selection, effects and accessibility

- [x] 4.1 Implement bounded history viewport queries, tracked selection/anchor operations and copy extraction in owner; verify wide graphemes, soft wrap, reflow, alternate-screen and concurrent-output selection in both modes.
- [x] 4.2 Move search to owner snapshot workers with cancellation/results limits and generation checks; verify Unicode folding, stale results, closed sessions and input latency while searching a large history.
- [x] 4.3 Migrate UIA and SurfaceText/control reads to consistent cached or bounded query results; verify screen-reader ranges, command-output boundaries and all-history readback without blocking UI paint.
- [x] 4.4 Route display injection, clear/reset/theme and persistence operations to owner; verify current control API side effects, save/restore history fidelity and no saved command/buffer replay into adopted sessions; document any identified compatibility difference before rollout.
- [x] 4.5 Forward ordered host effects and durable metadata, with owner-only terminal replies/completion persistence; verify OSC clipboard policy, title/cwd, progress, bell/notification behavior and no duplicate ephemeral effects after reconnect.

## 5. Graphics and complete UI migration

- [x] 5.1 Implement bounded image payload transfer, generation references, eviction/re-request and session-based UI caches; verify layers, placements, links, Unicode placeholders, oversized payload rejection and reconnect correctness.
- [x] 5.2 Move animation state/deadlines to owner and preserve UI GPU/raster caches; verify static images cause no polling and animated images remain correct under hidden/visible transitions and stalled transport.
- [ ] 5.3 Finish renderer, scroll, input, search, UIA, control and error-presentation migration; verify no new-path UI code casts to Ghostty or feeds terminal bytes, using the consumer checklist and live counters.
- [ ] 5.4 Run paired serverless regression checks including default-terminal handoff, graphics, history and IME; deliver B/D results proving the common abstraction did not introduce IPC, another live core or measurable regressions beyond the gates.

## 6. Lifecycle and upgrade compatibility

- [x] 6.1 Remove legacy managed/Rust backend choices and old Windows hosts from the shipped runtime; retain Ghostty hosted/serverless operation and the Rust Linux helper. Migrate old settings and verify effective owner diagnostics.
- [x] 6.2 Preserve creation tickets, cancellation fencing, attachment epochs and pipe disposal ownership; verify failures during create/attach/write/resize/exit, late callbacks and malformed/oversized requests do not leak or mutate replacement sessions.
- [x] 6.3 Test new-version UI quit/crash, host loss and reconnect with large history/images; preserve new-host process identity and final-output ordering. Separately test upgrade from installed 0.20.19 and Ghostty preview: save state, close old sessions, restore saved state without old-host attachment.
- [x] 6.4 Run WSL/private-desktop and available real remote recovery suites; verify exact surviving transport adoption, daemon incarnation and no replay, documenting unavailable real environments separately from synthetic coverage.
- [x] 6.5 Implement and document save-before-close installer migration, state backup and failure recovery. Verify layout/history/settings fidelity, explicit loss of running process execution, and safe retry if save or shutdown fails.

## 7. Integrated performance and rollout

- [ ] 7.1 Run sequential paired A/C and B/D latency/throughput/CPU/memory matrix on pinned worker resources; deliver raw samples, per-run distributions, confidence bounds, configuration hashes and explicit pass/fail/inconclusive for every gate.
- [ ] 7.2 Run at least 60 minutes of output/resize/search/attach churn with 1/8/32-session workloads within worker limits; verify bounded memory/queues, no missing input/output and stable tail latency.
- [x] 7.3 Investigate any missed hosted latency-improvement target or regression using stage/lock/queue timings; deliver a revised measured result, keeping the path experimental until acceptance is resolved.
- [x] 7.4 Run relevant full local/CI checks and inspect/wait for clean Greptile feedback on the final implementation commit; deliver validation evidence with unresolved environment limits clearly identified.
- [x] 7.5 Enable the new path for new Ghostty hosted sessions after acceptance, retain serverless operation and old saved-state import, and update migration/performance documentation; verify final configuration routing and one-instance diagnostics in packaged builds.

## Workflow follow-up

- Review these planning artifacts, then implement through the apply workflow when requested.
- Archive only after implementation and the repository's validation/review requirements are satisfied.
