# WebView2 initial hosting gate

OpenSpec change: `add-webview2-cookbook-overlays`. Tasks 1.1–1.3 are complete;
task 1.4 is in progress. This is an initial vertical slice, not a release-ready
implementation of the complete web-overlay specifications.

## Implementation and reproducible checks

The Win32 project pins Microsoft.Web.WebView2 **1.0.4314.35**, retaining only the
Core reference and native loader rather than introducing WPF/WinForms hosting.
`WebViewHost` creates controllers asynchronously on the existing STA message
pump. Compatible pages share an environment; each controller uses a unique
InPrivate profile. Close marks the profile for deletion, closes the controller,
and releases the environment lease. A late cancelled controller is closed.

The initial CLI/control path accepts `session overlay open --html PATH` or
`--url HTTP_URL`, with optional `--js`, sizes and follow. Scripts default off.
It returns `result.pageID`; `tree` reports the initial `htmlOverlays` snapshot.
Program/web slot conflicts refuse; a HUD is kept until controller creation
succeeds. Hide/show preserve the controller. Window/session teardown releases it.

The slice has since expanded to canonical Windows/WSL directory grants and sibling
assets, the opt-in review bridge and bounded outcome recovery, native navigation,
live theme variables and pinned cookbook fixtures. Their remaining acceptance
checks are tracked individually in `tasks.md`; this gate report does not imply
that the full change or integrated release measurements are complete.

Commands, run from the repository root:

```powershell
dotnet build Agwinterm.slnx -c Release
dotnet test Agwinterm.slnx -c Release --no-build
dotnet run --project tests/WebViewHost.Gate -c Release
pwsh -NoProfile -File tests/integration/web-overlay-host.ps1
```

The integration script creates a private app identity, uses only its own window
handles and pipe, and restores inherited routing/runtime variables. It preserves
private profile directories for inspection. It does not send global input.
The standalone gate links the production host and runs a real native STA loop.

## Results on 2026-10-31

Environment: Windows 11 build 26300, SDK 10.0.303, WebView2 Runtime
**156.0.4314.58**; hardware details are in [the baseline](webview2-baseline.md).

- Release solution build passed; latest build: zero warnings/errors.
- Core: **517 passed**; Pty: **1492 passed**, including new web request validation.
- Terminal-only launch loads no WebView2 modules and has no browser child process.
- A missing runtime is simulated using WebView2's executable-folder override.
  Requests refuse with an installation hint; a hidden program overlay is unchanged.
  This does not replace the clean-machine missing-runtime test in task 1.5.
- Local document load, page identity, occupied-slot refusal, hide/show and close pass.
- Reload retains localStorage; a second profile at the same origin cannot read it.
- Cancellation during controller creation rejects creation and closes the late controller.
- UIA discovers the HTML editor through the parent window and edits its value.
- Unicode character input sent to the owned renderer reaches the editor without
  appearing in the underlying terminal. This is **not** an IME composition test.
- A mapped shortcut sent to the owned browser widget opens the native palette;
  dismissal restores editor focus and text.
- Palette, settings, switcher and native ask hide browser presentation and restore
  it without losing DOM edits. Resize keeps geometry inside the window.
- The only physical monitor is DISPLAY1 at **144 DPI**. Synthetic WM_DPICHANGED
  checks at **96, 192 and 144 DPI** pass clipping and DOM-preservation checks.
  Actual movement between monitors with different DPI remains unverified.

Local disposable evidence: `artifacts/webview2-build.log`,
`artifacts/webview2-unit-tests.log`, `artifacts/webview2-storage-gate.log`,
`artifacts/webview2-host-test.log`.

## Remaining hosting gate

Additional development evidence (2026-10-31): native navigation buttons are exposed
through UIA and Tab traverses them. Theme CSS aliases change without reloading the
editor or losing its draft. PowerShell's UIA test client explicitly loads standard
Win32 providers; its default discovery failed in this non-WPF host. A scoped browser
process termination is observable as page failure, and explicit reload recreates
the browser with the same page ID (`artifacts/webview2-recovery-test.log`).
The existing program-overlay acceptance suite also passes
(`artifacts/webview2-program-regression.log`).

Task 1.4 stays unchecked pending the user's manual IME result. Only Russian and English keyboard layouts were
available; no IME composition test has run. The user chose to verify IME manually
on another computer; do not install Japanese IME on this machine. That manual
result is pending. Verify composition and candidate selection in a focused web
editor, no duplicate input in the terminal, and focus/edit preservation after
opening and dismissing the native palette. Record the tested IME, Windows and
WebView2 Runtime versions with the result. A physical mixed-DPI setup is also
unavailable. On 2026-10-31 the user explicitly accepted the synthetic DPI checks
and instructed implementation to continue without waiting for manual IME testing.
The automated hosting gate therefore permits subsequent implementation; IME
remains a tracked manual validation item, not a claimed pass. No observed overlap/UIA failure currently
requires the design's DirectComposition fallback.

The final native host gate also verifies permission denial, blocked automatic
downloads/popups and removal of closed page profile directories after browser
exit (`artifacts/webview2-policy-host-gate.log`). The cookbook run verifies two
real review documents retain independent saved notes after reload and keep
unsaved drafts through theme changes/session switching. See
[release validation](webview2-release-validation.md) for final tests and packaging.
