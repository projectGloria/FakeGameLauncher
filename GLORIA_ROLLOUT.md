# GloriaSetup rollout notes

## Please check

- **Version:** the project files carry no version (the Inno script defaulted to `0.0.0`), so `gloria.toml` uses `1.1.0` from the GloriaLauncher catalog. Tag builds override it with the tag.
- **Runtime files:** only `GameLauncher.exe` is packed, as the old Inno installer did. `custom_games.json`, `qualified_games.json` and `last_simulated.json` are not shipped. The app reads them from its exe folder only as a fallback and writes them to `%LOCALAPPDATA%\DiscordQuestLauncher`. Add them to the staging step if a seeded game list is wanted.
- **Data folder:** `%LOCALAPPDATA%\DiscordQuestLauncher` (logs, caches, game lists). The launcher also uses `%TEMP%\DiscordDummyTools` for helper processes; it is temporary and not listed.
- **Legacy name:** `Discord Quest Launcher` is the old Inno `AppName`, listed so GloriaSetup can migrate old installs.
- **Icon:** there is no `FakeGameLauncher.ico` in the root, so `assets/AppIcon.ico` (already embedded in the exe) is used.
- **Old installer:** the Inno step and the separate release job were removed from `build-installers.yml`. `installer/DiscordQuestLauncher.iss` is left in place, unused.
- **Workflow:** the release is created with `gh release create --generate-notes` if missing. Regression tests still run before the build. Unsigned setups are produced until the `WINDOWS_PFX_BASE64` and `WINDOWS_PFX_PASSWORD` secrets are set.
- **Not verified:** nothing was built or run locally.
