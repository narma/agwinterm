# Spec Delta

## MODIFIED Requirements

### Requirement: Viewer controls preserve origin ownership
Viewer disconnect and close SHALL preserve the origin workload. Leadership SHALL be explicit and scoped to a fresh attachment nonce; follower resize SHALL not resize the workload. The adapter SHALL restrict controls to supported exact exported targets and refuse terminal lifecycle mutation. Presentation and scoped job/page channels SHALL require separate negotiation and current origin authorization; terminal attachment alone SHALL not authorize them.

#### Scenario: Viewer closes
- **WHEN** agterm closes a remote pane
- **THEN** the WSL workload and other attachments continue until explicit origin-owned termination

#### Scenario: Different viewer sizes
- **WHEN** a follower resizes and later explicitly claims leadership
- **THEN** only the explicit claim changes the authoritative workload geometry

#### Scenario: Presentation has not been negotiated
- **WHEN** a terminal viewer requests presentation or a job/page channel without its required authorization
- **THEN** the adapter refuses without changing terminal access or starting a program

#### Scenario: Authorized interactive presentation
- **WHEN** the current presenter claims an origin-created job or page for the exact exported session
- **THEN** only that delegated interaction is available and no general command or terminal lifecycle authority is granted
