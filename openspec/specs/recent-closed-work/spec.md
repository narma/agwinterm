# recent-closed-work Specification

## Purpose

Let users inspect and recover recently closed local work across application restarts without losing failed recovery entries or duplicating live identities.

## Requirements

### Requirement: History has stable addressable entries
`recent list` SHALL return newest-first session/workspace summaries with exact stable entry IDs distinct from session/workspace IDs, source window ID, display name and remaining session count. History SHALL be shared by library windows of one app identity; quick terminals SHALL not add entries. Listing SHALL neither launch nor select work.

#### Scenario: Discover closed work
- **WHEN** sessions and a workspace are closed in different windows
- **THEN** one ordered list exposes unique entry IDs, including entries whose source window is now closed or deleted

### Requirement: Open uses an explicit eligible destination
`recent open ID` SHALL require an exact entry ID and route to the explicitly selected open library window or the active library window when omitted. Unknown, ambiguous, closed and quick destinations SHALL refuse before mutation. Opening SHALL select restored work within the destination without forcing OS activation. Original session workspace SHALL be reused only when present in that destination; otherwise its active workspace SHALL be used.

#### Scenario: Deleted source window
- **WHEN** an entry's source window was deleted and another open window is explicitly selected
- **THEN** restoration occurs only in that destination, without reopening the source window

### Requirement: Reopening preserves supported local state
Recovery SHALL restore up to two panes with saved order, axis, ratios, font sizes, active pane, profile/cwd, names, flags, context and restore-command pins. Free identities SHALL remain stable; live collisions SHALL receive fresh identities and the result SHALL report actual IDs. Transient surfaces and process state SHALL not be restored. Recovery SHALL not infer or replay foreground commands.

#### Scenario: Swapped WSL split
- **WHEN** a swapped horizontal split with WSL profile, distinct directories and pins is reopened
- **THEN** its pane identities/layout and supported metadata return under the same distribution/user, with pins retained and no arbitrary command replay

### Requirement: Workspace recovery retains unsuccessful members
Opening a workspace SHALL report per-session successes/errors and remaining count. Only successfully restored members SHALL leave history. A partially restored entry SHALL retain its entry ID and destination workspace for retry in that window; retry elsewhere SHALL refuse until the user clears it. A failed session restore SHALL leave its record available and clean up any partially created session.

#### Scenario: Retry a partial workspace
- **WHEN** one workspace member fails while another succeeds
- **THEN** success is reported honestly, the failed member remains, and retry in the same destination restores only remaining members without duplicating successful ones

### Requirement: Persisted history is bounded and validated
History SHALL persist independently of live restore files, bounded to 25 session and 25 workspace entries, 1024 total member sessions and 8 MiB serialized data. Overflow SHALL evict oldest whole entries. Loaded data SHALL validate identities, counts, strings and pane numeric values. Corrupt files SHALL be retained for inspection and yield empty history with a diagnostic; write failures SHALL be visible while retaining in-memory state.

#### Scenario: Restart and corruption
- **WHEN** the app restarts after closing work
- **THEN** surviving entries retain IDs and metadata; invalid/corrupt history cannot launch work automatically or damage live restore state

#### Scenario: Eviction
- **WHEN** history exceeds a bound
- **THEN** oldest whole entries are evicted deterministically and no surviving workspace is silently truncated

### Requirement: Clearing affects only history
`recent clear` SHALL clear all history, or one exact entry when an ID is supplied, and persist the result. Unknown explicit IDs SHALL refuse. Menu clear/reopen actions SHALL use the same history and recovery behavior. Live sessions, restore pins and `restore clear` checkpoint semantics SHALL be unaffected.

#### Scenario: Clear with live work
- **WHEN** history is cleared while sessions are running
- **THEN** those sessions and their restore configuration remain unchanged and cleared history stays absent after restart

### Requirement: Requests are validated before dispatch
CLI and wire SHALL reject unknown, duplicate or incompatible options and invalid IDs before mutation. Local CLI misuse SHALL exit 2; refused requests SHALL exit 1, including JSON output. Accepted workspace recovery SHALL return a structured completed/partial outcome with member errors; partial outcome SHALL not claim that every member reopened.

#### Scenario: Incompatible selector
- **WHEN** recent list or clear is given a session target or destination window
- **THEN** the request refuses without clearing history or selecting work
