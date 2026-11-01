# Repository preferences

- Create pull requests in the user's fork, `narma/agwinterm`.
- Target the `narma` branch by default, matching the fork's GitHub default branch.
- Merge pull requests using Squash unless the user explicitly requests another merge method.
- Run local UI tests in Windows Sandbox with `pwsh -NoProfile -File tests/integration/sandbox-ui.ps1 -Suite <suite> -Background` so test windows never interrupt the user's desktop. For UI scripts not covered by that runner, add Sandbox support before running them; do not fall back to the host desktop. Host-free unit tests and fixture checks can run directly.
