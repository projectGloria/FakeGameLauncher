# AGENTS.md — FakeGameLauncher

Windows Forms (.NET Framework) Discord Rich Presence and quest companion compiled via PowerShell 5.1 CodeDOM.

## Commands
- Build: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Launcher.ps1`
- Test (all): `powershell.exe -NoProfile -STA -File .\tests\Run-Tests.ps1`
- Run: `.\GameLauncher.exe`
- Validate manifest: `npx --yes gloria-setup@latest --check --manifest gloria.toml`

## Architecture
- `GameLauncher.cs` — Single-file WinForms app (`DiscordQuestLauncher` namespace): entry point, Discord IPC/RPC client, game database, icon cache, custom controls, and main form.
- `Build-Launcher.ps1` — Compiles `GameLauncher.cs` via .NET CodeDOM into a staged exe with `assets/AppIcon.ico` embedded, stops matching launcher and `%TEMP%\DiscordDummyTools` helpers, and atomically replaces `GameLauncher.exe`.
- `tests/Run-Tests.ps1` & `tests/LauncherRegressionTests.cs` — Compiles production source (warnings fail) and runs isolated regression tests with redirected paths/IPC pipes and real helper processes.
- `assets/AppIcon.ico` — Required embedded app icon; `custom_games.json`, `qualified_games.json`, `last_simulated.json` store mutable game data/history; runtime logs go to `%LOCALAPPDATA%\DiscordQuestLauncher\launcher.log`.
- **Version Manifests:** `gloria.toml` (only release version manifest; `installer/DiscordQuestLauncher.iss` is legacy Inno Setup).

## Personal Rules (Brain)
- **Git Branching:** Never create new branches or git worktrees. Always work directly on `main` until explicitly told otherwise.
- **Versioning:** Never bump versions during normal work. The user picks the version at release time and `/monjita-github-release` sets it in every version file at once; keep version files in sync with each other otherwise.
- **Commit Format:** Write one plain sentence stating what changed and why; do not use `feat:`/`fix:`/`chore:` conventional-commit prefixes.
- **Commit & Push Policy:** Never run `git commit`, `git tag`, or `git push` unless explicitly asked; never force-push to `main`. When the user says "commit and push", verify the build and tests pass, commit on `main`, and push to `origin main`. Pushing never releases anything.
- **Releases:** Releases happen only through `/monjita-github-release`, which bumps the version, pushes, runs `gh workflow run release.yml`, and watches it until the GitHub Release is live. Never create release tags or releases by hand.
- **Path Privacy:** Always use `C:\Users\you` in documentation, comments, and examples—never hardcode the local machine's real username.
- **Release Workflow:** `.github/workflows/release.yml` must be a copy of `GloriaSetup/templates/release.yml` (shared `check`/`release` jobs from `projectGloria/GloriaSetup/.github/workflows/app-{check,release}.yml@main`, plus this repo's own `build` job). Don't hand-write release workflows or pin `gloria-setup`; see `.brain/github-actions.md`.
- **Window Chrome:** Disable native OS titlebars; build an integrated frameless titlebar with drag region, double-click maximize, and custom min/max/close controls.
- **Desktop Shell Lock:** Lock the app shell to `100vw x 100vh` (`overflow: hidden`) with internal scroll panes and `user-select: none` on UI chrome.
- **Scrollbars:** Ban raw OS scrollbars; use thin (4–6px) themed scrollbars with transparent tracks and zero layout shift.
- **Interactive Controls:** Custom-style all form controls; define explicit `hover`, `active`, `focus-visible`, and `disabled` states with snappy 120–180ms transitions.

## Playbooks (Read Before Task)
- **Building & Packaging:** Read `.brain/building-the-project.md` before packaging, compiling release binaries, or building an installer.
- **GitHub Actions & Releases:** Read `.brain/github-actions.md` before creating/modifying `.github/workflows/*.yml`, setting up release automation, or debugging a failed release.

## Repo Guardrails & Gotchas
- **PowerShell 5.1 Only:** Run `Build-Launcher.ps1` and `tests/Run-Tests.ps1` with Windows PowerShell 5.1 (`powershell.exe`); both scripts reject PowerShell Core (`pwsh`).
- **C# / CodeDOM Compatibility:** Keep `GameLauncher.cs` compatible with .NET Framework CodeDOM (`CSharpCodeProvider`); preserve 4-space indentation, Allman braces, and lowercase JSON property names (`id`, `name`, `exe`).
- **Discord Integration:** Rich Presence acknowledgements confirm activity display only; local session timers and `qualified_games.json` do not verify Discord quest credit—keep connection status separate from quest progress.
- **Mutable State & Backups:** Preserve schemas of `custom_games.json`, `qualified_games.json`, and `last_simulated.json`; exclude incidental play-history, `.bak` backups, and built binaries from commits unless intentionally required.

## Done Criteria
- `powershell.exe -NoProfile -STA -File .\tests\Run-Tests.ps1` passes (including zero compiler warnings).
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Launcher.ps1` succeeds and manual UI/tray/taskbar checks show no errors in `%LOCALAPPDATA%\DiscordQuestLauncher\launcher.log`.
- `gloria.toml` version is untouched unless cutting a release via `/monjita-github-release`.

<!-- brain-sync: 31e69a675214 -->
