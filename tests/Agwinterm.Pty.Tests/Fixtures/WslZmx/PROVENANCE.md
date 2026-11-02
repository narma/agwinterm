# Pinned model fixtures

`tree.json` and `contracts.json` are derived examples, not real-peer captures or a claim of interoperability.
agterm revision: `0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b` (MIT).

Source shapes:
- `agtermCore/Sources/agtermCore/ControlPayloads.swift`: ControlRemoteTree,
  ControlZmxEndpoint, ControlRemoteSession, ControlRemotePane.
- `agtermCore/Sources/agtermCore/ZmxLead.swift`: ZmxLeadNotice and ZmxScreen.
- `agtermCore/Tests/agtermCoreTests/ZmxLeadTests.swift`: role/generation cases.

Source: https://github.com/umputun/agterm/tree/0716007052a6cedb8c43bb745ce6e5e7d3bc6d2b
The Linux executable/socket paths are origin paths. Local pane IDs are allocated
independently. Optional remote presentation fields are deliberately not advertised
by this local-only adapter seam.

`remote-empty.json` is a read-only real-peer capture on 2026-11-02 from macOS
arm64 agterm 0.38.1, with JSON slash escaping normalized. It is the complete
`ok/result/remote` envelope. Live mode was disabled, so sessions are empty.
See `docs/agterm-remote-development.md` for the binary hash and limits of this
evidence. Nonempty envelope tests wrap/mutate the derived model fixture and
are not real-peer attachment evidence. Stage-2 field definitions were checked
against `ControlPayloads.swift` and `RemoteTree.swift` at
`9133258994a6cafb330ee354df949b76b1b2834b`.
