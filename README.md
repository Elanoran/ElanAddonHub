<p align="center"><img src="src/Assets/hub.png" width="96" alt="Elan's Addon Hub"></p>

<h1 align="center">Elan's Addon Hub</h1>

<p align="center">Installs and updates <b>Elan's Hunter Helper</b> for WoW Forever, with one click.</p>

---

## Get started

1. Download **`ElansAddonHub.exe`** from the [latest release](https://github.com/Elanoran/ElanAddonHub/releases/latest).
2. Run it. Windows may say *"Windows protected your PC"* because the app isn't code-signed:
   click **More info → Run anyway** (only the first time).
3. The hub finds your World of Warcraft folder by itself (or press **Change** and pick any `Wow*.exe`).
4. Press **Install**. In game, type `/reload`.

From then on the hub sits in the tray, checks for updates every 30 minutes and tells you when
there's a new version. Turn on **Update automatically** (gear icon) and you never have to think about it.

No installer, no runtime to download - it's one small file (~150 KB) that runs on any Windows 10/11 PC.

## What it does

- **Install / update in one click**, with a short "what's new" list before you update.
- **Safe updates**: every download is checksum-verified, the previous version is backed up
  (last 3 kept in `%LOCALAPPDATA%\ElansAddonHub\backups`) and restored if anything goes wrong.
- **Tray & notifications**, optional **start with Windows** and **automatic updates**.
- **Updates itself** when a new hub version is released.
- Never touches a development copy (a folder with `.git`).

## The Lodge: chat, voice and files

The **Lodge** tab is a small private Discord for you and your friends:

- **Text chat** with history, who's online and "is typing".
- **Voice**: join with one click, mute/deafen, voice activation with a sensitivity meter or push-to-talk
  (any key or mouse button, works while WoW has focus), a green ring shows who's talking. Opus codec, ~30 kbit/s.
- **Files**: drop a file into the chat (up to 25 MB); pictures show inline, everything else downloads with one click.

A lodge runs on someone's own server (see [`server/`](server/), one small .NET service behind your existing
web server). Ask the lodge owner for the address and your personal invite code. The code is stored encrypted
for your Windows user and is only ever sent in a request header, never in a URL.

## Elan's Hunter Helper

The hunter toolkit for WoW Forever: pet abilities and where to tame them, Beast Finder with spawn
points, pet training builds, stable, Auto Shot timer, range and dead zone, rare radar, feeding with
food sources, ammo vendors, hunter skills by level, talent builds, threat bar, pet spell bar, macros,
tips, and a quiet Lua error catcher (Bug Trap).

## Credits

The addon stands on the work of others - thank you!

- **[Petopia Forever](https://www.wow-petopia.com/forever/)** - pet families, abilities and ranks, which beasts teach
  them, and beast spawn locations. The Beast Finder, family pages and teacher tooltips are built from Petopia's data.
- **[Questie](https://github.com/Questie/Questie) / QuestieDB** (GPL-3.0) - NPC and item data: food and ammo vendors,
  food drops, hunter and pet trainers, Stable Masters and their coordinates.
- **[WoW Forever Tools](https://wowforevertools.com)** - hunter trainer spells, ranks, levels and costs.
- **talentsforever.com** (CC BY 4.0) - hunter talent trees used for the builds.
- **classic-hunter Forever wiki** - Forever-specific hunter mechanics behind several tips.
- **[HereBeDragons](https://github.com/Nevcairiel/HereBeDragons)** - the map-to-world math the minimap pins follow.
- World of Warcraft, its icons and names belong to Blizzard Entertainment. This is a fan project, not affiliated
  with Blizzard.

If you're one of these authors and want something credited differently or removed, please
[open an issue](https://github.com/Elanoran/ElanAddonHub/issues) and it will be fixed.

---

## For developers

Built with WPF on .NET Framework 4.8 (ships with Windows, so the exe stays tiny).

    cd src
    dotnet build -c Release            -> src/bin/Release/ElansAddonHub.exe

Releases are made from the addon repo with `Tools/release.py`:

    python Tools/release.py            build addon zip + hub exe + manifest.json (nothing uploaded)
    python Tools/release.py --local    manifest with local paths, for testing the hub
    python Tools/release.py --publish  upload as a GitHub release here

The hub reads `releases/latest/download/manifest.json`, which lists the newest addon and hub versions
with download links, SHA-256 checksums and the changelog.

Testing without clicking: `ElansAddonHub.exe --selftest <dir>` renders the window to PNGs, installs the
first addon that needs it, writes `result.txt` and quits. Log: `%LOCALAPPDATA%\ElansAddonHub\hub.log`.
