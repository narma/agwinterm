# Spec Delta

## MODIFIED Requirements

### Requirement: Lifecycle events describe committed changes
C1 SHALL add types selection, focus, split, scratch, overlay, hud, and session-status alongside existing status/notification/session/tree/bind types, using the lifecycle payload contract below. UI and API changes SHALL produce equivalent events after state commit. Refusals and unchanged operations SHALL not produce false change events. Close/removal SHALL terminate observers of the removed identity.

#### Scenario: Background focus and refused resize
- **WHEN** background focus changes and an axis-incompatible resize is refused
- **THEN** a focus event identifies the changed session/pane, no selection event is generated, and no split resize event is emitted for the refusal

#### Scenario: Overlay replacement and process exit
- **WHEN** an overlay is replaced and its old process exits afterwards
- **THEN** the close/open events identify distinct overlay IDs and a late old-process callback cannot report an exit for the new overlay

#### Scenario: Lifecycle payload schema

All rows include `window`, `action` and applicable exact `sessionId`/`paneId`. Optional absent owners are omitted, never filled with an unrelated active owner.

| type | actions | additional fields |
| --- | --- | --- |
| selection | selected, cleared | previousSessionId when present; new sessionId when selected |
| focus | changed | previousPaneId, paneId (new main/split focus) |
| split | opened, closed, axis-changed, resized, swapped | paneIds in current slot order, focusedPane, axis; ratios for split layouts; removedPaneId on close |
| scratch | opened, shown, hidden, closed | paneId of scratch |
| overlay | opened, exited, closed | overlayId, slot (session|pane), ownerPaneId for pane slot; exitCode on exit; reason on close (closed|replaced|owner-removed) |
| hud | opened, updated, closed | slot=session, anchorPaneId when anchored, reason on close (closed|replaced|owner-removed|expired); no message body |
| session-status | changed | status, previousStatus, winningPaneId, statusChangedAt |
| status (existing) | changed | paneId, status, statusChangedAt, blink, autoReset; retain legacy session/info |

Existing session lifecycle records gain `action:created|closed` and `sessionId` while keeping legacy `session`/`info`. Other legacy event types retain their existing fields and gain applicable owner IDs. Overlay hidden/shown actions are reserved for C2 and SHALL NOT imply that C1 implements those operations.
