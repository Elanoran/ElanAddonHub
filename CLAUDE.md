# Elan's Outpost (this repo)

The Outpost is a Windows app (WPF, .NET 10) for WoW addons, with the Atlas, the world map and the Lodge. It used to be called the hub, so the folder, exe and namespaces still say `ElansAddonHub`, and the GitHub repo is `ElanAddonHub` (no "s"). **Elan's Hub** is something else: the required in-game addon in `addon/ElansHub`.

> This repo is public. Keep this file free of personal data, our hosting setup and private repo names. A local pre-push check and the self-test's infra guard both look for them.

## Layout
- `src/` the app. `src/WorldMap/` the map pages, `src/Services/` game readers and logic, `src/CHANGELOG.md` its changelog.
- `addon/ElansHub/` the Hub addon. Its tests and release tooling are in the addon repo (`../ElansHunterHelper`).
- `addons/*.md` public docs for each addon.
- `server/` the Lodge server (`Lodge.Server`, `Lodge.Tests`, `tests/`, `PROTOCOL.md`, `SECURITY.md`, `deploy/`).
- `shared/Elans.Shared` signed formats and the typed Lodge protocol.
- `docs/design/` plans, to-dos and screenshots: git-ignored, and the only place for them.
- `ARCHITECTURE.md` is the contract between addons, Hub, Outpost and Lodge. Read it before adding data that crosses between them: addons → Hub → Outpost → Lodge, and back the same way.

## Tests
| What changed | Run |
|---|---|
| Outpost app | `pwsh -NoProfile -File Tools\selftest.ps1` (only what changed since origin/main; `-Only a,b` while iterating; `-List` for sections) |
| Shared library | `dotnet test shared/Elans.Shared.Tests` |
| Lodge server | `cd server` then `dotnet test Lodge.Tests` |
| Hub addon, `/ehub copy` | `python -u Tools/tests/hub_copy_test.py`, run from `../ElansHunterHelper`. The addon smoke tests don't load the Hub. |

Self-test rules:
- Use PowerShell 7 and this script. It runs on a hidden desktop, because visible test windows disturb the user.
- Never run two at once: check for `--selftest` processes first.
- Never point it at the real `%LOCALAPPDATA%\ElansAddonHub`.
- Before a release: `-Full -UseCache -TimeoutMinutes 40`. Use `-NoCache` only when shared readers changed (`Services/Casc.cs`, `Blp.cs`, `Db2/Wdc5.cs`, `Maps/*`) or after a game patch, and say why.

Tuning the 3D map or its pages: use `Tools\devoutpost.ps1` with the `outpost-dev` MCP tools and small screenshots instead of repeated self-test runs.

## Versions and changes
- Outpost version: `<Version>` in `src/ElansAddonHub.csproj`, plus a `## x.y.z` entry in `src/CHANGELOG.md`. Release notes are built from it.
- Hub addon version: `addon/ElansHub/ElansHub.toc`, plus `addon/ElansHub/CHANGELOG.txt`.
- Commit subjects name the part and version: `Elan's Hub 1.18.4: what the user gets`.
- Public docs follow `docs/design/DOCSTYLE.md`.

## Pitfalls
- Plans, to-do files and notes never go in tracked files, and only `docs/readme/` may be tracked under `docs/`. Tracked images belong only in `src/Assets/` or `docs/readme/`.
- Never overwrite an addon folder that contains `.git`. Keep any dev-copy replace path small and strongly worded.
- `string.GetHashCode` changes per process on .NET 10: anything shared across processes needs a stable hash, like the private `StableHash` in `App.xaml.cs`.
- Lodge invite codes go only in the `X-Lodge-Code` header, never in URLs.
- Server changes aren't live after a push. Deploy only when asked.
- Releases run from `../ElansHunterHelper` with `Tools/release.py`, on its `main` branch; its `CLAUDE.md` has how we work in bundles: dev, then test, then promote. Parallel agents each get their own worktree.
