using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ElansAddonHub.Services
{
    // Which parts of --selftest to run. The sections, their dependencies and the path -> section table all live here, so
    // adding a self-test block means touching this file plus the driver in MainWindow.SelfTest.
    //   --only a,b      run just those sections (plus whatever they depend on)
    //   --changed base  run the sections the files changed since <base> can affect (git diff base...HEAD + uncommitted)
    //   --repo dir      where to run git for --changed (default: the current directory)
    //   --list          print the section names
    //   --paths a;b     as if exactly those files had changed (Tools\selftest.ps1 -WhatIf -Paths ...)
    // No flag: everything (the release run). A file maps to the sections that test it: a new source file no rule knows runs the sections its
    // header comment names, anything else unknown is ALL. Only the truly shared files (App, Brand, Util, SettingsStore, Models, Json, Net, the
    // fixture) are ALL; the window, the theme, the art, the project file and the self-test plumbing pick their own few sections, because the
    // setup every run does already starts the whole app with them. Keep a typical one-feature change to a couple of minutes.
    public static class TestScope
    {
        // execution order == order of the full run
        public static readonly string[] All =
        {
            "addons", "thirdparty", "startup", "addonsgrid", "selfupdated", "viewer", "voice", "update", "strip",
            "lodge", "lodgefeatures", "lodgehardening", "lodgefun", "chatlist", "invites", "invites2", "linkopen", "calendar", "wowevents", "flightshare", "atlaslodge", "lodgefiles", "badges", "realms", "hubdata", "readers", "characters", "upgrades", "armory", "itemdata", "spelldata", "gametooltip", "money", "talents", "professions", "profguide", "mapdata", "mapdatareal", "worldmap", "worldmapreal", "instances", "instancesreal", "buildings", "buildingsreal", "dungeonmaps", "dungeonmapsreal", "seendata", "video", "browser", "radio", "presence", "profile", "toasts", "dialogs", "inventory", "health", "inputs", "menucolors", "atlasclassic", "atlasclassiclodge", "atlaspage", "atlaspagereal", "atlasbrowse", "atlashome", "atlashosts", "atlasmenu", "atlasclasses", "factions", "atlasdata", "atlasdatareal", "zonelore", "zonelorereal", "about", "art", "round3", "idlecpu", "coldstart",
        };

        // section -> sections that must also run because they read state the other one leaves behind. Everything else a section
        // needs (window shown, fixture, first update check, isolation check) is shared setup that runs for every selection.
        // Verified by running each section alone and comparing its lines with the full run.
        static readonly Dictionary<string, string[]> Deps = new Dictionary<string, string[]>
        {
            ["video"] = new[] { "lodgefeatures" },   // feeds the chat channel the video cards are tested in
        };

        // one line of what each section covers (for --list and the docs)
        public static readonly Dictionary<string, string> Info = new Dictionary<string, string>
        {
            ["addons"] = "install + check-all + pills + settings tour (Hunter Helper cards)",
            ["thirdparty"] = "other addons: scan, suggest, CurseForge, zip install/rollback, real GitHub/WoWI lookups",
            ["startup"] = "Addons page start-up: placeholders match the cards, nothing above the bottom of a list moves while the check and the scan run (cold start, slow update server, counts equal / different / first run), counts remembered",
            ["menucolors"] = "section colours of the secondary menus (Outpost 2.71.1): the Brush.Section tokens (a hue + .Tint per section, every hue as readable as Text.Secondary on the menu fill), the Atlas side navigation (group labels and rows in their section hue, the selected row's tint and 4 px bar for Items / Zones / Factions / Finds / Data sources, no Search row), the Atlas Home without a search box of its own (spacing, Ctrl+K and the title bar box open the palette from Home), the Settings menu (one hue per category, the chosen one's tint and bar), the Characters list (the class colour as the selected row's tint and bar), the Lodge sidebar (Text channels, Voice rooms, people groups, Calendar / Files / Ladder icons, the selected channel and the Calendar row), the faint menu fill behind each menu, screenshots",
            ["addonsgrid"] = "Addons page grid: columns at 760 / 1300 / 1700 (1 / 2 / 3), equal card heights, status / source / updated chips and the button on the face, a card opening in place without reshuffling, placeholders exactly where the real cards land, the Grid | List switch and its setting, the other addons in the same columns, every button reachable",
            ["selfupdated"] = "addons that update themselves: the Self source (CurseForge / Wago id but not managed by the CurseForge app), git-describe dev builds (label, comparisons), the addon's own logo as the row icon (IconTexture, heuristic, depth / size / path limits, decode cache, no network)",
            ["viewer"] = "in-app picture viewer",
            ["voice"] = "voice effects",
            ["update"] = "update channel + installer",
            ["strip"] = "pixel strip",
            ["lodge"] = "live Lodge session (only with ELANSHUB_TEST_LODGE=url|code|name)",
            ["lodgefeatures"] = "Lodge chat features from synthetic frames",
            ["lodgehardening"] = "Lodge 2.8 hardening: safe file names, merge on welcome, drafts, restored composer, full voice room, unread on delete",
            ["lodgefun"] = "Lodge 2.37: grouped online list, Looking-for-group cards, item links, history paging, search",
            ["chatlist"] = "the Lodge message list in a long channel: where a channel opens, new messages at / away from the end, sending, the unread line and the Jump pill, jumps far up, pictures and video cards that arrive late (no jump), edits / reactions / deletes above the view, scrolling up to the first message, keys, menu, selection, typing, accessibility; plus the numbers of a 5000-message channel (open, rows, memory, scroll steps)",
            ["invites"] = "Lodge 2.38: one-time invite links, device tokens, upgrade prompt, Guild Master invite list, Your devices, error messages",
            ["invites2"] = "Lodge 2.12 invites in the app: the Invite sheet (ranks by role, person / guest, Copy and Copy as web link, open invites + Revoke), web-link parsing, profile-card device link, Create-a-lodge guide, Danger zone (typed name, leave, transfer ownership), Your lodge card, join banner",
            ["linkopen"] = "clicking an elanshub:// link: accepted links, per-user registration (test root), pipe hand-off, join panel (never auto-joined), Settings toggle",
            ["calendar"] = "Lodge 2.11: raid / event calendar - agenda grouping, local time, month dots, form checks, sign-up buttons, 15-minute reminders, next-event line, chat card, hidden on older servers",
            ["wowevents"] = "WoW's own calendar in the Lodge calendar (companion addon data): parsing, local time, agenda 'until', month bands, switches, the strip above the chat (fixed height, no shift of the message list), nothing sent to the server",
            ["flightshare"] = "Lodge 2.13 shared flight times: Hunter Helper saved variables (validation, several accounts), upload batches (500 legs, once per 10 minutes, only what changed), merge answer, SharedData.lua (escaping, injection, structure, written only when the toc lists it, atomically), setting off, guests, older server = nothing",
            ["atlaslodge"] = "Lodge 2.15 Atlas pool: the Hub module atlas.obs (parsing, validation of every field, too new, absent), deltas (signature per entry, batch limits and the 56 KB frame, revealed-items hook), the persisted upload record, upload batches (once per 3 minutes, only what changed), the aggregate (pages, validation of the answer, cache file, read API: drops, vendors, finds, spawns, revealed items), sharing off, guests, feature disabled, older server = nothing; where NPCs really stand (Elan's Toolkit npcspots, atlas8: the section, sharing, the lodge's merge and cache, Lodge-first spots, the NPC page's Seen at and map ahead of the Classic baseline, the world map's NPCs seen by Lodge members layer and the vendor / rare layers)",
            ["lodgefiles"] = "Lodge 2.14 shared files: slots, upload (new slot, Replace = new version), the Updated badge, Save with the SHA-256 check and the Mark of the Web, damaged download, roll back and delete (with confirms), officer-only buttons (members and guests only save), blocked programs and zips holding programs, size limit, the notice above the chat, hidden on a server without the feature (HTTP goes to a fake of the server endpoints)",
            ["badges"] = "Lodge badges and ladder (feature badges): the Ladder view (rows, your own row highlighted, this month / all time asks for the right season, empty month, hidden rows), the badges card and the Settings > Profile list (earned with date, unearned with how to earn and a progress line, secret ones as ???), the strip of the best badges on a profile card (top 5, +N), the quiet new-badge dot that goes when you look, Hide me from the ladder sending the flag, the Guild Master switch, a lodge switched off or locked, guests, a server without the feature = everything hidden and nothing sent, refusals never shown",
            ["realms"] = "Lodge realm status (feature realms): the dots at the top of the window (one per realm, labelled by ruleset or name, green up / red down / grey unknown), tooltips (name, ruleset, status, population, queue, checked N min ago, via your Lodge), a change recolours them, hidden without a Lodge, on a server without the feature and when the Forever realms were not found, the Settings switch (on by default), the back-up note (off by default, a quiet toast, silenced by the toast switch), nothing sent to the server",
            ["hubdata"] = "Elan's Hub data layer (Outpost side): ElansHubData parsing (core v1, account modules, a module newer than the app understands = TooNew without data, no ElansHubData, garbage), newest file wins, parse cache, locked file keeps the last good read, shared SavedVariables finder (links skipped)",
            ["video"] = "YouTube preview cards + in-app player",
            ["radio"] = "Radio Paradise in the title bar (2.72): the published-link allow-list, User-Agent + Icy-MetaData, one connection on Play and none at start, Pause closes it, ICY titles, reconnect waits + Retry, favourite channel, the ducking curve in voice rooms, the Settings switch, the title bar layout at every width; 2.73: covers from the stream's StreamUrl (https img.radioparadise.com only, never api.radioparadise.com), Latin-1 titles, the pill (marquee, equaliser, animations off), the pop-out (channel chips, volume + mute), keyboard-only focus rings; 2.75: Sound quality (Standard MP3 / High AAC / Lossless FLAC): the AAC decoder (Windows Media Foundation, a test stream made with Windows' own encoder) and the Outpost's FLAC decoder (bit-exact against its own test encoder, damaged frames skipped), titles per format (ICY, Vorbis comments), quality switches also while playing, Radio 2050 -> High, Serenity, a missing decoder (simulated) steps down (fake local ICY + picture servers, never the real ones)",
            ["browser"] = "Browser page: URL/search rules, scheme + ad-host lists, tabs, tiles, WebView2 hardening",
            ["presence"] = "member presence",
            ["profile"] = "profiles and the editor",
            ["toasts"] = "toast notifications",
            ["dialogs"] = "themed dialogs",
            ["inventory"] = "Characters page: bags, bank, guild bank views, search, tooltips with counts across characters, character list (Bags data)",
            ["readers"] = "how the app reads the game's saved files: one folder walk finds the same files as the old per-reader walks (flavors, accounts, links skipped), ElansHub.lua parsed once when presence + calendar + looks + hub + health + flights ask (parses counted), parsed again after a change, last good parse kept for a half-written or locked file, two threads reading at once",
            ["characters"] = "Characters page (Hub data): Hub modules bags / bank / guildbank / gear / talents / professions (parsing, TooNew), merge of Hub and Bags characters (Hub primary, newer wins, realm-exact keys), conversion to the inventory model, talent grouping, Overview / Gear / Talents / Professions tabs, empty states",
            ["upgrades"] = "Characters > Gear > Upgrades and the Atlas item fact: the upgrade engine (slot fit, class armor and weapon skills incl. mail / plate from level 40 and the level offset, two-hand vs one-hand + off hand, rings and trinkets in two slots, unique pieces, faction filtering of vendors and quests by race), stat weights per class and talent tree (presets, spec from the talents, edited and reset weights), the score and its plain-words difference, sources and their ranking (Lodge first, rare drops low, vendors / quests / crafts with Lodge crafters), filters (dungeons, raids, Classic baseline, tradeable, +3 levels), the cache; the panel (per slot top 3, filters, slot click, tooltip comparison, weights editor, missing-data notes) with screenshots; \"Upgrade for\" on item pages; enchants (applicability by item class / subclass / inventory type from SpellEquippedItems, the current enchant from the item link, scoring and words against it, formulas and armor kits / scopes, who can apply them and how the formula is learned, reagents with what I own, the filters, missing data) and food & drink (well-fed buffs by weights, healing food, mana drinks, how to get them); enchants (SpellItemEnchantment / SpellEquippedItems read with checked layouts, applicability by item class / subclass / inventory type, the current enchant from the item link, score and words against it, formulas and armor kits / scopes, who can apply them, how the formula is learned, reagents with what I own, filters, missing data) and food and drink (well-fed buffs by weights, healing food, mana drinks, how to get them); optional read-only check of the real game files",
            ["armory"] = "Lodge Armory (server 2.16, Outpost 2.48.1): sharing is OFF by default, one switch per character (Settings > Lodge), the snapshot holds gear / talents / professions / pets and nothing of bags, bank, gold, zone or experience, signature + debounce + put / delete frames (only what changed, unticked = deleted, switch off = delete all), refusals (rate, full, guest, old server, switched off), the upload record on disk; the Characters page: My characters | Lodge switch, the list with class / level / profession filters and search, a member's character in the same Overview / Gear / Talents / Professions / Pet views (no bags, bank, gold, zone, none of my counts), Ctrl+click opens the Atlas, offline / old server / guest lines",
            ["itemdata"] = "Atlas item data (2.48.0): the WDC5 table reader (inline / bit-packed / signed / pallet / pallet array / common data, string block, id list, copy table, an encrypted section skipped), the DBCache.bin hotfix reader (override, add, invalid and truncated rows), the ItemSparse layout table (an unknown layout hash = unavailable, nothing guessed), stat amounts from the item-level budgets, the saved index (round trip, invalidated by a new game build or a changed DBCache.bin, debounced refresh), name search, tooltip lines, the Characters page (slot and result tooltips, not-revealed text, items you do not own in the search, gear item level fallback), the Settings line and switch; optional read-only smoke check of the real game files",
            ["mapdata"] = "Atlas maps (layer B): the map tables read from the player's own game files (UiMap tree, art, tiles, assignments, Map, AreaTable; unknown layout / foreign table / locked rows = that part unavailable, nothing guessed; multi-section string offsets, relationship data, float arrays), world <-> map coordinates (most specific zone, cities, neighbouring zones, continent and world map windows, round trips), instances and floors, tile composition and the PNG render cache (round trip, per-build, never on the UI thread), the saved index (round trip, new build, damaged file), the AtlasMap control (fit, wheel zoom, drag, markers at the right pixel, clustering, hover text, LinkClicked, breadcrumb, floors, empty and loading states), the Settings status line; optional read-only check of the real game files with a rendered Durotar and screenshots",
            ["mapdatareal"] = "the real game files for the map data (slow: reads the player's CASC): the real map tables, tile composition from real BLPs, the real AtlasMap render. Runs in -Full, with -Only mapdatareal, and in changed mode only when game-data readers (Casc, Blp, Wdc5, Maps/) or its own test files change; skipped when the game build and those files are unchanged since a passing run (-NoCache forces)",
            ["worldmap"] = "Atlas World map (2.51 phase 1, 2D; 2.53 phase 2, terrain, zone borders, 3D): the root ADT reader (reversed tags, MCNK / MCVT / area ids on a synthetic file and on real tiles), the 65 x 65 height grids (down-sampling, seams between neighbouring tiles), the terrain cache (build, resume, cancel, damaged and missing files, clean-up), zone outlines on invented area grids (two zones, a hole, islands, corners that touch) and on the real continents (accuracy against the chunk grid), which zone a point is in, the flat map's Zone borders layer and Navigate here with the terrain's zones, the 2D | 3D switch, the 3D page in a real WebView2 (WebGL or the fallback to the flat map, markers on the ground, borders, FPS and memory), the new protocol messages and &m= ids; and in phase 1: the tile / world math (grid corners, Orgrimmar / Stormwind / Razor Hill tiles, the Leaflet plane, pyramid ranges), the WDT reader (MAID, reversed tags, stray tiles), the tile pyramid on a fake source (counts per level, pixels, missing tiles, cache reuse, resume after a cancel, damaged tiles, build-key cleanup, size and Clear), the flight tables TaxiNodes / TaxiPath / TaxiPathNode (synthetic WDC5 tables, unknown layout, saved index), the message protocol (bad JSON, unknown actions, positions outside the plane, links and layers off the lists), worldmap: ids and history, Navigate here (/etk way text), the layers on an invented continent, chat links, then the page in a real WebView2 (build progress, ready, layers, the panel, right-click menu, junk messages, the sandbox, Back, Show on world map, a new game build) and a read-only check of the real game files (WDT FileDataIDs, orientation proven on the real flight masters, a real region build) with screenshots (ELANSHUB_WORLDMAP_FULL=1 also builds both whole continents (pictures and terrain) in the scratch folder, times them, measures the borders' accuracy, flies the 3D camera over the real mountains and takes the screenshots)",
            ["worldmapreal"] = "the real game files for the world map (slow): both continents' WDTs, flight tables, a real region build, real terrain, water and zone borders, the page on the real region and (with ELANSHUB_WORLDMAP_FULL=1) the whole continents; same run rules as mapdatareal",
            ["instances"] = "Dungeon and raid entrances on the maps: Map.db2's ghost spot (CorpseMapID + Corpse[2]) read from synthetic WDC5 tables in the real layout (unknown layout = none), LFGDungeons + ContentTuning for the suggested level (joined by name: an s more, a letter off, one inside the other), entrance choice (no continent, 0/0, outside the world, NaN = none), the zone an entrance is in (the terrain's area decides where the map rectangles overlap), the saved index, the Dungeons & raids world map layer (messages, kinds, catalog, protocol, settings migration), the instance page's Entrance card (zone, position, map, Show on world map, Navigate here copy), the zone page's list and door markers, the browse list's zone / level columns; optional read-only check of the real game files (Ragefire Chasm in Orgrimmar, Wailing Caverns in the Barrens, Molten Core at Blackrock Mountain, Razorfen Kraul / Onyxia by the terrain's areas, levels) with screenshots (ELANSHUB_INSTANCES_SHOTS=1: the real continents in 2D and 3D); the 2D / 3D pages with the layer are checked in the worldmap section",
            ["instancesreal"] = "the real game files for the instances: Map.db2 entrances, the terrain areas that name the zones, suggested levels, the Dungeons & raids layer on the real tables; same run rules as mapdatareal",
            ["buildings"] = "3D buildings and bridges on the world map (Outpost 2.58): the obj0 MODF reader (reversed tags, 64-byte records, scale, name-index files left out), the WMO root and group readers (MOHD / MOMT / MOGI / GFID, MOGP header, MOVT / MOVI / MONR / MOPY / MOBA, exterior groups only) on synthetic chunks, the placement transform (axes, scale, Ry Rx Rz) against the placement's own bounding box, the bridge rule on invented ground, the simplified model (weld, clustering to the triangle caps, three levels, the box, file round trip), placements.bin and the manifest, the estimate and the card sentence, the build service (scan, estimate, build, pause, resume, cancel, remove, a new game build asks again, a damaged model is a stub), the protocol messages, the Buildings switch and card in the 3D page (states, instancing counts, culling by detail, memory cap, Off removes them); optional read-only check of the real game files (counts, a bridge's deck above the water, Orgrimmar and Stormwind shapes) with screenshots, build times and frame rates (ELANSHUB_WORLDMAP_FULL=1 builds the whole continents' pictures and terrain first; ELANSHUB_BUILDINGS_CACHE=<folder> reuses them; ELANSHUB_BUILDINGS_SHOTS=<folder> keeps the pictures)",
            ["buildingsreal"] = "the real game files for the 3D buildings: both continents' placements, the bridge rule against the community names, build times and sizes, the 3D page with the real shapes; same run rules as mapdatareal",
            ["dungeonmaps"] = "drawn plans of the instances with no map art (Outpost 2.63): the WDT global building (MPHD 0x1, MODF; the model turned half way round) and the WMO group names (MOGN), the walkable rule on a synthetic group (MOPY collision / render / detail, faces up by MONR, a ceiling, a wall, deep under MLIQ), terrain chunks with holes, the terrain rule (buildings on it or a filler) and its crop, the floor split (a gallery over its own hall cut in two, a climbing dungeon one floor, rooftop islands left out, a far room kept, names), the role pictures (frame, floor / ghost / edge / ledge, closed cracks, PNG round trip, theme colours) and plan.json, the event and placeholder labels of the instance list (CashTest, TEST_ folders, no way in from the world and no level, an arena with no map; the browse group; a levelled one stays a dungeon), the ways in the game data does not give (an approximate spot from a map point or an area of the same name; the entrances Elan's Toolkit notes, the Lodge's merge (atlas6) and which spot is shown where: game data, Lodge members, you, approximate), the plan service (draw, cache, restart, damaged file, a big one waits, minimap for terrain, failures, a new game build) and the map card (progress, floors, caption, Draw the map, the old words when nothing can be drawn, released off screen); optional read-only check of the real game files (Deadmines, Molten Core, Zul'Gurub with timings and known positions; ELANSHUB_DUNGEONMAPS_SHOTS=<folder> keeps the pictures, ELANSHUB_DUNGEONMAPS_ALL=1 draws every instance)",
            ["dungeonmapsreal"] = "the real game files for the drawn dungeon plans: the real WDT / WMO / terrain of the instances with no map art, floors, the event and placeholder labels; same run rules as mapdatareal",
            ["seendata"] = "Atlas A3, what the client has seen: the WDB container (header, duplicate ids, cut-off and garbage files), the creature / game object / quest decoders (every record checked against its computed length), the saved index (round trip, invalidated by a changed file), search (ranking, ids, kinds), the background service (status line, switch, debounced refresh, other files ignored), the Lodge hook; optional read-only check of the real cache",
            ["gametooltip"] = "Game-style tooltips (2.50.1): the GameTooltip control (panel colours as theme tokens, name in the quality colour, Item Level gold, white / green / gold lines in the game's order, two-column slot and damage | speed rows, coin glyphs, requirement red when the character is too low, set lines, spell variant, the muted footer under a hairline), the Atlas kinds (creature reaction colours and level line, object, quest, spell, zone, instance, faction), the font check (name table; a font that is not Friz Quadrata is never used), optional read-only check of the real game files",
            ["money"] = "Money like the game (2.77): MoneyFormat (gold / silver / copper parts, zero parts left out, 0c, negative, compact, thousands, reading amounts back out of a line), the MoneyText control (number then coin, the game's order, sizes, screen-reader words), money inside a line of text (MoneyText.Coins / MoneyText.Source), the drawn coins without game data, the game's own coin pictures (UI-GoldIcon / UI-SilverIcon / UI-CopperIcon, read-only, skipped without a WoW install), the old helpers agreeing, a source scan for money formatted by hand or shown without coins, a picture",
            ["spelldata"] = "Atlas A2 + A4 and the item details: spells (names, ranks, descriptions with the $s1 / $o1 / $d tokens filled in, icons, cast time, duration, range), skills and professions, recipes and reagents (what makes an item, what uses it), factions and their templates (friendly / hostile to the Alliance and the Horde), armor and weapon damage from the item-level tables, item effects (Use / Equip / Chance on hit), item sets with bonuses; the WDC5 additions (relationship data, uncompressed arrays, the string space across sections); an unknown layout makes only that table unavailable; the saved index keyed by the game build; tooltips on the Characters page (item facts, set count, recipes under a profession, the spell in a talent tile); the revealed-items hook of the Lodge; optional read-only check of the real game files",
            ["talents"] = "Atlas layer G, talent calculator: the Trait* tables read from the game files (trees per class from the skill lines, nodes with repaired positions and duplicates left out, entries, definitions, edges, row gates as group conditions, point sources by level and the legacy Talented ranks, rank curves for rank-aware descriptions; an unknown layout = unavailable, an optional table only drops its feature), the rules (open / closed talents, prerequisites, refunds that would break dependants refused, points and levels), build codes (round trip, versions, junk, bad check sum, illegal layouts), build links from Seemeta / WoW Forever Talent / Talents Forever (constructed from the formats: ranks, leveling order, unreadable sites, damaged, wrong class, talent not in the tree; also on the real trees of all nine classes), Hunter Helper Builds.lua (fixture copy: parsing, name to node mapping, leveling order, unmatched names), saved builds, a character's talents from the Hub module, the saved index, the Talents page; optional read-only check of the real game files with screenshots",
            ["professions"] = "Atlas layer H, Professions page: skill-up colours from the trivial ranks (orange / yellow / green / grey), recipes with how they are learned (trainer, taught with the profession, recipe item from a vendor or a drop), the ranks and their caps from the rank spells, camp kits / objects / blueprints / buffs found from the Tier recipes, what you can craft now across characters, the leveling guide (steps, rank-cap milestones, craft counts, shopping list minus holdings, copy as text), the gathering zone guide, Atlas link hook; optional read-only check of the real game files with screenshots",
            ["profguide"] = "the leveling guide in game, gathering nodes and routes (profguide contract A-D): the route builder (clusters, nearest neighbour, 2-opt, closed loop, the Classic fallback below 8 Forever nodes, yards and minutes, caps, several object kinds in one loop, saved routes), Hub `nodes` (parsing, caps), upload batches (only with atlas3 on a 2.18 server, deltas, 50 objects / 1000 points per message), the Lodge's merged nodes (read API, cache), SharedData.lua composed from flights + profGuides (flights-only byte-identical, escaping, caps, size, extra sections, flights kept while only the guides change, Stop following), Follow in game / Stop following on the Guide tab (crafting and gathering guides, routes, pins with n = 0 for Classic, a saved route chosen for the guide, debounced rebuild), Forever nodes on object and zone pages above the Classic positions, the Routes panel (combined loop, save, use for guide, delete), Show route from the gathering guide, screenshots",
            ["health"] = "addon health",
            ["inputs"] = "text inputs: Input / SecretInput / Dropdown draw g j p q y without clipping at 100 % and 125 % (pixel check)",
            ["round3"] = "settings round 3 (WoW watch pause / resume, lodge update, welcome, privacy page, CurseForge status ...)",
            ["coldstart"] = "the game data's start against the real game files (skipped without a WoW install): cold (no saved copies), warm and after a patch (the previous build's data at once, the new files read in the background); time to Atlas Home, the Zones list, its levels and all data, peak working set, UI stalls, the progress strip, the waiting page and the 'Updated with the new game data' notice; budgets in MainWindow.ColdStartTest.cs",
            ["idlecpu"] = "the CPU the idle hub uses with WoW running vs paused (3 alternating samples per mode, medians, about a minute): runs when something that ticks changes (WoW watch, pixel strip, presence, the Lodge session, shared files)",
            ["atlasclassiclodge"] = "Classic baseline from the Lodge (server 2.17, Outpost 2.49): the bundle of the original upstream files (pack, unpack with safe names, the version string and which release is newer), a fake Lodge with the dataset endpoints; a member without Classic data gets it automatically (SHA-256 checked, unpacked, read, labelled \"via your Lodge\"), nothing is downloaded when the same release is installed, a newer release at the Lodge replaces an older one, a damaged download is refused with the banner as fallback and not retried in a loop, the switch off or a removal by the person = no automatic fetch, the owner / officer share button in Settings and in the Atlas (with the quiet offer after their own download, asked once per release), a member sees no share button, a guest is not offered anything, a server older than 2.17 = the banner only, the Settings lines, screenshots",
            ["atlaspage"] = "Atlas layer F, the Atlas page: search (grouping by kind, ranking, Forever above Classic baseline, id links, one-letter and blank), the item page (the game's tooltip, your characters, the sources table with Lodge-observed rows above the Classic baseline's, merged rows, world drops under 0.1 % folded, labels on every row, recipes and reagents, found by the Lodge with member names), markers from Classic world coordinates on the right zone map (most specific zone, instance floors, instances without a map), NPC / object / zone / instance / quest / spell / skill / faction pages with every layer, without Classic, without a Lodge, with game data off and with nothing at all, the Classic baseline card with a fake downloader (size question, progress, cancel, failure and retry, ready with the dump date, remove, credit and licence links), links and history, the page in the window (keyboard search, zone picker, folded rows, virtualised lists, narrow layout, never blocking the UI, stale answers dropped), screenshots",
            ["atlaspagereal"] = "the real game files for the Atlas page: real items, spells and seen data, the page models and cards built from them; same run rules as mapdatareal",
            ["atlashome"] = "Atlas Home (2.74): the hero with a live character (zone, painting, zone note, creature-level fit, status chips, XP and rested), the saved character (last seen), a character in a dungeon, no character; the cards from fixture data (quests, dungeons, rares, upgrades, recipes) and the character chips driving them; the Lodge today line and near you with sharing respected (the main window's member mapping too); the recent finds of this zone or all; live zone change, combat flag and level-up in place without a new page or a scroll jump; the cave fix (a uiMap walked up its breadcrumb to its zone); the main window's GamePresence wiring; screenshots",
            ["atlasbrowse"] = "Atlas browse by category: the category registry (built-in list categories, a hosted category with an id prefix and a sub-path, replace / unregister), browse ids (parse, canonical text, history replace), every category listing with rows, the item sub-categories from the client's class names, name / level / quality / slot / data source filters, how to get it (drops, vendor, quest, crafted, fishing, containers, gathered), usable by a character (class proficiency and level), the sorts, creatures / objects / quests / zones / dungeons / spells / skills / recipes / factions, the start page tiles and presets, history through browse ids, missing layers (no Classic baseline, no Lodge, no game data) with their notes, the page in the window (typing narrows the list without a redraw, back / forward restore the filters), 24 000 items (index off the UI thread, virtualised list), the player's own game files, screenshots",
            ["atlasmenu"] = "the Atlas side menu (2.77): the numbers on the right remembered in memory and in atlas-nav-counts.txt (shown at once on coming back and on the next start, worked out again in the background when the data changes, put into the row in place, never wiped by a pass without the data; Zones, World map = continents, Professions have one), one width for every side menu (Atlas, Settings, Characters, Lodge: Size.SideMenu) with the title bar search box ending where they end, and the one rule of spells and skills (Spells = what you cast, Skills = weapon / armor skills, languages, riding, other; nothing in both, weapon skills reachable, old ids redirected, spell:/skill: pages under the right row)",
            ["factions"] = "Atlas Factions and reputations (Outpost 2.77): which side a faction belongs to and the All | Alliance | Horde | Shared switch of the Factions list (counts, partition, other filters, the window), \"Your characters\" on a faction page from the reputations Elan's Hub 1.16.0 saves (fixture SavedVariables, hidden characters, partial reads, the bar), \"Your lodge\" with the Armory's reputations feature (opt-in upload without names, the question per faction, junk answers, older server / guest / switched off = no section), faction and dungeon icons from the game's own pictures (candidates, race portraits, side banners, generic dungeon / raid pictures, the glyph where none), screenshots",
            ["atlasclasses"] = "Atlas Classes and Races (ids class:<id> / race:<id>, browse:classes / browse:races, search by name and lore name, the palette Pages scope): ChrClasses / ChrRaces / CharBaseInfo / ChrClassesXPowerTypes / SkillRaceClassInfo / SpellLevels / SpellPower / SpellCooldowns on synthetic WDC5 tables (unknown layout = that table or the layer unavailable with a calm note, a missing or cut-off table, the saved index and its build key), the class page (identity, resource, roles, armor and weapons from the skill lines, races, talents link, notes), Spells by level (groups by level, the no-level group, spell-level labels, rank chains, helper and variant spells left out, only new spells, level range, tabs, pet tab, search, talent spells), the Classic trainer merge (labelled, none = a note), the race page (side, languages, mount skills, racial abilities, classes, start-zone note), the spell pages (Learned by / Racial ability of, Cost, Cooldown, game tooltip lines), the page in the window (tabs, filters, clear, links both ways, pin), the class and race pages' About card (2.81: the game's own description marked 'From the game', the Outpost's notes (classlore.json) marked 'Outpost notes', the Skyborne pair written from the game's data only, At a glance from the page's tables, no tooltip card) and fixed top (hero and tabs stay, the content scrolls, the page's bar never shows at 3 window heights, wheel and keys), no class / spell data; optional read-only check of the real game files with screenshots",
            ["atlasdata"] = "Atlas data (2.77): how objects are classified (ObjectKinds: the lock first, an Alliance chest with a herb in it is a chest, real herbs and veins stay herbs and ore; the rule table, the live Objects lists / world map groups, the object page's Needs from the Classic lock), the Classic baseline's new fields (object locks from data0, quest chain links, skill / reputation needs, enders; index format 2, a format-1 file still read and rebuilt once from the dump on this PC), quest chains (QuestChains: step N of M, pre-quests, exclusive and all-of groups, breadcrumbs, quests taken while another is active, forks, loops and very long chains guarded, Forever's quest lines and next-quest answers) with the chain card on the quest page (folding, show all, links), and the game version of a find (Toolkit b -> Lodge gb, the words, unknown version, the item page and the strip), screenshots",
            ["atlasdatareal"] = "Atlas data on the real data, read-only: the Classic dump of the data folder read again into the scratch folder, the game's items and Lock table, the client's caches; every object classified the old and the new way (objects-audit.txt lists what moved), the Alliance chests are chests, herbs and veins stay put, every Herbalism-locked object is a herb; the chains of every quest worked out without a hang; same run rules as mapdatareal",
            ["atlashosts"] = "Atlas hosted categories Talents and Professions: the tiles on the start page, ids and sub-paths (talents:tree/<class>, talents:build/<code>, professions:<profession>/<tab>, professions:camp), the pages kept alive and the id following the class / profession / tab picked in them (no new history step), breadcrumb Atlas > Talents > Hunter, history item page -> Talents -> Professions and back, Ctrl+6 and ShowTab landing in the Atlas, the rail without the two buttons, the links the entity pages give (crafted by, recipe profession, talent of, skills list, Ctrl+click on a talent), a recipe link inside Professions and Back, the search offering the pages by name, no game data, screenshots; the Professions list in front of the page (2.85: browse:professions, its rows, chips, counts, banner, a row opens the profession, old ids)",
            ["zonelore"] = "the Outpost's zone notes (Assets\\Data\\zonelore.json, ZoneLore): the shipped notes read (every entry, facts line, sources, the 2665 -> 2521 alias, link rules for classic / forever-seen / unverified creatures, damaged notes), the spawn check on an invented world (here, capital city, cannot tell, elsewhere), the race page's start from the notes (never over a start the data found), the 'About <zone>' card on the Overview of Durotar, Elwynn Forest and Zephras Isle (summary, facts, Starts here with race links, History, Notable creature links, Worth knowing, On Forever, sources opening in the browser, reading width), 2665 showing 2521's notes, a zone without notes unchanged",
            ["zonelorereal"] = "the zone notes against the real data, read-only: every zone a map of the game, every 'classic' creature in the Classic baseline (index.bin of the data folder, only read) and with a spawn point in its zone unless marked unchecked (verdicts in zonelore-check.json); pictures of the real zone pages",
            ["about"] = "Settings > About (2.77): the public changelog inside the exe (one section per '## x.y.z', this version first, lead-ins, dates, the companion's CHANGELOG.txt, the manifest's addon notes) and its privacy scan (no user paths, e-mails, IPs, account names or tokens; the scanner catches planted ones), the campfire centred at 1100 / 1400 / 1900 px, the trademark line on one line when wide and wrapping when narrow, What's new (cards, this version marked, newer version / addon banners, older versions on demand, the picker), the one-time note after an update, the log viewer (levels and colours, exceptions and stack frames, personal details hidden in the view and in Copy, search, Warnings / Errors only, live tail, a 20 000-line log virtualised)",
            ["art"] = "painted art (Assets\\Art, ArtLibrary): every picture loads, the window background, the zone banners (Durotar / Teldrassil only), the Atlas Home cards with art, Characters > Talents spec names, hunter spec art and the columns / icons growing with the window",
            ["atlasclassic"] = "Atlas layer C, Classic baseline (cMaNGOS classic-db): SQL statement reader and value parser (escapes, NULL, negative / decimal, multi-row INSERT, REPLACE, UPDATE / DELETE with WHERE, @variables), loot chances (groups, references, quest-only, loops), update scripts above the dump's version, downloader against a fake GitHub (size shown first, progress, cancel, damaged / truncated / non-SQL dump refused, earlier set untouched, https only, safe names), index round trip, queries and the Classic baseline label",
        };

        const string Star = "*";

        // path (repo-relative, forward slashes) -> sections. All matching rules are unioned; a file no rule matches means ALL.
        // "*" = ALL (shared by everything), no sections = nothing the self-test exercises.
        static readonly List<Tuple<Regex, string[]>> Rules = BuildRules();

        static List<Tuple<Regex, string[]>> BuildRules()
        {
            var r = new List<Tuple<Regex, string[]>>();
            void R(string rx, params string[] s) => r.Add(Tuple.Create(new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), s));
            var lodgeAll = new[] { "armory", "lodge", "lodgefeatures", "lodgehardening", "lodgefun", "chatlist", "invites", "invites2", "linkopen", "calendar", "wowevents", "flightshare", "atlaslodge", "lodgefiles", "badges", "realms", "atlasclassiclodge", "video", "viewer", "toasts", "presence", "profile", "round3" };

            // ---- shared: everything depends on these (kept short on purpose: every file here makes a changed-mode run a full run)
            R(@"^src/(App\.xaml(\.cs)?|Brand\.cs)$", Star);
            R(@"^src/Services/(Util|SettingsStore|Models|Json|Net|SelfTestFixture)\.cs$", Star);
            // ---- the window itself and the self-test plumbing: the setup every run does (window shown, first update check, isolation, the scope
            //      and cache checks) already starts the app with them; on top of that, the sections that drive the window's own parts
            var core = new[] { "addons", "startup", "addonsgrid", "update", "round3", "atlashosts", "linkopen" };
            R(@"^src/MainWindow\.xaml(\.cs)?$", core);
            R(@"^src/MainWindow\.Welcome\.cs$|^src/WelcomeGuide\.cs$", "round3", "addons", "startup");
            R(@"^src/(MainWindow\.Others|SourceIcons|PillBase)\.cs$", "addons", "thirdparty", "selfupdated", "addonsgrid", "startup");
            R(@"^src/Services/(TestScope|SelfTestCache|SelfTestClock)\.cs$");   // their own checks run in every run (scope selftest, cache selftest)
            R(@"^src/Services/FakeUpdateServer\.cs$", "addons", "thirdparty", "startup", "addonsgrid", "selfupdated", "update", "round3");
            R(@"^src/Services/WowLocator\.cs$", "addons", "update", "round3", "readers", "inventory", "itemdata", "seendata");
            R(@"^src/Services/(Splash|SplashScene)\.cs$", "startup", "round3");
            // a project change that is not only the <Version> bump (references, resources): the build proves it compiles; these prove the app,
            // its resources and WebView2 still start
            R(@"^src/ElansAddonHub\.csproj$", "addons", "startup", "art", "browser");
            // animations (Motion): the sections that wait for something to open, collapse or fade
            R(@"^src/Motion\.cs$", "startup", "addonsgrid", "viewer", "lodgefun", "calendar", "wowevents", "lodgefiles", "badges", "video", "browser", "toasts", "dialogs", "health", "atlaspage", "atlasbrowse", "round3");
            // ---- theme and assets: the design sections, not everything (a broken XAML resource fails the start-up every run does)
            // the secondary menus' section colours (2.71.1): the tokens, the four menus and their own test
            R(@"^src/(Theme/Theme\.xaml|AtlasPage\.(Nav|Start)\.cs|AtlasPage\.xaml|SettingsPage\.xaml|InventoryPage(\.xaml(\.cs)?|\.Armory\.cs)|Lodge/(LodgeView\.xaml|LodgeModels\.cs|LodgeView\.(Calendar|Files|Ladder)\.cs)|MainWindow\.MenuColorsTest\.cs)$", "menucolors");
            R(@"^src/Theme/Theme\.xaml$", "inputs", "gametooltip", "dialogs", "toasts", "addonsgrid", "art", "calendar", "wowevents");
            R(@"^src/Theme/AddonIcons\.xaml$", "addons", "thirdparty", "addonsgrid", "selfupdated");
            R(@"^src/Theme/(Avatars|ProfileCard)\.xaml$", "profile", "presence", "lodgefeatures", "badges");
            R(@"^src/Theme/Badges\.xaml$", "badges", "profile");
            R(@"^src/Theme/ReactionIcons\.xaml$", "lodgefeatures", "lodgefun");
            R(@"^src/Assets/Art/", "art");
            R(@"^src/Assets/[\w.-]+\.(jpe?g|png)$", "art");
            R(@"^src/Assets/(hub\.(png|ico)|logo-[\w-]+\.png)$", "addons", "addonsgrid", "art");
            R(@"^src/(Controls/CardGrid|MainWindow\.(AddonsGrid|AddonsGridTest)|AddonCard)\.cs$", "addonsgrid", "addons", "startup", "selfupdated", "thirdparty");
            R(@"^src/MainWindow\.(InputsTest|DesignTest\.Inputs)\.cs$", "inputs");
            R(@"^src/Assets/Data/zonelore\.json$", "zonelore", "atlasclasses");
            R(@"^src/(Assets/Data/classlore\.json|Services/Atlas/ClassLore\.cs)$", "atlasclasses");     // the class and race notes (2.81)
            R(@"^src/Controls/TalentTreePanel\.cs$", "talents", "characters", "art", "atlashosts");
            R(@"^src/Controls/FindsStrip\.cs$", "atlasbrowse", "atlaspage");
            R(@"^src/MainWindow\.Search\.cs$", "atlaspage", "atlashosts");
            R(@"^src/FodyWeavers\.(xml|xsd)$", "addons", "startup", "art", "browser");
            R(@"^src/MainWindow\.DesignTest(\.\w+)?\.cs$");   // the screenshot-only design runs (ELANSHUB_DESIGN_SHOTS), not part of --selftest; the ones a section uses have their own rule
            R(@"^src/MainWindow\.RealSections\.cs$", SelfTestCache.Defs.Select(d => d.Name).ToArray());
            // ---- inventory
            R(@"^src/(Services/(Maps/|Casc\.cs|Blp\.cs|Db2/Wdc5\.cs)|Controls/AtlasMap\.cs|MainWindow\.MapDataTest(\.(?!Real\.)\w+)?\.cs)", "mapdata", "worldmap", "instances", "dungeonmaps");
            R(@"^src/(Services/DungeonMaps/|Controls/AtlasMap\.Drawn\.cs|MainWindow\.DungeonMapsTest(\.(?!Real\.)\w+)?\.cs)", "dungeonmaps", "mapdata", "atlaspage");
            R(@"^src/Services/WorldMap/(WdtFile|WmoFile|AdtFile)\.cs$", "dungeonmaps");
            R(@"^src/Services/(Maps/NamedEntrances|Atlas/AtlasObs\.Entrances|Atlas/AtlasInstances)\.cs$", "dungeonmaps", "instances", "atlaslodge", "atlaspage");
            R(@"^src/(Services/WorldMap/|Controls/WorldMapView\.cs|WorldMap/|MainWindow\.WorldMapTest(\.(?!Real\.)\w+)?\.cs)", "worldmap", "instances", "atlashosts", "atlaspage");
            R(@"^src/(HelpCard\.cs|MainWindow\.Atlas\.cs)$", "worldmap");    // the how-to cards (the live position's), the live position wiring
            R(@"^src/(Services/WorldMap/(Wmo\w+|AdtObjects|WorldMapBuildings\w*)|MainWindow\.BuildingsTest(\.(?!Real)\w+)?|SettingsPage(\.xaml)?)\.cs$", "buildings", "worldmap");
            R(@"^src/(Services/Atlas/AtlasInstances\.cs|AtlasPage\.Entrance\.cs|MainWindow\.InstancesTest(\.(?!Real\.|Audit\.)\w+)?\.cs)$", "instances", "atlaspage", "atlasbrowse");
            R(@"^src/Services/(InventoryData|IconStore|IconSheet|Blp|Casc|CharPortrait|LuaData|ZoneNames)\.cs$", "inventory");
            R(@"^src/(Services/(Db2/|Casc\.cs)|MainWindow\.ItemDataTest\.cs)", "itemdata");
            R(@"^src/(Services/(Db2/Wdb|Atlas/(SeenData|AtlasObs))|MainWindow\.SeenDataTest)\w*\.cs$", "seendata");
            R(@"^src/InventoryPage", "inventory", "characters", "upgrades", "armory", "itemdata", "spelldata");
            R(@"^src/(MainWindow\.ArmoryTest(\.\w+)?|SettingsPage\.Armory|Lodge/(ArmoryModels|LodgeSession\.Armory))\.cs$", "armory");
            R(@"^src/(Services/(Db2/|Casc\.cs)|MainWindow\.ItemDataTest\.cs)", "itemdata", "spelldata", "atlasclasses");
            R(@"^src/(MainWindow\.FactionsTest\.cs|AtlasPage\.Factions\.cs|Services/(FactionIcons|Db2/FactionData|Atlas/AtlasQuery\.Reputation)\.cs|Lodge/LodgeSession\.Reps\.cs)$", "factions", "atlaspage", "atlasbrowse", "armory");
            R(@"^src/(Services/(Db2/Class\w*|Atlas/(AtlasClasses|AtlasQuery\.Classes|AtlasBrowse\.Classes))|AtlasPage\.Classes|MainWindow\.AtlasClassesTest(\.\w+)?)\.cs$", "atlasclasses", "atlaspage", "atlasbrowse", "gametooltip");
            R(@"^src/MainWindow\.SpellDataTest(\.\w+)?\.cs$", "spelldata");
            R(@"^src/(Controls/MoneyText|Services/MoneyFormat|MainWindow\.MoneyTest)\.cs$", "money", "gametooltip", "inventory", "characters", "atlaspage", "professions", "upgrades");
            R(@"^src/(Controls/GameTooltip(\.Atlas)?\.cs|Services/(GameFont|Atlas/AtlasTip)\.cs|MainWindow\.GameTooltipTest\.cs)$", "gametooltip", "itemdata", "spelldata", "inventory", "atlaspage", "professions", "lodgefun", "talents", "atlasclasses");
            R(@"^src/(Services/(Talents/|Db2/(Talent|Wdc5|Spell)|Casc\.cs|HubModules\.cs)|TalentsPage|MainWindow\.TalentsTest)", "talents", "atlashosts");
            R(@"^src/(ProfessionsPage[\w.]*|MainWindow\.ProfessionsTest[\w.]*|Services/Professions/.*)$", "professions", "profguide", "atlashosts");
            R(@"^src/(MainWindow\.ProfGuideTest\.cs|Services/(SharedDataFile|Atlas/(RouteBuilder|GatherNodes|AtlasQuery\.Nodes))\.cs|AtlasPage\.Routes\.cs|ProfessionsPage\.Follow\.cs)$", "profguide", "flightshare", "atlaspage", "atlaslodge");
            R(@"^src/(MainWindow\.HubDataTest|Services/(HubData|SavedVarsFiles|LuaData))\.cs$", "hubdata", "characters");
            R(@"^src/(MainWindow\.ReadersTest|Services/(SavedVarsFiles|GamePresence|InventoryData|WowCalendar|FlightShare|AddonHealth|HealthMonitor|HubData|LuaData))\.cs$", "readers");
            R(@"^src/(MainWindow.AtlasClassicTest.cs|Services/Atlas/Classic/)", "atlasclassic", "atlasclassiclodge", "atlaspage");
            R(@"^src/(MainWindow\.AtlasClassicLodgeTest\.cs|Services/Atlas/ClassicLodge\.cs|SettingsPage\.Classic\.cs)$", "atlasclassiclodge", "atlaspage");
            R(@"^src/(AtlasPage(\.\w+)?\.(xaml|cs)|MainWindow\.(AtlasPageTest(\.(?!Real\.)\w+)?|Atlas)\.cs|Services/Atlas/(AtlasModel|AtlasQuery(\.\w+)?|AtlasDerived|ClassicSetup)\.cs)$", "atlaspage", "atlasbrowse", "atlashome", "atlashosts", "atlasclassiclodge", "atlasclasses", "upgrades");
            R(@"^src/(MainWindow\.AtlasHomeTest|Services/Atlas/AtlasHome)\.cs$", "atlashome", "atlaspage");
            R(@"^src/(MainWindow\.AtlasBrowseTest\.cs|Services/Atlas/(AtlasBrowse(\.\w+)?|AtlasCategories)\.cs)$", "atlasbrowse", "atlaspage", "atlasclasses", "atlashome");
            R(@"^src/(AtlasPage\.(Lore|Entity)|MainWindow\.(ZoneLoreTest|AtlasClassesTest)|Services/Atlas/(ZoneLore(\.\w+)?|AtlasQuery\.(Lore|World|Classes)|AtlasModel))\.cs$", "zonelore");   // the zone notes and the pages that show them
            R(@"^src/(MainWindow\.AtlasHostsTest(\.\w+)?\.cs|Services/Atlas/(AtlasHosts|AtlasBrowse\.Classes|AtlasCategories|AtlasNav)\.cs|Assets/Art/banner-professions\.jpg)$", "atlashosts", "atlaspage");     // 2.85: the Professions list
            R(@"^src/(MainWindow\.AtlasMenuTest\.cs|Services/Atlas/(AtlasNavCounts|AtlasSkillModel|AtlasNav|AtlasCategories|AtlasHosts|AtlasBrowse(\.Index)?)\.cs|AtlasPage\.(Nav|xaml)\.cs|AtlasPage\.xaml|MainWindow\.(Atlas|Search|Radio)\.cs|SideMenu.cs|MainWindow\.xaml|Theme/Theme\.xaml|SettingsPage\.xaml|InventoryPage\.xaml|Lodge/LodgeView\.xaml)$", "atlasmenu");
            R(@"^src/(MainWindow\.AtlasDataTest\.cs|AtlasPage\.(QuestChain|Finds|Start)\.cs|Services/Atlas/(ObjectKinds|QuestChains|FindVersions|AtlasBrowse(\.\w+)?|AtlasQuery\.(Other|Creature|Item)|AtlasObs|SeenView|ClassicSetup|AtlasTabs)\.cs|Services/Atlas/Classic/\w+\.cs|Lodge/LodgeSession\.Atlas\.cs)$", "atlasdata");
            R(@"^src/(Services/(Maps/|Db2/|Atlas/(SeenData|AtlasObs))|Controls/AtlasMap|Lodge/LodgeSession\.Atlas)", "atlaspage", "atlasbrowse");   // the layers the page combines
            R(@"^src/(MainWindow\.CharactersTest|Services/(HubModules|CharacterMerge))\.cs$", "characters", "upgrades", "inventory");
            R(@"^src/Services/Db2/", "upgrades");
            // the game data's start: every reader, the shared game storage, the progress model and its views, the zone levels, the page's update offer
            R(@"^src/(Services/(Casc|CascShared|GameDataProgress|GameDataStart)\.cs|Services/Db2/(ItemData|SpellData|ClassData|TalentData|DbCache)\.cs|Services/Maps/(MapData|AreaProbe|EntranceAreas|NamedEntrances|MapTables)\.cs|Services/Atlas/(SeenData|ZoneLevels|AtlasDerived|AtlasBrowse(\.Index)?)\.cs|Services/WorldMap/AdtFile\.cs|Controls/GameDataProgressView\.cs|AtlasPage\.(Updates|Browse|Entity)\.cs|TalentsPage\.xaml\.cs|MainWindow\.ColdStartTest\.cs)$", "coldstart");
            R(@"^src/(Services/(GameDataProgress|GameDataStart|CascShared)|Controls/GameDataProgressView|AtlasPage\.Updates)\.cs$", "atlaspage", "atlasbrowse", "talents", "itemdata", "mapdata");
            R(@"^src/(Services/Upgrades/|InventoryPage\.Upgrades|MainWindow\.UpgradesTest(\.\w+)?\.cs$|Services/Atlas/AtlasQuery\.Upgrades)", "upgrades", "characters", "atlaspage");
            R(@"^src/Services/Blp\.cs$", "selfupdated");
            R(@"^src/(ArtLibrary|MainWindow\.ArtTest|AtlasPage\.(Entity|Start)|InventoryPage\.Chars)\.cs$", "art");
            R(@"^src/MainWindow\.(InventoryTest|DesignTest\.Inventory)\.cs$", "inventory");
            // ---- the real-game-file sections: they run when what reads / parses / renders game data (or their own test file) changes, not for UI-only edits
            foreach (var d in SelfTestCache.Defs) { R(d.Readers, d.Name); R(d.Tests, d.Name); }
            // ---- lodge: chat, voice, files, profiles, toasts, presence, viewer
            // the Lodge's feature files pick their own sections (+ lodgefeatures as the chat smoke); the session core, the models and the page
            // itself (LodgeSession.cs, LodgeModels.cs, LodgeView.xaml(.cs)) and anything new under Lodge/ still run every Lodge section
            const string lodgeFeature = @"(Calendar\w*|LodgeView\.Calendar|LodgeSession\.(Calendar|WowCalendar)|Badges\w*|LadderView\w*|LodgeView\.Ladder|LodgeSession\.Badges|Files\w*|LodgeFiles|LodgeView\.Files|LodgeSession\.Files|SafeFile"
                + @"|Invite\w*|CreateLodgeGuide\w*|LodgeInvites|LodgeSession\.(Invites|Leave)|Lfg\w*|LodgeView\.(Lfg|History)|LodgeSession\.History|ItemChipVM|MemberGroups|LodgeSession\.(Realms|Armory|Atlas|Classic|FlightShare)|ArmoryModels"
                + @"|VideoViewer\w*|YouTube|ImageViewer\w*|Toast\w*|Overlay\w*|Avatar\w*|Profile\w*|AutoStatus)(\.xaml)?(\.cs)?$";
            R(@"^src/Lodge/(?!" + lodgeFeature + ")", lodgeAll);
            R(@"^src/Lodge/(Calendar\w*|LodgeView\.Calendar|LodgeSession\.(Calendar|WowCalendar))(\.xaml)?(\.cs)?$", "calendar", "wowevents", "lodgefeatures");
            R(@"^src/Lodge/(Badges\w*|LadderView\w*|LodgeView\.Ladder|LodgeSession\.Badges)(\.xaml)?(\.cs)?$", "badges", "profile", "lodgefeatures");
            R(@"^src/Lodge/(Files\w*|LodgeFiles|LodgeView\.Files|LodgeSession\.Files|SafeFile)(\.xaml)?(\.cs)?$", "lodgefiles", "lodgehardening", "lodgefeatures");
            R(@"^src/Lodge/(Invite\w*|CreateLodgeGuide\w*|LodgeInvites|LodgeSession\.(Invites|Leave))(\.xaml)?(\.cs)?$", "invites", "invites2", "linkopen", "lodgefeatures");
            R(@"^src/Lodge/(Lfg\w*|LodgeView\.(Lfg|History)|LodgeSession\.History|ItemChipVM|MemberGroups)(\.xaml)?(\.cs)?$", "lodgefun", "presence", "lodgefeatures");
            R(@"^src/Lodge/LodgeSession\.Realms\.cs$", "realms", "lodgefeatures");
            R(@"^src/Lodge/LodgeSession\.Classic\.cs$", "atlasclassiclodge", "lodgefeatures");
            R(@"^src/Lodge/LodgeSession\.FlightShare\.cs$", "flightshare", "profguide", "lodgefeatures");
            R(@"^src/Lodge/LodgeSession\.Atlas\.cs$", "lodgefeatures");   // its own rules above add atlaslodge / atlaspage / atlasbrowse
            R(@"^src/Lodge/(VideoViewer\w*|YouTube)(\.xaml)?(\.cs)?$", "video", "lodgefeatures");
            R(@"^src/Lodge/ImageViewer(\.xaml)?(\.cs)?$", "viewer", "lodgefeatures");
            R(@"^src/Lodge/(Toast\w*|Overlay\w*)(\.xaml)?(\.cs)?$", "toasts", "round3", "lodgefeatures");
            R(@"^src/Lodge/(Avatar\w*|Profile\w*|AutoStatus)(\.xaml)?(\.cs)?$", "profile", "presence", "badges", "lodgefeatures");
            R(@"^src/Services/(LodgeClient|AvatarArt|GamePresence|ItemLinks)\.cs$", lodgeAll);
            R(@"^src/Services/Voice\w*\.cs$", "voice", "lodge");
            R(@"^src/Services/Sounds\.cs$", "toasts", "lodge", "voice");
            R(@"^src/(SettingsPage\.About|MainWindow\.AboutTest|LogViewerWindow|Services/(Changelog|LogView))\.cs$|^src/CHANGELOG\.md$|^addon/ElansHub/CHANGELOG\.txt$", "about");   // Settings > About, the log viewer, the changelog shown there (privacy scan)
            R(@"^src/SettingsPage(?!\.About\.cs$)", "addons", "profile", "round3", "voice", "lodge", "browser", "invites", "invites2", "linkopen", "calendar", "wowevents", "flightshare", "atlaslodge", "lodgefiles", "badges", "realms", "armory", "atlasclassiclodge");
            // Radio Paradise in the title bar: the player, its window wiring and test; the title bar layout it shares with the search box, the realm dots and the game data chip
            R(@"^src/(RadioBar\.xaml(\.cs)?|MainWindow\.Radio(Test)?\.cs|SettingsPage\.Radio\.cs|Services/Radio\w*\.cs)$", "radio");
            R(@"^src/(MainWindow\.(xaml|Search\.cs|Realms\.cs)|Controls/GameDataProgressView\.cs)$", "radio");
            R(@"^src/MainWindow\.ProfileTest\.cs$", "profile");
            R(@"^src/MainWindow\.VideoTest\.cs$", "video");
            R(@"^src/(BrowserPage(\.xaml(\.cs)?)?|MainWindow\.BrowserTest|Services/(WebEngine|BrowserUrl|AdBlock))(\.cs)?$", "browser", "video");
            R(@"^src/MainWindow\.PresenceTest\.cs$", "presence");
            R(@"^src/MainWindow\.HardeningTest\.cs$", "lodgehardening");
            R(@"^src/MainWindow\.LodgeFunTest(\.\w+)?\.cs$", "lodgefun");
            R(@"^src/MainWindow\.ChatListTest\.cs$", "chatlist");
            R(@"^src/MainWindow\.InvitesTest\.cs$", "invites");
            R(@"^src/MainWindow\.Invites2Test\.cs$", "invites2");
            R(@"^src/MainWindow\.DesignTest\.Invites2\.cs$");   // screenshot-only design run
            R(@"^src/(MainWindow\.LinkOpen(Test)?|Services/LinkOpen)\.cs$", "linkopen", "invites");
            R(@"^src/MainWindow\.DesignTest\.LinkOpen\.cs$");   // screenshot-only design run
            R(@"^src/MainWindow\.CalendarTest\.cs$", "calendar");
            R(@"^src/MainWindow\.(WowEvents|WowDistinct)Test\.cs$", "wowevents", "calendar");
            R(@"^src/Services/WowCalendar\.cs$", "wowevents", "calendar", "lodge");
            R(@"^src/(MainWindow\.FlightShareTest|Services/FlightShare)\.cs$", "flightshare", "lodge");
            R(@"^src/(MainWindow\.Atlas(Lodge|Notes)Test|Lodge/LodgeSession\.Atlas|Services/Atlas/(AtlasObs(\.Notes)?|AtlasQuery\.Notes))\.cs$", "atlaslodge", "lodge");
            R(@"^src/Services/HubData\.cs$", "atlaslodge");
            R(@"^src/(MainWindow\.NpcSpotsTest|Services/Atlas/(AtlasObs|AtlasQuery)\.NpcSpots)\.cs$", "atlaslodge", "worldmap", "atlaspage");   // where NPCs really stand (npcspots, atlas8)
            R(@"^src/(MainWindow\.FindsAccuracyTest|Services/Atlas/AtlasObs\.Extras)\.cs$", "atlaslodge", "atlaspage");   // more accurate loot (counter extras, atlas9)
            R(@"^src/MainWindow\.FilesTest\.cs$", "lodgefiles", "lodge");
            R(@"^src/MainWindow\.BadgesTest\.cs$", "badges", "lodge");
            R(@"^src/(MainWindow\.RealmsTest|MainWindow\.Realms|Lodge/LodgeSession\.Realms)\.cs$", "realms", "lodge");
            R(@"^src/MainWindow\.DesignTest\.Calendar\.cs$");   // screenshot-only design run
            R(@"^src/MainWindow\.DesignTest\.WowEvents\.cs$");   // screenshot-only design run
            R(@"^src/MainWindow\.DesignTest\.Invites\.cs$");   // screenshot-only design run
            R(@"^src/MainWindow\.DesignTest\.LodgeFun\.cs$");   // screenshot-only design run
            R(@"^src/MainWindow\.ToastTest\.cs$", "toasts");
            R(@"^src/MainWindow\.DesignTest(\.Toasts)?\.cs$");     // screenshot-only design runs, not part of --selftest
            R(@"^server/", "lodge", "lodgefeatures", "presence", "profile", "invites", "invites2", "lodgefiles", "badges", "realms", "atlaslodge", "atlasclassiclodge");
            // ---- addons
            R(@"^src/(AddonCard|ThirdPartyCard)\.cs$", "addons", "thirdparty", "startup", "selfupdated");
            R(@"^src/(MainWindow\.SelfUpdatedTest|Services/(AddonLogo|AddonIcon|DevVersion))\.cs$", "selfupdated", "thirdparty");
            R(@"^src/MainWindow\.DesignTest\.SelfUpdated\.cs$");   // screenshot-only design run
            R(@"^src/(MainWindow\.Startup(ShiftTest(\.Checks)?)?\.cs|Skeleton\.cs)$", "startup", "addons");
            R(@"^src/Services/(AddonScanner|AddonSuggest|AddonSources|CurseForgeLocal|CurseForgeDirect|GitHubStats)\.cs$", "thirdparty", "addons", "selfupdated");
            R(@"^src/Services/(Installer|SelfUpdater|PathSafety)\.cs$", "update", "addons");
            R(@"^src/Services/UpdateChannel\.cs$", "update", "round3");
            R(@"^addon/", "addons");
            // ---- health, dialogs, strip
            R(@"^src/(HealthDialog\.cs|MainWindow\.Health(Hub)?(Test)?\.cs)$", "health");
            R(@"^src/Services/(AddonHealth|HealthMonitor|WowWatch)\.cs$", "health", "round3");
            R(@"^src/(Services/(HealthMonitor|WowWatch|PixelStrip|StripSelfTest|GamePresence|LodgeClient|VoiceEngine)|Lodge/LodgeSession(\.History)?|MainWindow\.IdleCpuTest)\.cs$", "idlecpu");   // what ticks while the hub is idle (the session's timers live in LodgeSession.cs / .History.cs)
            R(@"^src/MainWindow\.Round3Test\.cs$", "round3");
            R(@"^src/ThemedDialog", "dialogs", "invites2");
            R(@"^src/Services/(StripSelfTest|PixelStrip)\.cs$", "strip");
            // ---- not exercised by the self-test
            R(@"^(docs/|README\.md$|DESIGN\.md$|ARCHITECTURE\.md$|\.gitignore$|\.gitattributes$|Tools/)");
            // the release bumps: a project file whose only change is <Version>, and the newest Lodge server constant (it only feeds the owner's update notice)
            R(@"^src/ElansAddonHub\.csproj#version$");
            R(@"^src/Brand\.cs#lodgeserver$", "lodge", "lodgefeatures");
            R(@"^src/Services/Models\.cs#setting$", "round3");   // a new setting: Settings save / load is exercised by round3; the feature's own files pick the rest
            return r;
        }

        // sections affected by the changed files, with the reason per file; unknown/shared files make it ALL.
        // head(path) returns the start of a file (null when it is gone): a source file no rule knows is mapped by the sections its header
        // comment names (--selftest "atlaspage", "(sections art, characters, talents)"), so a new test file runs its own section, not everything.
        public static List<string> FromPaths(IEnumerable<string> files, out List<string> why, Func<string, string> head = null)
        {
            why = new List<string>();
            var set = new HashSet<string>();
            bool any = false;
            foreach (var raw in files ?? Enumerable.Empty<string>())
            {
                var f = (raw ?? "").Trim().Replace('\\', '/');
                if (f.Length == 0) continue;
                any = true;
                var hit = Rules.Where(x => x.Item1.IsMatch(f)).ToList();
                if (hit.Count == 0)
                {
                    var named = f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && head != null ? HeaderSections(SafeHead(head, f)) : new List<string>();
                    if (named.Count > 0) { why.Add(f + " -> " + string.Join(",", named) + " (named in its header)"); set.UnionWith(named); continue; }
                    why.Add(f + " -> ALL (unknown file: add a rule in TestScope or name its section in the file's header comment)"); set.UnionWith(All); continue;
                }
                var secs = new HashSet<string>(hit.SelectMany(x => x.Item2));
                if (secs.Contains(Star)) { why.Add(f + " -> ALL (shared)"); set.UnionWith(All); continue; }
                why.Add(f + " -> " + (secs.Count == 0 ? "nothing (not self-tested)" : string.Join(",", Order(secs))));
                set.UnionWith(secs);
            }
            if (!any) why.Add("no changed files");
            return Closure(set);
        }

        static IEnumerable<string> Order(IEnumerable<string> s) => All.Where(a => s.Contains(a));

        static string SafeHead(Func<string, string> head, string f) { try { return head(f); } catch { return null; } }

        static readonly Regex HeaderSelftest = new Regex(@"--selftest\s+""?([a-z0-9]+)""?", RegexOptions.CultureInvariant);
        static readonly Regex HeaderQuoted = new Regex(@"""([a-z0-9]+)""", RegexOptions.CultureInvariant);
        static readonly Regex HeaderSectionList =new Regex(@"\(sections?\s+([a-z0-9, ]+?)\)", RegexOptions.CultureInvariant);

        // the sections a file's leading comment names; only the comment lines before the code starts count
        public static List<string> HeaderSections(string text)
        {
            var found = new HashSet<string>();
            if (string.IsNullOrEmpty(text)) return new List<string>();
            int code = 0;
            foreach (var raw in text.Split('\n').Take(80))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("using ") || l.StartsWith("namespace ") || l == "{") continue;
                if (!l.StartsWith("//")) { if (++code > 2) break; continue; }   // the class line and its brace may come before a member's comment
                foreach (Match m in HeaderSelftest.Matches(l)) if (All.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
                if (l.Contains("section") || l.Contains("selftest"))   // The "armory" section, ... / "wowevents" / "calendar" selftest, part 2
                    foreach (Match m in HeaderQuoted.Matches(l)) if (All.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
                foreach (Match m in HeaderSectionList.Matches(l))
                    foreach (var w in Regex.Split(m.Groups[1].Value, @"[\s,]+")) if (All.Contains(w)) found.Add(w);
            }
            return Order(found).ToList();
        }

        static string ReadHead(string repo, string path)
        {
            var full = Path.Combine(repo, path.Split('#')[0]);
            if (!File.Exists(full)) return null;
            using (var r = new StreamReader(full)) { var buf = new char[6000]; int n = r.ReadBlock(buf, 0, buf.Length); return new string(buf, 0, n); }
        }

        // the selection plus everything it depends on, in run order
        public static List<string> Closure(IEnumerable<string> picked)
        {
            var set = new HashSet<string>(picked ?? Enumerable.Empty<string>());
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var s in set.ToList())
                    if (Deps.TryGetValue(s, out var d))
                        foreach (var x in d) if (set.Add(x)) grew = true;
            }
            return Order(set).ToList();
        }

        // --only a,b,c -> sections; unknown names are returned so the caller can fail loudly instead of silently running nothing
        public static List<string> ParseOnly(string csv, out List<string> unknown)
        {
            var names = (csv ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).ToList();
            unknown = names.Where(n => n != "all" && !All.Contains(n)).ToList();
            if (names.Contains("all")) return All.ToList();
            return Closure(names.Where(n => All.Contains(n)));
        }

        // the chosen sections for this process (no flag = everything); `explain` goes into result.txt, `error` means run nothing
        public static List<string> FromArgs(string[] args, out string explain, out string error)
        {
            explain = "scope: all sections (no --only / --changed)"; error = null;
            string only = ArgValue(args, "--only"), changed = ArgValue(args, "--changed");
            if (only == null && changed == null && ArgValue(args, "--paths") == null) return All.ToList();
            var picked = new HashSet<string>();
            var sb = new StringBuilder();
            if (only != null)
            {
                var s = ParseOnly(only, out var unknown);
                if (unknown.Count > 0) { error = "unknown section(s): " + string.Join(", ", unknown) + " (valid: " + string.Join(", ", All) + ")"; return new List<string>(); }
                picked.UnionWith(s);
                sb.AppendLine("scope --only " + only + " -> " + string.Join(",", s));
            }
            var paths = ArgValue(args, "--paths");   // --paths a;b;c: as if exactly those files had changed (for -WhatIf -Paths)
            if (paths != null)
            {
                var repo = ArgValue(args, "--repo") ?? Directory.GetCurrentDirectory();
                var s = FromPaths(paths.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries), out var why, p => ReadHead(repo, p));
                picked.UnionWith(s);
                sb.AppendLine("scope --paths -> " + (s.Count == All.Length ? "ALL" : s.Count == 0 ? "no sections" : string.Join(",", s)));
                foreach (var w in why) sb.AppendLine("  " + w);
            }
            if (changed != null)
            {
                var repo = ArgValue(args, "--repo") ?? Directory.GetCurrentDirectory();
                var files = GitChanged(changed, repo, out var gitErr);
                if (files == null) { picked.UnionWith(All); sb.AppendLine("scope --changed " + changed + " -> ALL (git failed: " + gitErr + ")"); }
                else
                {
                    var s = FromPaths(files, out var why, p => ReadHead(repo, p));
                    picked.UnionWith(s);
                    sb.AppendLine("scope --changed " + changed + ": " + files.Count + " changed file(s) -> " + (s.Count == All.Length ? "ALL" : s.Count == 0 ? "no sections" : string.Join(",", s)));
                    foreach (var w in why) sb.AppendLine("  " + w);
                }
            }
            explain = sb.ToString().TrimEnd();
            return Closure(picked);
        }

        public static string ArgValue(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
        }

        // git diff --name-only <base>...HEAD + uncommitted tracked changes + untracked files; null if git is not usable.
        // (diff paths are repo-root relative; ls-files needs --full-name for that)
        public static List<string> GitChanged(string baseRef, string repo, out string error)
        {
            error = null;
            var all = new List<string>();
            var bs = Git("rev-parse --verify --quiet \"" + baseRef + "^{commit}\"", repo, out error);
            if (bs == null || bs.Count == 0) { error = error ?? "unknown ref " + baseRef; return null; }
            var hd = Git("rev-parse HEAD", repo, out error);
            if (hd == null || hd.Count == 0) return null;
            var cmds = new List<string>();
            if (bs[0] != hd[0]) cmds.Add("diff --name-only " + baseRef + "...HEAD");   // base == HEAD: only uncommitted work
            cmds.Add("diff --name-only HEAD");
            cmds.Add("ls-files --others --exclude-standard --full-name");
            foreach (var a in cmds)
            {
                var o = Git(a, repo, out error);
                if (o == null) return null;
                all.AddRange(o);
            }
            var files = all.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            // a release bump alone must not make it a full run: the file is listed by the one line that changed in it
            for (int i = 0; i < files.Count; i++)
            {
                var tag = BumpOnly(files[i], bs[0] != hd[0] ? baseRef : null, repo);
                if (tag != null) files[i] = files[i] + "#" + tag;
            }
            return files;
        }

        // the lines a release bumps, per file; any other changed line in the file keeps it a normal (shared) change
        static readonly Dictionary<string, Tuple<string, Regex>> Bumps = new Dictionary<string, Tuple<string, Regex>>
        {
            ["src/ElansAddonHub.csproj"] = Tuple.Create("version", new Regex(@"^\s*<Version>[0-9.]+</Version>\s*$")),
            ["src/Brand.cs"] = Tuple.Create("lodgeserver", new Regex(@"^\s*public const string LatestLodgeServer = ""[0-9.]+"";\s*$")),
            // a new saved setting: [DataMember] auto-properties and comments only - the feature that reads it is in the same change and picks its sections
            ["src/Services/Models.cs"] = Tuple.Create("setting", new Regex(@"^\s*(\[DataMember\(Name = ""\w+""\)\] public [\w<>,\[\]? ]+ \w+ \{ get; set; \}( = [^;]+;)?\s*(//.*)?|//.*|)$")),
        };

        static string BumpOnly(string file, string baseRef, string repo)
        {
            if (!Bumps.TryGetValue(file, out var b)) return null;
            var diffs = new List<string>();
            if (baseRef != null) diffs.Add("diff -U0 " + baseRef + "...HEAD -- \"" + file + "\"");
            diffs.Add("diff -U0 HEAD -- \"" + file + "\"");
            int changed = 0;
            foreach (var d in diffs)
            {
                var o = Git(d, repo, out _);
                if (o == null) return null;
                foreach (var l in o)
                {
                    if (l.StartsWith("+++") || l.StartsWith("---") || !(l.StartsWith("+") || l.StartsWith("-"))) continue;
                    if (!BumpLine(b.Item2, l.Substring(1))) return null;
                    changed++;
                }
            }
            return changed > 0 ? b.Item1 : null;
        }

        static bool BumpLine(Regex r, string line) => r.IsMatch(line.TrimEnd('\r'));

        static List<string> Git(string args, string repo, out string error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo("git", args) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    var o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000)) { error = "git timed out"; return null; }
                    if (p.ExitCode != 0) { error = "git " + args + ": " + err.Result.Trim(); return null; }
                    return o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
            }
            catch (Exception e) { error = e.Message; return null; }
        }

        // pure-function checks (no git, no UI)
        public static string SelfTest()
        {
            var n = new List<string>(); bool ok = true;
            void Ck(string w, bool c) { n.Add((c ? "ok   " : "FAIL ") + w); ok &= c; }
            bool Same(IEnumerable<string> a, params string[] b) => a.SequenceEqual(b);
            List<string> Map(params string[] f) => FromPaths(f, out _);
            Ck("inventory files -> the Characters page sections only (characters, inventory, the list's section colours); the icon store also reads game files, so the Atlas page's real-file section", Same(Map("src/InventoryPage.xaml.cs", "src/Services/InventoryData.cs", "src/Services/IconStore.cs"), "readers", "characters", "upgrades", "armory", "itemdata", "spelldata", "inventory", "menucolors", "atlaspagereal"));
            Ck("UI-only edits do not run the real-game-file sections (world map page, Atlas page, Characters page); a reader does (Casc, Wdc5, WorldMap/WdtFile)",
                !Map("src/Controls/WorldMapView.cs", "src/WorldMap/worldmap.js", "src/AtlasPage.Nav.cs", "src/InventoryPage.Bags.cs").Any(s => s.EndsWith("real")) && Same(Map("src/Services/Casc.cs").Where(s => s.EndsWith("real")), "mapdatareal", "worldmapreal", "instancesreal", "buildingsreal", "dungeonmapsreal", "atlaspagereal")
                && Map("src/Services/WorldMap/WdtFile.cs").Contains("worldmapreal") && !Map("src/Services/WorldMap/WdtFile.cs").Contains("atlaspagereal") && Map("src/MainWindow.BuildingsTest.Real.cs").SequenceEqual(new[] { "buildingsreal" })
                && Map("src/MainWindow.WorldMapTest.Real.cs").SequenceEqual(new[] { "worldmapreal" }) && !Map("src/MainWindow.WorldMapTest.Page.cs").Contains("worldmapreal"));
            Ck("backslash paths are normalised", Same(Map("src\\InventoryPage.Bags.cs"), "characters", "upgrades", "armory", "itemdata", "spelldata", "inventory"));
            var lodge = Map("src/Lodge/LodgeView.xaml.cs");
            Ck("Lodge files -> lodge, viewer, toasts, presence (not inventory)", new[] { "lodge", "viewer", "toasts", "presence" }.All(lodge.Contains) && !lodge.Contains("inventory"));
            Ck("a Lodge feature file -> its own sections (the calendar view: calendar, wowevents + the chat smoke), the Lodge core (session, models, page) -> every Lodge section; a feature partial of the session does not run idlecpu",
                Same(Map("src/Lodge/CalendarView.xaml.cs"), "lodgefeatures", "calendar", "wowevents") && Same(Map("src/Lodge/LodgeSession.Realms.cs"), "lodge", "lodgefeatures", "realms")
                && new[] { "src/Lodge/LodgeSession.cs", "src/Lodge/LodgeModels.cs", "src/Lodge/LodgeView.xaml", "src/Lodge/SomethingNew.cs" }.All(f => new[] { "lodge", "lodgefun", "invites2", "calendar", "badges" }.All(Map(f).Contains))
                && Map("src/Lodge/LodgeSession.cs").Contains("idlecpu") && !Map("src/Lodge/LodgeSession.Badges.cs").Contains("idlecpu"));
            Ck("a theme token or a painted picture -> the design sections only (no real-game-file, Lodge-protocol or map sections)",
                Map("src/Theme/Theme.xaml").Contains("inputs") && Map("src/Theme/Theme.xaml").Contains("gametooltip") && Map("src/Theme/Theme.xaml").Count < 12 && !Map("src/Theme/Theme.xaml").Any(s => s.EndsWith("real"))
                && Same(Map("src/Assets/Art/zone-1411.jpg", "src/Assets/Art/talent-mage-0.jpg"), "art"));
            Ck("the window, the project file and the animations -> a handful of sections, not ALL; the self-test's own plumbing -> nothing extra (its checks always run)",
                new[] { "src/MainWindow.xaml.cs", "src/MainWindow.xaml", "src/ElansAddonHub.csproj", "src/Motion.cs" }.All(f => Map(f).Count > 0 && Map(f).Count < 20 && !Map(f).Any(s => s.EndsWith("real") || s == "worldmap" || s == "buildings" || s == "idlecpu"))
                && Map("src/Services/TestScope.cs", "src/Services/SelfTestCache.cs", "src/Services/SelfTestClock.cs").Count == 0);
            Ck("shared files -> ALL", new[] { "src/App.xaml.cs", "src/Brand.cs", "src/Services/Util.cs", "src/Services/SettingsStore.cs", "src/Services/Json.cs" }.All(f => Map(f).Count == All.Length));
            Ck("unknown file -> ALL", Map("src/Brand_new_file.cs").Count == All.Length && Map("somewhere/else.bin").Count == All.Length && Map("src/Assets/Data/new.json").Count == All.Length);
            Ck("one shared file among specific ones -> ALL", Map("src/InventoryPage.xaml", "src/Services/Util.cs").Count == All.Length);
            var heads = new Dictionary<string, string>
            {
                ["src/MainWindow.NewThingTest.cs"] = "using System;\r\n\r\nnamespace ElansAddonHub\r\n{\r\n    // --selftest \"atlaspage\", part 9: something new\r\n    public partial class MainWindow\r\n",
                ["src/MainWindow.TreeData.cs"] = "using System;\r\nnamespace ElansAddonHub\r\n{\r\n    // Full-size trees for the self-test (sections art, characters, talents): data\r\n",
                ["src/Services/Quiet.cs"] = "using System;\r\nnamespace X\r\n{\r\n    public class Quiet\r\n    {\r\n        int a;\r\n        int b;\r\n    // --selftest \"atlaspage\" mentioned far below the code start\r\n",
            };
            List<string> MapH(string f) => FromPaths(new[] { f }, out _, p => heads.TryGetValue(p, out var h) ? h : null);
            Ck("a new source file no rule knows runs the sections its header comment names ('--selftest \"x\"', '(sections a, b, c)'); without such a header it is ALL",
                Same(MapH("src/MainWindow.NewThingTest.cs"), "atlaspage") && Same(MapH("src/MainWindow.TreeData.cs"), "characters", "talents", "art")
                && MapH("src/Services/Quiet.cs").Count == All.Length && MapH("src/Services/Gone.cs").Count == All.Length);
            Ck("docs only -> nothing", Map("README.md", "docs/design/x.png", "ARCHITECTURE.md").Count == 0);
            Ck("the changelog is shown in Settings > About (inside the exe) -> its privacy scan runs: about", Same(Map("src/CHANGELOG.md"), "about") && Same(Map("addon/ElansHub/CHANGELOG.txt"), "addons", "about"));
            Ck("a release bump (project <Version> only) -> nothing; the newest Lodge server constant -> the Lodge sections only; any other project change -> the start-up smoke sections; any other Brand change -> ALL",
                Map("src/ElansAddonHub.csproj#version").Count == 0 && Same(Map("src/Brand.cs#lodgeserver"), "lodge", "lodgefeatures") && Same(Map("src/ElansAddonHub.csproj"), "addons", "startup", "browser", "art") && Map("src/Brand.cs").Count == All.Length);
            Ck("bump lines: a version line and the Lodge server line are bumps, anything else is not",
                BumpLine(Bumps["src/ElansAddonHub.csproj"].Item2, "    <Version>2.63.1</Version>\r") && !BumpLine(Bumps["src/ElansAddonHub.csproj"].Item2, "    <PlatformTarget>AnyCPU</PlatformTarget>")
                && BumpLine(Bumps["src/Brand.cs"].Item2, "        public const string LatestLodgeServer = \"2.24.0\";") && !BumpLine(Bumps["src/Brand.cs"].Item2, "        public const string Name = \"Elan's Outpost\";"));
            Ck("a new saved setting (Models.cs: [DataMember] auto-properties and comments only) -> round3, not ALL; any other Models change -> ALL",
                Same(Map("src/Services/Models.cs#setting"), "round3") && Map("src/Services/Models.cs").Count == All.Length
                && BumpLine(Bumps["src/Services/Models.cs"].Item2, "        [DataMember(Name = \"atlasPins\")] public List<string> AtlasPins { get; set; }              // the Atlas pages the person pinned")
                && BumpLine(Bumps["src/Services/Models.cs"].Item2, "        [DataMember(Name = \"upgradeWeights\")] public Dictionary<string, string> UpgradeWeights { get; set; }")
                && BumpLine(Bumps["src/Services/Models.cs"].Item2, "        // ---- gathering routes") && BumpLine(Bumps["src/Services/Models.cs"].Item2, "")
                && !BumpLine(Bumps["src/Services/Models.cs"].Item2, "    public class QuickLink") && !BumpLine(Bumps["src/Services/Models.cs"].Item2, "        public bool IsOld => Version < 3;"));
            Ck("no changes -> nothing", Map().Count == 0);
            Ck("results are in run order", Map("src/InventoryPage.xaml", "src/HealthDialog.cs", "src/ThemedDialog.xaml").SequenceEqual(All.Where(a => a == "dialogs" || a == "invites2" || a == "characters" || a == "upgrades" || a == "armory" || a == "itemdata" || a == "spelldata" || a == "inventory" || a == "health" || a == "menucolors" || a == "atlasmenu")));
            Ck("every rule section is a real section", Rules.SelectMany(r => r.Item2).All(s => s == Star || All.Contains(s)));
            Ck("every dependency is a real section", Deps.All(d => All.Contains(d.Key) && d.Value.All(All.Contains)));
            Ck("every section has a description", All.All(Info.ContainsKey));
            // closure through the same code path, with synthetic edges
            var saved = Deps.ToDictionary(k => k.Key, k => k.Value);
            try
            {
                Deps["toasts"] = new[] { "presence" }; Deps["presence"] = new[] { "profile" };
                Ck("dependency closure is transitive and in run order", Same(Closure(new[] { "toasts" }), "presence", "profile", "toasts"));
                Ck("closure of nothing is nothing", Closure(new string[0]).Count == 0);
            }
            finally { Deps.Clear(); foreach (var k in saved) Deps[k.Key] = k.Value; }
            var only = ParseOnly("Inventory, toasts", out var unk);
            Ck("--only parses names case-insensitively", Same(only, "toasts", "inventory") && unk.Count == 0);
            ParseOnly("inventory,bogus", out unk);
            Ck("--only reports unknown names", Same(unk, "bogus"));
            Ck("--only all -> everything", ParseOnly("all", out _).Count == All.Length);
            Ck("no flags -> everything", FromArgs(new[] { "x.exe", "--selftest", "d" }, out _, out _).Count == All.Length);
            var bad = FromArgs(new[] { "--only", "nope" }, out _, out var err);
            Ck("unknown --only -> error and nothing runs", bad.Count == 0 && err != null);
            var fb = FromArgs(new[] { "--changed", "no-such-ref-xyz", "--repo", Path.GetTempPath() }, out var ex, out _);
            Ck("git unusable -> falls back to ALL", fb.Count == All.Length && ex.Contains("ALL"));
            return "scope selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", n);
        }
    }
}
