# Spec Delta

## MODIFIED Requirements

### Requirement: Compatibility is advertised by supported capability
Capabilities SHALL distinguish local WSL attachment, outbound agterm attachment, inbound export and remote presentation per endpoint/provider, preserving origin and pane identities. Inbound export SHALL require an explicitly provisioned, verified adapter. Presentation SHALL remain unsupported until separately negotiated. Unsupported requests SHALL refuse; outbound support SHALL NOT grant local export, presentation or remote termination.

#### Scenario: Local endpoint queried for remote support
- **WHEN** diagnostics are read for a ready local WSL endpoint
- **THEN** local attach capabilities are distinct from provisioned agterm tree capabilities and unsupported presentation capabilities

#### Scenario: Export adapter has not been provisioned
- **WHEN** a local WSL workload is running without an export adapter
- **THEN** the system does not advertise inbound SSH export as ready

#### Scenario: Outbound adapter is available
- **WHEN** diagnostics describe a configured and verified agterm origin
- **THEN** outbound terminal capabilities are reported for that origin without granting local WSL export, presentation or remote termination capabilities
