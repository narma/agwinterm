# Repository preferences

- Create pull requests in the user's fork, `narma/agwinterm`.
- Target the `narma` branch by default, matching the fork's GitHub default branch.
- Merge pull requests using Squash unless the user explicitly requests another merge method.
- Run local Windows UI tests in background Windows Sandbox: use `sandbox-ui.ps1 -Suite <suite> -Background` for HUD suites or `sandbox-ui.ps1 -Test <test> -Background` for standalone runners (including Win32Control). WSL tests automatically use the host distribution on a private non-input Win32 desktop via `wsl-ui.ps1`; this keeps their Windows UI off the user's desktop. Local entry points route before UI work. Never bypass routing with CI environment flags or fall back to the user's desktop. Host-free unit tests and fixture checks can run directly.
