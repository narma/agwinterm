# Stage 4: real Hyper-V browser acceptance

2026-11-04. The user authorized replacing the outstanding manual checks with real
Hyper-V acceptance, including installing Chinese IME inside disposable guests.
The installed worker was used from the ordinary Windows account; no host language
settings, live application sessions or installed base image were changed.

## Candidate and isolation

- Production candidate: `840fcd59ba23cdcae14fa479d072fd951727afd6`, which merges
  `origin/narma` (`f17ec00`) into `codex/agterm-remote-presentation`.
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
| `agwinterm-setup-0.20.18.exe` | `127324ADCF79300D5F874E3660287811873F9B673B7C3133B63E70B6CC78C21A` |
| `agwinterm-portable-0.20.18-win-x64.exe` | `286671D5F6FADB1F5FF4BE477F550EBB77E805412C6C914FD42F1E98B94209BC` |
| Installed `Agwinterm.Win32.exe` | `2F782F6AABE3F910DED4331E328718CE3FBBE0CEF2917C8A68855660B68F65A8` |
| Installed `agwintermctl.exe` | `5DBDF578E75E87C4D5C9DF54C8C4735E3D66615DAD0F25606319B476A12FF674` |

## Clean/offline results

All four required offline cells passed, including worker shutdown and evidence
export. Each result has `worker-result.json`, guest stdout/stderr, initial guest
inventory and `results/artifacts/web-release-acceptance/acceptance.json`.

| Package / runtime | Result | Evidence under `artifacts/hyperv-ui/` |
| --- | --- | --- |
| Installer / absent | PASS | `stage4-release-installer-absent` |
| Portable / absent | PASS | `stage4-release-portable-absent` |
| Installer / present | PASS | `stage4-release-installer-present-dpi` |
| Portable / present | PASS | `stage4-release-portable-present-dpi` |

Every cell verified terminal and program use, actionable page refusal when
appropriate, preservation of the existing program, and restoration of terminal
identity without ephemeral pages. Present-runtime cells additionally ran the full
published browser-host and cookbook suite: review submit/cancel, local resources,
navigation, editing/focus, isolation, profile/owner cleanup, and busy-terminal
dashboard interaction. Dashboard response during terminal output was 50 ms for
installer and 57 ms for portable. Offline external URL failure stayed within its
page.

The native loader came from the installed application directory for installer and
from a fresh `stage4-portable-bundle/portable/<bundle-id>/WebView2Loader.dll` for
portable, rather than a staged SDK. The ordinary missing-runtime cells are distinct
from the older synthetic missing-runtime regression inside the browser-host suite.

Network-connected runtime download/install cases from the manual scenario were
not run in this offline worker. No online installation pass is claimed; the
required clean/offline checks above exercise actual runtime presence/absence.

## Chinese IME

Status: language installation, real candidate selection and cancellation verified; full focus/interaction acceptance still pending.
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
commit, cancellation, focus restoration and pane input isolation. The remaining
IME item stays pending until this driver succeeds against the real installed IME.

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
Active preedit consumed palette shortcut attempts; menu/focus checks remain pending.
