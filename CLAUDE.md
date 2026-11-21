# Explorer2

Windows 11 File Explorer replacement (.NET 8 WinForms) hosting native shell views (`IExplorerBrowser`) with tabs, split mode and Total Commander style keys. See `README.md` for features and help.

## Definition of done

- Release build with **0 warnings, 0 errors**:
  `dotnet build Explorer2.sln -c Release --no-incremental`
  (on this machine `dotnet` may not be on PATH; use the portable SDK at
  `C:\Users\savi\.gemini\antigravity\brain\bd97570f-968b-4fb3-a7f5-a0fb1969ef4f\scratch\dotnet-sdk\dotnet.exe`).
- UI changes are checked in the running app (`bin\Release\net8.0-windows\Explorer2.exe`); test files go in the session scratchpad, never in `C:\` or the user's folders.
- Commit and push to `main` after every iteration.

## Agent skills

### Issue tracker

Issues live in GitHub Issues on `savilior/ExplorerSplit`, managed with the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default triage labels (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
