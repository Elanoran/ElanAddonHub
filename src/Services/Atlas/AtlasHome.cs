using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;
using ElansAddonHub.Services.Upgrades;

namespace ElansAddonHub.Services.Atlas
{
    // --selftest "atlashome". Atlas Home (Outpost 2.74): where you are and what fits you there. The page (AtlasPage.Home.cs) only draws what this works out:
    // the character and the zone (live from the Hub status strip, else the last saved character, "last seen"), the creature-level fit, the status chips,
    // the cards for the character (quests, dungeons, rares, upgrades, recipes) and the Lodge's day (who is online, finds today, the next event and raid
    // reset, the members in the same place). Everything here is plain data: no WPF, any thread.

    // what the page is told about the game right now (the main window fills it from GamePresence; a test hands it in)
    public sealed class HomeLive
    {
        public GamePresence.Character Char;     // null = no character on the strip
        public StripCodec.Info Strip;           // the strip's raw read (the uiMap id of a cave the zone table has no name for), may be null
        public bool Playing;                    // WoW is running
    }

    // "you, here": one character in one place
    public sealed class HomeHere
    {
        public string Name = "", Realm = "", ClassFile = "", RaceFile = "", CharKey = "";
        public int Sex, Level;
        public bool Live;                       // from the strip now; false = what the Hub saved last ("last seen")
        public long SeenAt;                     // unix seconds of the saved data (Live = false)
        public int ZoneId;                      // uiMap id of the zone (a cave or a micro map is walked up to its zone), 0 = not known
        public string ZoneName = "";
        public bool InInstance;
        public int InstanceId;                  // the instance's map id (0 = not known)
        public string InstanceName = "";
        public bool HasLive;                    // the strip's second row was read (flags, XP, group)
        public int Flags, GroupSize, XpPercent = -1;
        public bool Rested;
        public bool InCombat => (Flags & 1) != 0;
        public bool Dead => (Flags & 2) != 0;
        public bool Afk => (Flags & 4) != 0;
        public bool Resting => (Flags & 8) != 0;
        public bool Grouped => GroupSize > 1;
        public bool Known => Name.Length > 0;
        public string Place => InInstance && InstanceName.Length > 0 ? InstanceName : ZoneName;
        // what the cards depend on: a change here refreshes them; the chips and the XP bar change without it
        public string CardSig => string.Join("|", Name, Realm, Level, ZoneId, ZoneName, InInstance, InstanceId, InstanceName, Live);
        public string ChipSig => string.Join("|", Flags, GroupSize, XpPercent, Rested, HasLive);
    }

    public enum HomeFit { Unknown, Good, High, TooHigh, Low }

    // one card of the "Recommended for" row
    public sealed class HomeCard
    {
        public string Key = "", Title = "", Note = "", MoreText = "", MoreLink = "";
        public int Total;
        public List<AtlasRow> Rows = new List<AtlasRow>();
    }

    // a Lodge member as Home shows them (only what they share: the server leaves out the zone when they keep it to themselves)
    public sealed class HomeMember
    {
        public string Name = "", CharName = "", ClassFile = "", Place = "", Status = "";
        public int Level;
        public bool Playing, Invisible, IsMe;
        public System.Windows.Media.Brush Color;   // the member's colour (their class), drawn as the avatar's letter
    }

    public sealed class HomeLodge
    {
        public int Online;                                          // members online (appear-offline ones not counted)
        public List<HomeMember> Members = new List<HomeMember>();
        public string NextEventTitle = ""; public DateTime NextEventUtc;
        public string NextResetTitle = ""; public DateTime NextResetUtc;
    }

    public static class AtlasHome
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- the place
        // The zone a map belongs to: the map itself when it is a zone, else the nearest zone above it (a cave, a micro map or a dungeon floor under a zone).
        // 0 = none (a continent, a map the index does not know, an orphan with no zone above it).
        public static int ZoneOf(MapIndex idx, int uiMapId)
        {
            if (idx == null || uiMapId <= 0) return 0;
            var chain = idx.Breadcrumb(uiMapId);
            for (int i = chain.Count - 1; i >= 0; i--) if (chain[i].Type == UiMapInfo.Zone) return chain[i].Id;
            return 0;
        }

