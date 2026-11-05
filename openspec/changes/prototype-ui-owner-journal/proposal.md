# Proposal

## Why

The measured host-owner prototype reduces fixed-work CPU to approximately 30% of baseline, but its input delivery under load still misses the non-regression gate. Compare a UI-owned Ghostty with a parser-free PTY journal broker before extending the frame-transport architecture.

## What Changes

- Add an explicitly experimental journal mode with one Ghostty in the UI/owner process and no terminal parser in the broker. Keep the existing C wrapper and .NET host to isolate the architecture comparison.
- Journal PTY output and ordered resize events before publication. Bound queues and retained bytes; apply backpressure rather than lose output. Do not wait for disk or checkpoint acknowledgements per input event.
- Capture checkpoints in Ghostty, associate them with an exact journal position, store them in the broker, and trim only covered journal segments after successful checkpoint publication.
- Keep the owner process servicing terminals when all windows close, without rendering. Reopening reuses that owner. Following an owner crash, recovery may pause applications while restoring state; uninterrupted crash recovery is explicitly not a goal.
- Fence attachments and terminal responses; never replay user input or duplicate external effects during restoration.
- Measure the same echo/input/output workloads, CPU and memory, including checkpoint interference and trace-disabled throughput. Preserve pinned legacy and host-owner packages.

## Capabilities

### New Capabilities
- `journaled-terminal-owner`: Experimental parser-free PTY broker with a persistent UI-side terminal owner, checkpoint recovery and measured steady-state performance.

### Modified Capabilities
None. This opt-in prototype does not change the default window/session behavior or existing WSL/remote transports.

## Impact

Session backend, host negotiation, bounded journal transport, Ghostty checkpoint ownership, Win32 owner/window lifetime and routed measurement fixtures. No runtime dependencies, host-language rewrite, default rollout or forced adoption of existing sessions. The previous `single-owner-ghostty-sessions` implementation remains an experimental comparison, not a completed migration.
