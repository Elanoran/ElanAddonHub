# How Elan's addons and the Outpost fit together

This is the contract between the in-game addons, Elan's Hub and Elan's Outpost. Read it before adding
any data that crosses from the game to the Outpost (or back). If something new doesn't fit a role
below, change this file first.

## Roles

| Part | Its job | It never |
|---|---|---|
| **Feature addons** (Hunter Helper, Paladin Helper, Bags, Toolkit, future ones) | Make the game better while you play. Hand data that is only theirs to know to Elan's Hub with one call. | Talk to the Outpost, write files for it, or collect general character data the Hub already has. |
| **Elan's Hub** (addon, required) | The data layer and the only bridge. Collects general character data for every class, stores what feature addons hand over, versioned, in its own saved data. Owns the pixel strip and reads `SharedData.lua`. Gives feature addons read access to shared data. | Has game features of its own beyond small utilities (emote wheel, `/rl`). |
| **Elan's Outpost** (app) | Reads Elan's Hub's saved data, shows it, shares what the user allows with the Lodge. Writes back only `SharedData.lua` in the Hub folder. | Reads feature addons' saved files (except the legacy readers below, until migrated), writes any SavedVariables or anything else in the WoW folder besides installing addons. |
| **Lodge server** | Members, voice, chat, calendar, shared flight times. | Talks to the game or the addons directly. Only the Outpost talks to it. |

One sentence version: **addons → Hub → Outpost → Lodge, and back the same way.**

### Which data belongs where

- **General character data** (every class has it) is collected by **Elan's Hub**: identity, level/xp,
  money, equipped gear, talents, professions, bags, bank, guild bank, location.
- **Feature data** (only one addon knows it) is collected by that addon and **handed to the Hub**:
  e.g. Hunter pets and their abilities.
- **Class-agnostic features living in a class addon** are candidates to move to the Hub. Today that is
  the flight timer (Hunter Helper), which every class could use.
- **Settings** stay in each addon's own SavedVariables and are never read by the Outpost.

## Three shared layers

| Layer | Holds | Where |
|---|---|---|
| **Live data** | What changes while you play: character data, bags, talents, handed-over feature data | Elan's Hub (`Publish` / `Get` / `Subscribe`) |
| **Shared code and reference data** | Code and fixed data used by **two or more** addons: map/minimap pins, Bug Trap, Diag, theme, widgets, tooltips, the secret-value check, `copyDefaults` | **ElansLib** (planned, see below) |
| **Own** | Everything only one addon uses (e.g. hunter trainers, pet abilities) | That addon |

