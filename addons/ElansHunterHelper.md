# Elan's Hunter Helper

The hunter toolkit for **WoW Forever**: your pet, where to tame the next one, the Auto Shot rhythm and the range you
shoot from, all in one place.

**[Download ElansHunterHelper.zip](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansHunterHelper.zip)**
· needs **[Elan's Toolkit](ElansToolkit.md)** · or get both with [Elan's Outpost](../README.md)

## What it does

**Your pet**
- **My Pet**: your pet's training plan (what it can still learn, and from which beasts) and what it eats.
- **Stable**: a snapshot of each pet you have had out, and which pets are stabled.
- **Pet Build**: a training-point planner (a rank costs its own training points, at most 4 active abilities).
- **Feeding**: a Feed button that glows when your pet needs food and picks the right food. WoW Forever hides pet
  happiness, so a feed timer stands in for it.
- **Pet spell bar**: a paw button that opens Call, Dismiss, Revive, Mend, Feed, Tame and Beast Lore, usable in combat.

**Taming**
- **Beast Finder**: 598 tameable beasts with their spawn points, a 3D model and a waypoint button.
- **Pet Abilities** and **Pet Families**: every ability rank and which beasts teach it; an overview of each family.
- **Tooltips** on beasts: tameable or not, what it teaches, whether that is a new rank for you, diet and speed.
- **Rare radar**: an icon when a tameable rare's spawn spot is near; alerts for rares and for beasts that teach you a new rank.
- **Taming helper**: after a tame, which ranks you still have to learn from that beast.
- **Map pins** for beasts (filterable), Stable Masters, hunter and pet trainers and ammo vendors: hover the paw button
  next to the world map.

**Combat** (shown on Elan's Toolkit's HUD)
- **Distance strip**: melee, dead zone or in range.
- **Auto Shot timer** on the cast bar: "Reload - free to move" and "Aiming - don't move".
- A warning when Aspect of the Cheetah or Pack may daze you, and a **post-fight summary** (clipped shots, damage, tips).

**Hunter**
- **Hunter Skills**: what your trainer teaches by level (train now, coming up, known).
- **Specs**: 7 talent builds for WoW Forever (leveling, dungeon, raid, PvP and more); the talent window names your
  build's next talent. Builds can be shared as text.
- **Macros**: 17 tested macros for WoW Forever. **Tips & Tricks**: short tip cards.
- **Items**: a gear upgrade line for your build, a pet food line on food, ammo vendors on tooltips.
- **Sharing**: export and import your discoveries, or share them with your guild.

## Commands

| Command | |
|---|---|
| `/ehh` | open or close the window |
| `/ehh pet`, `stable`, `planner`, `abilities`, `families`, `skills`, `specs`, `macros`, `tips`, `settings` | open that tab |
| `/ehh find <name>` | search the Beast Finder |
| `/ehh stats` | show your last fight's summary again |
| `/ehh layout` | move the HUD pieces |
| `/ehh hud` | show or hide the feed / ammo strip |
| `/ehh pins off\|new\|tameable\|rare\|elite\|all` | which beasts get map pins |
| `/ehh known` | the pet ability ranks you know |
| `/ehh fed` | reset the feed timer · `/ehh food`: foods you told it to ignore |
| `/ehh export`, `/ehh import`, `/ehh sync` | share discoveries |
| `/ehh minimap` | show or hide the minimap button |
| `/ehh bugs`, `/ehh debug` | for bug reports |

`/hunterhelper` works too. **Key bindings** (Key Bindings > AddOns): open the window, open the Beast Finder, open My Pet.

## Settings

The Settings tab starts with a **Quick setup** (Minimal, Recommended, Everything), then has sections for tooltips, the
Shot HUD, feeding, alerts, what the addon learns and records, the HUD, sharing, talents, and the window and map pins.

## Good to know

- **Install it together with Elan's Toolkit.** The HUD it adds its pieces to (cast bar, threat bar, flight timer) lives
  in the Toolkit. Folder names: `ElansHunterHelper` and `ElansToolkit`.
- A **Quick setup** window appears once after the first login. Open **Beast Training** once so the addon knows your pet's ranks.
- After an update that adds files, quit WoW completely and start it again; a `/reload` is not enough.
- With Elan's Outpost, your pets also show on the Outpost's Characters page.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
