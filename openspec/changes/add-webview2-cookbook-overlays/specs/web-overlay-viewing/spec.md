# Spec Delta

## Purpose

Display local documents and web applications within native terminal sessions without imposing browser startup on terminal-only use.

## ADDED Requirements

### Requirement: Web runtime is optional until requested
Terminal-only startup SHALL neither initialize browser processes nor require a browser runtime installation. A web request with an unavailable runtime SHALL return an actionable error without modifying existing overlays or preventing terminal use. Runtime installation SHALL require an explicit user action.

#### Scenario: Runtime missing on a portable installation
- **WHEN** a user starts the portable application without the runtime and requests a web overlay
- **THEN** the terminal remains usable, the request identifies the missing dependency and no download or replacement occurs automatically

### Requirement: Web opening has explicit source and identity
Web open SHALL accept exactly one supported source, return `result.pageID`, and create a session-wide or explicitly pane-owned page with JavaScript disabled unless `--js` is supplied. Invalid sources, incompatible options and ambiguous targets SHALL refuse before mutation. HTML, HTTP(S) URL and bounded image sources SHALL follow their respective authority contracts.

#### Scenario: Background page opens
- **WHEN** a valid HTML request targets an unselected session without follow
- **THEN** it returns a page ID without selecting or raising that session and later reports loading completion or failure

#### Scenario: Mutually exclusive sources
- **WHEN** a request supplies both a program command and a web source, both web sources, or an unsupported scheme
- **THEN** CLI validation exits 2 before dispatch or raw validation refuses through the ordinary envelope without mutation

### Requirement: Web ownership respects occupied slots
A web open SHALL refuse an occupied program or web session slot; a successful web open MAY replace a HUD. Opening a program or HUD into a web-occupied session slot SHALL refuse until explicit close. Validation SHALL precede HUD replacement. Existing program-to-program and HUD-to-program rules SHALL remain unchanged.

#### Scenario: Hidden program protects its slot
- **WHEN** a web open targets a session with a hidden program overlay
- **THEN** it refuses and preserves the program, its visibility and its waiter

### Requirement: Page readback distinguishes loading and visibility
Session tree SHALL add `htmlOverlays` with `id`, optional pane identity, original source, current URL, title, history availability, navigation/JS/chromeless/persistent/browse flags, `state` in loading/loaded/failed, error on failure, zoom, hidden, visible and geometry. Existing program readback SHALL retain its meaning. Web occupancy SHALL set the session overlay presence flag. Geometry SHALL match rendered client pixels without selecting background owners.

#### Scenario: Failed navigation is observable
- **WHEN** a page fails navigation
- **THEN** tree reports failed and a diagnostic for the same page ID, rather than claiming open acceptance meant successful loading

### Requirement: Native navigation and geometry remain usable
Pages SHALL support reload, back/forward and explicit external-browser handoff, optional `--navigation`, `--chromeless`, `--follow`, percentage and independent DIP width/height sizing. Web resize SHALL use existing session-overlay sizing rules. Navigation and chromeless SHALL be mutually exclusive. Host close SHALL remain available in chromeless mode.

#### Scenario: Fitted review on another DPI
- **WHEN** a page is resized using DIP dimensions then moved to another DPI
- **THEN** requested dimensions remain stable, geometry is clamped to available content and zoom readback permits cookbook fit adjustment

### Requirement: Local content has bounded resource access
HTML paths SHALL resolve in the target Windows or WSL context, including spaces and Unicode. Without --cwd only the entry document SHALL be readable. An explicit --cwd SHALL grant its canonical directory, require the entry within it, reject filesystem/home roots, and reject traversal and links escaping it.

#### Scenario: WSL report includes sibling resources
- **WHEN** a report with an explicit --cwd grant in the addressed WSL distribution references an image beside it and another through an escaping symbolic link
- **THEN** the sibling image is available and the escaping resource is refused without exposing other files

### Requirement: Documents cannot expand local file authority
Local-document navigation leaving its allowed origin/root SHALL require explicit external handoff. Review authorization SHALL remain bound to its original document. File chooser, file paste and external drops SHALL be refused.

#### Scenario: A page requests file selection
- **WHEN** a page activates a file input or receives pasted or dropped files
- **THEN** the browser grants no file access

### Requirement: Remote navigation modes match agterm
URL pages SHALL pin their original origin by default. Explicit `--browse` SHALL allow HTTP(S) redirects and links across origins without per-navigation confirmation. Navigation SHALL never grant local-resource access or a host bridge. Unsupported schemes and embedded URL credentials SHALL refuse. A refused navigation SHALL preserve an already loaded document; a refused initial navigation SHALL explain the restriction rather than expose OperationCanceled.

#### Scenario: Bare web domain redirects to canonical host
- **WHEN** a remote page opened as `https://google.com` with `--browse` redirects to `https://www.google.com/`
- **THEN** the destination loads in the same page, its page ID remains stable and current URL reports the destination

#### Scenario: Remote page crosses origins without receiving host access
- **WHEN** an HTTP(S) page opened with `--browse` follows a redirect or link to another HTTP(S) origin
- **THEN** navigation and back/forward history work without a local-file grant or review bridge

#### Scenario: Remote page attempts a local or custom scheme
- **WHEN** a loaded remote page attempts `file:`, `javascript:`, `data:` or a custom-protocol main-frame navigation
- **THEN** the destination is refused and the existing document remains usable without a misleading page failure

#### Scenario: Default URL mode refuses an automatic origin change
- **WHEN** a URL page without `--browse` is redirected to a different scheme, host or effective port
- **THEN** the redirect is refused with a useful explanation naming `--browse`; no external application opens automatically

