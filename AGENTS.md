# Repository Guidelines

## Project Structure & Module Organization

This project is a Windows Forms application in the `DiscordQuestLauncher` namespace.

- `GameLauncher.cs` contains the entry point, Discord RPC client, game database, icon cache, custom controls, and main form. Keep changes within the relevant class.
- `Build-Launcher.ps1` compiles the source into `GameLauncher.exe` using .NET Framework CodeDOM.
- `assets/` contains `AppIcon.png` and `AppIcon.ico`; the ICO is required and embedded during compilation.
- `custom_games.json`, `qualified_games.json`, and `last_simulated.json` hold mutable game data and history. Preserve their schemas.
- `tests/` contains a C# regression harness and PowerShell runner. `.bak` files are backups, not active source. No solution/project file exists.

## Build, Test, and Development Commands

Run these commands from the repository root on Windows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Launcher.ps1
powershell.exe -NoProfile -STA -File .\tests\Run-Tests.ps1
.\GameLauncher.exe
```

Use Windows PowerShell 5.1; the scripts reject PowerShell Core (`pwsh`). The build compiles a staged executable before closing the launcher and helper processes under `%TEMP%\DiscordDummyTools` and replacing the binary. Failed compilation preserves the previous executable. The test command runs isolated regression checks; the final command opens the application. There is no package installation step.

## Coding Style & Naming Conventions

Use four-space indentation and opening braces on separate lines in C#. Follow surrounding code: PascalCase for types and methods, camelCase for parameters and locals, and existing field conventions within each class. Preserve lowercase JSON properties such as `id`, `name`, and `exe` for serialization compatibility. Keep syntax compatible with the build script's .NET Framework compiler. No formatter or linter is configured.

## Testing Guidelines

Run `tests/Run-Tests.ps1` after code changes. The harness uses .NET assertions, isolated persistence, a local RPC emulator, and real helper processes; no external test framework or coverage threshold is configured. Add regression cases to `tests/LauncherRegressionTests.cs`. Also manually verify affected UI behavior, icons, and taskbar behavior. Record results and inspect `%LOCALAPPDATA%\DiscordQuestLauncher\launcher.log` for errors.

## Commit & Pull Request Guidelines

This checkout has no Git metadata, so historical conventions cannot be verified. Use concise imperative messages, such as `Fix helper process cleanup`. Pull requests should explain the change, link relevant issues, record build/manual checks, and include screenshots for visual changes. Exclude incidental play-history, cache, backup, and binary changes unless intentionally required.

## Discord Integration

Rich Presence acknowledgements confirm activity display only. Local session timers and `qualified_games.json` do not verify Discord quest credit. Keep connection status separate from quest progress.
