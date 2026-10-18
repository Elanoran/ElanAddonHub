using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // The five sources of the Atlas (2.93) as the Data sources page shows them, and the numbers behind its pictures. No UI here (AtlasPage.DataSources.cs
    // draws it), nothing guessed: every figure is counted from data on this PC.
    //   Your game        the player's own game files (items, spells, quests, maps ...): read from the WoW install, stays on this PC
    //   You              what this PC's own game noted (Elan's Toolkit / Hunter Helper / Hub SavedVariables): drops, vendors, spawn points, nodes, flight times
    //   Your Lodge       the Lodge's pool: what the members who share have seen
    //   The Cairn        the dataset pooled from many Lodges (give to get, CairnData.cs)
    //   Classic baseline old Classic numbers (cMaNGOS classic-db), downloaded by the user; the fallback
    // The same key names the source's colour (Theme.xaml Brush.Source.*) and icon (AtlasIcons "src-*"), here and on the source chips of Atlas rows.
    // Self-test section "datasources".
    public static class DataSources
    {
        public const string Game = "game", You = "you", Lodge = "lodge", Cairn = "cairn", Classic = "classic";
        public static readonly string[] Order = { Game, You, Lodge, Cairn, Classic };

        // a row's source: its own (the Lodge's rows say so), else from its label
        public static string KeyOf(string label, string source)
        {
            if (!string.IsNullOrEmpty(source)) return source;
            switch (label ?? "")
            {
                case AtlasLabel.Forever: case AtlasLabel.Hotfix: return Game;
                case AtlasLabel.Classic: return Classic;
                case AtlasLabel.Cairn: return Cairn;
                case AtlasLabel.Mine: return You;
                default: return "";
            }
        }

        public static string BrushKey(string key)
        {
            switch (key)
            {
                case Game: return "Brush.Source.Game";
                case You: return "Brush.Source.You";
                case Lodge: return "Brush.Source.Lodge";
                case Cairn: return "Brush.Source.Cairn";
                case Classic: return "Brush.Source.Classic";
                default: return "Brush.Text.Tertiary";
            }
        }
        public static string TintKey(string key) => BrushKey(key) + (Order.Contains(key) ? ".Tint" : "");
        public static string IconKey(string key) => "src-" + key;

        public static string Title(string key)
        {
            switch (key)
            {
                case Game: return "Your game";
                case You: return "You";
                case Lodge: return "Your Lodge";
                case Cairn: return "The Cairn";
                case Classic: return "Classic baseline";
                default: return key;
            }
        }

        // what the source holds, in a few words
        public static string What(string key)
        {
            switch (key)
            {
                case Game: return "Items, spells, talents, quests, maps, zones and what your game has met.";
                case You: return "What your own game noted: drops, vendors, spawn points, nodes and flight times - and, with the combat log on, the spells creatures use.";
                case Lodge: return "What the members who share have seen: drops, vendors, spawn points, nodes, finds.";
                case Cairn: return "Many Lodges' numbers pooled into one daily dataset: ids and counts, no names.";
                case Classic: return "Old Classic drops, vendors and spawn points: the fallback where Forever has no data yet.";
                default: return "";
            }
        }

        // where a source card takes you: a list filtered to that source ("" = no list of its own)
        public static string ListOf(string key)
        {
            switch (key)
            {
                case Game: return "browse:items?src=game";
                case Lodge: return "browse:items?src=lodge";
                case Cairn: return "browse:items?src=cairn";
                case Classic: return "browse:items?src=classic";
                default: return "";
            }
        }

        public static string ListTitle(string key)
        {
            switch (key)
            {
                case Game: return "Items in your game";
                case Lodge: return "Items the Lodge has seen";
                case Cairn: return "Items in the Cairn's data";
                case Classic: return "Items from the Classic baseline";
                default: return "";
            }
        }

        static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
        static string Pct(int part, int whole) => whole <= 0 ? "0 %" : Math.Round(100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + " %";

        // ================================================================================================================ who owns it
        public static string Owner(string key, SourcesFacts f)
        {
            f = f ?? new SourcesFacts();
            switch (key)
            {
                case Game: return f.GameReady ? "Read from your WoW install - stays on this PC." : "Read from your WoW install when it is ready - stays on this PC.";
                case You: return "Noted by your own game, kept on this PC" + (f.Cairn2.HasLodge ? (f.Cairn2.Sharing ? " and shared with your Lodge." : " - not shared (Settings > Lodge).") : ".");
                case Lodge:
                    if (!f.Cairn2.HasLodge) return "You are not in a Lodge.";
                    if (f.LodgeMembers > 0) return "Shared by " + (f.LodgeMembers == 1 ? "1 member" : N(f.LodgeMembers) + " members") + " of your Lodge.";
                    return f.Lodge != null && f.Lodge.HasData ? "Shared by the members of your Lodge." : "Shared by the members of your Lodge - nothing yet.";
                case Cairn:
                    if (f.Cairn != null) return "Pooled by " + (f.Cairn.Lodges == 1 ? "1 Lodge" : N(f.Cairn.Lodges) + " Lodges") + " (give to get).";
                    return "Pooled by many Lodges (give to get) - not on this PC.";
                case Classic: return f.Classic != null ? "Old Classic data, downloaded by you." : "Old Classic data - not downloaded.";
                default: return "";
            }
        }

        // the one figure on a source card
        public static string Figure(string key, SourcesFacts f)
        {
            f = f ?? new SourcesFacts();
            switch (key)
            {
                case Game: return f.GameItems > 0 ? N(f.GameItems) + " items" + (f.GameSpells > 0 ? " · " + N(f.GameSpells) + " spells" : "") : "Not read yet";
                case You: { int n = f.OwnCount; return n > 0 ? AtlasFormat.Count(n, "thing noted", "things noted") : "Nothing noted yet"; }
                case Lodge: return f.Lodge != null && f.Lodge.HasData ? AtlasFormat.Count(f.Lodge.KillSources + f.Lodge.ObjectSources, "loot source", "loot sources") + " · " + AtlasFormat.Count(f.Lodge.FindCount, "find", "finds") : f.Cairn2.HasLodge ? "No pool yet" : "No Lodge";
                case Cairn: return f.Cairn != null ? "Dataset of " + f.Cairn.Day : "Not received";
                case Classic: return f.Classic != null ? "On this PC" : "Not on this PC";
                default: return "";
            }
        }

        // ================================================================================================================ coverage per kind
        public static readonly string[] Kinds = { "drops", "vendors", "spawns", "nodes", "flights" };

        // every thing of a kind once, counted for the first source that has it: you, your Lodge, the Cairn, then the Classic baseline (only what no
        // Forever source has). The bar is the honest "how complete is the Forever data" picture.
        public static List<KindCoverage> Coverage(SourcesFacts f)
        {
            f = f ?? new SourcesFacts();
            var res = new List<KindCoverage>();
            var own = f.Own; var L = f.Lodge; var C = f.Cairn; var cl = f.Classic;
            IEnumerable<string> P(string pre, IEnumerable<int> ids) => (ids ?? Enumerable.Empty<int>()).Select(i => pre + i.ToString(CultureInfo.InvariantCulture));

            // drops: creatures and objects with loot
            var cd = ClassicSets(cl);
            res.Add(Count("drops", "Drops", "creature or object", "creatures and objects",
                P("npc:", own?.Kills.Keys).Concat(P("obj:", own?.Objects.Keys)),
                L == null ? null : P("npc:", Safe(() => L.KillIds)).Concat(P("obj:", Safe(() => L.ObjectLootIds))),
                C == null ? null : C.AllSources.Where(s => s.Kind == "kill" || s.Kind == "object").Select(s => (s.Kind == "kill" ? "npc:" : "obj:") + s.Id.ToString(CultureInfo.InvariantCulture)),
                cd?.Drops));
            res.Add(Count("vendors", "Vendors", "vendor", "vendors",
                P("npc:", own?.Vendors.Keys), L == null ? null : P("npc:", Safe(() => L.AllVendors.Select(kv => kv.Key))), C == null ? null : P("npc:", C.VendorNpcIds), cd?.Vendors));
            res.Add(Count("spawns", "Spawn points", "creature", "creatures",
                P("npc:", own?.Spawns.Keys), L == null ? null : P("npc:", Safe(() => L.AllSpawns.Select(kv => kv.Key))), C == null ? null : P("npc:", C.SpawnNpcIds), cd?.Spawns));
            res.Add(Count("nodes", "Gathering nodes", "kind of node", "kinds of node",
                P("obj:", own?.Nodes.Keys), L == null ? null : P("obj:", Safe(() => L.NodeObjectIds)), C == null ? null : P("obj:", C.NodeObjectIds), cd?.Nodes));
            var fl = Count("flights", "Flight times", "flight", "flights", f.OwnFlights, f.LodgeFlights, C?.FlightLegs, null);
            fl.ClassicApplies = false;
            fl.Text = CoverageText(fl);
            res.Add(fl);
            return res;
        }

        static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> get) { try { return (get() ?? Enumerable.Empty<T>()).ToList(); } catch (Exception) { return Enumerable.Empty<T>(); } }

        static KindCoverage Count(string kind, string title, string one, string many, IEnumerable<string> you, IEnumerable<string> lodge, IEnumerable<string> cairn, ICollection<string> classic)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int Add(IEnumerable<string> ids) { int n = 0; if (ids != null) foreach (var i in ids) if (seen.Add(i)) n++; return n; }
            var k = new KindCoverage { Kind = kind, Title = title, One = one, Many = many };
            k.You = Add(you); k.Lodge = Add(lodge); k.Cairn = Add(cairn); k.Classic = Add(classic);
            k.Total = seen.Count;
            k.ClassicKnown = classic != null;
            k.Text = CoverageText(k);
            return k;
        }

        // "Drops: 1,240 creatures and objects. Seen on Forever: 460 (37 %) - 120 by you, 240 by your Lodge, 100 by the Cairn. Only in the Classic baseline: 780."
        public static string CoverageText(KindCoverage k)
        {
            if (k.Total == 0) return k.Title + ": nothing known yet" + (k.ClassicApplies && !k.ClassicKnown ? " (the Classic baseline is not on this PC)." : ".");
            int fv = k.You + k.Lodge + k.Cairn;
            var parts = new List<string>();
            if (k.You > 0) parts.Add(N(k.You) + " by you");
            if (k.Lodge > 0) parts.Add(N(k.Lodge) + " by your Lodge");
            if (k.Cairn > 0) parts.Add(N(k.Cairn) + " by the Cairn");
            var s = k.Title + ": " + AtlasFormat.Count(k.Total, k.One, k.Many) + ". ";
            s += fv == 0 ? "None seen on Forever yet." : "Seen on Forever: " + N(fv) + (k.ClassicApplies ? " (" + Pct(fv, k.Total) + ")" : "") + " - " + string.Join(", ", parts) + ".";
            if (k.ClassicApplies) s += k.ClassicKnown ? (k.Classic > 0 ? " Only in the Classic baseline: " + N(k.Classic) + "." : "") : " The Classic baseline is not on this PC.";
            else s += " The Classic baseline has no " + k.Title.ToLowerInvariant() + ".";
            return s;
        }

        // the Classic baseline's ids per kind, worked out once per index
        sealed class ClassicKindSets { public HashSet<string> Drops = new HashSet<string>(StringComparer.Ordinal), Vendors = new HashSet<string>(StringComparer.Ordinal), Spawns = new HashSet<string>(StringComparer.Ordinal), Nodes = new HashSet<string>(StringComparer.Ordinal); }
        static ClassicIndex setsOf; static ClassicKindSets sets;
        static readonly object setsGate = new object();
        static ClassicKindSets ClassicSets(ClassicIndex ci)
        {
            if (ci == null) return null;
            lock (setsGate)
            {
                if (ReferenceEquals(ci, setsOf) && sets != null) return sets;
                var s = new ClassicKindSets();
                foreach (var l in ci.ItemSources.Values)
                    foreach (var src in l)
                    {
                        if (src.Kind == ClassicSourceKind.NpcDrop) s.Drops.Add("npc:" + src.SourceId.ToString(CultureInfo.InvariantCulture));
                        else if (src.Kind == ClassicSourceKind.ObjectLoot) s.Drops.Add("obj:" + src.SourceId.ToString(CultureInfo.InvariantCulture));
                        else if (src.Kind == ClassicSourceKind.Vendor) s.Vendors.Add("npc:" + src.SourceId.ToString(CultureInfo.InvariantCulture));
                    }
                foreach (var n in ci.Npcs.Values) if (n.Spawns != null && n.Spawns.Length > 0) s.Spawns.Add("npc:" + n.Id.ToString(CultureInfo.InvariantCulture));
                foreach (var o in ci.Objects.Values)
                {
                    if (o.Spawns == null || o.Spawns.Length == 0) continue;
                    var need = ObjectKinds.LockNeeds(o.LockId);
                    bool node = need != null ? need.Any(x => (x.LockType == WorldLookups.LockTypeHerbalism || x.LockType == WorldLookups.LockTypeMining) && x.Skill > 0) : ObjectKinds.IsOreName(o.Name);
                    if (node) s.Nodes.Add("obj:" + o.Id.ToString(CultureInfo.InvariantCulture));
                }
                setsOf = ci; sets = s;
                return s;
            }
        }

        // ================================================================================================================ growth
        // the Lodge's finds per day over the last `days` days (from each find's own date): the one history this PC really has
        public static Growth LodgeGrowth(LodgeAtlas L, DateTime nowUtc, int days = 28)
        {
            var g = new Growth { PerDay = new int[days] };
            var today = nowUtc.Date;
            if (L != null)
            {
                foreach (var f in Safe(() => L.AllFinds))
                {
                    if (f == null || f.At <= 0) continue;
                    var d = DateTimeOffset.FromUnixTimeSeconds(f.At).UtcDateTime.Date;
                    int back = (int)(today - d).TotalDays;
                    if (back < 0 || back >= days) continue;
                    g.PerDay[days - 1 - back]++;
                }
            }
            g.Total = g.PerDay.Sum();
            g.Has = g.Total > 0;
            if (!g.Has) g.Text = L == null ? "No Lodge, so no finds to count." : "No finds in the last " + (days / 7) + " weeks.";
            else
            {
                int best = Array.IndexOf(g.PerDay, g.PerDay.Max());
                var bestDay = today.AddDays(best - (days - 1));
                g.Text = "Finds per day, last " + (days / 7) + " weeks: " + N(g.Total) + " in all, the most on " + bestDay.ToString("d MMMM", CultureInfo.InvariantCulture) + " (" + N(g.PerDay[best]) + ").";
            }
            return g;
        }

        // the Cairn: one dataset a day, and this PC keeps only the newest - no history to draw (said, not drawn)
        public static string CairnHistory(CairnSet c) =>
            c == null ? "" : "This PC keeps only the newest dataset (" + c.Day + "), so there is no history to draw: today's numbers are " + AtlasFormat.Count(c.SourceCount, "loot source", "loot sources") + ", " + AtlasFormat.Count(c.VendorNpcCount, "vendor", "vendors") + " and " + AtlasFormat.Count(c.FlightCount, "flight time", "flight times") + ".";

        // ================================================================================================================ the Cairn card
        public static CairnCardInfo CairnCard(SourcesFacts f)
        {
            f = f ?? new SourcesFacts();
            var s = f.Cairn2 ?? new CairnFacts();
            var c = f.Cairn;
            var r = new CairnCardInfo();
            if (!s.HasLodge) { r.Standing = CairnStanding.NoLodge; r.Line = "The Cairn's data comes only through a Lodge, to its members who share."; }
            else if (s.Guest) { r.Standing = CairnStanding.NotSharing; r.Line = "Guests don't get the Cairn's data - it is for Lodge members who share."; }
            else if (!s.Sharing) { r.Standing = CairnStanding.NotSharing; r.Line = "Your Atlas sharing is off, so your Lodge doesn't give you the Cairn's numbers. Give to get: share with your Lodge and they come with it."; }
            else if (s.JoinStanding == "lapsed") { r.Standing = CairnStanding.Lapsed; r.Line = "Your Lodge's last contribution is too old; it receives again with the next one."; }
            else if (c != null) { r.Standing = CairnStanding.Good; r.Line = "Your Lodge contributes and passes the Cairn's numbers on to you."; }
            else if (s.Refused == "lodge" || (s.Online && !s.LodgeHandsOut) || (s.JoinKnown && !s.JoinOn)) { r.Standing = CairnStanding.NotJoined; r.Line = "Your Lodge isn't part of the Cairn" + (s.JoinKnown && !s.JoinOn ? " (its Guild Master can switch it on in Settings > Guild Master)." : " right now (a Lodge has to contribute to receive)."); }
            else { r.Standing = CairnStanding.Waiting; r.Line = "Not received yet - your Lodge sends it when it has a copy."; }
            r.Title = StandingTitle(r.Standing);
            r.Dataset = c != null ? "Dataset of " + c.Day + (c.Seq > 0 ? " (number " + N(c.Seq) + ")" : "") : "No dataset on this PC";
            r.Lodges = c != null ? (c.Lodges == 1 ? "1 Lodge contributes" : N(c.Lodges) + " Lodges contribute") : "";
            r.GiveToGet = "Give to get: your Lodge sends the pooled totals of the members who share (ids and counts, never names) and gets the Cairn's numbers back for them.";
            return r;
        }

        public static string StandingTitle(CairnStanding s)
        {
            switch (s)
            {
                case CairnStanding.Good: return "In good standing";
                case CairnStanding.Lapsed: return "Lapsed";
                case CairnStanding.NotJoined: return "Not joined";
                case CairnStanding.NotSharing: return "Not sharing";
                case CairnStanding.NoLodge: return "No Lodge";
                default: return "Waiting";
            }
        }
    }

    // what the Data sources page is drawn from (gathered on the UI thread by the page; the self-test fills its own)
    public sealed class SourcesFacts
    {
        public bool GameReady; public int GameItems, GameSpells;
        public AtlasObs.Snapshot Own;                               // this PC's own observations (null = none)
        public LodgeAtlas Lodge;                                    // the Lodge's pool (null = none)
        public int LodgeMembers;                                    // members who share (the most any count says)
        public CairnSet Cairn;                                      // the Cairn's dataset on this PC (null = none)
        public ClassicIndex Classic;                                // the Classic baseline (null = not downloaded)
        public ICollection<string> OwnFlights, LodgeFlights;        // timed flight legs ("Horde|11>22")
        public CairnFacts Cairn2 = new CairnFacts();
        public DateTime NowUtc = DateTime.UtcNow;
        public int OwnCount => Own == null ? 0 : Own.Kills.Count + Own.Objects.Count + Own.Fishing.Count + Own.Vendors.Count + Own.Spawns.Count + Own.Nodes.Count + Own.FishSpots.Count + (OwnFlights?.Count ?? 0);
    }

    // where the user stands with the Cairn (the main window's session and settings)
    public sealed class CairnFacts
    {
        public bool HasLodge, Online, Sharing = true, Guest, LodgeHandsOut;
        public string Refused = "";                                 // CairnAtlas.RefusedWhy
        public bool JoinKnown, JoinOn; public string JoinStanding = "";   // the Guild Master panel's status (officers and the Guild Master only)
    }

    public sealed class KindCoverage
    {
        public string Kind = "", Title = "", One = "", Many = "", Text = "";
        public int You, Lodge, Cairn, Classic, Total;
        public bool ClassicApplies = true, ClassicKnown;
        public int Forever => You + Lodge + Cairn;
        public override string ToString() => Kind + " " + You + "/" + Lodge + "/" + Cairn + "/" + Classic + " of " + Total;
    }

    public sealed class Growth { public int[] PerDay = new int[0]; public int Total; public bool Has; public string Text = ""; }

    public enum CairnStanding { Good, Lapsed, Waiting, NotJoined, NotSharing, NoLodge }

    public sealed class CairnCardInfo { public CairnStanding Standing; public string Title = "", Line = "", Dataset = "", Lodges = "", GiveToGet = ""; }
}
