using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Professions
{
    // Where an item comes from, for recipe items and reagents. Two sources, always labelled (ARCHITECTURE.md, "Atlas"):
    //   "Classic baseline"  - the public cMaNGOS classic-db the user downloaded (unverified for WoW Forever)
    //   "Forever (Lodge)"   - what Lodge members' clients saw (drops with counts, vendors with real prices)
    // The page and the guide builders only see IProfessionSources, so the self-test feeds them a synthetic one.
    public sealed class SourceLine
    {
        public string Kind;          // vendor | drop | object | skin | pickpocket | quest | fish
        public string Text;          // "Sold by Innkeeper Biribi", "Dropped by Kobold Worker (12 % of 340)"
        public string Link;          // npc:123 / obj:45 / quest:6 / zone:1429 (AtlasIds)
        public string Label;         // Classic baseline | Forever (Lodge)
        public long UnitPrice;       // vendor lines: copper for one item, 0 when unknown
        public double Chance;        // loot lines, percent
    }

    public enum PriceKind { Vendor, Estimated, Unknown }

    public struct ItemPrice
    {
        public long Copper;
        public PriceKind Kind;
        public ItemPrice(long copper, PriceKind kind) { Copper = copper; Kind = kind; }
        public bool Known => Kind != PriceKind.Unknown;
    }

    public interface IProfessionSources
    {
        // the best few ways to get an item: vendors first (cheapest first), then the likeliest loot
        List<SourceLine> SourcesOf(int itemId, int max);
        // a vendor that is known to sell it (any price)
        bool SoldByVendor(int itemId);
        // what one costs: the vendor's price when a vendor is known, else an estimate from the client's item value, else unknown
        ItemPrice PriceOf(int itemId);
    }

    public static class SourceLabels
    {
        public const string Classic = ClassicLabel.Source;          // "Classic baseline"
        public const string Lodge = "Forever (Lodge)";
    }

    // the real thing: the Classic baseline + the Lodge pool + the client's own price table
    public sealed class LiveSources : IProfessionSources
    {
        readonly Func<LodgeAtlas> lodge;
        readonly Func<int, ClientItem> item;
        readonly Dictionary<int, ItemPrice> prices = new Dictionary<int, ItemPrice>();

        public LiveSources(Func<LodgeAtlas> lodge = null, Func<int, ClientItem> item = null)
        {
            this.lodge = lodge;
            this.item = item ?? ItemData.Get;
        }

        LodgeAtlas Lodge { get { try { return lodge?.Invoke(); } catch { return null; } } }

        static string NpcName(int id)
        {
            var n = ClassicAtlas.NpcInfo(id);
            if (n != null && !string.IsNullOrEmpty(n.Name)) return n.Name;
            var s = SeenData.Npc(id);
            return s != null && !string.IsNullOrEmpty(s.Name) ? s.Name : "npc " + id.ToString(CultureInfo.InvariantCulture);
        }

        static string ObjName(int id)
        {
            var o = ClassicAtlas.ObjectInfo(id);
            if (o != null && !string.IsNullOrEmpty(o.Name)) return o.Name;
            var s = SeenData.Object(id);
            return s != null && !string.IsNullOrEmpty(s.Name) ? s.Name : "object " + id.ToString(CultureInfo.InvariantCulture);
        }

        public bool SoldByVendor(int itemId)
        {
            foreach (var s in ClassicAtlas.SourcesOf(itemId)) if (s.Kind == ClassicSourceKind.Vendor) return true;
            var l = Lodge;
            return l != null && l.Vendors(itemId).Count > 0;
        }

        public ItemPrice PriceOf(int itemId)
        {
            if (prices.TryGetValue(itemId, out var p)) return p;
            long best = 0;
            var l = Lodge;
            if (l != null) foreach (var v in l.Vendors(itemId)) if (v.Price > 0 && !v.Extended) { long unit = v.Price / Math.Max(1, v.Stack); if (unit > 0 && (best == 0 || unit < best)) best = unit; }
            var it = item(itemId);
            if (best > 0) p = new ItemPrice(best, PriceKind.Vendor);
            else if (it != null && SoldByVendor(itemId) && it.BuyPrice > 0) p = new ItemPrice(it.BuyPrice, PriceKind.Vendor);
            else if (it != null && it.BuyPrice > 0) p = new ItemPrice(it.BuyPrice, PriceKind.Estimated);
            else if (it != null && it.SellPrice > 0) p = new ItemPrice(it.SellPrice * 4, PriceKind.Estimated);
            else p = new ItemPrice(0, PriceKind.Unknown);
            prices[itemId] = p;
            return p;
        }

        public List<SourceLine> SourcesOf(int itemId, int max)
        {
            var res = new List<SourceLine>();
            var price = PriceOf(itemId);
            foreach (var s in ClassicAtlas.SourcesOf(itemId))
            {
                switch (s.Kind)
                {
                    case ClassicSourceKind.Vendor:
                        res.Add(new SourceLine { Kind = "vendor", Text = "Sold by " + NpcName(s.SourceId), Link = "npc:" + s.SourceId, Label = SourceLabels.Classic, UnitPrice = price.Kind == PriceKind.Vendor ? price.Copper : 0 });
                        break;
                    case ClassicSourceKind.NpcDrop:
                        res.Add(new SourceLine { Kind = "drop", Text = "Dropped by " + NpcName(s.SourceId) + Pct(s.Chance), Link = "npc:" + s.SourceId, Label = SourceLabels.Classic, Chance = s.Chance });
                        break;
                    case ClassicSourceKind.NpcSkin:
                        res.Add(new SourceLine { Kind = "skin", Text = "Skinned from " + NpcName(s.SourceId) + Pct(s.Chance), Link = "npc:" + s.SourceId, Label = SourceLabels.Classic, Chance = s.Chance });
                        break;
                    case ClassicSourceKind.NpcPickpocket:
                        res.Add(new SourceLine { Kind = "drop", Text = "Pickpocketed from " + NpcName(s.SourceId) + Pct(s.Chance), Link = "npc:" + s.SourceId, Label = SourceLabels.Classic, Chance = s.Chance });
                        break;
                    case ClassicSourceKind.ObjectLoot:
                        res.Add(new SourceLine { Kind = "object", Text = "Found in " + ObjName(s.SourceId) + Pct(s.Chance), Link = "obj:" + s.SourceId, Label = SourceLabels.Classic, Chance = s.Chance });
                        break;
                    case ClassicSourceKind.QuestReward: case ClassicSourceKind.QuestChoice:
                        var q = ClassicAtlas.QuestInfo(s.SourceId);
                        res.Add(new SourceLine { Kind = "quest", Text = "Quest reward: " + (q?.Title ?? "quest " + s.SourceId), Link = "quest:" + s.SourceId, Label = SourceLabels.Classic });
                        break;
                }
            }
            var l = Lodge;
            if (l != null)
            {
                foreach (var v in l.Vendors(itemId))
                    res.Add(new SourceLine { Kind = "vendor", Text = v.Extended ? "Sold by " + NpcName(v.NpcId) + " (special cost)" : "Sold by " + NpcName(v.NpcId) + " for " + Money(v.Price / Math.Max(1, v.Stack)), Link = "npc:" + v.NpcId, Label = SourceLabels.Lodge, UnitPrice = v.Extended ? 0 : v.Price / Math.Max(1, v.Stack) });
                foreach (var d in l.Drops(itemId))
                {
                    string who = d.Kind == "npc" ? "Dropped by " + NpcName(d.Id) : d.Kind == "obj" ? "Found in " + ObjName(d.Id) : "Fished in zone " + d.Id;
                    res.Add(new SourceLine
                    {
                        Kind = d.Kind == "npc" ? "drop" : d.Kind == "obj" ? "object" : "fish",
                        Text = who + " (" + d.Chance.ToString("0.#", CultureInfo.InvariantCulture) + " % of " + d.Kills.ToString("N0", CultureInfo.InvariantCulture) + ")",
                        Link = d.Kind == "fish" ? "zone:" + d.Id : d.Kind + ":" + d.Id, Label = SourceLabels.Lodge, Chance = d.Chance,
                    });
                }
            }
            return Rank(res, max);
        }

        static string Pct(double c) => c > 0 ? " (" + c.ToString("0.#", CultureInfo.InvariantCulture) + " %)" : "";

        public static string Money(long copper) => ProfMoney.Format(copper);

        internal static List<SourceLine> Rank(List<SourceLine> all, int max)
        {
            // vendors first by price (known prices before unknown), then loot by chance; the same line from both labels stays (they differ in label)
            var ordered = all.OrderBy(s => s.Kind == "vendor" ? 0 : s.Kind == "quest" ? 2 : 1)
                .ThenBy(s => s.Kind == "vendor" ? (s.UnitPrice > 0 ? s.UnitPrice : long.MaxValue) : 0)
                .ThenByDescending(s => s.Chance)
                .ThenBy(s => s.Label == SourceLabels.Lodge ? 0 : 1)
                .ToList();
            return ordered.Take(max).ToList();
        }
    }

    // money as the game writes it: 1g 20s 5c
    public static class ProfMoney
    {
        public static string Format(long copper) => MoneyFormat.Text(Math.Max(0, copper));
    }

    // a source table for tests and for a page without any data: answers from dictionaries
    public sealed class FixedSources : IProfessionSources
    {
        public readonly Dictionary<int, List<SourceLine>> Lines = new Dictionary<int, List<SourceLine>>();
        public readonly Dictionary<int, ItemPrice> Prices = new Dictionary<int, ItemPrice>();
        public bool SoldByVendor(int itemId) => Lines.TryGetValue(itemId, out var l) && l.Any(s => s.Kind == "vendor");
        public ItemPrice PriceOf(int itemId) => Prices.TryGetValue(itemId, out var p) ? p : new ItemPrice(0, PriceKind.Unknown);
        public List<SourceLine> SourcesOf(int itemId, int max) => Lines.TryGetValue(itemId, out var l) ? LiveSources.Rank(l, max) : new List<SourceLine>();
    }

    // the one hook to open an Atlas link (item:, spell:, npc:, obj:, quest:, zone:, skill:). The Atlas page subscribes when it exists; until then
    // `Opened` is simply raised and a link does nothing else. Every item / spell / npc in the Professions page goes through here.
    public static class ProfessionLinks
    {
        public static event Action<string> OpenRequested;
        public static string Last;                       // the last link asked for (self-test)
        public static void Open(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            Last = id;
            try { OpenRequested?.Invoke(id); } catch (Exception e) { Util.Log("professions: link " + id + ": " + e.Message); }
        }
        public static bool HasListener => OpenRequested != null;
    }
}