        // a zone by its name (the Hub saves names, not ids): the game's own maps first, else the status strip's table
        public static int ZoneIdByName(MapIndex idx, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            if (idx != null)
            {
                var m = idx.UiMaps.Where(u => u.Type == UiMapInfo.Zone && u.System == 0 && string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)).OrderBy(u => u.Id).FirstOrDefault();
                if (m != null) return m.Id;
            }
            foreach (var kv in ZoneNames.Maps) if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return 0;
        }

        static string ZoneNameOf(MapIndex idx, int id) { var n = idx?.Name(id); return !string.IsNullOrEmpty(n) ? n : ZoneNames.Map(id) ?? ""; }

        // the character Home is about: the one playing now (live), else the one the Hub saved last
        public static HomeHere Resolve(HomeLive live, IList<InvChar> chars, MapIndex idx)
        {
            chars = chars ?? new List<InvChar>();
            var c = live != null && live.Playing ? live.Char : null;
            if (c != null && !string.IsNullOrEmpty(c.Name))
            {
                var h = new HomeHere
                {
                    Live = true, Name = c.Name ?? "", Realm = c.Realm ?? "", ClassFile = c.ClassFile ?? "", RaceFile = c.RaceFile ?? "", Sex = c.Sex, Level = c.Level,
                    HasLive = c.HasLive, Flags = c.Flags, GroupSize = c.GroupSize, XpPercent = c.XpPercent, Rested = c.Rested, InInstance = c.InInstance, InstanceName = c.InstanceName ?? "",
                };
                var s = live.Strip;
                bool sameChar = s != null && s.HasV2 && string.Equals(s.Name, c.Name, StringComparison.OrdinalIgnoreCase);
                int map = sameChar ? s.MapId : 0;
                if (sameChar && s.InInstance) h.InstanceId = s.InstanceId;
                // the strip's map first (a cave has no name in the zone table, so GamePresence says no zone: its breadcrumb leads to the zone around it)
                int zone = map > 0 ? ZoneOf(idx, map) : 0;
                if (zone == 0 && map > 0 && ZoneNames.Map(map) != null) zone = map;
                if (zone == 0 && !h.InInstance) zone = ZoneIdByName(idx, c.Zone);
                h.ZoneId = zone;
                h.ZoneName = zone > 0 ? ZoneNameOf(idx, zone) : h.InInstance ? "" : c.Zone ?? "";
                if (h.InInstance && h.InstanceId == 0 && idx != null && h.InstanceName.Length > 0)
                    h.InstanceId = idx.Instances.FirstOrDefault(i => !i.Placeholder && string.Equals(i.Name, h.InstanceName, StringComparison.OrdinalIgnoreCase))?.MapId ?? 0;
                if (h.InInstance && h.InstanceName.Length == 0 && h.InstanceId > 0) h.InstanceName = idx?.Instance(h.InstanceId)?.Name ?? ZoneNames.Instance(h.InstanceId) ?? "";
                var mine = Match(chars, c.Name, c.Realm);
                if (mine != null)
                {
                    h.CharKey = mine.Key ?? "";
                    if (h.Realm.Length == 0) h.Realm = mine.Realm ?? "";
                    if (h.Level <= 0) h.Level = mine.Core?.Level > 0 ? mine.Core.Level : mine.Level;
                    if (h.RaceFile.Length == 0) h.RaceFile = mine.RaceFile ?? "";
                }
                return h;
            }
            // not playing: the character saved last, where it was then
            var last = chars.Where(x => x.Core != null && x.Core.Updated > 0).OrderByDescending(x => x.Core.Updated).FirstOrDefault()
                ?? AtlasBrowse.DefaultCharacter(chars);
            return last == null ? new HomeHere() : Saved(last, idx);
        }

        // what the Hub saved about one character: its zone then (by name), its level, XP and rested
        public static HomeHere Saved(InvChar c, MapIndex idx)
        {
            if (c == null) return new HomeHere();
            var core = c.Core;
            var h = new HomeHere
            {
                Live = false, Name = c.Name ?? "", Realm = c.Realm ?? "", ClassFile = (!string.IsNullOrEmpty(core?.ClassFile) ? core.ClassFile : c.Class ?? "").ToUpperInvariant(),
                RaceFile = !string.IsNullOrEmpty(core?.RaceFile) ? core.RaceFile : c.RaceFile ?? "", Sex = c.Sex, Level = core != null && core.Level > 0 ? core.Level : c.Level,
                CharKey = c.Key ?? "", SeenAt = core != null && core.Updated > 0 ? core.Updated : c.Updated,
            };
            if (core != null)
            {
                h.ZoneId = ZoneIdByName(idx, core.Zone);
                h.ZoneName = h.ZoneId > 0 ? ZoneNameOf(idx, h.ZoneId) : core.Zone ?? "";
                if (core.XpMax > 0) h.XpPercent = (int)Math.Max(0, Math.Min(100, core.Xp * 100 / core.XpMax));
                h.Rested = core.Rested > 0;
            }
            return h;
        }

        public static InvChar Match(IList<InvChar> chars, string name, string realm)
        {
            if (chars == null || string.IsNullOrEmpty(name)) return null;
            var same = chars.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (same.Count <= 1 || string.IsNullOrEmpty(realm)) return same.OrderByDescending(x => x.Updated).FirstOrDefault();
            return same.FirstOrDefault(x => string.Equals(x.Realm, realm, StringComparison.OrdinalIgnoreCase)) ?? same[0];
        }

        // ---- how the zone fits the level (its creatures' range from the Classic baseline)
        public static HomeFit Fit(int level, int min, int max)
        {
            if (level <= 0 || min <= 0) return HomeFit.Unknown;
            if (max < min) max = min;
            if (level < min - 5) return HomeFit.TooHigh;
            if (level < min - 1) return HomeFit.High;
            if (level > max + 3) return HomeFit.Low;
            return HomeFit.Good;
        }

        public static string FitText(HomeFit f)
        {
            switch (f)
            {
                case HomeFit.Good: return "Good for your level";
                case HomeFit.High: return "A bit high for you";
                case HomeFit.TooHigh: return "Too high for you";
                case HomeFit.Low: return "Low for you, little XP";
                default: return "";
            }
        }

        // the creature levels of a zone ("" / 0 when the Classic baseline does not say)
        public static bool ZoneLevels(MapIndex idx, int zoneId, out int min, out int max)
        {
            min = max = 0;
            if (idx == null || zoneId <= 0) return false;
            var dv = AtlasDerived.For(ClassicAtlas.Current);
            if (dv == null) return false;
            // never makes the page wait for the terrain tiles: what is worked out already, else this zone alone (a handful of tiles at most)
            var z = dv.HasZone(idx, zoneId) ? dv.Zone(idx, zoneId) : (dv.ZoneAny(zoneId) ?? dv.Zone(idx, zoneId));
            if (z == null || z.MinLevel <= 0) return false;
            min = z.MinLevel; max = z.MaxLevel;
            return true;
        }

        // the status chips of the hero, in this order
        public static List<string> Chips(HomeHere h)
        {
            var l = new List<string>();
            if (h == null || !h.Live || !h.HasLive) return l;
            if (h.Dead) l.Add("Dead");
            else if (h.InCombat) l.Add("In combat");
            if (h.Afk) l.Add("AFK");
            if (h.Resting) l.Add("Resting");
            if (h.Grouped) l.Add("Grouped (" + h.GroupSize.ToString(Inv) + ")");
            if (h.InInstance && h.InstanceName.Length > 0) l.Add("In " + h.InstanceName);
            return l;
        }

        // the first sentence of the zone note (the hero's one-liner)
        public static string OneLiner(string summary)
        {
            summary = (summary ?? "").Trim();
            if (summary.Length == 0) return "";
            int i = summary.IndexOf(". ", StringComparison.Ordinal);
            return i > 0 ? summary.Substring(0, i + 1) : summary;
        }

        // ---- the cards for a character in a place (built off the UI thread)
        public const int CardRows = 3;
        public static Func<HomeHere, InvChar, HomeCard> UpgradesOverrideForTest;
        public static Func<InvChar, int> RecipesOverrideForTest;

        public static string QuestsId(int zone, int level) => "browse:quests?zone=" + zone.ToString(Inv) + "&minlvl=" + Math.Max(1, level - 4).ToString(Inv) + "&maxlvl=" + (level + 3).ToString(Inv) + "&sort=level";
        public static string DungeonsId(int level) => "browse:instances/dungeons?minlvl=" + Math.Max(1, level).ToString(Inv) + "&maxlvl=" + (level + 2).ToString(Inv) + "&sort=level";
        public static string RaresId(int zone) => "browse:creatures?zone=" + zone.ToString(Inv) + "&rank=rare&sort=level";
        public static string RecipesId(string key) => "browse:recipes?learn=can" + (string.IsNullOrEmpty(key) ? "" : "&for=" + Uri.EscapeDataString(key));

        static BrowseResult Run(string id)
        {
            try { var r = AtlasBrowse.Run(id); return r == null || r.NotFound ? null : r; }
            catch (Exception e) { Util.Log("atlas home " + id + ": " + e.Message); return null; }
        }

        public static List<HomeCard> Cards(HomeHere h, InvChar me, bool withUpgrades = true)
        {
            var l = new List<HomeCard>();
            if (h == null || h.Level <= 0) return l;
            var q = QuestsCard(h); if (q != null) l.Add(q);
            var d = DungeonsCard(h); if (d != null) l.Add(d);
            var r = RaresCard(h); if (r != null) l.Add(r);
            if (withUpgrades) { var u = UpgradesCard(h, me); if (u != null) l.Add(u); }
            var c = RecipesCard(me); if (c != null) l.Add(c);
            return l;
        }

        public static HomeCard QuestsCard(HomeHere h)
        {
            if (h.ZoneId <= 0) return null;
            var id = QuestsId(h.ZoneId, h.Level);
            var r = Run(id);
            if (r == null || r.Total == 0) return null;
            // nearest to the level first (the list itself is by level)
            var rows = r.Rows.Where(x => x.Link != null).OrderBy(x => Math.Abs(LevelOf(x) - h.Level)).ThenBy(x => LevelOf(x)).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).Take(CardRows).ToList();
            return new HomeCard
            {
                Key = "quests", Title = "Quests here", Total = r.Total, Rows = rows,
                Note = "In " + h.ZoneName + " for level " + h.Level.ToString(Inv) + ". Quests you finished still show: the Outpost cannot tell yet.",
                MoreText = r.Total > rows.Count ? "All " + AtlasFormat.Count(r.Total, "quest", "quests") : "As a list", MoreLink = id,
            };
        }

        public static HomeCard DungeonsCard(HomeHere h)
        {
            var id = DungeonsId(h.Level);
            var r = Run(id);
            if (r == null || r.Total == 0) return null;
            var rows = r.Rows.Where(x => x.Link != null).Take(CardRows).ToList();
            foreach (var x in rows) if (!string.IsNullOrEmpty(x.Extra)) x.Detail = "Entrance in " + x.Extra + (LevelText(x).Length > 0 ? " · level " + LevelText(x) : "");
            return new HomeCard
            {
                Key = "dungeons", Title = h.Grouped ? "Dungeons for your group (" + h.GroupSize.ToString(Inv) + ")" : "Dungeons for your level", Total = r.Total, Rows = rows,
                Note = "Level " + h.Level.ToString(Inv) + " fits them.",
                MoreText = r.Total > rows.Count ? "All " + AtlasFormat.Count(r.Total, "dungeon", "dungeons") : "As a list", MoreLink = id,
            };
        }

        public static HomeCard RaresCard(HomeHere h)
        {
            if (h.ZoneId <= 0) return null;
            var id = RaresId(h.ZoneId);
            var r = Run(id);
            if (r == null || r.Total == 0) return null;
            var idx = MapData.Index; var L = AtlasQuery.LodgeOrNull();
            var rows = r.Rows.Where(x => x.Link != null).Take(CardRows).ToList();
            foreach (var x in rows)
            {
                if (!AtlasQuery.TryParse(x.Link, out var kind, out var npc) || kind != "npc") continue;
                var where = WhereSeen(idx, L, npc, h.ZoneId);
                if (where.Length > 0) x.Detail = (LevelText(x).Length > 0 ? "Level " + LevelText(x) + " · " : "") + where;
            }
            return new HomeCard
            {
                Key = "rares", Title = "Rares here", Total = r.Total, Rows = rows, Note = "Rare creatures of " + h.ZoneName + " and where they were seen.",
                MoreText = r.Total > rows.Count ? "All " + AtlasFormat.Count(r.Total, "rare", "rares") : "As a list", MoreLink = id,
            };
        }

        // "seen by the Lodge at 31 / 41", else "spawns at 45 / 38" (the Classic baseline), "" when neither says
        public static string WhereSeen(MapIndex idx, LodgeAtlas L, int npc, int zone)
        {
            try
            {
                var seen = L?.Spawns(npc)?.Where(s => s.Zone == zone).ToList();
                if (seen != null && seen.Count > 0) return "seen by the Lodge at " + AtlasInstances.Pct(seen[0].X) + " / " + AtlasInstances.Pct(seen[0].Y);
                var n = ClassicAtlas.NpcInfo(npc);
                if (n != null && idx != null)
                    foreach (var sp in n.Spawns)
                    {
                        var p = idx.WorldToMap(sp.Map, sp.X, sp.Y);
                        if (p != null && p.Value.UiMapId == zone) return "spawns at " + AtlasInstances.Pct(p.Value.Fx) + " / " + AtlasInstances.Pct(p.Value.Fy);
                    }
            }
            catch (Exception) { }
            return "";
        }

        // the best upgrades whose source is in this zone (the Characters page's engine; heavy the first time, cached after)
        public static HomeCard UpgradesCard(HomeHere h, InvChar me)
        {
            if (UpgradesOverrideForTest != null) return UpgradesOverrideForTest(h, me);
            if (me == null || h.ZoneId <= 0) return null;
            try
            {
                var who = UpgradeChar.From(me);
                if (who == null) return null;
                var set = UpgradeEngine.Compute(who);
                if (set == null || set.Problem.Length > 0) return null;
                var picks = UpgradeEngine.Pick(set, new UpgradeFilters());
                var here = new List<KeyValuePair<UpgradeCandidate, UpgradeSource>>();
                foreach (var slot in picks)
                    foreach (var c in slot.Upgrades)
                    {
                        var src = c.Sources.FirstOrDefault(s => s.ZoneMap == h.ZoneId || (s.ZoneMap == 0 && string.Equals(s.Zone, h.ZoneName, StringComparison.OrdinalIgnoreCase)));
                        if (src != null && here.All(x => x.Key.Item.Id != c.Item.Id)) here.Add(new KeyValuePair<UpgradeCandidate, UpgradeSource>(c, src));
                    }
                if (here.Count == 0) return null;
                var rows = here.OrderByDescending(x => x.Key.Rank).ThenBy(x => x.Key.Item.Id).Take(CardRows).Select(x => new AtlasRow
                {
                    Link = "item:" + x.Key.Item.Id.ToString(Inv), Title = x.Key.Item.Name ?? AtlasQuery.ItemName(x.Key.Item.Id), Quality = x.Key.Item.Quality, IconId = x.Key.Item.IconId,
                    Detail = (x.Key.Pct == null ? "fills an empty slot" : UpgradeScore.PctText(x.Key.Pct)) + " · " + x.Value.Line,
                }).ToList();
                return new HomeCard { Key = "upgrades", Title = "Upgrades from here", Total = here.Count, Rows = rows, Note = "Better than what " + me.Name + " wears, from " + h.ZoneName + "." };
            }
            catch (Exception e) { Util.Log("atlas home upgrades: " + e.Message); return null; }
        }

        public static HomeCard RecipesCard(InvChar me)
        {
            if (me == null) return null;
            int n;
            var id = RecipesId(me.Key);
            if (RecipesOverrideForTest != null) n = RecipesOverrideForTest(me);
            else { var r = Run(id); n = r?.Total ?? 0; }
            if (n <= 0) return null;
            return new HomeCard
            {
                Key = "recipes", Title = "Recipes to learn", Total = n, Note = me.Name + " has the skill for " + AtlasFormat.Count(n, "recipe", "recipes") + " not learned yet.",
                MoreText = "Show " + AtlasFormat.Count(n, "recipe", "recipes"), MoreLink = id,
            };
        }

        static int LevelOf(AtlasRow r)
        {
            var t = LevelText(r);
            int dash = t.IndexOf('-');
            return int.TryParse(dash > 0 ? t.Substring(0, dash) : t, NumberStyles.None, Inv, out var n) ? n : 0;
        }

        // "12" / "20-22" from a row's detail ("Level 12 · ...", "Levels 20-22 · ...")
        static string LevelText(AtlasRow r)
        {
            var d = r?.Detail ?? "";
            foreach (var part in d.Split('·'))
            {
                var p = part.Trim();
                if (p.StartsWith("Levels ", StringComparison.Ordinal)) return p.Substring(7).Trim();
                if (p.StartsWith("Level ", StringComparison.Ordinal)) return p.Substring(6).Trim();
            }
            return "";
        }

        // ---- the Lodge today
        // the finds of today (local midnight to now)
        public static int FindsToday(LodgeAtlas L, DateTime nowUtc)
        {
            if (L == null) return 0;
            var midnight = nowUtc.ToLocalTime().Date.ToUniversalTime();
            long from = (long)(midnight - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return L.AllFinds.Count(f => f.At >= from);
        }

        // "4 online · 2 finds today · Raid night Thu 20:00 · Raid reset Tue 09:00"
        public static string TodayLine(HomeLodge l, int findsToday, DateTime nowUtc)
        {
            if (l == null) return "";
            var parts = new List<string> { AtlasFormat.Count(l.Online, "member", "members") + " online" };
            parts.Add(findsToday == 0 ? "no finds today" : AtlasFormat.Count(findsToday, "find", "finds") + " today");
            if (l.NextEventTitle.Length > 0 && l.NextEventUtc > nowUtc) parts.Add(l.NextEventTitle + " " + When(l.NextEventUtc, nowUtc));
            if (l.NextResetUtc > nowUtc) parts.Add((l.NextResetTitle.Length > 0 ? l.NextResetTitle : "Raid reset") + " " + When(l.NextResetUtc, nowUtc));
            return string.Join(" · ", parts);
        }

        // "today 20:00", "tomorrow 09:00", "Thu 20:00", "12 Nov" (local time)
        public static string When(DateTime utc, DateTime nowUtc)
        {
            var t = utc.ToLocalTime(); var n = nowUtc.ToLocalTime();
            var en = CalendarRules.English;
            int days = (t.Date - n.Date).Days;
            if (days == 0) return "today " + t.ToString("HH:mm", en);
            if (days == 1) return "tomorrow " + t.ToString("HH:mm", en);
            if (days > 1 && days < 7) return t.ToString("ddd HH:mm", en);
            return t.ToString("d MMM", en);
        }

        // the members playing in the same place as you, as far as they share it: never you, never someone who appears offline, never someone whose
        // zone is not shared (the server sends no zone then), never someone not playing
        public static List<HomeMember> NearYou(HomeLodge l, HomeHere h)
        {
            var res = new List<HomeMember>();
            if (l == null || h == null || !h.Known) return res;
            var places = new[] { h.InInstance ? h.InstanceName : "", h.ZoneName }.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (places.Count == 0) return res;
            foreach (var m in l.Members)
            {
                if (m == null || m.IsMe || m.Invisible || !m.Playing || string.IsNullOrEmpty(m.Place)) continue;
                if (string.Equals(m.CharName, h.Name, StringComparison.OrdinalIgnoreCase) && h.Live) continue;      // my own character, seen through the Lodge
                if (places.Any(p => string.Equals(p, m.Place, StringComparison.OrdinalIgnoreCase))) res.Add(m);
            }
            return res.OrderByDescending(m => m.Level).ThenBy(m => m.CharName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
