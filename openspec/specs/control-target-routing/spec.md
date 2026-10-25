# control-target-routing Specification

## Purpose

Make explicit control targets reliable for pane focus, split resizing and custom commands without disturbing unrelated visible sessions.

## Requirements

### Requirement: Explicit targets constrain the operation
Session focus, session resize and command run SHALL honor explicit targets through both CLI and raw control requests. An explicit window SHALL bound target resolution. Unknown, ambiguous, empty or incompatible targets SHALL fail without mutation or fallback to an active session.

#### Scenario: Target outside selected window
- **WHEN** a request selects window A and a session belonging only to window B
- **THEN** it is refused and neither window's sessions change

#### Scenario: Ambiguous prefix
- **WHEN** a supplied target prefix matches more than one eligible target
- **THEN** the operation refuses without choosing the active session

#### Scenario: Target disappears while queued
- **WHEN** a queued request's explicit target is removed before execution
- **THEN** it fails without substituting the newly active session

### Requirement: Legacy untargeted operations remain active-scoped
For focus, resize and command run, an omitted target SHALL retain the existing active-session behavior in the routed window, including when caller environment identifies another pane. Explicit `active` SHALL select the active context of that window. Other commands' caller-based defaults SHALL remain unchanged.

#### Scenario: CLI caller differs from selected session
- **WHEN** the CLI runs from pane A while B is selected and invokes focus without an explicit target
- **THEN** B remains the operation's target as in the existing documented active-session behavior

### Requirement: Background split operations do not select the session
Targeted focus SHALL change the target session's remembered pane focus; targeted resize SHALL change only its divider. Neither SHALL select that session, raise its window, clear its unseen notifications or reorder session recency solely to perform the operation.

#### Scenario: Focus a background split
- **WHEN** B is visible and focus selects the other pane of background session A
- **THEN** A's remembered focus changes while B remains visible and owns keyboard input

#### Scenario: Resize a background split
- **WHEN** A's divider is resized while B is selected
- **THEN** only A's ratio changes and the new layout is used when A is next shown

#### Scenario: Invalid direction or single pane
- **WHEN** focus or resize targets a single-pane session or uses an axis-incompatible direction
- **THEN** it refuses without changing focus or divider state elsewhere

### Requirement: Pane identity and split units remain stable
An explicit main/split pane ID for focus or resize SHALL resolve its owning session without changing pane IDs. Auxiliary panes SHALL be refused for these split operations. Existing ratio bounds, integer row/column growth units and axis meanings SHALL remain unchanged.

#### Scenario: Target after swap
- **WHEN** focus or resize names a pane ID that moved sides in a swap
- **THEN** it acts on that pane's owning session and retains all existing IDs

#### Scenario: Quick target for split resize
- **WHEN** resize addresses a quick-terminal target
- **THEN** the operation refuses instead of mutating a library session

### Requirement: Custom commands use the addressed context
Explicitly targeted command run SHALL derive its input destination, cwd, workspace, profile and AGW context from the addressed session or pane, using existing pane resolution rules. Named and raw commands SHALL follow the same routing rules. Windows and WSL execution SHALL retain their respective existing launch contracts.

#### Scenario: WSL command from another session
- **WHEN** a command is targeted to a WSL pane while a Windows session is selected
- **THEN** its execution context uses the target distribution/user and Linux cwd, and AGW identity describes that target

#### Scenario: Exact pane send with broadcast enabled
- **WHEN** a send command explicitly targets pane A while another session has workspace broadcast enabled
- **THEN** only A receives the command and the existing untargeted broadcast behavior is unchanged

#### Scenario: Readonly destination
- **WHEN** a send command explicitly targets a readonly or input-closed pane
- **THEN** it reports refusal without sending to another pane

### Requirement: Target selection does not add command side effects
Resolving a custom-command target SHALL NOT temporarily select or raise its session. Existing mode-specific behavior SHALL remain: new mode can select its newly created session, overlay mode opens over its target, and detached mode launches independently. Success SHALL describe accepted input or launch, not prove program completion.

#### Scenario: New mode workspace
- **WHEN** new mode runs against a background session
- **THEN** the new session is created in the target's workspace/profile context and retains normal new-mode selection behavior without first selecting the source session

#### Scenario: Quick command restrictions
- **WHEN** command run addresses quick with new or overlay mode
- **THEN** it retains the documented refusal; supported send/detached modes retain quick context

#### Scenario: Transport uncertainty
- **WHEN** an input or timeout failure cannot establish whether bytes reached the child
- **THEN** the reply reports failure or uncertainty without claiming rollback or safe retry
