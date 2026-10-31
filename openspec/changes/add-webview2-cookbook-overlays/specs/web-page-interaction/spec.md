# Spec Delta

## Purpose

Allow interactive local documents to return review results to agents through a bounded page-specific contract independent of program-overlay exit results.

## ADDED Requirements

### Requirement: Page control access is explicit and bounded
Trusted local HTML SHALL support upstream declarative controls and, with --js, asynchronous `agwinterm.request` and compatible `agterm.request` aliases, subject to pinned upstream command exclusions and current owner context. Explicit --bridge review SHALL restrict authority to `session.overlay.submit` and `session.overlay.close`, refusing other commands and target/window overrides. Subframe and URL/image/replica bridge requests SHALL refuse in every mode.

#### Scenario: Dashboard runs scripts without terminal authority
- **WHEN** an HTTP page opens with `--js` and attempts a control request
- **THEN** it has no authorized bridge and cannot submit another page's answer or control a terminal

#### Scenario: Local review tries to type a command
- **WHEN** an explicit --bridge review page requests session.type or supplies another session's target
- **THEN** the request is refused and no terminal input is sent

### Requirement: Bridge requests belong to a live document
Bridge authorization SHALL bind to the originating page ID and its authorized top-level document. Navigation away, reload or close SHALL invalidate old document requests. Message parsing SHALL reject malformed payloads and values larger than 1 MiB UTF-8 before changing outcomes. Submit SHALL accept only a string value.

#### Scenario: Late message from an old document
- **WHEN** a document queues a submit and the page reloads or is replaced before dispatch
- **THEN** the old request cannot finish the new document or replacement page

### Requirement: Local blocking requests use exact page outcomes
Local HTML --block SHALL wait for its exact page ID and exit 0 for submission, 2 for dismissal, and 1 for runtime/error failure. Empty submitted text SHALL remain a successful result. URL --block SHALL refuse. Existing program-overlay waiters SHALL retain their contract.

#### Scenario: Two reviews finish independently
- **WHEN** two blocking HTML requests own different panes and only one submits
- **THEN** only its waiter completes, and the other stays pending until its own completion or teardown

### Requirement: General bridge follows its live owner
Inherited general bridge context SHALL follow page ownership through pane swaps and moves. Explicit targets SHALL follow upstream validation. Forbidden commands and stale document tokens SHALL refuse before mutation.

#### Scenario: Declarative control after pane swap
- **WHEN** a trusted local page moves with its pane then invokes a declarative control without an explicit target
- **THEN** the control uses its current pane/session/window context

### Requirement: Page outcomes are independent and first completion wins
`session overlay result --page ID` SHALL return `result.pageOutcome` containing pageID, outcome and a string value only for submitted. Outcomes SHALL be pending, submitted or dismissed. Empty-string submission SHALL be success. The first completion SHALL be final; close without submission SHALL dismiss. Legacy program result replies SHALL remain unchanged.

#### Scenario: Submit races with host close
- **WHEN** a pending review submits an empty string and then the host closes it
- **THEN** its outcome remains submitted with an empty value and does not become dismissed

### Requirement: Results survive page teardown within the instance
Finished outcomes SHALL remain queryable after page, owner or window removal, up to the most recent 32 finished pages per application instance. Pending pages SHALL not be evicted. Unknown or evicted IDs SHALL refuse. Results SHALL not persist across application restart; recipe adapters SHALL support saving returned results independently.

#### Scenario: Agent recovers after its wait times out
- **WHEN** a review completes after its caller stopped polling and the page closes
- **THEN** the caller can retrieve that retained result by its original page ID without reopening the document

### Requirement: Successful completion closes only its page
Accepted submit SHALL record the value before closing that page. Host close, owner removal and application shutdown SHALL dismiss pending reviews. Hidden pages SHALL remain pending. Failure of navigation or rendering SHALL expose a load error without fabricating a submission; explicit close SHALL still dismiss the page.

#### Scenario: Close a background review owner
- **WHEN** a session with a hidden pending review is removed
- **THEN** its outcome becomes dismissed and a different session's review remains untouched

### Requirement: Review state survives non-destructive presentation changes
Local review storage SHALL be isolated per page and remain available across reloads of that page. Hide/show, DPI changes, resize and theme changes SHALL preserve DOM edits. Closing SHALL release the page's temporary storage; no persistent browser profile SHALL be required for review notes.

#### Scenario: Reload with saved notes
- **WHEN** a review writes notes to localStorage and explicitly reloads
- **THEN** its saved notes remain available to the reloaded document under the same page identity

### Requirement: Annotate-document round trip is compatible
The adapted review recipe SHALL render Markdown and highlighted code with bundled assets, return annotation text through submit, distinguish cancel from an empty successful review, and recover by page ID. Its script SHALL map submitted/dismissed/error to exits 0/2/1; ordinary control CLI success and refusal codes SHALL retain their existing contract.

#### Scenario: Agent receives a review
- **WHEN** the user annotates a block and a whole file then submits
- **THEN** the adapter prints the corresponding annotation records and exits 0; a subsequent cancelled review exits 2 without printing a successful empty answer
