# Elan's Addon Hub

Tiny Windows app (.NET Framework 4.8, ~150 KB, no install) that installs and updates
Elan's WoW addons for friends.

- Finds the World of Warcraft folder (registry + common paths, or "Change").
- Reads `manifest.json` from the latest GitHub release, compares with the installed `.toc` version.
- One-click install/update: download, SHA-256 check, backup of the old version
  (`%LOCALAPPDATA%\ElansAddonHub\backups`, last 3), then replace. Never touches a git checkout.
- Tray icon, checks every 30 min, optional auto-update and start with Windows.
- Updates itself (the manifest also carries the hub's own version).

## Build

    cd src
    dotnet build -c Release        -> src/bin/Release/ElansAddonHub.exe

## Release (from the addon repo)

    python Tools/release.py            build zip + exe + manifest into Tools/dist/release
    python Tools/release.py --local    manifest with local paths, for testing the hub
    python Tools/release.py --publish  upload as a GitHub release to Elanoran/ElansAddonHub

Testing without clicking: `ElansAddonHub.exe --selftest <dir>` renders the window to PNGs,
installs the first addon that needs it, writes result.txt and quits.

Log: `%LOCALAPPDATA%\ElansAddonHub\hub.log`
