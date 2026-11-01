# Proposal

## Why

Agwinterm cannot display the HTML documents, interactive document reviews and localhost dashboards used by agterm's cookbook. Add an optional embedded web surface while preserving native terminal rendering, fast terminal-only startup and a small application download.

## What Changes

- Add session-owned HTML/HTTP(S) overlays using WebView2 Evergreen, initialized only on the first web request, with explicit missing-runtime handling.
- Support navigation, reload, geometry, zoom readback, theme variables, visibility, focus and accessible Windows hosting without changing terminal identity.
- Match agterm's navigation modes: URL pages pin the original origin by default; explicit `--browse` permits HTTP(S) redirects and cross-origin links, including bare-domain to www redirects. Local document and review grants remain confined.
- Add page identities and queryable submitted/dismissed outcomes, plus an explicitly enabled local-page bridge limited to submit and close. JavaScript alone grants no control access.
- Cover `html-doc-overlay`, `annotate-document` and the web portion of `scheduled-jobs` with adapted cookbook fixtures; additionally verify the standalone `session-snapshot` HTML viewer and clipboard behavior.
- Preserve existing program-overlay result semantics and passive native HUD behavior. Document intentional cookbook adaptations rather than claiming unchanged upstream scripts work.
- Measure startup, first-page latency, process-tree memory/CPU/GPU, cleanup and published package growth. Gate windowed hosting on correct interaction with native overlays.

## Capabilities

### New Capabilities

- `web-overlay-viewing`: Optional WebView2 runtime, session-owned web presentation, source boundaries, navigation, lifecycle and cookbook viewer compatibility.
- `web-page-interaction`: Explicit local-page submit/close bridge, stable page outcomes and interactive cookbook review compatibility.

### Modified Capabilities

None. Existing program-overlay and HUD requirements remain applicable to their own surface kinds; the new capabilities define cross-kind slot arbitration without changing existing program-to-program behavior.

## Impact

Win32 hosting/rendering/input/UIA and overlay ownership; Core page lifecycle models; Pty control dispatch, snapshots and queued UI calls; Ctl parsing/help; WebView2 Core SDK and native loader distribution; installer/portable packaging; integration tests and control documentation. The main native shell remains Win32/Direct2D.

Scope extension approved 2026-10-31: match the pinned upstream web-overlay contracts, including independent split panes, explicit local resource grants, persistent URL storage, native chrome/readback/zoom, reload and external handoff, trusted local general/declarative bridge, blocking results, image and companion pages, remote display/replication, browser-link policy and soft-close/undo lifetime. Section 7 of tasks tracks this separately from previously accepted cookbook fixtures.

The user subsequently deferred remote display/replication to a separate OpenSpec change, likely targeting agterm/zmx interoperability; it is not part of this implementation's acceptance.

Not included: remote display/replication, full-browser tabs/extensions, bundled fixed Chromium runtime, general browser automation, Cloudflare publishing or a Windows scheduler implementation. Web-overlay parity does not certify all 46 recipes or all macOS tooling.
