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
    // No flag: everything, exactly as before. Anything unknown or shared maps to ALL: safe rather than clever.
    public static class TestScope
    {
        // execution order == order of the full run
        public static readonly string[] All =
        {
            "addons", "thirdparty", "startup", "addonsgrid", "selfupdated", "viewer", "voice", "update", "strip",
            "lodge", "lodgefeatures", "lodgehardening", "lodgefun", "invites", "invites2", "linkopen", "calendar", "wowevents", "flightshare", "atlaslodge", "lodgefiles", "badges", "realms", "hubdata", "readers", "characters", "upgrades", "armory", "itemdata", "spelldata", "gametooltip", "talents", "professions", "profguide", "mapdata", "worldmap", "instances", "seendata", "video", "browser", "presence", "profile", "toasts", "dialogs", "inventory", "health", "round3", "inputs", "atlasclassic", "atlasclassiclodge", "atlaspage", "atlasbrowse", "atlashosts", "atlasclasses",
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
            ["invites"] = "Lodge 2.38: one-time invite links, device tokens, upgrade prompt, Guild Master invite list, Your devices, error messages",
            ["invites2"] = "Lodge 2.12 invites in the app: the Invite sheet (ranks by role, person / guest, Copy and Copy as web link, open invites + Revoke), web-link parsing, profile-card device link, Create-a-lodge guide, Danger zone (typed name, leave, transfer ownership), Your lodge card, join banner",
            ["linkopen"] = "clicking an elanshub:// link: accepted links, per-user registration (test root), pipe hand-off, join panel (never auto-joined), Settings toggle",
            ["calendar"] = "Lodge 2.11: raid / event calendar - agenda grouping, local time, month dots, form checks, sign-up buttons, 15-minute reminders, next-event line, chat card, hidden on older servers",
            ["wowevents"] = "WoW's own calendar in the Lodge calendar (companion addon data): parsing, local time, agenda 'until', month bands, switches, the strip above the chat (fixed height, no shift of the message list), nothing sent to the server",
            ["flightshare"] = "Lodge 2.13 shared flight times: Hunter Helper saved variables (validation, several accounts), upload batches (500 legs, once per 10 minutes, only what changed), merge answer, SharedData.lua (escaping, injection, structure, written only when the toc lists it, atomically), setting off, guests, older server = nothing",
            ["atlaslodge"] = "Lodge 2.15 Atlas pool: the Hub module atlas.obs (parsing, validation of every field, too new, absent), deltas (signature per entry, batch limits and the 56 KB frame, revealed-items hook), the persisted upload record, upload batches (once per 3 minutes, only what changed), the aggregate (pages, validation of the answer, cache file, read API: drops, vendors, finds, spawns, revealed items), sharing off, guests, feature disabled, older server = nothing",
            ["lodgefiles"] = "Lodge 2.14 shared files: slots, upload (new slot, Replace = new version), the Updated badge, Save with the SHA-256 check and the Mark of the Web, damaged download, roll back and delete (with confirms), officer-only buttons (members and guests only save), blocked programs and zips holding programs, size limit, the notice above the chat, hidden on a server without the feature (HTTP goes to a fake of the server endpoints)",
            ["badges"] = "Lodge badges and ladder (feature badges): the Ladder view (rows, your own row highlighted, this month / all time asks for the right season, empty month, hidden rows), the badges card and the Settings > Profile list (earned with date, unearned with how to earn and a progress line, secret ones as ???), the strip of the best badges on a profile card (top 5, +N), the quiet new-badge dot that goes when you look, Hide me from the ladder sending the flag, the Guild Master switch, a lodge switched off or locked, guests, a server without the feature = everything hidden and nothing sent, refusals never shown",
            ["realms"] = "Lodge realm status (feature realms): the dots at the top of the window (one per realm, labelled by ruleset or name, green up / red down / grey unknown), tooltips (name, ruleset, status, population, queue, checked N min ago, via your Lodge), a change recolours them, hidden without a Lodge, on a server without the feature and when the Forever realms were not found, the Settings switch (on by default), the back-up note (off by default, a quiet toast, silenced by the toast switch), nothing sent to the server",
            ["hubdata"] = "Elan's Hub data layer (Outpost side): ElansHubData parsing (core v1, account modules, a module newer than the app understands = TooNew without data, no ElansHubData, garbage), newest file wins, parse cache, locked file keeps the last good read, shared SavedVariables finder (links skipped)",
            ["video"] = "YouTube preview cards + in-app player",
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
            ["worldmap"] = "Atlas World map (2.51 phase 1, 2D; 2.53 phase 2, terrain, zone borders, 3D): the root ADT reader (reversed tags, MCNK / MCVT / area ids on a synthetic file and on real tiles), the 65 x 65 height grids (down-sampling, seams between neighbouring tiles), the terrain cache (build, resume, cancel, damaged and missing files, clean-up), zone outlines on invented area grids (two zones, a hole, islands, corners that touch) and on the real continents (accuracy against the chunk grid), which zone a point is in, the flat map's Zone borders layer and Navigate here with the terrain's zones, the 2D | 3D switch, the 3D page in a real WebView2 (WebGL or the fallback to the flat map, markers on the ground, borders, FPS and memory), the new protocol messages and &m= ids; and in phase 1: the tile / world math (grid corners, Orgrimmar / Stormwind / Razor Hill tiles, the Leaflet plane, pyramid ranges), the WDT reader (MAID, reversed tags, stray tiles), the tile pyramid on a fake source (counts per level, pixels, missing tiles, cache reuse, resume after a cancel, damaged tiles, build-key cleanup, size and Clear), the flight tables TaxiNodes / TaxiPath / TaxiPathNode (synthetic WDC5 tables, unknown layout, saved index), the message protocol (bad JSON, unknown actions, positions outside the plane, links and layers off the lists), worldmap: ids and history, Navigate here (/etk way text), the layers on an invented continent, chat links, then the page in a real WebView2 (build progress, ready, layers, the panel, right-click menu, junk messages, the sandbox, Back, Show on world map, a new game build) and a read-only check of the real game files (WDT FileDataIDs, orientation proven on the real flight masters, a real region build) with screenshots (ELANSHUB_WORLDMAP_FULL=1 also builds both whole continents (pictures and terrain) in the scratch folder, times them, measures the borders' accuracy, flies the 3D camera over the real mountains and takes the screenshots)",
            ["instances"] = "Dungeon and raid entrances on the maps: Map.db2's ghost spot (CorpseMapID + Corpse[2]) read from synthetic WDC5 tables in the real layout (unknown layout = none), LFGDungeons + ContentTuning for the suggested level (joined by name: an s more, a letter off, one inside the other), entrance choice (no continent, 0/0, outside the world, NaN = none), the zone an entrance is in (the terrain's area decides where the map rectangles overlap), the saved index, the Dungeons & raids world map layer (messages, kinds, catalog, protocol, settings migration), the instance page's Entrance card (zone, position, map, Show on world map, Navigate here copy), the zone page's list and door markers, the browse list's zone / level columns; optional read-only check of the real game files (Ragefire Chasm in Orgrimmar, Wailing Caverns in the Barrens, Molten Core at Blackrock Mountain, Razorfen Kraul / Onyxia by the terrain's areas, levels) with screenshots (ELANSHUB_INSTANCES_SHOTS=1: the real continents in 2D and 3D); the 2D / 3D pages with the layer are checked in the worldmap section",
            ["seendata"] = "Atlas A3, what the client has seen: the WDB container (header, duplicate ids, cut-off and garbage files), the creature / game object / quest decoders (every record checked against its computed length), the saved index (round trip, invalidated by a changed file), search (ranking, ids, kinds), the background service (status line, switch, debounced refresh, other files ignored), the Lodge hook; optional read-only check of the real cache",
            ["gametooltip"] = "Game-style tooltips (2.50.1): the GameTooltip control (panel colours as theme tokens, name in the quality colour, Item Level gold, white / green / gold lines in the game's order, two-column slot and damage | speed rows, coin glyphs, requirement red when the character is too low, set lines, spell variant, the muted footer under a hairline), the Atlas kinds (creature reaction colours and level line, object, quest, spell, zone, instance, faction), the font check (name table; a font that is not Friz Quadrata is never used), optional read-only check of the real game files",
            ["spelldata"] = "Atlas A2 + A4 and the item details: spells (names, ranks, descriptions with the $s1 / $o1 / $d tokens filled in, icons, cast time, duration, range), skills and professions, recipes and reagents (what makes an item, what uses it), factions and their templates (friendly / hostile to the Alliance and the Horde), armor and weapon damage from the item-level tables, item effects (Use / Equip / Chance on hit), item sets with bonuses; the WDC5 additions (relationship data, uncompressed arrays, the string space across sections); an unknown layout makes only that table unavailable; the saved index keyed by the game build; tooltips on the Characters page (item facts, set count, recipes under a profession, the spell in a talent tile); the revealed-items hook of the Lodge; optional read-only check of the real game files",
            ["talents"] = "Atlas layer G, talent calculator: the Trait* tables read from the game files (trees per class from the skill lines, nodes with repaired positions and duplicates left out, entries, definitions, edges, row gates as group conditions, point sources by level and the legacy Talented ranks, rank curves for rank-aware descriptions; an unknown layout = unavailable, an optional table only drops its feature), the rules (open / closed talents, prerequisites, refunds that would break dependants refused, points and levels), build codes (round trip, versions, junk, bad check sum, illegal layouts), build links from Seemeta / WoW Forever Talent / Talents Forever (constructed from the formats: ranks, leveling order, unreadable sites, damaged, wrong class, talent not in the tree; also on the real trees of all nine classes), Hunter Helper Builds.lua (fixture copy: parsing, name to node mapping, leveling order, unmatched names), saved builds, a character's talents from the Hub module, the saved index, the Talents page; optional read-only check of the real game files with screenshots",
            ["professions"] = "Atlas layer H, Professions page: skill-up colours from the trivial ranks (orange / yellow / green / grey), recipes with how they are learned (trainer, taught with the profession, recipe item from a vendor or a drop), the ranks and their caps from the rank spells, camp kits / objects / blueprints / buffs found from the Tier recipes, what you can craft now across characters, the leveling guide (steps, rank-cap milestones, craft counts, shopping list minus holdings, copy as text), the gathering zone guide, Atlas link hook; optional read-only check of the real game files with screenshots",
            ["profguide"] = "the leveling guide in game, gathering nodes and routes (profguide contract A-D): the route builder (clusters, nearest neighbour, 2-opt, closed loop, the Classic fallback below 8 Forever nodes, yards and minutes, caps, several object kinds in one loop, saved routes), Hub `nodes` (parsing, caps), upload batches (only with atlas3 on a 2.18 server, deltas, 50 objects / 1000 points per message), the Lodge's merged nodes (read API, cache), SharedData.lua composed from flights + profGuides (flights-only byte-identical, escaping, caps, size, extra sections, flights kept while only the guides change, Stop following), Follow in game / Stop following on the Guide tab (crafting and gathering guides, routes, pins with n = 0 for Classic, a saved route chosen for the guide, debounced rebuild), Forever nodes on object and zone pages above the Classic positions, the Routes panel (combined loop, save, use for guide, delete), Show route from the gathering guide, screenshots",
            ["health"] = "addon health",
            ["inputs"] = "text inputs: Input / SecretInput / Dropdown draw g j p q y without clipping at 100 % and 125 % (pixel check)",
            ["round3"] = "settings round 3 (WoW watch, lodge update, welcome, ...)",
            ["atlasclassiclodge"] = "Classic baseline from the Lodge (server 2.17, Outpost 2.49): the bundle of the original upstream files (pack, unpack with safe names, the version string and which release is newer), a fake Lodge with the dataset endpoints; a member without Classic data gets it automatically (SHA-256 checked, unpacked, read, labelled \"via your Lodge\"), nothing is downloaded when the same release is installed, a newer release at the Lodge replaces an older one, a damaged download is refused with the banner as fallback and not retried in a loop, the switch off or a removal by the person = no automatic fetch, the owner / officer share button in Settings and in the Atlas (with the quiet offer after their own download, asked once per release), a member sees no share button, a guest is not offered anything, a server older than 2.17 = the banner only, the Settings lines, screenshots",
            ["atlaspage"] = "Atlas layer F, the Atlas page: search (grouping by kind, ranking, Forever above Classic baseline, id links, one-letter and blank), the item page (the game's tooltip, your characters, the sources table with Lodge-observed rows above the Classic baseline's, merged rows, world drops under 0.1 % folded, labels on every row, recipes and reagents, found by the Lodge with member names), markers from Classic world coordinates on the right zone map (most specific zone, instance floors, instances without a map), NPC / object / zone / instance / quest / spell / skill / faction pages with every layer, without Classic, without a Lodge, with game data off and with nothing at all, the Classic baseline card with a fake downloader (size question, progress, cancel, failure and retry, ready with the dump date, remove, credit and licence links), links and history, the page in the window (keyboard search, zone picker, folded rows, virtualised lists, narrow layout, never blocking the UI, stale answers dropped), screenshots",
            ["atlasbrowse"] = "Atlas browse by category: the category registry (built-in list categories, a hosted category with an id prefix and a sub-path, replace / unregister), browse ids (parse, canonical text, history replace), every category listing with rows, the item sub-categories from the client's class names, name / level / quality / slot / data source filters, how to get it (drops, vendor, quest, crafted, fishing, containers, gathered), usable by a character (class proficiency and level), the sorts, creatures / objects / quests / zones / dungeons / spells / skills / recipes / factions, the start page tiles and presets, history through browse ids, missing layers (no Classic baseline, no Lodge, no game data) with their notes, the page in the window (typing narrows the list without a redraw, back / forward restore the filters), 24 000 items (index off the UI thread, virtualised list), the player's own game files, screenshots",
            ["atlasclasses"] = "Atlas Classes and Races (ids class:<id> / race:<id>, browse:classes / browse:races, search by name and lore name, the palette Pages scope): ChrClasses / ChrRaces / CharBaseInfo / ChrClassesXPowerTypes / SkillRaceClassInfo / SpellLevels / SpellPower / SpellCooldowns on synthetic WDC5 tables (unknown layout = that table or the layer unavailable with a calm note, a missing or cut-off table, the saved index and its build key), the class page (identity, resource, roles, armor and weapons from the skill lines, races, talents link, notes), Spells by level (groups by level, the no-level group, spell-level labels, rank chains, helper and variant spells left out, only new spells, level range, tabs, pet tab, search, talent spells), the Classic trainer merge (labelled, none = a note), the race page (side, languages, mount skills, racial abilities, classes, start-zone note), the spell pages (Learned by / Racial ability of, Cost, Cooldown, game tooltip lines), the page in the window (tabs, filters, clear, links both ways, pin), no class / spell data; optional read-only check of the real game files with screenshots",
            ["atlashosts"] = "Atlas hosted categories Talents and Professions: the tiles on the start page, ids and sub-paths (talents:tree/<class>, talents:build/<code>, professions:<profession>/<tab>, professions:camp), the pages kept alive and the id following the class / profession / tab picked in them (no new history step), breadcrumb Atlas > Talents > Hunter, history item page -> Talents -> Professions and back, Ctrl+6 and ShowTab landing in the Atlas, the rail without the two buttons, the links the entity pages give (crafted by, recipe profession, talent of, skills list, Ctrl+click on a talent), a recipe link inside Professions and Back, the search offering the pages by name, no game data, screenshots",
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
            var lodgeAll = new[] { "armory", "lodge", "lodgefeatures", "lodgehardening", "lodgefun", "invites", "invites2", "linkopen", "calendar", "wowevents", "flightshare", "atlaslodge", "lodgefiles", "badges", "realms", "atlasclassiclodge", "video", "viewer", "toasts", "presence", "profile", "round3" };

            // ---- shared: everything depends on these
            R(@"^src/(App\.xaml(\.cs)?|MainWindow\.xaml(\.cs)?|MainWindow\.Others\.cs|MainWindow\.Welcome\.cs|ElansAddonHub\.csproj|Motion\.cs|Brand\.cs|SourceIcons\.cs|PillBase\.cs|WelcomeGuide\.cs)$", Star);
            R(@"^src/Theme/", Star);
            R(@"^src/(Controls/CardGrid|MainWindow\.(AddonsGrid|AddonsGridTest)|AddonCard)\.cs$", "addonsgrid", "addons", "startup", "selfupdated", "thirdparty");
            R(@"^src/MainWindow\.(InputsTest|DesignTest\.Inputs)\.cs$", "inputs");
            R(@"^src/Services/(Util|SettingsStore|Models|Json|Net|SelfTestFixture|FakeUpdateServer|TestScope|WowLocator|Splash|SplashScene)\.cs$", Star);
            R(@"^src/Assets/", Star);
            // ---- inventory
            R(@"^src/(Services/(Maps/|Casc\.cs|Blp\.cs|Db2/Wdc5\.cs)|Controls/AtlasMap\.cs|MainWindow\.MapDataTest\.cs)", "mapdata", "worldmap", "instances");
            R(@"^src/(Services/WorldMap/|Controls/WorldMapView\.cs|WorldMap/|MainWindow\.WorldMapTest(\.\w+)?\.cs)", "worldmap", "instances", "atlashosts", "atlaspage");
            R(@"^src/(Services/Atlas/AtlasInstances\.cs|AtlasPage\.Entrance\.cs|MainWindow\.InstancesTest(\.\w+)?\.cs)$", "instances", "atlaspage", "atlasbrowse");
            R(@"^src/Services/(InventoryData|IconStore|IconSheet|Blp|Casc|CharPortrait|LuaData|ZoneNames)\.cs$", "inventory");
            R(@"^src/(Services/(Db2/|Casc\.cs)|MainWindow\.ItemDataTest\.cs)", "itemdata");
            R(@"^src/(Services/(Db2/Wdb|Atlas/(SeenData|AtlasObs))|MainWindow\.SeenDataTest)\w*\.cs$", "seendata");
            R(@"^src/InventoryPage", "inventory", "characters", "upgrades", "armory", "itemdata", "spelldata");
            R(@"^src/(MainWindow\.ArmoryTest|SettingsPage\.Armory|Lodge/(ArmoryModels|LodgeSession\.Armory))\.cs$", "armory");
            R(@"^src/(Services/(Db2/|Casc\.cs)|MainWindow\.ItemDataTest\.cs)", "itemdata", "spelldata", "atlasclasses");
            R(@"^src/(Services/(Db2/Class\w*|Atlas/(AtlasClasses|AtlasQuery\.Classes|AtlasBrowse\.Classes))|AtlasPage\.Classes|MainWindow\.AtlasClassesTest(\.\w+)?)\.cs$", "atlasclasses", "atlaspage", "atlasbrowse", "gametooltip");
            R(@"^src/MainWindow\.SpellDataTest\.cs$", "spelldata");
            R(@"^src/(Controls/GameTooltip(\.Atlas)?\.cs|Services/(GameFont|Atlas/AtlasTip)\.cs|MainWindow\.GameTooltipTest\.cs)$", "gametooltip", "itemdata", "spelldata", "inventory", "atlaspage", "professions", "lodgefun", "talents", "atlasclasses");
            R(@"^src/(Services/(Talents/|Db2/(Talent|Wdc5|Spell)|Casc\.cs|HubModules\.cs)|TalentsPage|MainWindow\.TalentsTest)", "talents", "atlashosts");
            R(@"^src/(ProfessionsPage[\w.]*|MainWindow\.ProfessionsTest[\w.]*|Services/Professions/.*)$", "professions", "profguide", "atlashosts");
            R(@"^src/(MainWindow\.ProfGuideTest\.cs|Services/(SharedDataFile|Atlas/(RouteBuilder|GatherNodes|AtlasQuery\.Nodes))\.cs|AtlasPage\.Routes\.cs|ProfessionsPage\.Follow\.cs)$", "profguide", "flightshare", "atlaspage", "atlaslodge");
            R(@"^src/(MainWindow\.HubDataTest|Services/(HubData|SavedVarsFiles|LuaData))\.cs$", "hubdata", "characters");
            R(@"^src/(MainWindow\.ReadersTest|Services/(SavedVarsFiles|GamePresence|InventoryData|WowCalendar|FlightShare|AddonHealth|HealthMonitor|HubData|LuaData))\.cs$", "readers");
            R(@"^src/(MainWindow.AtlasClassicTest.cs|Services/Atlas/Classic/)", "atlasclassic", "atlasclassiclodge", "atlaspage");
            R(@"^src/(MainWindow\.AtlasClassicLodgeTest\.cs|Services/Atlas/ClassicLodge\.cs|SettingsPage\.Classic\.cs)$", "atlasclassiclodge", "atlaspage");
            R(@"^src/(AtlasPage(\.\w+)?\.(xaml|cs)|MainWindow\.(AtlasPageTest(\.\w+)?|Atlas)\.cs|Services/Atlas/(AtlasModel|AtlasQuery(\.\w+)?|AtlasDerived|ClassicSetup)\.cs)$", "atlaspage", "atlasbrowse", "atlashosts", "atlasclassiclodge", "atlasclasses", "upgrades");
            R(@"^src/(MainWindow\.AtlasBrowseTest\.cs|Services/Atlas/(AtlasBrowse(\.\w+)?|AtlasCategories)\.cs)$", "atlasbrowse", "atlaspage", "atlasclasses");
            R(@"^src/(MainWindow\.AtlasHostsTest\.cs|Services/Atlas/AtlasHosts\.cs)$", "atlashosts", "atlaspage");
            R(@"^src/(Services/(Maps/|Db2/|Atlas/(SeenData|AtlasObs))|Controls/AtlasMap|Lodge/LodgeSession\.Atlas)", "atlaspage", "atlasbrowse");   // the layers the page combines
            R(@"^src/(MainWindow\.CharactersTest|Services/(HubModules|CharacterMerge))\.cs$", "characters", "upgrades", "inventory");
            R(@"^src/Services/Db2/", "upgrades");
            R(@"^src/(Services/Upgrades/|InventoryPage\.Upgrades|MainWindow\.UpgradesTest(\.\w+)?\.cs$|Services/Atlas/AtlasQuery\.Upgrades)", "upgrades", "characters", "atlaspage");
            R(@"^src/Services/Blp\.cs$", "selfupdated");
            R(@"^src/MainWindow\.(InventoryTest|DesignTest\.Inventory)\.cs$", "inventory");
            // ---- lodge: chat, voice, files, profiles, toasts, presence, viewer
            R(@"^src/Lodge/", lodgeAll);
            R(@"^src/Services/(LodgeClient|AvatarArt|GamePresence|ItemLinks)\.cs$", lodgeAll);
            R(@"^src/Services/(VoiceEngine|VoiceEffects)\.cs$", "voice", "lodge");
            R(@"^src/Services/Sounds\.cs$", "toasts", "lodge", "voice");
            R(@"^src/SettingsPage", "addons", "profile", "round3", "voice", "lodge", "browser", "invites", "invites2", "linkopen", "calendar", "wowevents", "flightshare", "atlaslodge", "lodgefiles", "badges", "realms", "armory", "atlasclassiclodge");
            R(@"^src/MainWindow\.ProfileTest\.cs$", "profile");
            R(@"^src/MainWindow\.VideoTest\.cs$", "video");
            R(@"^src/(BrowserPage(\.xaml(\.cs)?)?|MainWindow\.BrowserTest|Services/(WebEngine|BrowserUrl|AdBlock))(\.cs)?$", "browser", "video");
            R(@"^src/MainWindow\.PresenceTest\.cs$", "presence");
            R(@"^src/MainWindow\.HardeningTest\.cs$", "lodgehardening");
            R(@"^src/MainWindow\.LodgeFunTest\.cs$", "lodgefun");
            R(@"^src/MainWindow\.InvitesTest\.cs$", "invites");
            R(@"^src/MainWindow\.Invites2Test\.cs$", "invites2");
            R(@"^src/MainWindow\.DesignTest\.Invites2\.cs$");   // screenshot-only design run
            R(@"^src/(MainWindow\.LinkOpen(Test)?|Services/LinkOpen)\.cs$", "linkopen", "invites");
            R(@"^src/MainWindow\.DesignTest\.LinkOpen\.cs$");   // screenshot-only design run
            R(@"^src/MainWindow\.CalendarTest\.cs$", "calendar");
            R(@"^src/MainWindow\.WowEventsTest\.cs$", "wowevents");
            R(@"^src/Services/WowCalendar\.cs$", "wowevents", "calendar", "lodge");
            R(@"^src/(MainWindow\.FlightShareTest|Services/FlightShare)\.cs$", "flightshare", "lodge");
            R(@"^src/(MainWindow\.Atlas(Lodge|Notes)Test|Lodge/LodgeSession\.Atlas|Services/Atlas/(AtlasObs(\.Notes)?|AtlasQuery\.Notes))\.cs$", "atlaslodge", "lodge");
            R(@"^src/Services/HubData\.cs$", "atlaslodge");
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
            R(@"^src/(MainWindow\.SelfUpdatedTest|Services/(AddonLogo|DevVersion))\.cs$", "selfupdated", "thirdparty");
            R(@"^src/MainWindow\.DesignTest\.SelfUpdated\.cs$");   // screenshot-only design run
            R(@"^src/(MainWindow\.Startup(ShiftTest(\.Checks)?)?\.cs|Skeleton\.cs)$", "startup", "addons");
            R(@"^src/Services/(AddonScanner|AddonSuggest|AddonSources|CurseForgeLocal|GitHubStats)\.cs$", "thirdparty", "addons", "selfupdated");
            R(@"^src/Services/(Installer|SelfUpdater|PathSafety)\.cs$", "update", "addons");
            R(@"^src/Services/UpdateChannel\.cs$", "update", "round3");
            R(@"^addon/", "addons");
            // ---- health, dialogs, strip
            R(@"^src/(HealthDialog\.cs|MainWindow\.Health(Test)?\.cs)$", "health");
            R(@"^src/Services/(AddonHealth|HealthMonitor|WowWatch)\.cs$", "health", "round3");
            R(@"^src/MainWindow\.Round3Test\.cs$", "round3");
            R(@"^src/ThemedDialog", "dialogs", "invites2");
            R(@"^src/Services/(StripSelfTest|PixelStrip)\.cs$", "strip");
            // ---- not exercised by the self-test
            R(@"^(docs/|README\.md$|DESIGN\.md$|\.gitignore$|\.gitattributes$|src/CHANGELOG\.md$|Tools/)");
            return r;
        }

        // sections affected by the changed files, with the reason per file; unknown/shared files make it ALL
        public static List<string> FromPaths(IEnumerable<string> files, out List<string> why)
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
                if (hit.Count == 0) { why.Add(f + " -> ALL (unknown file)"); set.UnionWith(All); continue; }
                var secs = new HashSet<string>(hit.SelectMany(x => x.Item2));
                if (secs.Contains(Star)) { why.Add(f + " -> ALL (shared)"); set.UnionWith(All); continue; }
                why.Add(f + " -> " + (secs.Count == 0 ? "nothing (not self-tested)" : string.Join(",", Order(secs))));
                set.UnionWith(secs);
            }
            if (!any) why.Add("no changed files");
            return Closure(set);
        }

        static IEnumerable<string> Order(IEnumerable<string> s) => All.Where(a => s.Contains(a));

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
            if (only == null && changed == null) return All.ToList();
            var picked = new HashSet<string>();
            var sb = new StringBuilder();
            if (only != null)
            {
                var s = ParseOnly(only, out var unknown);
                if (unknown.Count > 0) { error = "unknown section(s): " + string.Join(", ", unknown) + " (valid: " + string.Join(", ", All) + ")"; return new List<string>(); }
                picked.UnionWith(s);
                sb.AppendLine("scope --only " + only + " -> " + string.Join(",", s));
            }
            if (changed != null)
            {
                var files = GitChanged(changed, ArgValue(args, "--repo") ?? Directory.GetCurrentDirectory(), out var gitErr);
                if (files == null) { picked.UnionWith(All); sb.AppendLine("scope --changed " + changed + " -> ALL (git failed: " + gitErr + ")"); }
                else
                {
                    var s = FromPaths(files, out var why);
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
            return all.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
        }

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
            Ck("inventory files -> the Characters page sections only (characters, inventory)", Same(Map("src/InventoryPage.xaml.cs", "src/Services/InventoryData.cs", "src/Services/IconStore.cs"), "readers", "characters", "upgrades", "armory", "itemdata", "spelldata", "inventory"));
            Ck("backslash paths are normalised", Same(Map("src\\InventoryPage.Bags.cs"), "characters", "upgrades", "armory", "itemdata", "spelldata", "inventory"));
            var lodge = Map("src/Lodge/LodgeView.xaml.cs");
            Ck("Lodge files -> lodge, viewer, toasts, presence (not inventory)", new[] { "lodge", "viewer", "toasts", "presence" }.All(lodge.Contains) && !lodge.Contains("inventory"));
            Ck("Theme and Motion -> ALL", Map("src/Theme/Theme.xaml").Count == All.Length && Map("src/Motion.cs").Count == All.Length);
            Ck("shared files -> ALL", new[] { "src/App.xaml.cs", "src/MainWindow.xaml.cs", "src/ElansAddonHub.csproj", "src/Services/Util.cs", "src/Services/SettingsStore.cs" }.All(f => Map(f).Count == All.Length));
            Ck("unknown file -> ALL", Map("src/Brand_new_file.cs").Count == All.Length && Map("somewhere/else.bin").Count == All.Length);
            Ck("one shared file among specific ones -> ALL", Map("src/InventoryPage.xaml", "src/Motion.cs").Count == All.Length);
            Ck("docs only -> nothing", Map("README.md", "docs/design/x.png", "src/CHANGELOG.md").Count == 0);
            Ck("no changes -> nothing", Map().Count == 0);
            Ck("results are in run order", Map("src/InventoryPage.xaml", "src/HealthDialog.cs", "src/ThemedDialog.xaml").SequenceEqual(All.Where(a => a == "dialogs" || a == "invites2" || a == "characters" || a == "upgrades" || a == "armory" || a == "itemdata" || a == "spelldata" || a == "inventory" || a == "health")));
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
