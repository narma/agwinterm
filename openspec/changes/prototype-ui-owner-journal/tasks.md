# Tasks

## 1. Journal contract
- [x] 1.1 Implement bounded ordered output/resize journal, cancellation and checkpoint-prefix publication; verify ownership copies, sequence boundaries, failed/stale checkpoints and blocked-producer wakeups with host-free tests.
- [x] 1.2 Add explicit journal capability, framed transport and attachment/response fences; verify malformed payload rejection and unchanged legacy negotiation.

## 2. Early interactive path
- [x] 2.1 Connect a parser-free broker to a single UI Ghostty; verify owner/replica counts, Unicode/modes/replies and exact output ordering in a routed Windows harness.
- [x] 2.2 Pin the prototype and run early paired legacy/host-owner/UI-owner echo and fixed-work measurements; retain raw samples, CPU/memory and explicit gate outcomes before expanding the prototype.

## 3. Checkpoints and lifetime
- [x] 3.1 Add owner snapshot capture and separate broker checkpoint storage, replay and prefix trimming; verify incomplete escape state, resize order, failed storage and no repeated input/effects/responses after owner replacement.
- [x] 3.2 Retain the owner with all windows closed and reopen it without state transfer; verify terminal queries keep receiving replies, no hidden rendering, resource release on explicit termination and recovery after owner crash.
- [x] 3.3 Run checkpoint-on/off paired latency and trace-disabled throughput/CPU checks plus bounded-retention pressure tests; deliver capture-lock/queue timing, memory peaks and recovery duration.

## 4. Comparison decision
- [x] 4.1 Run relevant local and routed integration checks and document supported prototype scope, remaining risks and a measured architecture recommendation; do not enable the path by default or mark the original migration complete.
