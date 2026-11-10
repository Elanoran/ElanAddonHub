# Elan's Profession Helper

**Your professions in one addon** for every class on WoW Forever: fishing on one key, a cooking guide, your skills and the nearest
trainers with pins on the map, and the profession guides you follow from Elan's Outpost.

[![Download ElansProfessionHelper.zip](https://img.shields.io/badge/Download-ElansProfessionHelper.zip-2da44e?style=flat-square&logo=github)](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansProfessionHelper.zip)

Works on its own, or get it with [Elan's Outpost](../README.md) to keep it up to date.

## What it does

### Fishing

- **One key casts and hooks.** Bind *Cast / hook* (Key Bindings > AddOns, or `/angler bind F`). The key casts Fishing, and
  while the bobber is out it presses the game's Interact Key on the bobber to hook. It does nothing until the bobber is
  targeted, so a second press never cancels your cast.
- **Double right-click** on the world casts while you hold a fishing pole, and hooks while the bobber is out. Right-clicking
  the bobber itself still hooks, as in the base game.
- **Pole, weapons and lure on a press.** *Pole / weapons* (`/angler bindgear G`) puts your best pole on, and the next press
  gets the weapons you wore back, in a fight too. When your pole has no lure, the cast press puts on the best one your skill
  can use instead of casting.
- **A louder splash and auto loot** while you fish: sound effects up, music and ambience off, auto loot on. Your own
  settings come back 3 minutes after your last cast, when a fight starts or when you put the pole away.
- **The fishing card**: your catches, escapes, catches an hour and streak, your skill, your line, your last catch and a lure
  timer. With [Elan's Toolkit](ElansToolkit.md) it sits on the activity card under the Camp Boss's bar; without it, it is a
  window of its own you can drag.
- **The Camp Boss** (Elan's Toolkit, when she is on) has something to say about a good catch, a catch milestone, a long
  streak and the first of a kind. Without her, the big ones get a chat line.
- **Catches counted** per session and per character.
- **Key guide**: the **?** on the fishing card (or `/angler keys`) shows how to fish and your two keys, with a **Set**
  button for each. It opens by itself once if you have no fishing key yet.
- **Fishing guide**: pick what you fish for on the Fishing tab (`/angler for level / food / money`). It tells you
  where to fish for your skill, your next rank (a trainer up to 150, the Expert book in Booty Bay, Nat Pagle), which
  fish to catch for your class's food (Attack Power or Spell Damage) and what to cook, with waypoints. The fishing card
  shows the next step. Zone skill values are approximate. Fishing supplier pins on the world map (`/angler pins`).

### Cooking

- **Cook with a purpose.** On the Cooking tab, pick *Level my Cooking*, *Buff food for my class* or *Money*, and it says what
  to do next from your Cooking skill, level, class and side.
- **Levelling:** the three best recipes for you right now, in the colour the game gives them at your skill, about how many
  crafts they stay worth it, what they need, and where the meat drops at your level. A waypoint to the recipe vendor, and your
  next rank: Journeyman at a trainer, the Expert Cookbook at 125, the Artisan quest at 225.
- **Buff food:** the strongest food you can cook and eat for your class. On Forever most food gives one stat (Strength,
  Agility, Stamina, Intellect; the new teas Healing Power) plus 5% XP from kills.
- **Forever's own numbers:** skill-up ranges, reagents and buffs come from the Forever client, with the Forever-new recipes
  and the ones that need an Iron Oven next to the fire.
- **Recipes around your skill** in their colours, and **recipe seller pins** on the world map for your side.

### What to make next

- **A panel beside your profession window** for Alchemy, Blacksmithing, Enchanting, Engineering, Leatherworking, Tailoring,
  First Aid, Mining's smelting and Cooking: the five best recipes to level with right now, in the colour the game gives them at your
  skill, with about how many crafts they stay worth it. Hover one for its reagents (have / need); click one you know and the
  window opens it. It only sits beside the window and changes nothing in it.
- **Your bags and bank:** `x3` when you can make it now, or which reagents are missing (and when you can make a missing one
  yourself, like Bolt of Linen Cloth). The bank counts as of your last visit.
- **Shopping list to your cap:** unfold it under the panel. The recipes from your skill to your cap, and every reagent they
  take minus what you have; bars say how much ore Mining smelts them from. The crafts are an average, since skill-ups past
  orange are luck.
- **Cooking** also shows the best buff food for your class as its own row, and recipes you buy say so: hover for the
  nearest seller of your side, click for a waypoint.
- **Where to get each reagent:** mined, skinned, herbs, cloth from humanoids, disenchanted, fished, the nearest vendor of
  your side (click the line for a waypoint), or the zones where beasts drop it at your level.
- **Your other characters:** with [Elan's Bags](ElansBags.md) the list says who has a reagent you still need, in their bags or
  bank. Switch it off on the Skills & trainers tab.
- **It learns from the game:** the profession window tells it which recipes you know, and a trainer's window the skill each
  recipe is taught at. Recipes from a pattern or plans are only suggested once you know them.
- **Forever's own numbers** from the client: skill-up ranges, reagents, recipe items and where to craft.

### Skills and trainers

- **Skills & trainers tab**: every profession you have learned with its skill bar, a note when you are near your cap, and
  the three nearest trainers of your faction that can teach your next rank. Click a trainer for a waypoint.
- **Trainer pins** on the world map and the minimap: gold-framed when that trainer can teach your next rank soon, dimmed or
  hidden when you have outgrown them. The Toolkit's map bar button for its pins switches them too.

### The gathering guide

- **Guide tab**: the profession guides you follow from Elan's Outpost (Professions page, "Follow in game"; needs
  [Elan's Hub](ElansHub.md)).
- **Guide tracker**: your current step, the reagents with your bag and bank counts, a shopping list, the next step and, on a
  gather step, how far and which way the next node is.
- **Gathering pins**: the herbs, ore, chests and fishing spots you recorded with Elan's Toolkit, and the guide's nodes, on
  the world map and minimap, with the route of your gather step as a numbered line. With the Toolkit its arrow can walk
  the route.

### The window

`/eprof` or a click on the minimap button opens the window: a tab for Fishing, Skills & trainers, the Guide and General.
Right-click the minimap button for Fishing. It is also under Options > AddOns.

> [!IMPORTANT]
> Every cast and hook is your own key press or click. The addon never watches for a bite, never casts by itself and never
> crafts, buys or moves anything.

## Commands

| Command | |
|---|---|
| `/eprof` | open or close the window · `/eprof status`: each part in one line |
| `/eprof fishing` (or `/angler`) | your fishing numbers · `/angler settings`, `/angler keys`, `/angler stop`: your own sound and loot settings back now |
| `/angler bind <key>`, `/angler bindgear <key>` | the Cast / hook key and the Pole / weapons key |
| `/eprof skills` | the Skills & trainers tab · `/eprof skills map`, `minimap`, `outgrown`: the trainer pins |
| `/eprof guide` | the Guide tab · `/eprof guide show\|hide\|next\|prev`: the tracker |
| `/eprof next` | what to make next in each crafting profession · `/eprof next tailoring`: one profession · `/eprof next shop [profession]`: the plan and shopping list to your cap · `/eprof next panel`, `bank`: the panel, count the bank · `/eprof next gap 40`: the panel further from the window |
| `/eprof minimap` | show or hide the minimap button |
| `/eprof changes`, `bugs`, `diag` | what's new, its Bug Trap, a diagnostics run for the Outpost's Health window |

`/professionhelper` works too.

## Good to know

- Folder name: `ElansProfessionHelper`. It works on its own. [Elan's Toolkit](ElansToolkit.md) adds the activity card,
  the Camp Boss, the waypoint arrow and the nodes you recorded; [Elan's Hub](ElansHub.md) adds the guides from the Outpost.
- It took over the profession parts of Elan's Toolkit (0.23.0): your settings for them come along by themselves, once.

> [!WARNING]
> After an update that adds files, quit WoW completely and start it again; a `/reload` is not enough.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
