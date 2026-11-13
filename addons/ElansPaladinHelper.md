# Elan's Paladin Helper

For **paladins** on WoW Forever: never run without a Seal, Aura or Blessing again, keep your party blessed, and heal or
cleanse from party frames with a click.

[![Download ElansPaladinHelper.zip](https://img.shields.io/badge/Download-ElansPaladinHelper.zip-2da44e?style=flat-square&logo=github)](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansPaladinHelper.zip)

Works on its own, or get it with [Elan's Outpost](../README.md) to keep it up to date.

## What it does

- **Reminders**: an icon when your Seal, Aura, your own Blessing or (when you tank) Righteous Fury is missing. The Seal
  reminder only shows in combat or when you target something you can attack.
- **Seal and Judgement bar**: how long your Seal has left and when Judgement is ready again.
- **Blessing timers**: who you blessed and for how long. Gold means re-bless soon, red means it has run out.
- **Party and raid frames**: compact tiles with health, mana, range, aggro and a mark when you can cleanse someone. Each
  tile suggests the right Blessing for that player (by class and role, only Blessings you know) and shows "!" when it is
  missing. Tanks get Kings first; Salvation for damage dealers in raids is a setting.
- **Click-casting on the tiles** (set per character): left = suggested Blessing, right = Flash of Light / Holy Light,
  Shift+left = Holy Light, middle = Cleanse or Purify, Ctrl+left = target, Ctrl+right = the unit menu. Every click can be
  changed to any of these spells you know.
- **Trainer help**: the **Paladin Skills** tab lists every rank by level (train now, level N, known), with costs once you
  have visited a trainer and a mark on spells WoW Forever changed; a chat line at level-up; paladin trainers on the world
  map (click one for a waypoint).
- **Bar Plan**: a suggested level-60 action bar layout for each spec (Holy, Protection, Retribution), so you can put
  spells in their final place while levelling. Easy keys for the spells you press most, the same lifesavers and blessings
  on the same keys in every spec, a reason for every spot in the tooltip. Swap buttons to make it yours and drag a known
  spell onto your action bar.

## Commands

| Command | |
|---|---|
| `/eph` | open the window |
| `/eph bars` | the Bar Plan tab |
| `/eph skills`, `/eph settings` | open that tab |
| `/eph layout` | move the HUD and the party frames |
| `/eph hud`, `/eph frames` | turn the HUD / party frames on or off |
| `/eph tank` | tank mode (the Righteous Fury reminder) |
| `/eph minimap` | show or hide the minimap button |
| `/eph changes`, `/eph bugs` | what's new, trapped errors |

`/paladinhelper` works too.

## Settings

Reminders (and how early to warn before a Blessing runs out), each part of the HUD, the party frames (size, columns,
mana bar, pets, range fade, cleanse sound), the click bindings, and a Blessing override per class (Automatic, Might,
Wisdom, Kings, Salvation or Light).

## Good to know

- Folder name: `ElansPaladinHelper`. It only loads on a paladin.
- Buffs are read out of combat; in combat the tiles show the last known state, dimmed, plus your own casts.
- With [Elan's Toolkit](ElansToolkit.md) a map pin click gives you its waypoint arrow, and errors go to its Bug Trap;
  TomTom works for waypoints too. None of them is needed.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
