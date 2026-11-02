# Elan's Priest Helper

For **priests** on WoW Forever: heal frames with click-casting, your Renew and shield timers, a buff manager that keeps the
raid buffed, and a coach that shows how you spent your mana.

[![Download ElansPriestHelper.zip](https://img.shields.io/badge/Download-ElansPriestHelper.zip-2da44e?style=flat-square&logo=github)](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansPriestHelper.zip)

Works on its own, or get it with [Elan's Outpost](../README.md) to keep it up to date.

## What it does

- **Heal frames**: compact tiles with health, mana, range, aggro and a dispel mark, plus your Renew, Power Word: Shield,
  Weakened Soul, Prayer of Mending and Fear Ward with the seconds left. A tile shows "REZ name" when another priest with
  this addon is already resurrecting that player.
- **Click-casting**: nine clicks (left, right, Shift, Ctrl, middle, mouse buttons 4 and 5), each with a spell *and a
  rank*, so you can downrank. The **Smart** click resurrects a dead player out of combat, buffs a missing Fortitude, and
  otherwise heals (Flash Heal unless you choose another). Defaults: left = Smart, right = Renew, Shift+left = Greater Heal,
  Shift+right = Power Word: Shield, middle = Dispel or Cure Disease.
- **Your timers** on the HUD: who has your Renew, shield, Mending, Fear Ward, and for how long, in combat too.
- **Five-second rule bar**: how long until your mana regenerates again, and when the next mana tick lands.
- **Buff manager** (Buffs tab): who in your groups is missing Fortitude, Spirit or Shadow Protection. Pick the raid groups
  you buff; other priests with the addon see your groups and you see theirs. A **Buff next** button casts the right buff
  on the next player who needs it (the Prayer version when enough need it and you carry a candle).
- **Healer coach**: after each fight of 15 seconds or more, a short report in chat (casts, mana spent, time spent
  regenerating, a tip); the Coach tab compares the heal per mana of your ranks.
- **Trainer help**: the **Priest Skills** tab, a chat line at level-up, and priest trainers on the world map.

## Commands

| Command | |
|---|---|
| `/epr` | open the window |
| `/epr buffs`, `/epr skills`, `/epr settings` | open that tab |
| `/epr layout` | move the HUD and the heal frames |
| `/epr hud`, `/epr frames` | turn the HUD / heal frames on or off |
| `/epr minimap` | show or hide the minimap button |
| `/epr changes`, `/epr bugs` | what's new, trapped errors |

`/priesthelper` works too. **Key bindings** (Key Bindings > AddOns): open the window, Buff next.

## Settings

Each part of the HUD, the reminders, the heal frames (size, columns, range fade), the click bindings with their ranks,
the buff manager (which buffs, when to use the Prayer version, how early to warn) and the coach.

## Good to know

- Folder name: `ElansPriestHelper`. It only loads on a priest. This is its first version (0.1).
- Buff groups and resurrection coordination only work between priests who all run this addon.
- Health and mana are hidden from addons on this client, so there is no "heal the most hurt" click.
- With [Elan's Toolkit](ElansToolkit.md) a map pin click gives you its waypoint arrow, and errors go to its Bug Trap.
  It is not needed.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
