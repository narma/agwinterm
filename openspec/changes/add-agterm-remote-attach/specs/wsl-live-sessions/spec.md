# Spec Delta

## MODIFIED Requirements

### Requirement: Compatibility is advertised by supported capability
The endpoint model SHALL preserve origin and per-pane daemon identity. Capabilities SHALL be reported per endpoint and provider: local WSL attachment, outbound agterm attachment, inbound discovery/export and remote presentation are distinct. Unsupported requests SHALL refuse explicitly; adding an outbound adapter SHALL not advertise inbound export or presentation for a local WSL endpoint.

#### Scenario: Local endpoint queried for remote support
- **WHEN** diagnostics are read for a ready local WSL endpoint
- **THEN** local attach capabilities are distinct from unimplemented agterm tree and presentation capabilities

#### Scenario: Outbound adapter is available
- **WHEN** diagnostics describe a configured and verified agterm origin
- **THEN** outbound terminal capabilities are reported for that origin without granting local WSL export, presentation or remote termination capabilities
