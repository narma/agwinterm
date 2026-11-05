# Design

## Context

See proposal.md. Existing host-owner work is preserved at b8e7a9c on codex/single-owner-ghostty-sessions. Pinned v16 measurements remain under artifacts/single-owner. The baseline uses a UI Ghostty replica plus a host Ghostty; the new comparison removes host parsing rather than moving rendering to remote frames. Bundled ConPTY forwards terminal queries, so a parser must keep running with no visible windows.

## Goals / Non-Goals

Goals: measure steady-state UI-local parsing, preserve shells through owner recovery, and test checkpoint interference before a wider migration. Non-goals: uninterrupted owner-crash recovery, host-crash/power-loss survival of live processes, replacing existing WSL/remote transports, Rust rewrite, multi-client editing, or default rollout.

## Decisions

- Use a separate opt-in journal namespace/capability. Never reinterpret a surviving legacy or host-owner session as a journal session.
- Retain the .NET broker and C wrapper for the first comparison. A language rewrite would confound architecture measurements.
- Broker owns PTY and bounded journal; UI owns the only Ghostty. Broker records raw output before publication and orders resize events with output. Input and checkpoint transfer are independent of output publication.
- Start with a 64 MiB retained-event budget and a 64 MiB checkpoint limit per session. These are explicit prototype caps, not claimed optimal defaults. Full retention blocks the producer until a checkpoint covers a prefix; never trim based solely on a reader cursor.
- Checkpoints contain the native parser snapshot plus event position and initial/current dimensions. Capture happens against a coherent owner generation; storage happens off the UI thread. Store a replacement before atomically switching checkpoint identity and trimming. Measure the existing synchronous native capture, including its owner-lock hold, before optimizing it.
- Attachment epochs revoke older writers. Terminal responses carry source event/response indices so replay can regenerate an unaccepted response while the surviving broker deduplicates already accepted ones. User input is never replayed. Replay suppresses ephemeral effects; live effects resume at the attachment barrier.
- All input-channel records, including terminal responses, have one monotonically increasing submission sequence. A checkpoint includes the input cut observed at capture; broker publication waits until that cut is accepted before trimming output or response IDs. This prevents a separate checkpoint upload from overtaking a queued terminal reply. It adds no per-key acknowledgement wait.
- Storage in this comparison is broker memory, not a durable disk log. Capture triggers after 8 MiB of output or 8192 terminal responses. The response trigger prevents reply deduplication metadata from growing until its hard limit on query-heavy workloads.
- Resize and injection use the ordered writer queue: the input reader must remain free to accept the checkpoint cut while the writer waits for retention space. Prefix trimming uses contiguous sequence lookup, avoiding repeated sorted-tree enumeration under the publication lock.
- Publication batches only already-retained events (up to 128 records or approximately 64 KiB). It never waits for a batching timer; sparse interactive output is published immediately. Original event boundaries remain intact for deterministic response identities.
- Keep the owner process alive with no visible windows; do not migrate Ghostty on ordinary close/reopen. Broker survival permits recovery after owner failure; a pause and application timeouts in that exceptional situation are acceptable per the user.
- Reuse T0–T8 fixtures, single-key and marker-burst patterns, fixed-output and trace-disabled bulk measurements. Add checkpoint-on/off comparisons. Retain existing non-regression tolerances; modest bounded memory growth remains informational. Do not reuse old binaries under a new label.

## Risks / Trade-offs

- Native checkpoint serialization can stall input despite asynchronous upload -> record stage/lock durations and paired checkpoint interference before acceptance.
- Slow checkpoint production can fill retention -> bounded backpressure and explicit pressure metrics; no data loss or unbounded RAM.
- Closing windows differs from process exit -> explicit lifecycle tests and opt-in routing; no change to ordinary backends.
- UI crash delays terminal replies -> permitted recovery pause, fenced attachment and response deduplication, not a second continuously running parser.
- Native checkpoint format/version mismatch -> explicit incompatibility; do not truncate the only usable journal or silently recreate shells.

## Migration Plan

Build a journal/store and protocol contract first, then a minimal Windows hosted data path for early comparisons. Add checkpoints and background owner lifetime, test recovery and compare checkpoint-on/off latency. Keep both experimental paths and legacy packages available. Rollback disables journal mode for new sessions; existing journal sessions require their capable owner until explicitly closed. Full product migration requires a subsequent acceptance decision.
