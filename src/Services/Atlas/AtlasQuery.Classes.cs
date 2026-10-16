using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // The class and race pages of the Atlas (ids class:<id> and race:<id>), their search and their one-line descriptions. Everything is the player's own game data
    // (ChrClasses, ChrRaces, CharBaseInfo, SkillRaceClassInfo ... see ClassTables); what the client does not hold is said on the page, never filled in.
    public static partial class AtlasQuery
    {
        // ---------------------------------------------------------------------------------------------------------------- search / describe
        static List<AtlasHit> SearchClasses(string q)
        {
            var res = new List<AtlasHit>();
            var snap = ClassData.Data;
            if (snap == null) return res;
            foreach (var c in snap.ClassList())
            {
                int r = MatchRank(c.Name, q);
                if (r < 0) r = MatchRank(c.File, q);
                if (r < 0) continue;
                res.Add(new AtlasHit { Id = AtlasIds.Class(c.Id), Kind = "class", Name = c.Name, IconId = c.IconId, Detail = ClassDetail(c), Label = AtlasLabel.Forever, Rank = r, Tier = 0 });
            }
            return res;
        }

        static List<AtlasHit> SearchRaces(string q)
        {
            var res = new List<AtlasHit>();
            var snap = ClassData.Data;
            if (snap == null) return res;
            foreach (var r0 in snap.PlayableRaces())
            {
                int r = MatchRank(r0.Name, q);
                if (r < 0 && r0.LoreName.Length > 0) r = MatchRank(r0.LoreName, q);
                if (r < 0) continue;
                res.Add(new AtlasHit { Id = AtlasIds.Race(r0.Id), Kind = "race", Name = r0.Name, IconId = RaceIcons.For(r0), Detail = RaceDetail(r0), Label = AtlasLabel.Forever, Rank = r, Tier = 0 });
            }
            return res;
        }

        static string ClassDetail(ClassInfo c) => string.Join(" · ", new[] { c.RoleText, c.PowerTypes.Length > 0 ? ClassSnapshot.PowerName(c.PowerTypes[0]) : "" }.Where(s => !string.IsNullOrEmpty(s)));
        static string RaceDetail(RaceInfo r) => string.Join(" · ", new[] { r.SideName, r.LoreName.Length > 0 && !string.Equals(r.LoreName, r.Name, StringComparison.OrdinalIgnoreCase) ? r.LoreName : "" }.Where(s => !string.IsNullOrEmpty(s)));

        static AtlasHit DescribeClass(int id)
        {
            var c = ClassData.Data?.Class(id);
            return c == null ? null : new AtlasHit { Id = AtlasIds.Class(id), Kind = "class", Name = c.Name, IconId = c.IconId, Detail = ClassDetail(c), Label = AtlasLabel.Forever };
        }

        static AtlasHit DescribeRace(int id)
        {
            var r = ClassData.Data?.Race(id);
            return r == null || !r.Playable ? null : new AtlasHit { Id = AtlasIds.Race(id), Kind = "race", Name = r.Name, IconId = RaceIcons.For(r), Detail = RaceDetail(r), Label = AtlasLabel.Forever };
        }

        static string UnavailableClassText() => ClassData.Status == ClassData.State.Disabled ? "Game data is switched off (Settings > General), so there are no classes or races."
            : ClassData.Status == ClassData.State.Unavailable ? "Class and race data could not be read from your game files" + (string.IsNullOrEmpty(ClassData.Reason) ? "." : " (" + ClassData.Reason + ").")
            : "Classes and races are read from your game files and are not ready yet.";

        static string TalentsLink(ClassInfo c)
        {
            var cat = AtlasCategories.Find(AtlasHosts.Talents);
            return cat != null && cat.Hosted && !string.IsNullOrEmpty(c.File) ? AtlasHosts.TalentsId(c.File) : null;
        }

        // the armor / weapon skill lines a class can use as rows (a link to the skill page, "from level 40" where the client says so)
        static void ProficiencySection(AtlasPageModel m, ClassSnapshot snap, int cls, int category, SpellSnapshot spells, string key, string title)
        {
            Func<int, int> catOf = id => spells.Skills.TryGetValue(id, out var s) ? s.Category : 0;
            var prof = snap.Proficiencies(cls, category, catOf);
            var rows = new List<AtlasRow>();
            foreach (var kv in prof.OrderBy(k => k.Value).ThenBy(k => spells.Skills.TryGetValue(k.Key, out var sk) ? sk.Name : "", StringComparer.OrdinalIgnoreCase))
            {
                if (!spells.Skills.TryGetValue(kv.Key, out var skill) || string.IsNullOrEmpty(skill.Name)) continue;
                rows.Add(new AtlasRow { Key = AtlasIds.Skill(kv.Key), Link = AtlasIds.Skill(kv.Key), Title = skill.Name, IconId = skill.IconId, Detail = kv.Value > 1 ? "From level " + kv.Value : "", Label = AtlasLabel.Forever, Tier = 1, Type = "Skill" });
            }
            if (rows.Count == 0) return;
            var s0 = Section(m, key, title);
            s0.Note = "From the class's skill lines in your game files.";
            s0.Rows.AddRange(rows);
        }

        // ---------------------------------------------------------------------------------------------------------------- the class page
        static AtlasPageModel BuildClass(int id)
        {
            var snap = ClassData.Data;
            var c = snap?.Class(id);
            if (c == null) return NotFoundPage("class", id, snap == null ? UnavailableClassText() : "Your game has no class with this id.");
            string F = AtlasLabel.Forever;
            var m = new AtlasPageModel { Id = AtlasIds.Class(id), Kind = "class", Title = c.Name, IconId = c.IconId, Accent = c.HasColor ? c.ColorHex : "" };
            AddLayer(m, "client");
            m.Subtitle = "Class " + id + (c.File.Length > 0 ? " · " + c.File : "");
            if (c.RoleText.Length > 0) m.Facts.Add(new AtlasFact("Roles", c.RoleText, F));
            if (c.PowerTypes.Length > 0) m.Facts.Add(new AtlasFact("Resource", string.Join(", ", c.PowerTypes.Select(ClassSnapshot.PowerName)), F));
            else m.Facts.Add(new AtlasFact("Resource", ClassSnapshot.PowerName(c.DisplayPower), F));
            if (c.StartingLevel > 0) m.Facts.Add(new AtlasFact("Starts at level", c.StartingLevel.ToString(CultureInfo.InvariantCulture), F));
            var spells = SpellData.Spells;
            if (spells != null && snap.HasSkillRows)
            {
            }
            var tl = TalentsLink(c);
            if (tl != null) m.Facts.Add(new AtlasFact("Talents", "Talent calculator and recommended builds", F, tl));
            if (!string.IsNullOrWhiteSpace(c.Description)) m.Texts.Add(new AtlasText("About", c.Description, F));

            // races that can be this class
            var races = snap.RacesOf(id);
            if (races.Count > 0)
            {
                var s = Section(m, "races", "Races");
                s.Note = AtlasFormat.Count(races.Count, "race", "races") + " can be a " + c.Name + " (the client's race and class list).";
                foreach (var r in races)
                    s.Rows.Add(new AtlasRow { Key = AtlasIds.Race(r.Id), Link = AtlasIds.Race(r.Id), Title = r.Name, IconId = RaceIcons.For(r), Detail = r.SideName, Label = F, Tier = 1, Type = "Race" });
            }

            if (spells != null && snap.HasSkillRows)
            {
                ProficiencySection(m, snap, id, ClassSnapshot.CatArmor, spells, "armor", "Armor");
                ProficiencySection(m, snap, id, ClassSnapshot.CatWeapon, spells, "weapons", "Weapons and combat skills");
            }

            // the spellbook: a control of its own on the page
            if (SpellData.Spells == null) m.Notes.Add(SpellData.Status == SpellData.State.Disabled ? "Game data is switched off (Settings > General), so the spells are not listed." : "Spell data is read from your game files and is not ready yet, so the spells are not listed.");
            else
            {
                var book = ClassSpellBook.Build(id);
                if (book != null)
                {
                    m.Custom = book;
                    foreach (var n in book.Notes) m.Notes.Add(n);
                    if (!book.HasTrainerData) m.Notes.Add("What a spell costs to learn from a trainer is not in your game files, and the Classic baseline in this version carries no trainer table, so no learning costs are shown.");
                }
            }
            return m;
        }

        // ---------------------------------------------------------------------------------------------------------------- the race page
        static AtlasPageModel BuildRace(int id)
        {
            var snap = ClassData.Data;
            var r = snap?.Race(id);
            if (r == null || !r.Playable) return NotFoundPage("race", id, snap == null ? UnavailableClassText() : r == null ? "Your game has no race with this id." : "This race cannot be created as a player character in your game.");
            string F = AtlasLabel.Forever;
            var m = new AtlasPageModel { Id = AtlasIds.Race(id), Kind = "race", Title = r.Name, IconId = RaceIcons.For(r) };
            AddLayer(m, "client");
            m.Subtitle = "Race " + id + " · " + r.SideName;
            m.Facts.Add(new AtlasFact("Side", r.SideName, F));
            if (r.LoreName.Length > 0 && !string.Equals(r.LoreName, r.Name, StringComparison.OrdinalIgnoreCase)) m.Facts.Add(new AtlasFact("Also called", r.LoreName, F));
            if (r.StartingLevel > 0) m.Facts.Add(new AtlasFact("Starts at level", r.StartingLevel.ToString(CultureInfo.InvariantCulture), F));
            var spells = SpellData.Spells;
            if (spells != null && snap.HasSkillRows)
            {
                Func<int, int> catOf = x => spells.Skills.TryGetValue(x, out var sk) ? sk.Category : 0;
                var lang = snap.RaceRows(r, catOf, ClassSnapshot.CatLanguage).Where(x => x.Availability == 1).Select(x => x.Skill).Distinct().ToList();
                foreach (var l in lang)
                    if (spells.Skills.TryGetValue(l, out var ls)) m.Facts.Add(new AtlasFact("Language", ls.Name.Replace("Language: ", ""), F, AtlasIds.Skill(l)));
                // mount skills: the riding skill line that names exactly this race
                var riding = snap.RaceRows(r, catOf, ClassSnapshot.CatSecondary).Where(x => x.Races == r.BitMask).Select(x => x.Skill).Distinct()
                    .Where(x => spells.Skills.TryGetValue(x, out var sk) && (sk.Name.IndexOf("Riding", StringComparison.OrdinalIgnoreCase) >= 0 || sk.Name.IndexOf("Horsemanship", StringComparison.OrdinalIgnoreCase) >= 0 || sk.Name.IndexOf("Piloting", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                foreach (var rs in riding) m.Facts.Add(new AtlasFact("Mount skill", spells.Skills[rs].Name, F, AtlasIds.Skill(rs)));
            }
            if (!string.IsNullOrWhiteSpace(r.Description)) m.Texts.Add(new AtlasText("About", r.Description, F));

            // the classes this race can be
            var classes = snap.ClassesOf(id);
            if (classes.Count > 0)
            {
                var s = Section(m, "classes", "Classes");
                s.Note = AtlasFormat.Count(classes.Count, "class", "classes") + " can be " + (StartsWithVowel(r.Name) ? "an " : "a ") + r.Name + " (the client's race and class list).";
                foreach (var c in classes)
                    s.Rows.Add(new AtlasRow { Key = AtlasIds.Class(c.Id), Link = AtlasIds.Class(c.Id), Title = c.Name, IconId = c.IconId, Detail = ClassDetail(c), Label = F, Tier = 1, Type = "Class" });
            }

            // racial abilities: the spells of the race's racial skill lines
            if (spells != null && snap.HasSkillRows)
            {
                var racial = SpellOrigins.RacialSkills(snap, spells, r);
                var s = Section(m, "racial", "Racial abilities", "The game data holds no racial abilities for this race.");
                var seen = new HashSet<int>();
                foreach (var skill in racial)
                    foreach (var a in spells.AbilitiesOf(skill))
                    {
                        var sp = spells.Get(a.Spell);
                        if (sp == null || string.IsNullOrEmpty(sp.Name) || !seen.Add(a.Spell)) continue;
                        var view = SpellData.Spell(a.Spell);
                        var desc = view == null ? "" : (!string.IsNullOrWhiteSpace(view.Description) ? view.Description : view.AuraDescription ?? "");
                        s.Rows.Add(new AtlasRow { Key = AtlasIds.Spell(a.Spell), Link = AtlasIds.Spell(a.Spell), Title = sp.Name, IconId = sp.IconId, Detail = sp.Rank ?? "", Extra = desc.Length > 160 ? desc.Substring(0, 157).TrimEnd() + "…" : desc, Label = F, Tier = 1, Type = "Spell" });
                    }
                if (s.Rows.Count > 0) s.Note = AtlasFormat.Count(s.Rows.Count, "ability", "abilities") + " from " + string.Join(", ", racial.Where(x => spells.Skills.ContainsKey(x)).Select(x => spells.Skills[x].Name)) + ".";
            }
            else m.Notes.Add(SpellData.Spells == null ? "Spell data is read from your game files and is not ready yet, so the racial abilities are not listed." : "The skill lines of the races are not in the game data this Outpost can read, so the racial abilities are not listed.");
            AddStartZone(m, r);
            AddStartFromLore(m, r);
            return m;
        }

        // Where a race starts. Neither the client nor the Classic baseline stores start positions, but quests carry the races that may take them. Most
        // first quests are open to a whole faction; each starting area also has a few for its own races only (the "Welcome!" quests: Shadowglen for Night
        // Elves, Coldridge Valley for Dwarves and Gnomes, the Valley of Trials for Orcs and Trolls ...). So among the race's first quests (up to its starting
        // level + 1) the ones with the NARROWEST race mask decide, and their most common area is the start. Two sources of quests: the Classic baseline,
        // and the quests Forever itself described to this PC or to a Lodge member (SeenView, with Forever's race masks - the only source for Forever's own
        // races such as the Skyborne). The baseline's answer is shown when it has one; Forever's when the baseline has none.
        static void AddStartZone(AtlasPageModel m, RaceInfo r)
        {
            var ci = ClassicAtlas.Current;
            var idx = MapData.Index;
            if (idx == null || r.BitMask == 0) { m.Notes.Add("The game's zone tables are not read yet, so the starting zone is not shown."); return; }
            int start = Math.Max(1, r.StartingLevel);
            int quests = 0, seenQuests = 0;
            int area = ci == null ? 0 : StartArea(ci.Quests.Values, r.BitMask, start, out quests);
            int seenArea = StartAreaSeen(SeenView.AllQuests(), r.BitMask, start, out seenQuests);
            bool fromSeen = area == 0 && seenArea != 0;
            if (fromSeen) { area = seenArea; quests = seenQuests; }
            if (area == 0 && AddStartFromCharacters(m, r, idx, start)) return;
            if (area == 0)
            {
                m.Notes.Add(ci == null
                    ? "The starting zone is worked out from the first quests meant for this race: the Classic baseline is not loaded, and neither your game nor your Lodge's members have been shown such a quest yet, so it is not shown."
                    : "Neither the Classic baseline nor the quests your game and your Lodge's members have been shown have first quests for this race yet, so its starting zone is not shown.");
                return;
            }
            int ui = idx.UiMapForArea(area);
            string zone = ui > 0 ? ZoneName(ui) : null, place = idx.AreaName(area);
            string text = !string.IsNullOrEmpty(place) && !string.IsNullOrEmpty(zone) && !string.Equals(place, zone, StringComparison.OrdinalIgnoreCase) ? place + ", " + zone : zone ?? place;
            if (string.IsNullOrEmpty(text)) { m.Notes.Add("The area of this race's first quests is not in your game's zone tables, so the starting zone is not shown."); return; }
            m.Facts.Add(new AtlasFact("Starts in", text, fromSeen ? AtlasLabel.Forever : AtlasLabel.Classic, ui > 0 ? "zone:" + ui.ToString(CultureInfo.InvariantCulture) : null));
            m.Notes.Add("Starting zone: where the first quests meant for this race only are given (" + AtlasFormat.Count(quests, "quest", "quests") + " up to level " + (start + 1)
                + (fromSeen ? " that Forever described to your game or a Lodge member's)." : " in the Classic baseline)."));
            if (!fromSeen && seenArea != 0 && seenArea != area)
            {
                int sui = idx.UiMapForArea(seenArea);
                var sz = sui > 0 ? ZoneName(sui) : idx.AreaName(seenArea);
                if (!string.IsNullOrEmpty(sz)) m.Notes.Add("Forever's own quests for this race point at " + sz + " instead (" + AtlasFormat.Count(seenQuests, "quest", "quests") + ").");
            }
        }

        // the same rule on quests Forever described (WdbQuest.Races are ChrRaces.PlayableRaceBit bits; a quest open to every race says nothing)
        public static int StartAreaSeen(IEnumerable<WdbQuest> all, ulong raceMask, int startLevel, out int quests)
        {
            int Pop(ulong v) { int n = 0; while (v != 0) { v &= v - 1; n++; } return n; }
            var first = new List<KeyValuePair<int, int>>();
            foreach (var q in all ?? Enumerable.Empty<WdbQuest>())
            {
                if (q == null || !q.Full || !q.RaceLimited || q.Sort <= 0 || (q.Races & raceMask) == 0) continue;
                if (q.MinLevel > startLevel || q.Level > startLevel + 1) continue;
                first.Add(new KeyValuePair<int, int>(q.Sort, Pop(q.Races)));
            }
            quests = 0;
            if (first.Count == 0) return 0;
            int narrowest = first.Min(x => x.Value);
            var best = first.Where(x => x.Value == narrowest).GroupBy(x => x.Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First();
            quests = best.Count();
            return best.Key;
        }

        // the area id of a race's first quests (0 = none): the quests open to it up to startLevel + 1, narrowed to those with the fewest races
        // in their mask, then their most common area; `quests` = how many of those are in that area
        public static int StartArea(IEnumerable<ClassicQuest> all, ulong raceMask, int startLevel, out int quests)
        {
            int Pop(ulong v) { int n = 0; while (v != 0) { v &= v - 1; n++; } return n; }
            var first = new List<KeyValuePair<int, int>>();   // area, races in the mask
            foreach (var q in all)
            {
                ulong mask = (ulong)(uint)q.RequiredRaces;
                if (q.ZoneOrSort <= 0 || mask == 0 || (mask & raceMask) == 0) continue;
                if (q.MinLevel > startLevel || q.QuestLevel > startLevel + 1) continue;
                first.Add(new KeyValuePair<int, int>(q.ZoneOrSort, Pop(mask)));
            }
            quests = 0;
            if (first.Count == 0) return 0;
            int narrowest = first.Min(x => x.Value);
            var best = first.Where(x => x.Value == narrowest).GroupBy(x => x.Key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First();
            quests = best.Count();
            return best.Key;
        }

        // ---- the second source: a character of this race seen at its first levels stands where the race starts (the Hub saves race, level, zone
        // and subzone of every character). Forever's newer races (Skyborne) have first quests open to every race, so the quest rule cannot
        // see them; their characters can. A start once seen is remembered (cache/racestarts.txt), so it stays after the character levels up.
        public static Func<IEnumerable<HubCore>> StartCharsSource = () =>
        {
            var root = MapData.WowRoot();
            if (string.IsNullOrEmpty(root)) return Enumerable.Empty<HubCore>();
            return HubData.Read(root).Characters.Select(c => c.Core).Where(c => c != null);
        };
        public static string RaceStartsFile = System.IO.Path.Combine(Util.DataDir, "cache", "racestarts.txt");
        static readonly object startGate = new object();

        internal static bool AddStartFromCharacters(AtlasPageModel m, RaceInfo r, MapIndex idx, int start)
        {
            string zone = null, sub = null;
            try
            {
                var seen = StartCharsSource()
                    .Where(c => c.Level >= 1 && c.Level <= start + 1 && !string.IsNullOrWhiteSpace(c.Zone) && string.Equals((c.Race ?? "").Trim(), r.Name, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(c => c.Zone.Trim() + "|" + (c.Subzone ?? "").Trim()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
                if (seen != null) { var parts = seen.Key.Split('|'); zone = parts[0]; sub = parts[1]; RememberStart(r.Id, zone, sub); }
            }
            catch (Exception e) { Util.Log("race start from characters: " + e.Message); }
            if (zone == null && !RecallStart(r.Id, out zone, out sub)) return false;
            var ui = idx.UiMaps.FirstOrDefault(u => string.Equals(u.Name, zone, StringComparison.OrdinalIgnoreCase));
            string text = !string.IsNullOrEmpty(sub) && !string.Equals(sub, zone, StringComparison.OrdinalIgnoreCase) ? sub + ", " + zone : zone;
            m.Facts.Add(new AtlasFact("Starts in", text, AtlasLabel.Mine, ui != null ? "zone:" + ui.Id.ToString(CultureInfo.InvariantCulture) : null));
            m.Notes.Add("Starting zone: where your character of this race was at level " + (start == 1 ? "1-2" : start + "-" + (start + 1)) + " (its first quests are open to every race, so the quests cannot tell).");
            return true;
        }

        static void RememberStart(int race, string zone, string sub)
        {
            lock (startGate)
            {
                try
                {
                    var lines = System.IO.File.Exists(RaceStartsFile) ? System.IO.File.ReadAllLines(RaceStartsFile).ToList() : new List<string>();
                    string line = race.ToString(CultureInfo.InvariantCulture) + "|" + zone.Replace("|", " ") + "|" + (sub ?? "").Replace("|", " ");
                    if (lines.Contains(line)) return;
                    lines.RemoveAll(l => l.StartsWith(race.ToString(CultureInfo.InvariantCulture) + "|", StringComparison.Ordinal));
                    lines.Add(line);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RaceStartsFile));
                    System.IO.File.WriteAllLines(RaceStartsFile, lines);
                }
                catch (Exception e) { Util.Log("race starts file: " + e.Message); }
            }
        }

        static bool RecallStart(int race, out string zone, out string sub)
        {
            zone = sub = null;
            lock (startGate)
            {
                try
                {
                    if (!System.IO.File.Exists(RaceStartsFile)) return false;
                    foreach (var l in System.IO.File.ReadAllLines(RaceStartsFile))
                    {
                        var p = l.Split('|');
                        if (p.Length >= 3 && p[0] == race.ToString(CultureInfo.InvariantCulture) && p[1].Length > 0) { zone = p[1]; sub = p[2]; return true; }
                    }
                }
                catch (Exception e) { Util.Log("race starts file: " + e.Message); }
            }
            return false;
        }

        static bool StartsWithVowel(string s) => !string.IsNullOrEmpty(s) && "AEIOUaeiou".IndexOf(s[0]) >= 0;

        // "Learned by: Hunter, level 20 (rank 3)" and the racial ability of: the facts a spell page gets from the class layer, linking back to the class / race page
        static void AddSpellOrigins(AtlasPageModel m, int spellId)
        {
            var origins = SpellOrigins.Of(spellId);
            if (origins.Count == 0) return;
            string F = AtlasLabel.Forever;
            foreach (var o in origins.OrderBy(x => x.Racial ? 1 : 0).ThenBy(x => x.ClassId).ThenBy(x => x.RaceId))
            {
                if (o.Racial) m.Facts.Add(new AtlasFact("Racial ability of", ClassData.Data?.Race(o.RaceId)?.Name ?? "Race " + o.RaceId, F, AtlasIds.Race(o.RaceId)));
                else m.Facts.Add(new AtlasFact("Learned by", SpellOrigins.Describe(o), F, AtlasIds.Class(o.ClassId)));
            }
        }
    }
}
