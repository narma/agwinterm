# Tasks

## 1. Origin projection

- [x] 1.1 Implement endpoint-scoped owned-pane eligibility using independent exact observations; verify unit tests for ownership, process/socket replacement and distribution/user/origin isolation.
- [x] 1.2 Implement bounded agterm envelope projection for single/split layouts with stable IDs, canonical roles and no presentation advertisement; verify decoder roundtrips, partial splits, duplicate identities and swapped roles.
- [x] 1.3 Record the implemented projection boundary and pending deployment/acceptance in development documentation and the roadmap; verify links and strict OpenSpec validation.

## 2. Live origin integration

- [x] 2.1 Add a bounded read-only origin export control path with explicit endpoint/window routing, off-thread native probes and generation revalidation; verify concurrent close/replacement and multi-window tests, document the control contract.
- [x] 2.2 Implement private immutable export manifests and the native agterm attachment shim using the connected-FD client; verify no-create, name reuse, symlink/UID isolation, managed leadership and rejected trailing commands in native tests.
- [x] 2.3 Implement restricted agtermctl discovery/control relay and explicit adapter provisioning without SSH service changes; verify missing UI/interop, malformed requests, limits, unsupported commands and provisioning rollback, document setup and disable procedures.

## 3. Acceptance

- [x] 3.1 Run routed WSL integration for publication, partial split, stale snapshot, Windows UI restart and explicit owned termination; retain logs and verify no user-desktop UI.
- [x] 3.2 Run an actual pinned agterm viewer against the WSL origin for single/split, input/read, different sizes, leadership, disconnect/reconnect, both app restarts and endpoint/user isolation; record build hashes and evidence, leave unavailable checks pending.
- [x] 3.3 Run stage-2 and ordinary/local WSL regressions, complete current-head CI and clean Greptile review if a PR is opened; update stage-3 status only from passing evidence.

## Workflow follow-up

- Archive only after acceptance/review and update the roadmap archive link. Stage 4 remains required and separate.
