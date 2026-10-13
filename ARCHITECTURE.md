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

## Atlas: the game's own data (Outpost 2.48.0)

Atlas is the Outpost's reference layer about the game itself (items first; where things drop, who sells them and which
quest gives them come later). One principle decides everything about it:

- **The Outpost reads the user's own game files, locally, read-only, at runtime. It never ships, bundles, downloads or
  uploads third-party or game data.** Nothing from Blizzard's tables, and nothing from another project's database, is in
  the installer or on the Lodge server. If a layer needs data the user's install does not hold, that layer is a separate,
  later decision (see "Later layers"); it is not smuggled in here.
- **Read-only, shared.** Every file is opened `FileShare.ReadWrite | Delete` (the game rewrites some of them while it runs)
  and nothing is written to the WoW folder. The only thing Atlas writes is its own compact index in the Outpost's data
  folder (`cache\itemdata.bin`).
- **Layout-checked, never guessed.** Each table's layout hash is written in the file by the game. The Outpost has a small table
  of the layouts it knows (`Services/Db2/ItemTables.cs`, from WoWDBDefs for build 1.60.1.70338). A hash it does not know means
  the game was patched: item data says "unavailable: game updated", logs it, and every screen behaves as it did before Atlas.
- **Background, never on the UI thread.** The index is built on a worker, once per game build; the next start loads the saved
  index. A changed `DBCache.bin` (the game learned new items) re-reads only that file, after a quiet moment.
- **Off switch.** Settings > General > "Read item data from my WoW installation" (default on).

### Item data layer v1 (client tables + hotfix cache)

| Source | Holds | How it is read |
|---|---|---|
| `ItemSparse.db2` (CASC, FileDataID 1572924, WDC5, layout `6FCC3191`) | name, description, quality, item level, required level, slot, binding, sell/buy price, stat shares, speed, bag size | `Casc.cs` (read-only CASC reader; key-locked blocks read as zeros) + `Services/Db2/Wdc5.cs`. Records are "sparse" (strings inline); the 80 rows in key-locked sections are skipped. |
| `Item.db2` (841626, layout `9A2A4834`) | class, subclass, icon file id | same reader; optional (without it item classes are unknown) |
| `RandPropPoints.db2` (1310245, layout `4FD22743`) | item-level budgets | The game stores stats as a share of a per-item-level budget: amount = budget(item level, quality, slot group) x share / 10000, rounded. Without this table the stat amounts are left out, not guessed. Optional. |
| `Cache\ADB\<locale>\DBCache.bin` (XFTH v9) | the item rows the server pushed to this client, among them items that were hidden until somebody found them ("revealed") | `Services/Db2/DbCache.cs`. Same row layout as `ItemSparse`; its `Item` rows give class / icon. A row in the cache wins over the shipped one. |

- `ItemData` (`Services/Db2/ItemData.cs`) turns that into `id -> ClientItem` plus a lower-case name index (prefix / word / substring
  search). Memory is about 7 MB for 24,000 items; first read about 0.3 s, a start from the saved index about 50 ms.
- The saved index is keyed by the game build (`.build.info`) and, for the revealed part, `DBCache.bin`'s size and time.
- Used by the Characters page: item tooltips (bags, bank, guild bank, gear), "not revealed yet on WoW Forever" for items the
  client does not know, matches for items you do not own in the search ("Not on your characters"), and the item level on the
  Gear tab when the Hub did not save one. Elan's addons never see or need this data; it is Outpost-only.
- Not in the client, so not in this layer: loot tables, vendors, spawns, quest givers. (Armor, weapon damage, item effects and
  item sets are not stored in the item either: the next section reads them from further tables.)

### Spells, skills, recipes, factions and item details (layers A2 + A4)

Same rules as the item layer (read-only, layout-checked, background, saved index keyed by the game build, same off switch), implemented in
`Services/Db2/SpellData.cs` (service), `SpellModel.cs` (model + parsing), `SpellTables.cs` (the table registry), `SpellText.cs`
(description tokens), `ItemDetailTables.cs`, `FactionData.cs`, `RevealedItems.cs`. It starts and stops with `ItemData` and keeps its own
index `cache\spelldata.bin`. Each table has its own layout check; an unknown layout hash makes only THAT table unavailable (its group keeps
what the other tables give), the reason goes to the log and the Settings "Game data" line ("..., spell data unavailable (...)").
Layouts known (WoWDBDefs, build 1.60.1.70338): Spell E3D134FB (sparse: NameSubtext, Description, AuraDescription), SpellName 782EE721,
SpellMisc 434B3607, SpellEffect 5362E3D4, SpellReagents FF40D202, SpellDuration A931BD2B, SpellCastTimes 75B6BD3A, SpellRange ADA13705,
SpellRadius F9A913EE, SkillLine 6763217C, SkillLineAbility 224F7EA0, Faction 6D443C38, FactionTemplate 22B6DC22, ItemArmorTotal EB155D51,
ItemArmorQuality 2935AA9D, ItemArmorShield 7E6C94F9, ArmorLocation FB67352F, ItemDamage{OneHand,OneHandCaster,TwoHand,TwoHandCaster,Ammo}
56F30531, ItemDamage{Ranged,Thrown,Wand} 3CD722C1, ItemEffect 4CA77678, ItemXItemEffect 96F083AD, ItemSet F79068A4, ItemSetSpell 2666A73F.