Generated reference data (trainers, vendors, spells, pet data, and the Outpost's zone names in `ZoneNames.cs`) is built by
`Tools/build_all.py` in the Hunter Helper repo from **one QuestieDB snapshot** (`Tools/sources`), for every addon and the Outpost,
so the game and the Outpost agree. `build_all.py --check` (run by `release.py` and `Tools/tests/data_check.py`) fails when a
committed generated file is stale. Runtime sharing of data through ElansLib stays limited to data two addons actually use
at runtime (none today).

### ElansLib (planned)

- One source folder in the addon repo (ElansHunterHelper, `lib/ElansLib/`). The release script copies it into every addon that
  lists it, so it is never edited inside an addon.
- Embedded, LibStub style: each copy carries a version number, and when several addons load, the newest
  copy wins. No extra addon to install, and an addon one version behind still works.
- Modules are added only when a second addon needs the same thing; until then the code stays in its addon.
- Reference data is not an ElansLib module: it is generated at build time by `Tools/build_all.py` (see above), and only data two
  addons use at runtime would move into the lib.
- Migration order: pins, Bug Trap, Diag first (most copies), then theme/widgets/tooltips, one addon at a time
  with its tests.

## Paths and how fast they are

| Path | Direction | Speed | Use for |
|---|---|---|---|
| SavedVariables (`ElansHub.lua`) | game → Outpost | on `/reload` or logout | everything that can wait |
| Pixel strip (Hub, top-left corner) | game → Outpost | ~1 s | live presence only (who, where, combat/AFK). Use sparingly: every byte added costs screen space |
| `SharedData.lua` (Hub folder) | Outpost → game | read on `/reload` | data from the Lodge (shared flight times) |
| Lodge messages | Outpost ↔ server | live | only what the user's sharing settings allow |

## Handing data to the Hub

Elan's Hub exposes one global, `ElansHub` (API level `ElansHub.API`). A feature addon never depends on it
being installed:

```lua
local H = rawget(_G, "ElansHub")
if H and H.Publish then
  H.Publish("hunter.pets", 1, petsTable)                 -- this character
  H.Publish("hunter.flight", 1, legs, { scope = "account" })
end
```

- `ElansHub.Publish(module, version, data [, opts])`
  - `module`: lower-case id, `owner.topic` for feature addons (`hunter.pets`), plain for the Hub's own
    (`core`, `gear`). Pattern `^[a-z][a-z0-9_]*(%.[a-z0-9_]+)?$`.
  - `version`: integer schema version of `data` (see Versions).
  - `data`: a plain table: strings, numbers, booleans and tables only. It is copied, so the caller can
    keep changing its own table.
  - `opts.scope`: `"char"` (default, stored under the current character) or `"account"`.
  - Returns `true`, or `false, reason` (bad module name, bad version, too big, not a plain table).
  - In combat the call is queued (latest wins per module) and stored after combat.
- `ElansHub.Get(module [, charKey])` returns `data, version, at` for reading shared data in-game (for
  example Bags showing alt counts). `charKey` defaults to the current character, `"account"` for
  account-scoped modules.
- `ElansHub.Subscribe(module, fn)` calls `fn(module, scope, charKey)` after `module` was stored (also after a queued
  publish flushed; `charKey` is `"account"` for account scope). The call is pcall-wrapped, so a failing callback can
  never break the Hub or other subscribers. Returns an unsubscribe function, or `nil, reason`. Use it to refresh a
  display when `ElansHub.Get` data changed; guard it like Publish: `if H and H.Subscribe then ... end`.
- `ElansHub.CharKey()` returns `"Realm-Name"`, the one key format for characters everywhere.
- `ElansHub.Characters()` (Hub 1.13.0) lists every character the Hub has data for: `{ { key, at, modules = { ... } } }`,
  newest first. Feature addons use it instead of reading `ElansHubData` directly.

### Stored layout (`ElansHubData`, in `ElansHub.lua`)

```lua
ElansHubData = {
  schema = 1,                                  -- layout of this table itself
  chars = {
    ["Realm-Name"] = {
      at = 1760000000,
      mods = { core = { v = 1, at = ..., by = "ElansHub", ver = "1.11.0", data = { ... } },
               ["hunter.pets"] = { v = 1, at = ..., by = "ElansHunterHelper", ver = "1.26.0", data = { ... } } },
    },
  },
  account = { mods = { ["hunter.flight"] = { ... } } },
}
```

`ElansHubData` is separate from `ElansHubDB` (Hub settings and legacy keys), so data and settings don't
mix even though WoW saves both into the same file.

## Versions

- Each module carries an integer `v`. **Adding** a field keeps `v`; **renaming, removing or changing the
  meaning or unit** of a field bumps it.
- The Outpost keeps a table of the highest `v` it understands per module. A newer `v` is not parsed: the
  Outpost says "Update Elan's Outpost to show <module>" instead of guessing.
- Readers ignore unknown fields, so an older Outpost keeps working with an addon that only added fields.
- Every schema lives in **Schemas** below with its version history. Change the doc in the same commit.

## Rules

- **Never collect:** chat text, mail contents, other players' data beyond what the user's own group
  sees, account or Battle.net names, real names, `/played` (asking for it prints to chat), anything
  read from Blizzard UI code (taint).
- **Combat:** no scanning in combat. Collect on events, out of combat, throttled. `Publish` in combat
  only queues.
- **Secret values:** every value read from the game passes the secret check before it is stored.
- **Size:** at most 20,000 values per `Publish` call. Keep per-character data small; prefer item id +
  count over full links.
- **One writer per file.** The game writes `ElansHub.lua`; the Outpost writes only `SharedData.lua`,
  atomically, data only, validated.
- **Read-only consumers.** Feature addons may read Hub data with `ElansHub.Get`, never write another
  addon's tables.

## Schemas

### `core` v1 (Hub, per character)
`name, realm, class, classFile, race, raceFile, sex, faction, level, xp, xpMax, rested, money, guild,
zone, subzone, updated`

Short field names are used where a table repeats many times (items, slots, nodes).

**Item** (inside containers and gear): `{ i = itemId, c = count, q = quality, ic = iconFileId }`. Full links
are not repeated per slot; each module has one `links = { [itemId] = itemLink }` table instead.

**Container**: `{ n = slots, k = kind, id = bag itemId or nil, ic = icon, s = { [slot] = Item } }`.
`k` is `"backpack" | "bag" | "reagent" | "bank" | "bankbag" | "tab"`.

### `bags` v1 (Hub, per character)
`{ at, cont = { [containerIndex] = Container }, links }` — backpack, bags and reagent bag. Scanned out of
combat after `BAG_UPDATE_DELAYED`, throttled to once per 2 s.

### `bank` v1 (Hub, per character)
`{ at, cont = { [containerIndex] = Container }, links }` — only rescanned while the bank is open
(`BANKFRAME_OPENED` until closed); `at` tells how old it is.

### `guildbank` v1 (Hub, account)
`{ guilds = { ["Realm-Guild"] = { name, realm, money, at, tabs = { [tab] = { name, ic, at, s = { [slot] = Item } } } } }, links }`
Tabs are scanned only when the player views them; tabs not viewed keep their older data. The Hub merges
into its own stored table, so one guild's update never drops another guild.

### `gear` v1 (Hub, per character)
`{ at, ilvl = average item level or nil, slots = { [inventorySlotId] = { i, q, ic, il = itemLevel } }, links, stats = {...} or nil }`

`stats` (additive from Hub 1.13.0, stays v1; the Outpost's paper-doll stats panel) holds only what the client returned readably out of combat,
every field optional, secret values dropped: `health, power` (max), `powerType` ("MANA", "RAGE", ...), `str, agi, sta, int, spi` (effective),
`armor` (effective), `ap, rap` (base + buffs), `melee = { min, max, speed }`, `ranged = { min, max, speed }`, `crit, rcrit, dodge, parry, block`
(percent, 2 decimals), `speed` (movement, percent of base run speed). Refreshed with the gear scan, also on `UNIT_STATS`, `UNIT_MAXHEALTH`
(player) and `PLAYER_LEVEL_UP`, through the same throttled scheduler. Only maximum health and power are read, never live values.

### `talents` v1 (Hub, per character)
`{ at, api = "traits" | "classic", spent, unspent, nodes = { { id, r = rank, m = maxRank, x, y, s = spellId, n = name, ic = spell icon file id (optional) } } }`
Raw positions; the Outpost groups them into trees for display. `api` says which client API it came from.

### `professions` v1 (Hub, per character)
`{ at, list = { { name, skill, max, ic, kind = "primary" | "secondary" } } }`

### `flight` v1 (Elan's Toolkit, account)
`{ legs = { ["<Horde|Alliance>|<nodeIdA>><nodeIdB>"] = { t = seconds, n = samples (max 5), at } }, names = { [nodeId] = name } }`
Same format as Hunter Helper's old `flightLegs` / `flightNodeNames`. Published by Elan's Toolkit, which owns
the flight timer from Toolkit 0.2.0, with `by = "ElansToolkit"`. The Outpost shares it with the Lodge.

### `hunter.pets` v1 (Hunter Helper, per character)
```
{ at,
  active = petKey or nil,                      -- the pet that is with the hunter ("Name:Family")
  pets = { [petKey] = {
      name, family (text), familyId (Hunter Helper's family id, e.g. "bear"), level,
      loyalty = 1..6, loyaltyText = text as the game gave it (stabled pets only have this),
      ic = icon file id (only when known as a number),
      stable = stable slot, or nil = with you,
      tpTotal, tpSpent = training points (when readable),
      happiness = 1 unhappy | 2 content | 3 happy (the active pet, only when readable out of combat),
      abilities = { { id = abilityId, name, rank } } } },
  known = { [abilityId] = rank } }             -- abilities the hunter can teach at Beast Training
```
`petKey` is `name:family` as in Hunter Helper's own saved data. `abilityId` is Hunter Helper's lower-case ability id
(`bite`, `fasterattack`...); the Outpost has a small id-to-name table for `known`, and falls back to the id's text. Only pets that are
with the hunter or seen in the stable are published, never older leftovers. Published by Hunter Helper 1.26.5 (`by = "ElansHunterHelper"`)
a few seconds after a stable visit, a pet change or learning an ability, never in combat (it follows after combat). Hunter Helper keeps its
own copy (`cdb.pets`) for its own use; this module is only the hand-over. The Outpost caps what it reads (40 pets, 40 abilities per pet,
100 known), skips odd values, and shows the Pet tab on the Characters page for hunters only (Outpost 2.46.0).

### `health` v1 (Elan's Toolkit's Bug Trap host, account)
```
{ addons = { [addonFolder] = {                  -- every addon registered with the Bug Trap (Toolkit, Hunter Helper, Paladin Helper, Bags)
      version,                                  -- the addon's version as it reported it
      bugs = { { msg, stack, count, time, at, version, addon } },     -- max 40, newest first (the saved Bug Trap shape)
      diag = { runs = { <db.diag.runs entry> },                      -- max 6, newest first, same shape as db.diag.runs
               autoVersion, lastVersion } } },                       -- db.diag.autoVersion, db.lastVersion
  other = { bugs = { ... } } }                  -- errors of non-Elan addons the host kept (mode "all"), max 40
```
Built from the per-addon tables the Bug Trap and Diag keep anyway (`db.bugs`, `db.diag`, `db.lastVersion`), which are still written as
before: this is a second copy for the Outpost. Published by the Toolkit's Bug Trap host (ElansLib BugTrap 4, `by = "ElansToolkit"`) about 5 s
after the last new error or finished Diag run (a burst is one publish), not while in combat (it follows after combat), once after login and,
if something is still waiting, at `PLAYER_LEAVING_WORLD`. Nothing is published without Elan's Hub. The Outpost reads an addon's bugs / diag /
version from here when the module has an entry for it and is not too new; an addon without an entry (older addon version, or the Hub companion's
own `ElansHubDB`) falls back to its own saved variables. `other.bugs` are counted as "other addons" on the host and never listed or reported.
Character and guild names for the anonymous report also come from `core` (and `guildbank`).

## The HUD: one bar, every class adds to it

Elan's Toolkit owns the movable HUD block and its general segments: the cast bar (your casts and
channels, the flight timer) and the threat bar (who holds aggro, group tank). Class addons don't build
their own HUD; they register into it:

- `ElansToolkit.HUD.Register{ ... }` adds a segment (its own row in the block).
- `ElansToolkit.CastBar.AddProvider{ ... }` adds something the cast bar shows when you are not casting,
  e.g. Hunter Helper's Auto Shot reload/aim timing. Your own casts always win.

So Hunter Helper adds shot timers, Quick Shots, aspect and distance; a future class addon adds its own
(swing timer, seals). Class addons list `ElansToolkit` as a dependency.

## Migration (dual read)

Existing flows keep working while data moves to the Hub:

| Today | Moves to | Status |
|---|---|---|
| Hub `ElansHubDB.chars` / `current` / `online` | `ElansHubData` `core` | done (Hub 1.11.0, Outpost 2.43.0 reads it, not shown yet) |
| Bags `ElansBagsDB.chars` / `guilds` (read by the Outpost) | Hub modules `bags`, `bank`, `guildbank` (+ `gear`, `talents`, `professions` new) | done (Hub 1.12.0 collects; Outpost 2.44.0 Characters page reads it, Elan's Bags data as fallback) |
| Hunter Helper `flightLegs` / `flightNodeNames` (read by the Outpost) | Hub `flight` (account), published by Elan's Toolkit, which takes over the flight timer and the HUD | done (Toolkit 0.2.0 publishes, Hunter Helper 1.26.0 plugs in, Outpost 2.45.0 reads Hub + old file) |
| Hunter Helper `cdb.pets` / `cdb.known` (pets, stable, abilities; the Outpost never read it) | Hub `hunter.pets` (per character), published by Hunter Helper | done (Hunter Helper 1.26.5 publishes, Outpost 2.46.0 shows it as the Pet tab; `cdb` stays Hunter Helper's own) |
| Health (`bugs`, `diag` in each addon's file) | Hub `health` (account), published by the Toolkit's Bug Trap host | done, dual: Hub first, per-addon fallback (Toolkit 0.3.1 / Hunter Helper 1.26.6 / Paladin 0.6.9 / Bags 0.4.15 with ElansLib BugTrap 4 publish, Outpost 2.46.0 reads Hub first, then each addon's own file; the per-addon tables are still written) |

The Outpost reads the new layout first and falls back to the legacy keys. A legacy reader is deleted
only after the addon versions that wrote the old keys are no longer offered by the Outpost.

## Known issues found while mapping (to fix)

- ~~Installing or updating Elan's Hub replaces `SharedData.lua` with the empty shipped file until the next
  flight sync.~~ **Done (Outpost 2.43.0):** the Outpost rewrites it right after installing the Hub, from the last
  lodge data it received in this session (nothing is written if none arrived yet).
- ~~`ElansHub.lua` is parsed separately by four Outpost readers, and the folder walk is written four times.~~
  **Done (Outpost 2.46.0):** one folder walk (`SavedVarsFiles.For/Dirs`), one shared read, and `SavedVarsCache` parses
  each saved file once per change for every reader.
- ~~Zone names were copied by hand into `ZoneNames.cs`.~~ **Done:** the `Maps` table is generated by
  `Tools/build_zones.py` (run via `Tools/build_all.py`) from the same QuestieDB snapshot as the addon data.
- BugTrap, Diag, Tip, Theme, Widgets, map pins and the secret helper are copied between addons (4 copies
  once Elan's Toolkit lands). **Planned:** ElansLib (see "Three shared layers").
- Character race/sex are joined onto Bags characters by name, with a name-only fallback that can mix up
  same-named characters on different realms. The `core` module removes the need for this join.
