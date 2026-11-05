# Proposal

## Why

The UI-owned journal prototype delivers input sooner but uses about three times the CPU of host-owner on traced fixed-output work. Before choosing an architecture, isolate transport, parsing and presentation costs and test a bounded optimization against the pinned v9 implementation.

## What Changes

- Profile existing v9 stage timings, allocation counters and per-process CPU, distinguishing wall-time observations from CPU attribution.
- Reduce measured output-path scheduling/allocation overhead without timer-based batching or changing journal event identity, checkpoint fencing, recovery or backpressure.
- Repeat routed comparisons of legacy, host-owner and optimized UI-owner, with traced latency and trace-disabled throughput/CPU/memory.
- Preserve pinned before/after evidence and document an architecture recommendation, including inconclusive results.

## Capabilities

### New Capabilities
None.

### Modified Capabilities
None. This is an implementation/performance refactor of the experimental journal contract; `skip_specs: true` preserves its existing behavior.

## Impact

Journal wire reader/publisher, existing dedicated pipe IO, measurement tooling and documentation. No runtime dependency, Rust rewrite, default rollout, recovery redesign or changes to legacy/host-owner semantics.
