# Stage 4: real Hyper-V browser acceptance

2026-11-04. The user authorized replacing the outstanding manual checks with real
Hyper-V acceptance, including installing Chinese IME inside disposable guests.
The installed worker was used from the ordinary Windows account; no host language
settings, live application sessions or installed base image were changed.

## Candidate and isolation

- Production candidate: `12256cae08756c013b7353c1dcdd3f77cf507e9f`, after merging
  `origin/narma` (`f17ec00`) in `840fcd5`. Test driver head: `98c73d6` plus the
  pane mouse-focus check. The later remote-download budget fix is validated
  separately below; these package hashes identify the focus-fixed build.
- Version: `0.20.18`; Windows 11 Pro `10.0.26300`; .NET SDK `10.0.303`.
- Independent clean differencing disk per cell, no network adapters, no prior
  Agwinterm installation. Cells were run sequentially through the installed worker.
- Present-runtime image: WebView2 `152.0.4191.66`.
- Missing-runtime cells: the vendor uninstaller returned 93. The guarded fixture
  removed runtime files and documented Evergreen discovery registrations inside
  the disposable guest, then checked their actual absence. No simulated runtime
  override was used to establish these cells.
- The portable application directory contained one EXE. Its CLI was extracted
  from that same EXE into a separate test-driver directory.

| Artifact | SHA-256 |
| --- | --- |
| `agwinterm-setup-0.20.18.exe` | `F6A4EE4702E5D14B39D815AE9001B40463D28F1693C460D558AAF3B86462F243` |
| `agwinterm-portable-0.20.18-win-x64.exe` | `8AD09399A8D56DA004D2230151B31E7D10BB4FC48CBAE97E8DBFF4430C65A551` |
| Installed `Agwinterm.Win32.exe` | `0C4C0A54C50B89AF78BB621F47C82C09E399C13F6BE8D2BC49453372240071A5` |
| Installed `agwintermctl.exe` | `751BA0DDE2C2F9468286E0DAB76908EBDFFD974400EB8F4DF631240ADE3E2804` |

Installed application assembly SHA-256: `E34B709B252EAC7854522C2846A3C6B760AE69C2016EF7CBB9951F12CD46BB55`.
Installer: 36,006,347 bytes; portable: 91,098,312 bytes.

## Clean/offline results

All four offline cells passed again on the corrected packages above, including
worker shutdown and evidence export. Initial passes on `840fcd5` are retained
separately. Each result has `worker-result.json`, guest stdout/stderr, initial guest
inventory and `results/artifacts/web-release-acceptance/acceptance.json`.

| Package / runtime | Result | Evidence under `artifacts/hyperv-ui/` |
| --- | --- | --- |
| Installer / absent | PASS | `stage4-fixed-installer-absent` |
| Portable / absent | PASS | `stage4-fixed-portable-absent` |
| Installer / present | PASS | `stage4-fixed-installer-present` |
| Portable / present | PASS | `stage4-fixed-portable-present` |

Every cell verified terminal and program use, actionable page refusal when
appropriate, preservation of the existing program, and restoration of terminal
identity without ephemeral pages. Present-runtime cells additionally ran the full
published browser-host and cookbook suite: review submit/cancel, local resources,
navigation, editing/focus, isolation, profile/owner cleanup, and busy-terminal
dashboard interaction. Dashboard response during terminal output was 53 ms for
installer and 39 ms for portable. Offline external URL failure stayed within its
page.

The native loader came from the installed application directory for installer and
from a fresh `stage4-portable-bundle/portable/<bundle-id>/WebView2Loader.dll` for
portable, rather than a staged SDK. The ordinary missing-runtime cells are distinct
from the older synthetic missing-runtime regression inside the browser-host suite.

Network-connected runtime download/install cases from the manual scenario were
not run in this offline worker. No online installation pass is claimed; the
required clean/offline checks above exercise actual runtime presence/absence.

## Chinese IME

Status: PASS. The complete real Chinese Pinyin run finished with worker exit 0
in `artifacts/hyperv-ui/stage4-ime-onboarding`, run
`2180a2c9-2984-47c1-a7c9-1737bf08111e`; guest test time 134.199 seconds.
Ordinary Latin/Unicode text without composition is not counted as an IME pass.

