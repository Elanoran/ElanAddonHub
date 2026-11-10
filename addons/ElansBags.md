# Elan's Bags

**One tidy bag window** for every class on WoW Forever: search, categories, a free-space counter, a junk seller, and on
every item what your other characters carry.

[![Download ElansBags.zip](https://img.shields.io/badge/Download-ElansBags.zip-2da44e?style=flat-square&logo=github)](https://github.com/Elanoran/ElanAddonHub/releases/latest/download/ElansBags.zip)

Works on its own, or get it with [Elan's Outpost](../README.md) to keep it up to date.

## What it does

- **One window** for all your bags: B and the bag buttons open it. Search by name, type or quality (every word must
  match; "junk" finds grey items). The title counts new items, and the footer shows free slots and your money.
  Hover the money for every character's gold and the total.
- **Your other characters**: the **Alts** button (or a click on the money) shows any character's bags and bank as they
  were last saved, with a search.
- **Categories view**: New, Equipment, Consumables, Pet food (with Elan's Hunter Helper: what your pet eats), Trade goods,
  Quest, Ammo and quiver, Junk and Other. Click a heading to fold it.
- **Special bags** (quiver, ammo pouch, soul pouch, profession bags, key ring, reagent bag) get their own block and
  colour, and their empty slots are not counted as free space.
- **New items** have a gold ring until you hover them.
- **Bag bar** in the window: hover a bag to see its slots, drop a bag on it to swap.
- **Bank**: on WoW Forever's tabbed bank it opens next to the game's bank and lists every tab with the same search.
- **Mailbox list**: on the Send Mail tab, your other characters on this realm and faction, and your friends: this character's, your other characters' and
  Battle.net friends (remembered once seen playing). Click a name to
  put it in the To: field. Your other characters need [Elan's Hub](ElansHub.md) and one login each.
  Right-click a name to give it a note ("bank alt: herbs"): a small note icon marks the name, and hovering it shows the note.
  The last five people you mailed are listed under **Recent**. Tick **Bank alt** in that note box for one of your characters
  and it sits on top of the list, one click away (one per realm and faction).
- **Junk seller**: Alt+right-click an item to mark it as junk. At a merchant a **Sell junk** button sells grey and marked
  items; selling automatically is a setting (off by default).
- **Your other characters** on item tooltips, for example "Elan: 3 (bags) · Measley: 12 (bank)". This needs
  [Elan's Hub](ElansHub.md) and one login on each character.
- **Low ammo warning** for hunters.

## Commands

| Command | |
|---|---|
| `/ebags` | open or close the bags |
| `/ebags settings` | the settings window |
| `/ebags search <text>` | open the bags with a search |
| `/ebags view` | switch between All and Categories |
| `/ebags sort` | let the game sort your bags |
| `/ebags sell` | sell junk (at a merchant) · `/ebags junk`: the marked items · `/ebags junk auto`: auto-sell on/off |
| `/ebags chars` | the characters it knows · `/ebags forget <name>`: hide one |
| `/ebags alt <name>` | see that character's bags and bank |
| `/ebags bank on\|off` | use the Bags bank window or the game's |
| `/ebags mail on\|off` | show or hide the list next to the mailbox |
| `/ebags minimap` | show or hide the minimap button |

`/elansbags` works too.

## Settings

Tabs **General** (which windows it replaces, the mailbox list, the bag bar, new-item rings, slots per row, scale, opacity, tooltip line,
ammo warning), **Categories** (turn sections on or off), **Junk** (auto-sell, the marked items) and **Alts** (hide or forget
characters).

## Good to know

- Folder name: `ElansBags`. It works without any other addon; Elan's Hub adds the other characters on tooltips.

> [!WARNING]
> After an update that adds files, quit WoW completely and start it again; a `/reload` is not enough.

[All of Elan's addons](README.md) · [How to install by hand](README.md#installing-by-hand)
