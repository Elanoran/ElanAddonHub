using System;
using System.Collections.Generic;
using System.Linq;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services.Atlas
{
    // What the Forever server told ANY member's game about a quest, a creature or an object: this PC's own client cache (SeenData) merged with what the
    // Lodge's members shared (LodgeAtlas seen tables, server 2.25 with the details). Per id the record of the newest game build wins; on the same build this
    // PC's own copy wins. Both are the server's own words (label "Forever"); `FromLodge` tells the page which one it got.
    public static class SeenView
    {
        public static Func<LodgeAtlas> Lodge = () => AtlasQuery.LodgeOrNull();      // the Atlas page's Lodge

        static LodgeAtlas L() { try { return Lodge(); } catch { return null; } }

        public static WdbQuest Quest(int id) => Quest(id, out _);

        public static WdbQuest Quest(int id, out bool fromLodge)
        {
            fromLodge = false;
            var own = SeenData.Quest(id);
            var lq = L()?.QuestInfo(id);
            if (lq == null) return own;
            if (own == null || (lq.Detailed && (!own.Full || lq.Build > SeenData.LastBuild))) { fromLodge = true; return lq.ToWdb(); }
            return own;
        }

        // every quest with its details any member has seen (own first), for the race start zones and the "quests that ask for it" lists
        public static List<WdbQuest> AllQuests()
        {
            var res = new Dictionary<int, WdbQuest>();
            var snap = SeenData.Snapshot;
            if (snap != null) foreach (var q in snap.Quests.Values) res[q.Id] = q;
            var l = L();
            if (l != null)
                foreach (var lq in l.AllSeenQuests)
                {
                    if (!lq.Detailed) continue;
                    if (!res.TryGetValue(lq.Id, out var own) || !own.Full || lq.Build > SeenData.LastBuild) res[lq.Id] = lq.ToWdb();
                }
            return res.Values.ToList();
        }

        // how many quests with details the Atlas knows (own + Lodge), and how many the client's quest table lists
        public static int DetailedQuestCount() => AllQuests().Count(q => q.Full);

        // a creature's type flags and kill credits: own cache, else the Lodge's (null = nobody has seen it)
        public sealed class NpcFacts { public string Name = ""; public int Build; public uint TypeFlags; public int Kc1, Kc2, Family, Type; public bool FromLodge; public bool Tameable => (TypeFlags & WdbCreature.TypeTameable) != 0; public bool BossLevel => (TypeFlags & WdbCreature.TypeBoss) != 0; }

        public static NpcFacts Npc(int id)
        {
            var own = SeenData.Npc(id);
            var ln = L()?.Npc(id);
            if (ln != null && ln.Build > 0 && (own == null || ln.Build > SeenData.LastBuild))
                return new NpcFacts { Name = ln.Name, Build = ln.Build, TypeFlags = ln.TypeFlags, Kc1 = ln.Kc1, Kc2 = ln.Kc2, Family = ln.Family, Type = ln.Type, FromLodge = true };
            if (own != null) return new NpcFacts { Name = own.Name, Build = SeenData.LastBuild, TypeFlags = own.Flags0, Kc1 = own.KillCredit1, Kc2 = own.KillCredit2, Family = own.Family, Type = own.Type };
            return null;
        }

        // every creature either side knows that is a beast (type 1) or carries a pet family, with the same winner rule as Npc(id): the newest game build, this PC's copy on a tie.
        // Id -> facts; used by the SharedData.lua "beasts" section for Hunter Helper.
        public static Dictionary<int, NpcFacts> BeastFacts()
        {
            var res = new Dictionary<int, NpcFacts>();
            var snap = SeenData.Snapshot;
            if (snap != null)
                foreach (var n in snap.Npcs.Values)
                    res[n.Id] = new NpcFacts { Name = n.Name, Build = SeenData.LastBuild, TypeFlags = n.Flags0, Kc1 = n.KillCredit1, Kc2 = n.KillCredit2, Family = n.Family, Type = n.Type };
            var l = L();
            if (l != null)
                foreach (var ln in l.AllSeenNpcs)
                {
                    if (ln.Build <= 0 || ln.Build <= SeenData.LastBuild && res.ContainsKey(ln.Id)) continue;
                    res[ln.Id] = new NpcFacts { Name = ln.Name, Build = ln.Build, TypeFlags = ln.TypeFlags, Kc1 = ln.Kc1, Kc2 = ln.Kc2, Family = ln.Family, Type = ln.Type, FromLodge = true };
                }
            foreach (var id in res.Where(kv => kv.Value.Type != 1 && kv.Value.Family <= 0).Select(kv => kv.Key).ToList()) res.Remove(id);   // only beasts and pet families
            return res;
        }

        // the Lock an object opens with (own cache, else the Lodge's); 0 = unknown
        public static int LockOf(int objectId)
        {
            var own = SeenData.Object(objectId);
            if (own != null && own.LockId > 0) return own.LockId;
            var lo = L()?.Object(objectId);
            return lo != null && lo.Lock > 0 ? lo.Lock : 0;
        }

        // "Herbalism 70" for an object whose lock needs a gathering / lockpicking skill (null = none known)
        public static LockNeed? SkillNeedOf(int objectId)
        {
            int lk = LockOf(objectId);
            return lk > 0 ? SpellData.World?.SkillNeed(lk) : null;
        }

        public static string SkillNeedText(LockNeed n)
        {
            var name = SpellData.World?.LockTypeName(n.LockType);
            if (n.LockType == WorldLookups.LockTypeLockpicking) name = "Lockpicking";
            if (string.IsNullOrEmpty(name)) return null;
            return n.Skill > 0 ? name + " " + n.Skill : name;
        }
    }
}
