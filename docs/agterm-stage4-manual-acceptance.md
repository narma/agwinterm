# Stage 4: manual browser acceptance

Status: scenario prepared; no manual pass recorded. Covers task 6.6 and the outstanding IME/clean/offline items in `add-webview2-cookbook-overlays`. Remote-page acceptance remains a separate automated/actual-peer matrix.

Use a separate test machine or disposable VM, not the development machine's live configuration. IME must already be installed on the test machine: no language/runtime installation on the development host is requested. Run automated Windows UI suites only through the repository's background Sandbox routing.

## Record the build and environment

For each run record the installer or portable filename, SHA-256 (`Get-FileHash -Algorithm SHA256 <file>`), source commit, Windows edition/build, WebView2 Runtime version (or confirmed absence), IME name/version, and whether networking was disconnected. Use installer and portable artifacts from the same candidate commit. A development-folder launch or an upgrade over an existing installation is not a clean-install pass. A simulated missing-runtime override is not actual runtime absence.

## IME composition

1. Start the candidate in a fresh test profile and open a disposable terminal session. Note its exact session ID with `agwintermctl tree --json`.
2. Save this UTF-8 file as `ime-test.html` in a test directory:

   ```html
   <!doctype html><meta charset="utf-8"><title>IME acceptance</title>
   <label>Composition editor <textarea autofocus rows="12" cols="60"></textarea></label>
   <button type="button">Focus target</button>
   ```

3. Open it using the candidate's CLI: `agwintermctl session overlay open --html "C:\test\ime-test.html" --js --navigation --target SESSION_ID`. Replace the file path and ID with the recorded values; do not target an unrelated working session.
4. In the editor, use the installed IME to compose a multi-character phrase, open the candidate list, change the selected candidate and commit it. Repeat and cancel composition with Escape. Verify committed text appears once; cancelled composition inserts nothing.
5. Start composition again, open/dismiss the native action palette using its configured shortcut, then return to the editor. Verify focus, existing text and subsequent composition/candidate selection work. Repeat after resizing the window and switching away from/back to the session.
6. Use Tab/Shift+Tab between editor, button and navigation controls. Close the overlay and verify the underlying terminal received none of the editor's text, composition keys or candidate-selection input.
7. Repeat for a pane-scoped page in a split session using the exact pane target. Verify the other pane receives no input. Record any difference between session and pane overlays.

Record the phrase, expected/actual text, IME, each step's result and a screenshot/video of any failure. Ordinary Unicode typing without composition is not an IME pass.

## Clean/offline installer and portable matrix

Run each cell from an independent clean snapshot/profile. Do not remove a machine-wide runtime from a working computer to create a missing-runtime cell.

| Package | WebView2 | Network | Expected result |
| --- | --- | --- | --- |
| Installer | Present | Disconnected | Installation and terminal-only use work; local HTML/editor loads; URL failure is contained to the page |
| Portable, executable alone | Present | Disconnected | Terminal and local HTML work without adjacent SDK/loader files; bundled loader is extracted successfully |
| Installer | Absent | Disconnected | Terminal-only installation/use work; page open gives actionable runtime refusal; existing terminal/program overlay survives |
| Portable, executable alone | Absent | Disconnected | Terminal works; page refusal is actionable; no implicit download or runtime installation occurs |
| Installer | Absent | Connected | Terminal works before runtime installation; test the explicit runtime-install action only when approved by the test machine owner, then verify page open |
| Portable, executable alone | Absent | Connected | Same explicit-install/refusal behavior; no dependency on an adjacent development installation |

For every cell: start a terminal, print a recognizable marker, open/close a local page, open a program overlay, try the page operation again, and confirm the original terminal/program state is preserved when the page is refused. Restart the app and verify terminal restoration without restoring ephemeral web pages. For present-runtime cells also test Submit/Cancel with the cookbook review fixture, page cleanup and loader extraction. Record observed files/processes after closing pages; preserve logs of failures rather than converting them into passes.

An intentionally disconnected URL cannot be required to load. Record its page error and confirm the app/terminal stay usable. Runtime downloads require the explicit installer action; this scenario does not authorize unattended installation elsewhere.

## Result template

```text
Commit / package filename / SHA-256:
Windows version/build:
WebView2 version or confirmed absence:
IME name/version (IME cases):
Clean snapshot/profile and network state:
Case / step:
Expected:
Observed:
PASS / FAIL / NOT RUN:
Evidence paths:
```

Return filled results for each case. Leave unavailable cells NOT RUN. Task 6.6 and linked local browser tasks remain pending until the required evidence is reviewed.
