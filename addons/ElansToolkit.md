# Elan's Toolkit

Useful for **every class** on WoW Forever: a movable HUD, a waypoint arrow, profession trainers on your map, and one place
that catches the errors of all of Elan's addons.

[![Download ElansToolkit.zip](https://img.shields.io/badge/Download-ElansToolkit.zip-2da44e?style=flat-square&logo=github)](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansToolkit.zip)

Works on its own, or get it with [Elan's Outpost](../README.md) to keep it up to date.

## What it does

- **HUD**: one movable block with a **cast bar** (spell name, icon, time), a **threat bar** and a **flight path
  countdown** that learns how long each route takes ("~" marks an estimate). Elan's class addons add their own pieces to it.
- **Center bars** (off by default, `/etk bars`): curved health and power bars around your character, you on the left and your
  target on the right, with a label beside the right arc: level badge, name and range.
- **Tooltip** (`/etk tooltip`, everything off by default): the game's tooltip at the mouse instead of in the corner, a dark
  background with a border by class, reaction or item quality, the name in that colour, a slim health bar, and the item ID on
  Shift. Each switch works by itself.
- **Waypoint arrow**: a movable arrow (3D or classic) with the distance and time left. It turns from green to yellow to
  red as you face away. Numbered pins and lines show on the map and minimap, and a button bar under the world map steps
  through your stops or adds one where you click. A **trip** is a list of up to 10 stops.
- **Profession trainers**: pins on the world map and minimap for the professions you have learned. A gold frame means
  that trainer can teach your next rank; trainers you have outgrown are dimmed or hidden. The **Professions** tab shows
  your skill against its cap and the nearest trainers for your next rank.
- **Zone levels** on the world map: hover a zone on a continent map, or see "Level x-y" on a zone map.
- **Profession guides** from Elan's Outpost ("Follow in game"): a tracker with your current step, the reagents and what
  you have of them, a shopping list, and gathering routes drawn on the map. It only shows the way; it never crafts, buys
  or moves anything.
- **Gathering and fishing pins**: the nodes you have found.
- **Bug Trap**: catches the errors of Elan's addons (or of all addons, or none) without the game's pop-up, with a copy
  window for bug reports.
- **Atlas recording** (for Elan's Outpost, on by default): what drops, what vendors sell, where creatures stand, as ids
  and counts only. It can be turned off in Settings.

## Commands

| Command | |
|---|---|
| `/etk` | open the window |
| `/etk tooltip` | the tooltip's five switches, off by default |
| `/etk boss` | the Camp Boss: a small talking head, off by default, and what she has said |
| `/etk hud on\|off` | the HUD · `/etk bars`: the center bars on or off (the cast bar then hangs centered below them) · `/etk castbar`: what "Hide the game's own cast bar" finds · `/etk flight on\|off`: the flight timer · `/etk layout`: move the HUD |
| `/etk way <zone> <x> <y> [title]` | set a waypoint (zone name or map id; x and y from 0 to 100) |
| `/etk way list`, `way next`, `way prev`, `way clear` | your waypoints |
| `/etk trip <stop>; <stop>; ...` | a trip of up to 10 stops (`[fly] <zone> <x> <y> [title]` each) |
| `/etk prof show\|hide\|next\|prev` | the profession guide tracker |
| `/etk map`, `/etk minimap`, `/etk outgrown` | trainer pins on the map / minimap, hide outgrown trainers |
| `/etk professions`, `flights`, `nav`, `bugs`, `options` | open that tab |
| `/etk button` | show or hide the minimap button |

`/elanstoolkit` works too, and `/way` when no other addon (such as TomTom) has it.

## Settings

Trainer pins, outgrown trainers, zone levels, Atlas recording (and WoW's combat log, always or only in dungeons and
raids), the minimap button; the HUD's parts; the tooltip; and the arrow's style, colour, size, sound and when it hides.

## Good to know

- Folder name: `ElansToolkit`. It works on its own; with [Elan's Hub](ElansHub.md) and Elan's Outpost it also gets profession
  guides and the Lodge's gathering nodes, and shares what it learns.
- Elan's Hunter Helper needs this addon; Elan's Paladin and Priest Helper use its Bug Trap and waypoint arrow when it is there.

> [!WARNING]
> After an update that adds files, quit WoW completely and start it again; a `/reload` is not enough.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