Language packages are extracted from Microsoft's
[Languages and Optional Features ISO](https://software-static.download.prss.microsoft.com/dbazure/888969d5-f34g-4e03-ac9d-1f9786c66749/26100.1.240331-1435.ge_release_amd64fre_CLIENT_LOF_PACKAGES_OEM.iso).
ISO SHA-256: `FDBD87C2CD69BA84EF2EA69D5B468938355D0D634B7DE7A1988480F94713A738`.
The three CAB files are zipped together as the test's `LanguageSource` input;
Windows media are not committed to the repository.

| CAB | SHA-256 |
| --- | --- |
| `Microsoft-Windows-Client-Language-Pack_x64_zh-cn.cab` | `C135023E4CE5751435D810F62884A9DE8D204193A867B60498F290AFE7C5EEFC` |
| `Microsoft-Windows-LanguageFeatures-Basic-zh-cn-Package~31bf3856ad364e35~amd64~~.cab` | `A1E24B619BCD64A5A2A3535EF0F0FF143694AB0F77FA2454F1D60C19A6C33A35` |
| `Microsoft-Windows-LanguageFeatures-Fonts-Hans-Package~31bf3856ad364e35~amd64~~.cab` | `10389507EB82A9E022186B624E4D9A0A6564920C7F65E49971DA557E085499F2` |

`web-ime-acceptance.ps1` refuses to provision languages outside a verified
Hyper-V guest. It installs the supplied Microsoft packages, selects Pinyin,
records native candidate screenshots and DOM input events from scan-code input and checks candidate
commit, cancellation, focus restoration and pane input isolation. The complete
driver passed against the real installed IME.

## Reproduction and fixture corrections

Run `hyperv-ui.ps1 -Test WebReleaseAcceptance` with `TestParameters` containing
`Mode=Installer|Portable`, `Runtime=Present|Absent`, and the corresponding
`Installer` or `Portable` package path. The cookbook uses existing build/test-only
Python staged by the runner; no dependency is added to the shipped application.
Run `-Test WebImeAcceptance` with the published `-Exe` / `-CtlExe` and
`TestParameters.LanguageSource` pointing to the ZIP of the three official CABs.

Exploratory failures were retained rather than reported as passes. The fixture
now accepts a lost final-window close acknowledgement only when its exact process
exits normally, ends its private surviving Rust host after restoration checks,
and restores real monitor DPI after synthetic DPI checks before testing optional
toolbar actions. The initial IME window lookup was corrected to exclude the
application's separate quick-terminal window. The first input attempt selected a
Chinese layout but produced Latin input only; it is not IME acceptance evidence.

The installed Pinyin produces a native candidate window: typing NIHAO and choosing
candidate 2 committed exactly 你号; Escape cancelled ZHONG without adding text.
This path delivers committed input without DOM composition events. Screenshot
and input evidence is retained under stage4-ime-candidate-commit and later runs.
Active preedit consumes palette shortcut attempts in this Pinyin mode. The shortcut
works with Chinese selected outside preedit; the native Navigate → Command Palette
menu works during preedit and dismissal restores editor focus. Composition then
passes after palette, resize and session switching, followed by Tab/Shift+Tab and
pane-scoped commit/cancel. Neither terminal receives any of the editor input.
The initial native candidate path delivers committed input; later editor phases
also record genuine DOM compositionstart/update/end events. No Unicode injection
or synthesized composition event is used.

The run exposed a production focus defect: selection changed before browser
presentation was updated, and background pages discarded their resume-focus flag.
`12256ca` retains that flag until the page is visible again. The original build
failed real input after returning to the session despite reporting UIA editor
focus; the corrected build passes the same scenario and terminal-isolation checks.
The ordinary browser-host suite now also tests native input after a session switch,
without requiring an IME installation. A new pane is focused with an ordinary
mouse click before its typing test. The optional Bing onboarding button was not
exposed by UIA in the successful run; no Bing/cloud-suggestion opt-in was performed.

## Remote resource storage review correction

Greptile identified unbounded temporary-file growth from valid remote resource
chunks. Downloads now enforce 64 MiB per resource, 256 MiB and 64 retained files
per page, before allocating a file or writing an exceeding chunk. Browser-retained
responses keep their reservation until disposal. Refusal cancels the request,
deletes its partial file and leaves the shared channel usable. No wire capability
or shipped dependency changes. The focused resource/protocol suite passes
58 tests, including overflow, retained bodies, parallel requests and cancellation;
the Windows Release build passes without warnings. Logs are
`artifacts/stage4-page-budget-tests.log` and
`artifacts/stage4-budget-release-build.log`. The package/IME runs above predate
this remote-only correction and are not relabelled as tests of its binary.
