<p align="center"><img src="src/Assets/hub.png" width="96" alt="Elan's Outpost"></p>

<h1 align="center">Elan's Outpost</h1>
<p align="center"><em>formerly Elan's Addon Hub</em></p>

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
- **Manages your other addons too**: lists everything in `Interface\AddOns` (multi-folder addons grouped, flavor-specific
  `.toc` files handled) and updates them without any account or API key: GitHub releases (BigWigs `release.json` aware)
  and WoWInterface. Paste a link on the addon's card to connect it. Wago-only addons just get a link.
  **CurseForge-managed addons** (the CurseForge desktop app on this PC) get a *CurseForge* badge, are grouped by CurseForge's own
  folder lists and show "Update available: <file>" / "Up to date" with when CurseForge last checked. **Update** asks the CurseForge
  app to install that file (its `curseforge://install` link) - the hub never touches those folders, calls CurseForge's web API or
  downloads from it. **Check CurseForge now** (or the optional automatic daily / on-WoW-start check) starts CurseForge minimized,
  waits for its refresh and closes it again; if it is already running the hub just reads its data.
  Addons without a source get a **suggested WoWInterface match** (by folder names, title, author and game version,
  from WoWInterface's public file list cached for 24 h): one click on **Use this** links it, **Not this** hides it for good;
  nothing is ever linked or installed on its own.
  Multi-folder addons (Details + plugins, Questie + QuestieDB, HealBot + HealBot_*, DBM-*, `X-Part-Of`, folders that one
  WoWInterface file ships together) are grouped on one card; shared libraries (LibStub, Ace3, !BugGrabber) stay separate.
  **WoW Forever is its own flavor, not Classic** (Interface 16001, `_Camelot` / `_Forever` tocs, release.json flavor
  `forever`): Classic/Era downloads are never picked automatically, only by hand with a warning, and WoWInterface matches
  not marked for Forever are flagged and left out of **Link all exact matches**.
  Each update backs up the old folders (last 2 kept, one-click **Roll back**) and never touches `WTF`/SavedVariables.
- **Tray & notifications**, optional **start with Windows** and **automatic updates**.
- **Updates itself** when a new hub version is released.
- Never touches a development copy (a folder with `.git`).

## The Lodge: chat, voice and files

The **Lodge** tab is a small private Discord for you and your friends:

- **Channels and voice rooms** - text channels and voice rooms in a sidebar, with unread dots and @mention
  badges; rooms show who's inside. Ranks decide who sees which channel (e.g. an Officers channel).
- **Chat** - history per channel, replies, edit (or press Up) and delete your own messages, @mentions
  (highlighted, with a notification while you're in WoW), "is typing", paste screenshots with Ctrl+V.
- **Voice** - click a room to join; mute/deafen, voice activation with a level meter or push-to-talk (any key or
  mouse button, works while WoW has focus), per-person volume, join/leave chimes. Opus codec, ~30 kbit/s.
- **Files** - drop a file into the chat (up to 25 MB); pictures show inline, everything else downloads in one click.
- **Status** - Online, AFK, Busy, In a dungeon or Looking for group, plus a short note; AFK automatically after
  10 minutes idle.
- **Ranks** - Guild Master, Officer, Veteran, Member and Initiate (guest), shown as WoW item-quality frames.
  Officers can mute or kick lower ranks and delete messages; Initiates can't share files.
- **Guild Master panel** (Settings) - create invite links, change ranks, remove people, add and remove channels.
- **What you're playing** - names in WoW class colours with level, and a game icon while you're in WoW. Comes
  from the small **Elan's Hub** companion addon (installed by the hub for everyone; WoW saves it on /reload and logout).
- **In-game overlay** - the people in your voice room on top of WoW, whoever talks lights up. A click-through
  window (Windowed/Fullscreen mode) - nothing is injected into the game.

Join with the **invite link** you were sent (`https://<site>/lodge#invite=...`). A lodge runs on someone's own
server - see [`server/`](server/): one small .NET service behind an existing web server. Your code is stored
encrypted for your Windows user and only ever sent in a request header, never in a URL.

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
- **[NAudio](https://github.com/naudio/NAudio)** (MIT) - microphone and speaker audio for the Lodge voice chat.
- **[Concentus](https://github.com/lostromb/concentus)** (BSD) - the Opus voice codec in pure C#.
- **[Costura](https://github.com/Fody/Costura)** (MIT) - packs everything into the one exe.
- **[Octicons](https://github.com/primer/octicons)** (MIT) - the GitHub mark on third-party addon cards. The GitHub and
  CurseForge marks (the latter read at runtime from the user's own installed CurseForge app, never bundled) are
  trademarks of their owners and only show where an addon comes from. WoWInterface and Wago show text chips; the
  Local folder glyph is our own drawing.
- World of Warcraft, its icons and names belong to Blizzard Entertainment. This is a fan project, not affiliated
  with Blizzard.

If you're one of these authors and want something credited differently or removed, please
[open an issue](https://github.com/Elanoran/ElanAddonHub/issues) and it will be fixed.

## Live character and avatars

- **Status strip.** The Elan's Hub addon draws a tiny strip (32 coloured 4x4-pixel squares) in the top-left corner of the
  game. It encodes your class, race, sex, level and name, and the Hub reads that corner of the screen about every 2 seconds,
  so friends see your character without a /reload. Nothing is sent to WoW and no game memory is read - it only looks at
  pixels. Turn it off with `/ehub pixel off` in game, or the "Detect my character live" switch in the Hub settings.
  Without it, the Hub falls back to the addon's SavedVariables (updated on /reload and logout).
- **Avatars.** No Blizzard art is included in this repository. Class portraits are loaded at run time from your own
  WoW folder when an installed addon ships them (currently HealBot's class icons under `Interface\AddOns\HealBot\Images\class`);
  otherwise the avatar is the class colour with a race code.

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
