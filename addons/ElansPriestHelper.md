# Elan's Priest Helper

For **priests** on WoW Forever: heal frames with click-casting, your Renew and shield timers, your cooldowns, a buff manager
that keeps the raid buffed, a Rez next button, a coach that shows how you spent your mana, and a book of everyone you have raised.

[![Download ElansPriestHelper.zip](https://img.shields.io/badge/Download-ElansPriestHelper.zip-2da44e?style=flat-square&logo=github)](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansPriestHelper.zip)

Works on its own, or get it with [Elan's Outpost](../README.md) to keep it up to date.

## What it does

- **Heal frames**: compact tiles with health, mana, range, aggro and a dispel mark, plus your Renew, Power Word: Shield,
  Weakened Soul, Prayer of Mending and Fear Ward with the seconds left. A tile shows "REZ name" when another priest with
  this addon is already resurrecting that player.
- **Click-casting**: nine clicks (left, right, Shift, Ctrl, middle, mouse buttons 4 and 5), each with a spell *and a
  rank*, so you can downrank. The **Smart** click resurrects a dead player out of combat, buffs a missing Fortitude (not in
  the first 20 seconds after a fight, and you can turn this off), and otherwise heals (Flash Heal unless you choose another). Defaults: left = Smart, right = Renew, Shift+left = Greater Heal,
  Shift+right = Power Word: Shield, middle = Dispel or Cure Disease.
- **Ready cues on the tiles**: Renew goes red and pulses in its last 3 seconds, the shield says GO when Weakened Soul ends, and
  Prayer of Mending pulses on whoever last had it when it is off cooldown.
- **Your timers** on the HUD: who has your Renew, shield, Mending, Fear Ward, and for how long, in combat too.
- **Your cooldowns** on the HUD: Prayer of Mending, Penance, Fear Ward, racial spells and more, from your own casts.
- **Five-second rule**: how long until your mana regenerates again, and when the next mana tick lands. With
  [Elan's Toolkit](ElansToolkit.md) it shows on the shared cast bar, and during a cast the ticks are marked on the cast itself.
  It can show on the Toolkit's center bars instead, or on both: a thin arc that drains in gold, turns soft blue while you
  regenerate, and carries a glint up the curve at every mana tick. Without the Toolkit, it is a bar of its own.
- **Buff manager** (Buffs tab): who in your groups is missing Fortitude, Spirit or Shadow Protection. Pick the raid groups
  you buff; other priests with the addon see your groups and you see theirs. A **Buff next** button casts the right buff
  on the next player who needs it (the Prayer version when enough need it and you carry a candle).
- **Rez next**: a button (and key binding) that raises the next dead player nobody is raising yet. A finished rez counts as an
  offer for 60 seconds, so priests with this addon don't cast it twice.
- **Book of the Fallen** (Book tab): who you have raised, how often, and the title they have earned. Kept on your PC.
- **Healer coach**: after each fight of 15 seconds or more, a short report in chat (casts, mana spent, time spent
  regenerating, a tip); the Coach tab compares the heal per mana of your ranks.
- **Trainer help**: the **Priest Skills** tab, a chat line at level-up, and priest trainers on the world map.

## Commands

| Command | |
|---|---|
| `/epr` | open the window |
| `/epr buffs`, `/epr book`, `/epr skills`, `/epr settings` | open that tab |
| `/epr layout` | move the HUD and the heal frames |
| `/epr hud`, `/epr frames` | turn the HUD / heal frames on or off |
| `/epr minimap` | show or hide the minimap button |
| `/epr changes`, `/epr bugs` | what's new, trapped errors |

`/priesthelper` works too. **Key bindings** (Key Bindings > AddOns): open the window, Buff next, Rez next.

## Settings

Each part of the HUD, the reminders, the heal frames (size, columns, range fade), the click bindings with their ranks,
the buff manager (which buffs, when to use the Prayer version, how early to warn) and the coach.

## Good to know

- Folder name: `ElansPriestHelper`. It only loads on a priest.
- Buff groups and resurrection coordination only work between priests who all run this addon.
- Prayer of Mending jumps, and addons can't follow that in combat: until the addon has seen where it is, the timer says
  "cast here".
- Health and mana are hidden from addons on this client, so there is no "heal the most hurt" click.
- With [Elan's Toolkit](ElansToolkit.md) the five-second rule goes on its cast bar, a map pin click gives you its waypoint
  arrow, and errors go to its Bug Trap. It is not needed.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
