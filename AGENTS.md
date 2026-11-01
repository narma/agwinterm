# Repository preferences

- Create pull requests in the user's fork, `narma/agwinterm`.
- Target the `narma` branch by default, matching the fork's GitHub default branch.
- Merge pull requests using Squash unless the user explicitly requests another merge method.
- Run local UI tests in background Windows Sandbox: use `sandbox-ui.ps1 -Suite <suite> -Background` for HUD suites or `sandbox-ui.ps1 -Test <test> -Background` for standalone runners (including Win32Control). Local entry points automatically route there before UI work. Never bypass routing with CI environment flags or fall back to the host desktop; WSL-dependent tests require a separately provisioned Linux-capable environment. Host-free unit tests and fixture checks can run directly.
