using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services
{
    // The Characters page's one list (ARCHITECTURE.md "Migration", dual read): characters from Elan's Hub (ElansHubData, key "Realm-Name") are primary;
    // the legacy Elan's Bags characters (ElansBags.lua) fill in when the Hub has no usable bags / bank module for that character, or when the legacy
    // record is newer. Characters are joined on "Realm-Name" EXACTLY (ordinal): same names on different realms never mix, and there is no name-only
    // fallback for Hub characters. Everything is converted into the model the inventory views already use (InvChar containers, InvGuild), so the grids,
    // search and the cross-character tooltip counts work the same from either source. Pure: inputs are not changed.
    public static class CharacterMerge
    {
        public const string FromHub = "Elan's Hub", FromBags = "Elan's Bags";

        // "Realm-Name" of a legacy character (its saved key when it has no realm)
        public static string KeyOf(InvChar c) => !string.IsNullOrEmpty(c.Realm) && !string.IsNullOrEmpty(c.Name) ? c.Realm + "-" + c.Name : c.Key;

        // "Realm-Name" -> realm, name (the name has no dash, a realm may: split at the last one)
        public static void Split(string key, out string realm, out string name)
        {
            realm = null; name = key;
            if (string.IsNullOrEmpty(key)) return;
            var i = key.LastIndexOf('-');
            if (i > 0 && i < key.Length - 1) { realm = key.Substring(0, i); name = key.Substring(i + 1); }
        }

        public static InvData Merge(HubModel hub, InvData legacy)
        {
            hub = hub ?? new HubModel();
            legacy = legacy ?? new InvData();
            var res = new InvData { Client = legacy.Client, Hub = hub };

            var old = new Dictionary<string, InvChar>(StringComparer.Ordinal);
            foreach (var c in legacy.Chars) old[KeyOf(c)] = c;

            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in hub.Characters)
            {
                old.TryGetValue(h.Key, out var o);
                done.Add(h.Key);
                res.Chars.Add(FromHubChar(h, o, hub));
            }
            foreach (var kv in old)
            {
                if (done.Contains(kv.Key)) continue;
                var c = kv.Value.Clone();
                c.BagsFrom = c.BagsCont != null || c.Bags.Count > 0 ? FromBags : null;
                c.BankFrom = c.Bank != null ? FromBags : null;
                res.Chars.Add(c);
            }
            res.Chars = res.Chars.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Realm, StringComparer.CurrentCultureIgnoreCase).ToList();
            res.Guilds = MergeGuilds(hub, legacy.Guilds);
            return res;
        }

        static InvChar FromHubChar(HubChar h, InvChar o, HubModel hub)
        {
            var core = h.Core;
            Split(h.Key, out var keyRealm, out var keyName);
            var c = new InvChar
            {
                Key = h.Key,
                Name = !string.IsNullOrEmpty(core?.Name) ? core.Name : !string.IsNullOrEmpty(o?.Name) ? o.Name : keyName,
                Realm = !string.IsNullOrEmpty(core?.Realm) ? core.Realm : !string.IsNullOrEmpty(o?.Realm) ? o.Realm : keyRealm,
                Class = !string.IsNullOrEmpty(core?.ClassFile) ? core.ClassFile : !string.IsNullOrEmpty(core?.Class) ? core.Class : o?.Class,
                Faction = !string.IsNullOrEmpty(core?.Faction) ? core.Faction : o?.Faction,
                RaceFile = !string.IsNullOrEmpty(core?.RaceFile) ? core.RaceFile : o?.RaceFile,
                Sex = core != null && core.Sex >= 2 ? core.Sex : (o?.Sex ?? 0),
                Level = core != null && core.Level > 0 ? core.Level : (o?.Level ?? 0),
                Money = core != null ? core.Money : (o?.Money ?? 0),
                Guild = core != null ? core.Guild : o?.Guild,
                Updated = Math.Max(Math.Max(h.At, core?.Updated ?? 0), o?.Updated ?? 0),
                Hidden = o?.Hidden ?? false,
                File = hub.SourceFile ?? o?.File,
                Hub = h,
                Core = core,
            };

            // names / icons the character's own modules know, so slots without a link still read well
            var known = new Dictionary<int, HubItem>();

            // ---- bags and bank: the Hub's when it is usable and not older than the legacy record
            var hb = HubModules.BagsOf(h, "bags");
            var hk = HubModules.BankOf(h);
            bool legacyBags = o != null && (o.BagsCont != null || o.Bags.Count > 0);
            long legacyBagsAt = o == null ? 0 : (o.BagsAt > 0 ? o.BagsAt : o.Updated);
            if (hb != null && (!legacyBags || hb.At >= legacyBagsAt))
            {
                c.BagsCont = ToContainers(hb, false, known);
                c.Bags = Sum(c.BagsCont);
                c.BagsAt = hb.At;
                c.BagsFrom = FromHub;
            }
            else if (legacyBags)
            {
                c.BagsCont = o.BagsCont; c.Bags = o.Bags; c.BagsAt = o.BagsAt > 0 ? o.BagsAt : o.Updated; c.BagsFrom = FromBags;
            }
            bool legacyBank = o != null && o.Bank != null;
            long legacyBankAt = o?.BankAt ?? 0;
            if (hk != null && (!legacyBank || hk.At >= legacyBankAt))
            {
                c.BankCont = ToContainers(hk, true, known);
                c.Bank = Sum(c.BankCont);
                c.BankAt = hk.At;
                c.BankFrom = FromHub;
            }
            else if (legacyBank)
            {
                c.BankCont = o.BankCont; c.Bank = o.Bank; c.BankAt = o.BankAt; c.BankFrom = FromBags;
            }

            // ---- worn items (for the "Worn" counts in tooltips and the search): Hub gear, else the legacy list
            var gear = HubModules.GearOf(h);
            if (gear != null)
            {
                c.Equipped = gear.Slots.Values.Select(it => new InvItem { Id = it.I, Count = 1, Quality = it.Q, Icon = it.Ic, Name = NameFor(it, known) }).ToList();
            }
            else if (o != null) c.Equipped = o.Equipped ?? new List<InvItem>();
            return c;
        }

        public static string NameFor(HubItem it, Dictionary<int, HubItem> known)
        {
            if (!string.IsNullOrEmpty(it.Name)) return it.Name;
            if (known != null && known.TryGetValue(it.I, out var k) && !string.IsNullOrEmpty(k.Name)) return k.Name;
            var ik = ItemKnowledge.Get(it.I);
            if (ik != null && !string.IsNullOrEmpty(ik.Name)) return ik.Name;
            return "Item #" + it.I;
        }

        static List<InvContainer> ToContainers(HubBagsModule m, bool bank, Dictionary<int, HubItem> known)
        {
            foreach (var hc in m.Cont) foreach (var it in hc.S.Values) if (!known.ContainsKey(it.I) || (known[it.I].Name == null && it.Name != null)) known[it.I] = it;
            var res = new List<InvContainer>();
            foreach (var hc in m.Cont)
            {
                int bag = hc.K == "backpack" ? 0 : hc.K == "bank" ? -1 : hc.Index;
                var c = new InvContainer { BagId = bag, Size = hc.N, BagItemId = hc.Id, BagIcon = hc.Ic, BagName = hc.BagName, BagQuality = hc.BagQuality };
                if (!bank && hc.K == "reagent") c.Special = BagKinds.Reagent;
                foreach (var kv in hc.S)
                {
                    var it = kv.Value;
                    int icon = it.Ic;
                    if (icon <= 0 && known.TryGetValue(it.I, out var k) && k.Ic > 0) icon = k.Ic;
                    c.Slots[kv.Key] = new InvSlot { Slot = kv.Key, Id = it.I, Count = Math.Max(1, it.C), Quality = it.Q, Icon = icon, Name = NameFor(it, known) };
                }
                res.Add(c);
            }
            return res.OrderBy(c => c.BagId).ToList();
        }

        // the summed list per item (what the search and the older tooltips use)
        static List<InvItem> Sum(List<InvContainer> conts)
        {
            var sums = new Dictionary<int, InvItem>();
            var order = new List<int>();
            foreach (var ct in conts)
                foreach (var sl in ct.Slots.Values)
                {
                    if (!sums.TryGetValue(sl.Id, out var it)) { sums[sl.Id] = it = new InvItem { Id = sl.Id, Count = 0, Quality = sl.Quality, Icon = sl.Icon, Name = sl.Name }; order.Add(sl.Id); }
                    it.Count += sl.Count;
                    if (it.Icon <= 0) it.Icon = sl.Icon;
                }
            return order.Select(id => sums[id]).ToList();
        }

        // ---- guild banks: the Hub's account module and the legacy ones, per tab the newer scan wins
        static List<InvGuild> MergeGuilds(HubModel hub, List<InvGuild> legacy)
        {
            var map = new Dictionary<string, InvGuild>(StringComparer.Ordinal);
            var gb = HubModules.GuildBankOf(hub);
            var known = new Dictionary<int, HubItem>();
            if (gb != null)
                foreach (var g in gb.Guilds)
                {
                    var ig = new InvGuild { Key = g.Key, Name = g.Name, Realm = g.Realm, Money = g.Money, Updated = g.At };
                    foreach (var t in g.Tabs)
                    {
                        var tab = new InvGuildTab { Index = t.Index, Name = t.Name, Icon = t.Ic, At = t.At, Size = 98 };
                        foreach (var kv in t.S)
                            tab.Slots[kv.Key] = new InvSlot { Slot = kv.Key, Id = kv.Value.I, Count = Math.Max(1, kv.Value.C), Quality = kv.Value.Q, Icon = kv.Value.Ic, Name = NameFor(kv.Value, known) };
                        ig.Tabs.Add(tab);
                        ig.Meta.Add(new InvGuildTabMeta { Index = t.Index, Name = t.Name, Icon = t.Ic, CanView = true });
                        if (t.At > ig.Updated && g.At <= 0) ig.Updated = t.At;
                    }
                    ig.NumTabs = g.Tabs.Count == 0 ? 0 : g.Tabs.Max(t => t.Index);
                    map[g.Key] = ig;
                }
            foreach (var lg in legacy ?? new List<InvGuild>())
            {
                if (!map.TryGetValue(lg.Key, out var hg)) { map[lg.Key] = lg; continue; }
                var merged = new InvGuild { Key = hg.Key, Name = hg.Name, Realm = hg.Realm ?? lg.Realm, NumTabs = Math.Max(hg.NumTabs, lg.NumTabs) };
                bool legacyNewer = lg.Updated > hg.Updated;
                merged.Money = legacyNewer ? lg.Money : hg.Money;
                merged.Updated = Math.Max(hg.Updated, lg.Updated);
                foreach (var idx in hg.Tabs.Select(t => t.Index).Union(lg.Tabs.Select(t => t.Index)))
                {
                    var a = hg.Tabs.FirstOrDefault(t => t.Index == idx);
                    var b = lg.Tabs.FirstOrDefault(t => t.Index == idx);
                    merged.Tabs.Add(a != null && (b == null || a.At >= b.At) ? a : b);
                }
                foreach (var idx in hg.Meta.Select(t => t.Index).Union(lg.Meta.Select(t => t.Index)))
                    merged.Meta.Add(hg.Meta.FirstOrDefault(t => t.Index == idx) ?? lg.Meta.First(t => t.Index == idx));
                merged.Tabs = merged.Tabs.OrderBy(t => t.Index).ToList();
                merged.Meta = merged.Meta.OrderBy(t => t.Index).ToList();
                map[lg.Key] = merged;
            }
            return map.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