- **Reader additions (`Wdc5`)**: relationship data (the foreign key of a column that is not stored in the record: SpellMisc / SpellEffect ->
  spell, ItemXItemEffect -> item, ItemSetSpell -> set), arrays stored without compression (one field entry for the whole array), and the
  string space of tables with several sections (the client counts the records of ALL sections, locked ones included, before the string blocks).
- **Spells**: `SpellData.Spell(id)` = name, rank text, icon, cast time, duration, range and the descriptions with the tokens filled in
  (`$s1 $m1 $o1 $t1 $a1 $x1 $q1 $d`, `$12345s1`, `$@spellname123`, `${...}` arithmetic, `$/1000;s1`, `$l a:b;`, `$g`, `$?s1[a][b]` = else branch).
  Anything it cannot work out (character-scaled amounts, `$w1`, unknown letters) stays EXACTLY as the game wrote it. Amounts are the table's base
  amount (for a "14 to 22" spell the client's table holds the middle: 18).
- **Skills and recipes**: `SpellsOfSkill(skill)`, `RecipesOfSkill(skill)`, `RecipesProducing(item)` (spell, skill, rank, reagents), `ReagentFor(item)`.
  A recipe = a skill-line ability of a profession (SkillLine category 9 / 11) whose spell makes an item (effect 24) or uses reagents.
- **Factions (A4)**: `FactionData.Name(id)`, `NameOfTemplate(templateId)`, `GroupOfTemplate`, `ToAlliance(template)` / `ToHorde(template)` (friendly /
  hostile / neutral by the client's own rule: enemy group and enemy factions first, then friends), `Describe(template)`.
- **Item details**: armor = ItemArmorQuality x ItemArmorTotal x ArmorLocation (shields from ItemArmorShield), weapon damage per second from the ItemDamage
  table of the weapon kind (caster weapons = `Flags[1] & 0x200`), x speed, spread from `DmgVariance`; item effects ("Use: / Equip: / Chance on hit:" with the
  spell's own description) and item sets ("Set name (n/N)", the pieces, the bonuses). `ClientItem.Armor` / `TryDamage` compute from these tables on demand.
- **Ids**: `AtlasIds.Item/Spell/Skill/Faction` build and parse the `item:` / `spell:` / `skill:` / `faction:` links.
- **Used by**: item tooltips (armor, damage, speed, dps, effects, set), Characters > Professions ("Recipes you can learn for this profession", each with the
  item's tooltip and the reagents), Characters > Talents (the spell's rank and description in the tile's tooltip), the Settings line, and the Lodge hook
  (`RevealedItemsSource` hands the DBCache-revealed items to `AtlasObs.RevealedSource`; armor and damage stay local, the wire format has no field for them).

### Later layers (not built, not decided)

Sources, loot and vendors cannot be read from the client (its Adventure Guide tables are empty). Any such layer needs its own
decision on where the data comes from, how it is labelled (for example "Classic baseline" versus "seen on Forever") and what its licence
requires, before any of it enters a release. Until then Atlas answers only what the user's own installation can answer.

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

## Atlas: items, maps, instances and where things come from

Goal: a complete item and world reference for WoW Forever in the Outpost, where a Lodge can trace every item it
finds (who found it, from what, where, how often).

**Principle: we never ship game or third-party data.** Each Outpost reads the user's own WoW install (read-only) and
downloads public data itself on request; the Lodge only pools what its members observe. Every value carries its
source label: **Forever** (the user's client, or observed by the Lodge) or **Classic baseline** (unverified).

| Layer | Source | Where | Status |
|---|---|---|---|
| A. Items | Client `ItemSparse`/`Item` DB2 from CASC + revealed items in `Cache\ADB\enUS\DBCache.bin` | Outpost `Services\Db2`, `ItemData` | building |
| A2. Spells, skills, professions | Client `SpellName`, `Spell`, `SpellEffect`, `SpellMisc`, `SkillLine`, `SkillLineAbility`, `ItemEffect`, `ItemSet` DB2 (recipes, what a skill/profession teaches, item use/equip effects) | Outpost `Services\Db2`, `SpellData` | built (Outpost, Services\Db2\SpellData) |
| A3. What the client has seen | The client's own caches of things the player met: `creaturecache.wdb` (Forever NPC names, levels, types), `gameobjectcache.wdb`, `questcache.wdb` — like revealed items, they fill the Forever-only gaps and are pooled in the Lodge | Outpost `Services\Db2` (WDB reader) | planned |
| A4. Factions & reputation | Client `Faction` / `FactionTemplate` DB2 (which faction an NPC/vendor belongs to, reputation rewards) | Outpost `Services\Db2` | built (Outpost, Services\Db2\FactionData) |
| B. Maps & instances | Client `UiMap`, `UiMapArt*`, `UiMapAssignment`, `Map`, `AreaTable` DB2 + map tile textures (BLP) | Outpost `Services\Maps` | planned |
| C. Classic baseline | Public cMaNGOS classic-db dump (GPL-3.0), downloaded by the user's Outpost on request, credited; creatures, spawns, creature/object loot with chances, vendors, quests | Outpost `Services\Atlas\Classic` | planned |
| D. Observations | Elan's Toolkit records loot (incl. kills with no drop), object loot, vendor stock and prices, quest rewards, and items the client reveals; with zone + position | Toolkit → Hub module `atlas.obs` (account) | planned |
| E. Lodge pool | Lodge server feature `atlas`: members upload observations, the server aggregates (counts, per-member dedupe, outlier and rate limits), everyone downloads the aggregate | server `AtlasModule` + `LodgeSession.Atlas` | built (server 2.15, see server/PROTOCOL.md) |
| F. Atlas page | Search items / mobs / zones / instances; item page (tooltip, sources with Lodge-observed % and Classic %, vendors, quests, who in the Lodge has it); zone and instance maps with spawn and find markers | Outpost UI | planned |

### `atlas.obs` v1 (Elan's Toolkit, account) — cumulative since first record
```lua
{ since = time,
  kills   = { [npcId]  = { n = lootedKills, items = { [itemId] = { n = timesDropped, c = totalCount } }, z = { [uiMapId] = kills } } },
  objects = { [objectId] = { n = opened, items = { [itemId] = { n, c } }, z = { [uiMapId] = n } } },   -- chests, herbs, ore
  fishing = { [uiMapId] = { n = casts looted, items = { [itemId] = { n, c } } } },
  vendors = { [npcId]  = { at, z = uiMapId, x, y, items = { [itemId] = { p = priceCopper, s = stackSize, l = limit (-1 none) } } } },
  quests  = { [questId] = { at, rewards = { itemId, ... }, choices = { itemId, ... } } },
  finds   = { { i = itemId, c = count, src = "npc:123" | "obj:45" | "fish" | "vendor:67" | "quest:89", z = uiMapId, x, y, at } },  -- newest 500
  spawns  = { [npcId] = { { z = uiMapId, x, y } ... max 20 per npc } },   -- where looted mobs were (player position at loot)
}
```
ids are numbers; x/y are map fractions 0..1 (3 decimals). Counters only grow; the Outpost uploads the difference since
its last upload (like flight times). Revealed-item details are not in the addon: the Outpost reads them from
`DBCache.bin` and shares parsed fields (name, quality, item level, slot, stats) with the Lodge.

### Lodge feature `atlas` (server 2.15.0)
`atlas.share { kills, objects, fishing, vendors, quests, finds, spawns, items }` (only the entries that changed, but each carrying the member's CUMULATIVE totals; `items` = revealed item
details) → the server keeps one record set per member and stores per field max(stored, sent), so a resend, a lost answer or a restored upload record can never count twice; the Lodge aggregate is the sum over members (each member's totals capped and checked), keeps the newest vendor list
per npc, the newest 2000 finds (with the member's Lodge display name — never character names), up to 50 spawn points
per npc, and revealed items by id. `atlas.get { since, epoch }` → `atlas.data` (everything changed since a server revision, in pages; the whole set again when the epoch differs). Members can switch
sharing off; guests can read but not share. Feature can be disabled by the owner (`LODGE_ATLAS_ENABLED=0`).

### Foundation the layers share
- **One id scheme for links everywhere:** `item:123`, `npc:456`, `obj:789`, `quest:12`, `zone:1440` (uiMapID),
  `instance:36` (map id), `spell:6150`, `skill:186`, `faction:76`. Atlas pages, searches, tooltips, map markers and Lodge
  chat item chips all link with these, so any reference opens the right Atlas page.
- **Every value knows where it came from:** `Forever` (client data / client cache / Lodge-observed) or
  `Classic baseline`, plus counts behind percentages ("12% of 340 kills").
- **Game build stamps:** every client-read layer records the game build it was read from. When the build changes, the
  Outpost re-reads and keeps a short diff (new items, changed stats, new spells/NPCs) — the basis for a "What changed in
  this patch" page.
- **Local only, rebuildable:** all Atlas indexes live in the Outpost's data folder and can be deleted and rebuilt at any
  time (the game install, the public download and the Lodge are the sources).
- **Later on top (not part of tonight):** a Lodge wishlist ("I'm looking for …", notified when a member finds it, built on
  finds); quest chains and who in the Lodge has done what; source lines in the in-game tooltip for tracked items (a
  small slice through `SharedData.lua`); languages other than English (enUS only for now).

Rules for the Atlas: read-only access to the game folder; downloads only on the user's click (size and licence shown);
sharing observations is on by default per Lodge member and can be switched off; observations carry no character names
when pooled (the Lodge sees "a member", the Outpost shows names only for the user's own characters); combat-safe
collection (loot, merchant and quest windows only, never the combat log).

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
