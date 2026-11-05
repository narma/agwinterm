# Tasks

## 1. Profile
- [x] 1.1 Deliver a reproducible v9 stage/CPU/allocation and T0-T8 breakdown, identify the first bounded optimization and document measurement limits.

## 2. Transport optimization
- [x] 2.1 Implement reusable dedicated journal reading, preserve framing/borrowed-byte lifetimes and validate fragmented, coalesced, malformed and cancelled reads with host-free tests.
- [x] 2.2 Pin the candidate, run routed contracts/lifetime and an early fixed-output comparison; use results to retain or revise bounded transport optimizations and record before/after findings.

## 3. Architecture decision
- [x] 3.1 Run serial traced latency and trace-disabled bulk comparisons for all three candidates plus sparse input verification; retain hashes, CPU/memory and explicit statistical limits.
- [x] 3.2 Complete relevant integration/local checks and deliver measured architecture recommendation with default rollout disabled.
