# Elan's Hub

The small **companion addon** of [Elan's Outpost](../README.md). It tells the Outpost (and your Lodge, if you join one)
which character you play, where you are and what you carry. Without the Outpost you don't need it; Elan's Bags uses it
for your other characters' items, and Elan's Toolkit for the Outpost's profession guides.

[![Download ElansHub.zip](https://img.shields.io/badge/Download-ElansHub.zip-2da44e?style=flat-square&logo=github)](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansHub.zip)

Works on its own, and comes with [Elan's Outpost](../README.md).

## What it does

- **Status strip**: a tiny block of coloured squares in the top-left corner that the Outpost on the same PC reads from the
  screen: your character, level, zone and status (in combat, dead, AFK, resting, in a group), and a few settings of other
  Elan addons that the Outpost uses (such as where a window of theirs sits). Hover it to see what it is;
  clicks go through it. It can be turned off.
- **Saves your characters** for the Outpost, when you `/reload` or log out: level, zone, money, bags, bank, gear and
  stats, talents, professions and recipes, reputations, and the guild bank tabs you looked at. The bank, recipes and
  guild bank are only saved after you open them once.
- **Your map position** for the Outpost's world map: off by default; only on when you turn it on.
- **Emote wheel**: a small icon that opens a ring of 6, 8 or 10 emotes (Thank, Cheer, Wave, Hello, Bow, Laugh, Applaud,
  Dance, or your own choice). It can be bound to a key (hold, release on the emote). Right-click the icon to unlock it, drag it to move it, and right-click again to lock it.
- **`/rl`** for `/reload`, when no other addon already has it.
- Other Elan addons share their data through it (for example Elan's Bags reads your other characters from it).

**On its own**, without the Outpost, you get the emote wheel and `/rl`; the rest is only read by the Outpost.

## Privacy

It sends **nothing** over chat or addon channels. Everything stays in WoW's own save files on your PC, where the Outpost
reads it. Your Lodge only gets what the Outpost shares, and each kind of sharing has its own switch in the Outpost
(your position, for example, is off by default).

## Commands

| Command | |
|---|---|
| `/ehub` | the settings window |
| `/ehub pixel on\|off` | the status strip |
| `/ehub position on\|off` | share your map position with the Outpost (off by default) |
| `/ehub calendar on\|off` | save the in-game calendar for the Outpost |
| `/ehub wheel` | emote wheel settings · `wheel on\|off\|lock\|unlock\|reset\|open` |
| `/ehub copy` | pick from the last 200 chat lines (click, Shift-click, drag or search), then copy only those (nothing is saved) |

`/elanshub` works too.

## Good to know

- Folder name: `ElansHub`.

> [!WARNING]
> After an update that adds files, quit WoW completely and start it again; a `/reload` is not enough.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
