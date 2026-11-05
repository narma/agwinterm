# Design

## Context

See proposal.md. v9 is pinned at commit 4083c34 and artifacts/single-owner/journal-v9-app. Its output reader runs on a dedicated thread but calls nested async framing reads, allocating a header and payload per record. The repository already has cancellation-safe PresentationPipeIo and a reusable buffered PresentationInputReader for host-owner. Broker output publication uses an async task signalled for each append. Roughly 197,000 writes carry the fixed-output pilot; snapshot capture is sub-millisecond, so checkpoint serialization alone cannot explain the observed CPU gap.

## Goals / Non-Goals

Goals: quantify the gap, remove avoidable hot-path work, preserve sparse-input responsiveness and evaluate all three modes. Non-goals: changing event boundaries/response IDs, batching timers, language changes, default adoption or larger recovery guarantees.

## Decisions

- First use the pinned raw stage/CPU/allocation evidence. Stage durations are elapsed observations, not CPU samples; do not subtract overlapping durations to claim exact CPU attribution.
- Prefer existing dedicated-thread overlapped pipe IO with reusable framing storage over async continuations per small event. Payload storage may be borrowed only until the next read; snapshot/input storage that outlives a read must remain owned.
- Preserve all original event boundaries, epoch/length validation before allocation, replay/effect fences and checkpoints. Bound any drain turn by bytes/time; publish sparse output immediately, never wait to fill a batch.
- Change one transport stage first and run an early routed pilot. Add publisher/notification changes only if the measured result still identifies them as useful within this bounded transport optimization.
- The v10 reader-only pilot reduced UI allocation/CPU substantially and left broker allocation near 209 MB per diagnostic run. A second bounded experiment tried a dedicated PresentationPipeIo publisher and lazy signal rearming. It reduced broker allocation but did not improve combined CPU/transport latency, so it was reverted. The selected implementation retains the original publisher and changes only the reader.
- Final comparisons retain identical fixtures, pinned packages, matched grids and serial VM runs. Trace-disabled bulk distinguishes real CPU from instrumentation cost. Preserve old statistical thresholds; inconclusive means inconclusive.

## Risks / Trade-offs

- Borrowed buffers reused too soon -> explicit lifetime contract and fragmented/coalesced record tests.
- Cancellation releases native storage while IO is pending -> reuse existing drained PresentationPipeIo, dispose only after reads return.
- Draining delays input/painting -> bounded turns and no wait for additional bytes; test sparse input as well as bulk.
- Micro-optimization does not close architectural gap -> retain measurements and recommend host-owner if warranted rather than expand into an unmeasured rewrite.

## Migration Plan

Keep journal mode opt-in. Pin the candidate beside v9, run framing and routed recovery/lifetime tests, then latency and trace-disabled comparisons. Rollback to 4083c34 restores the previous implementation without a protocol migration.
