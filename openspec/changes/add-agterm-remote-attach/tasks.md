# Tasks

Implementation started on 2026-11-02 from `origin/narma` at `5552130` (stage 1 merged). The origin bridge and shared agwinterm-zmx member are implemented. Real-client single/split and recovery acceptance passes against an isolated macOS origin; expanded fault cases and review remain in progress. See `docs/agterm-remote-development.md` for evidence and continuation.

## 1. Origin capability and compatibility gate

- [ ] 1.1 Capture the real tree/control envelopes and build provenance at the design's agterm pin, including the new managed-exit patch; verify fixtures against upstream source and document required versus optional fields.
- [x] 1.2 Implement the explicitly provisioned origin bridge's bounded probe and platform identity observation; verify on macOS that process/name/socket replacement is detected without Linux `/proc` or pidfd assumptions.
- [x] 1.3 Port and build the connected-FD client seam for the test origin; prove missing-daemon and replacement races create no workload and retain the verified connection through attach. Record exact source/build hashes and capability evidence.
- [x] 1.4 Document real-peer setup and rollback, then verify discovery/attach prerequisites on the supplied authorized agterm host. Leave this gate unchecked if no real host is available; do not install dependencies or SSH services implicitly.

## 2. Remote contracts and WSL SSH transport

- [ ] 2.1 Add validated remote configuration and binding models separate from local ownership; verify malformed/duplicate fields, changed WSL defaults, roundtrip identity and fail-closed restore tests.
- [ ] 2.2 Implement structured WSL SSH command construction and remote shell quoting; verify hostile destinations, spaces, apostrophes, backslashes and account-shell fixtures cannot change argument boundaries.
- [x] 2.3 Implement finite non-PTY requests with bounded output, deadlines and cancellation; verify failed exit with success-shaped stdout, truncated output, stderr saturation, lost replies and no prompt/configuration changes.
- [x] 2.4 Decode real tree envelopes and eligibility/topology; verify empty success, page companions, unknown additive metadata, reversed panes, duplicate roles/IDs, missing endpoint and axis mapping. Document discovery errors and limits.

## 3. Terminal import and borrowed lifetime

- [ ] 3.1 Add `zmx remote list|attach` and raw `zmx.remote.*` routing with exact local window and origin selectors; verify usage/refusal exit codes and unchanged stage-1 command contracts.
- [ ] 3.2 Implement prevalidated session/split import through Rust -> WSL SSH -> origin client; verify role-to-pane mapping, local/origin ID separation, disappeared target window and partial connection outcomes in routed integration tests.
- [ ] 3.3 Add borrowed close/detach behavior and refuse terminate/restart; verify app exit, failed import, split close and startup cleanup never invoke remote lifecycle mutation, and document ownership in UI/CLI readback.
- [ ] 3.4 Add explicit leadership control and current-attachment role filtering; verify two client sizes, follower resize/input, stale notices and reconnect cannot steal leadership.

## 4. Authoritative control and recovery

- [ ] 4.1 Implement origin bridge screen/cursor and acknowledged input operations on verified connections; verify readonly/control-byte rules, unsupported styled queries and exactly one send after a lost acknowledgement.
- [ ] 4.2 Integrate authoritative terminal interception for remote panes; verify origin geometry is returned even when the local follower layout differs, without active-pane fallback.
- [ ] 4.3 Add exact Rust-ticket adoption and serialized reconnect with bounded backoff; verify wrong tickets, unavailable origin, name reuse and close/retry races cannot attach replacement work or resurrect retired panes.
- [ ] 4.4 Persist remote metadata and suppress every replay source; verify UI/host restart, changed defaults, malformed bindings and no buffer/resume/pin/captured command injection.
- [ ] 4.5 Preserve remote descriptors through Recent and metadata clearing; verify explicit reopen is existing-only, absent origin stays unavailable and clearing history does not terminate remote work. Document downgrade/disable behavior.
- [ ] 4.6 Add endpoint-scoped capability and recovery diagnostics; verify outbound support never enables inbound WSL export, presentation or remote kill and document exact supported peer builds.

## 5. Cross-product acceptance

- [ ] 5.1 Run a real agterm-origin matrix through the routed WSL private desktop: single/split panes, input, authoritative reads, two client sizes, explicit lead, detach/close, UI/Rust-host loss and network reconnect. Verify original workload identities and absence of duplicate execution throughout.
- [ ] 5.2 Verify terminal continuity with both real clients: history once, hidden primary/alternate screens, styled wide glyphs, cursor, keyboard/paste modes, single query replies and managed-client exit behavior. Record precise known limitations rather than substituting fixture claims.
- [ ] 5.3 Run stage-1 local WSL recovery and ordinary Windows restore/Recent/control regressions using mandated private-desktop/background-Sandbox routing; verify no ownership, replay or UI isolation regressions.
- [ ] 5.4 Complete Release/unit/strict OpenSpec checks and a sanitized evidence report with commands, platform/build hashes, outcomes and missing coverage; accept stage 2 only when real-peer tasks pass.

## Workflow follow-up

- Stage 1 is already merged in `origin/narma` (`5552130`). Implementation branch `codex/agterm-remote-attach-implementation` starts from that commit and carries the existing proposal without duplicating stage-1 commits.
- Review and archive this change only after implementation and acceptance are complete. Update the [roadmap](../../../docs/plans/2026-11-01-wsl-zmx-agterm-roadmap.md) with evidence and the archive link, fixing relative paths when moved.
- Propose stage 3 (`export-wsl-sessions-to-agterm`) next; bidirectional presentation remains required stage 4.