### Requirement: Browsing mode is explicit and identifies the current site
`browse` SHALL be a strict boolean, default false, accepted only for URL pages and reported in `htmlOverlays`. It SHALL NOT imply JavaScript or persistence. Browse mode SHALL display the committed site's identity in native UI and refuse `--chromeless`. Chromeless URL pages SHALL require exactly localhost, 127.0.0.1 or ::1 and restrict HTTP(S) frames to their origin.

#### Scenario: Browse conflicts refuse before mutation
- **WHEN** a request combines browse with local HTML or chromeless, repeats the flag or supplies a non-boolean wire value
- **THEN** it refuses before changing the existing overlay

### Requirement: Page initiated external handoff requires confirmation
Clicked off-origin links (including their redirects) in default mode and user-initiated HTTP(S) popups SHALL request native confirmation before launching an external browser. Cancel SHALL be the default. Denial SHALL suppress prompts until real user input; hiding or closing SHALL dismiss its prompt. Automatic popups SHALL remain blocked. An explicit toolbar or CLI browser action SHALL use the original local file or shown URL, never a refused destination, and SHALL not need another confirmation.

#### Scenario: Declined external link preserves the document
- **WHEN** a user declines a page's external-browser prompt
- **THEN** no browser opens, the document remains loaded, and further requests do not repeat the prompt until real user input

### Requirement: Reload and page close match upstream lifecycle
CLI reload SHALL navigate the original source; --current and toolbar reload SHALL reload the shown document with the same page identity. Accepted browser window.close SHALL close only the originating page through deferred identity-checked host work. About navigation SHALL be supported without preserving bridge authority.

#### Scenario: Reload after browsing
- **WHEN** a page browses away from its source then receives bare CLI reload
- **THEN** it returns to its original source; --current instead reloads the shown page

### Requirement: Independent split web slots
Pane pages SHALL coexist with another pane's program or web page, use pane geometry, preserve identity through swaps/hidden splits, and route operations to their exact slot. Full-session pages SHALL remain supported with split enabled.

#### Scenario: Two pane pages
- **WHEN** different pages open in left and right panes and the panes swap
- **THEN** each page retains its DOM and identity with its owning pane and closing one leaves the other usable

### Requirement: Explicit persistent browser storage
URL-only --persistent SHALL share application-owned cookies/localStorage/IndexedDB across closes/restarts with explicit scoped clearing. Default storage SHALL remain temporary and isolated.

#### Scenario: Persistent page reopens
- **WHEN** a persistent URL stores data and is reopened after restart
- **THEN** its origin data remains available while temporary review data does not become shared

### Requirement: Expanded presentation and lifecycle parity
Native chrome, shared saved web zoom, browser-link policy, bounded images, shell-less companions and soft-close/undo SHALL match the pinned upstream contracts. Remote URL/image content SHALL not gain local file or command authority. Hard restart SHALL not restore live pages. Remote display/replication is deferred to a separate change.

#### Scenario: Undo grouped web owners
- **WHEN** an undo-enabled batch or workspace containing web pages closes
- **THEN** its live documents remain available for the three-second grace period and one Undo restores the entire group, including the workspace position and focus membership where applicable
- **AND** the close reply reports whether Undo is available; disabling Undo or explicitly requesting `undoable:false` closes immediately
- **AND** final parent teardown also disposes any separately parked companion pages

#### Scenario: Companion beside a parent overlay
- **WHEN** a companion page opens for a session already holding an overlay
- **THEN** it owns an independent shell-less session and leaves the parent's overlay intact

### Requirement: Visibility preserves documents and routes input correctly
Hide/show and session switching SHALL preserve page identity, DOM and edits, without completing a page outcome. Hidden pages SHALL receive no ordinary input or accessibility focus. Native palettes, dialogs and switchers SHALL remain visible and operable above web content. Web typing SHALL never also reach the terminal.

#### Scenario: Palette above a review
- **WHEN** the command palette opens while an HTML editor has focus
- **THEN** the palette is visible and receives input, and dismissing it restores usable page focus without duplicate terminal keystrokes

### Requirement: Page resources have a defined lifetime
Closing a page or removing its owner SHALL release its browser controller and prevent late callbacks from reaching replacements. Pages SHALL remain ephemeral across application restart. Hiding SHALL not automatically discard or suspend live pages; explicit close is the release operation in this version. Renderer failure SHALL expose a failed state and permit explicit reload.

#### Scenario: Owner closes during initialization
- **WHEN** a session is removed while browser initialization is pending
- **THEN** late completion disposes its resources and cannot attach a page to the active or replacement session

### Requirement: Cookbook documents retain theme and clipboard behavior
Local pages SHALL receive `--agwinterm-*` theme variables and compatible `--agterm-background`, `--agterm-foreground` and `--agterm-color-0` through `--agterm-color-15` aliases. Theme updates SHALL not reload the document or erase edits. User-initiated copy SHALL work in the snapshot viewer without granting background clipboard reads.

#### Scenario: Theme changes during editing
- **WHEN** the theme changes while a review contains a saved note and an unsaved editor draft
- **THEN** both remain intact and the page adopts the new colors

### Requirement: Cookbook web acceptance covers three page classes
Release validation SHALL exercise adapted static HTML documents, interactive reviews and localhost dashboards from the pinned cookbook baseline, plus the standalone snapshot viewer. Dashboard JS SHALL support its buttons, filters and periodic HTTP refresh. Unsupported surrounding macOS tooling SHALL be documented separately from web capability coverage.

#### Scenario: Local dashboard updates
- **WHEN** a localhost dashboard opens with JS enabled and remains visible
- **THEN** its run/toggle controls, filters and five-second refresh function while the terminal remains responsive
