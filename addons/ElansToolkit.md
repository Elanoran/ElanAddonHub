# Elan's Toolkit

Useful for **every class** on WoW Forever: a movable HUD, a waypoint arrow, the Camp Boss, and one place that catches the
errors of all of Elan's addons.

[![Download ElansToolkit.zip](https://img.shields.io/badge/Download-ElansToolkit.zip-2da44e?style=flat-square&logo=github)](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansToolkit.zip)

Works on its own, or get it with [Elan's Outpost](../README.md) to keep it up to date.

## What it does

- **HUD**: one movable block with a **cast bar** (spell name, icon, time), a **threat bar** and a **flight path
  countdown** that learns how long each route takes ("~" marks an estimate). Elan's class addons add their own pieces to it.
- **Center bars** (off by default, `/etk bars`): curved health and power bars around your character, you on the left and your
  target on the right, with a label beside the right arc: level badge, name and range. In combat a thin arc inside the right arc
  shows your threat, and the label says who holds aggro, like Pet tanking. Under each arc: health and power as numbers
  (current / max; when the game hides them in a fight, its own number or nothing). Under the level and range: your target's cast
  bar, and whether your interrupt is ready (Kick, Pummel, Counterspell, Earth Shock, Hammer of Justice, War Stomp and more),
  green with a pulsing edge when it is, else its cooldown, "too far" or "Can't interrupt". Above the label: your
  target's debuffs as icons (all, or only yours), with the time left and stacks, in combat too.
- **Tooltip** (`/etk tooltip`, everything off by default): choose where each kind of tooltip goes (world units, world objects, unit
  frames, buttons): the corner, the mouse, next to it or a spot you drag, with offsets. Also a dark background with a border by class, reaction or item quality, the name in that colour, Horde and Alliance as icons, who it is targeting (YOU when it is you), a mana, rage or energy bar, a slim health bar, and the item ID on
  Shift. Each switch works by itself.
- **Waypoint arrow**: a movable arrow (3D or classic) with the distance and time left. It turns from green to yellow to
  red as you face away. Numbered pins and lines show on the map and minimap, and a button bar under the world map steps
  through your stops or adds one where you click. A **trip** is a list of up to 10 stops.
- **The Camp Boss** (off by default, `/etk boss`): a small talking head with her tray icons in her bar. Click the bar to
  ask her something.
- **Activity card**: her bar opens up into a card for what you are doing right now, as wide as her bar and flush under it.
  Fishing with [Elan's Profession Helper](ElansProfessionHelper.md) is the first. She speaks over it. It is on her tab
  (**Activity card**, takes a `/reload`); with it off, addons use their own window.
- **Zone levels** on the world map: hover a zone on a continent map, or see "Level x-y" on a zone map.
- **Professions** moved to [Elan's Profession Helper](ElansProfessionHelper.md) in 0.23.0: the skills and trainers tab, the
  trainer pins and the gathering guide, with your settings. The Toolkit still records your nodes and fishing spots, and its
  arrow can walk a gathering route from the Profession Helper's tracker.
- **Bug Trap**: catches the errors of Elan's addons (or of all addons, or none) without the game's pop-up, with a copy
  window for bug reports.
- **Atlas recording** (for Elan's Outpost, on by default): what drops, what vendors sell, where creatures stand, as ids
  and counts only. It can be turned off in Settings.

## Commands

| Command | |
|---|---|
| `/etk` | open the window |
| `/etk tooltip` | where the tooltip goes and how it looks, off by default |
| `/etk boss` | the Camp Boss: a small talking head, off by default, who also tells you when you have played long enough. Her settings and what she has said. Click her bar to poke her and pick what to ask her (Report, Remind me, Quiet, Last line); right-click it for her last line. With Elan's Hub 1.19.0 she can also read out Elan's Outpost's notifications over the game (Outpost: Settings > In-game overlay > Who tells you) |
| `/etk hud on\|off` | the HUD · `/etk bars`: the center bars on or off (the cast bar then hangs centered below them) · `/etk castbar`: what "Hide the game's own cast bar" finds · `/etk flight on\|off`: the flight timer · `/etk layout`: move the HUD |
| `/etk way <zone> <x> <y> [title]` | set a waypoint (zone name or map id; x and y from 0 to 100) |
| `/etk way list`, `way next`, `way prev`, `way clear` | your waypoints |
| `/etk trip <stop>; <stop>; ...` | a trip of up to 10 stops (`[fly] <zone> <x> <y> [title]` each) |
| `/etk flights`, `nav`, `bugs`, `options` | open that tab |
| `/etk prof` | says where the profession features went (`/eprof`, Elan's Profession Helper) |
| `/etk button` | show or hide the minimap button |

`/elanstoolkit` works too, and `/way` when no other addon (such as TomTom) has it.

## Settings

Zone levels, Atlas recording (and WoW's combat log, always or only in dungeons and
raids), the minimap button; the HUD's parts; the tooltip; and the arrow's style, colour, size, sound and when it hides; the Camp Boss and her activity card.

## Good to know

- Folder name: `ElansToolkit`. It works on its own; with [Elan's Hub](ElansHub.md) and Elan's Outpost it shares what it learns.
- Elan's Hunter Helper needs this addon; Elan's Paladin and Priest Helper use its Bug Trap and waypoint arrow when it is there,
  and Elan's Profession Helper its activity card, Camp Boss, arrow and recorded nodes.

> [!WARNING]
> After an update that adds files, quit WoW completely and start it again; a `/reload` is not enough.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
