## MODIFIED Requirements

### Requirement: Compatibility is advertised by supported capability
The endpoint model SHALL preserve origin and per-pane daemon identity needed for agterm adapters. Local WSL readiness SHALL NOT imply inbound SSH export readiness. Inbound export SHALL be advertised only for an explicitly provisioned and verified adapter endpoint. Presentation SHALL remain unsupported until separately negotiated. Unsupported requests SHALL refuse explicitly rather than claiming compatibility because basic ZMX attachment works.

#### Scenario: Local endpoint queried for remote support
- **WHEN** diagnostics are read for a ready local WSL endpoint
- **THEN** local attach capabilities are distinct from provisioned agterm tree capabilities and unsupported presentation capabilities

#### Scenario: Export adapter has not been provisioned
- **WHEN** a local WSL workload is running without an export adapter
- **THEN** the system does not advertise inbound SSH export as ready
