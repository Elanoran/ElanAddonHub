using System;
using System.Collections.Generic;
using System.Linq;

namespace ElansAddonHub.Services.Db2
{
    // ============================================================================================================================
    // Permanent enchantments, read from the player's OWN game files (read-only): SpellItemEnchantment (what an enchant gives: stats, damage, resistances, spells)
    // and SpellEquippedItems (which item class / subclasses / inventory types a spell may be cast on). Which spell applies which enchant comes from the spell's
    // own effect (SpellData: effect 53, the enchant id is its first misc value). Layouts: wowdev/WoWDBDefs, checked against the real files of build 1.60.1.70338
    // (SpellItemEnchantment table E05AC589 layout 952B72B2, SpellEquippedItems table E44ABBD4 layout D9177916). A table of another layout = the layer is
    // unavailable with a calm note; nothing is guessed. Not saved to disk: the two tables are small and read once per run, on the thread that first asks.
    // ============================================================================================================================
    public sealed class EnchantInfo
    {
        public int Id, Icon, MinLevel, MaxLevel, ItemLevelMin, ItemLevelMax, SkillId, SkillRank, Flags;
        public string Name = "";
        public int[] Type = new int[3], Points = new int[3], Arg = new int[3];     // effect kind (5 stat, 2 damage, 4 resistance, 1 / 3 / 7 spells, 8 socket), amount, argument (stat type, school, spell)
        public const int KindDamage = 2, KindResist = 4, KindStat = 5;
        public bool HasEffect => Type.Any(t => t != 0);
        // the name with the game's tokens filled in: "+$k1 Agility" -> "+9 Agility" ($k1 .. $k3 are the amounts of the three effects)
        public string Text => string.IsNullOrEmpty(Name) ? "" : Name.Replace("$k1", Points[0].ToString()).Replace("$k2", Points[1].ToString()).Replace("$k3", Points[2].ToString());
    }

    // what a spell may be applied to; `Class` -1 = any, a mask of 0 or -1 = any
    public struct EquipReq
    {
        public int Class, InvMask, SubMask;
        public bool Fits(ClientItem it)
        {
            if (it == null) return false;
            if (Class >= 0 && it.ClassId != Class) return false;
            if (SubMask != 0 && SubMask != -1 && (it.SubclassId < 0 || it.SubclassId > 30 || ((SubMask >> it.SubclassId) & 1) == 0)) return false;
            if (InvMask != 0 && InvMask != -1 && (it.InventoryType < 0 || it.InventoryType > 30 || ((InvMask >> it.InventoryType) & 1) == 0)) return false;
            return true;
        }
    }

    public sealed class EnchantSet
    {
        public readonly Dictionary<int, EnchantInfo> Enchants = new Dictionary<int, EnchantInfo>();
        public readonly Dictionary<int, List<EquipReq>> Equipped = new Dictionary<int, List<EquipReq>>();     // spell -> alternatives
        public string Problem = "";                                                                          // "" = fine
        public EnchantInfo Get(int id) => Enchants.TryGetValue(id, out var e) ? e : null;
        public bool Applies(int spell, ClientItem it) => Equipped.TryGetValue(spell, out var l) && l.Any(r => r.Fits(it));
    }

    public static class EnchantParse
    {
        public static readonly SpellTables.Def EnchantDef = new SpellTables.Def { Name = "SpellItemEnchantment", FileId = 1362771, TableHash = 0xE05AC589, Layouts = new uint[] { 0x952B72B2 } };
        public static readonly SpellTables.Def EquippedDef = new SpellTables.Def { Name = "SpellEquippedItems", FileId = 1140011, TableHash = 0xE44ABBD4, Layouts = new uint[] { 0xD9177916 } };

        // SpellItemEnchantment (stored columns): Name_lang(0) HordeName_lang(1) Duration(2) Charges(3) Effect[3](4) EffectPointsMin[3](5) EffectArg[3](6) Flags(7)
        // EffectScalingPoints[3](8) ScalingClass(9) ScalingClassRestricted(10) Condition_ID(11) RequiredSkillID(12) RequiredSkillRank(13) MinLevel(14) MaxLevel(15)
        // IconFileDataID(16) ItemLevelMin(17) ItemLevelMax(18) ... ; the id is the id list
        // SpellEquippedItems: SpellID(0) EquippedItemClass(1) EquippedItemInvTypes(2) EquippedItemSubclass(3)
        public static EnchantSet All(byte[] enchant, byte[] equipped)
        {
            var set = new EnchantSet();
            var e = SpellTables.Open(enchant, EnchantDef);
            if (!e.Ok) { set.Problem = e.Problem; return set; }
            var q = SpellTables.Open(equipped, EquippedDef);
            if (!q.Ok) { set.Problem = q.Problem; return set; }
            try
            {
                var t = e.Table;
                for (int i = 0; i < t.Count; i++)
                {
                    var info = new EnchantInfo
                    {
                        Id = t.IdAt(i), Name = t.String(i, 0) ?? "", Flags = SpellTables.Int(t, i, 7), SkillId = SpellTables.Int(t, i, 12), SkillRank = SpellTables.Int(t, i, 13), MinLevel = SpellTables.Int(t, i, 14),
                        MaxLevel = SpellTables.Int(t, i, 15), Icon = SpellTables.Int(t, i, 16), ItemLevelMin = SpellTables.Int(t, i, 17), ItemLevelMax = SpellTables.Int(t, i, 18),
                    };
                    for (int k = 0; k < 3; k++) { info.Type[k] = SpellTables.IntArr(t, i, 4, k, 3); info.Points[k] = SpellTables.IntArr(t, i, 5, k, 3); info.Arg[k] = SpellTables.IntArr(t, i, 6, k, 3); }
                    if (info.Id > 0) set.Enchants[info.Id] = info;
                }
                var u = q.Table;
                for (int i = 0; i < u.Count; i++)
                {
                    int spell = SpellTables.Int(u, i, 0);
                    if (spell <= 0) continue;
                    if (!set.Equipped.TryGetValue(spell, out var l)) set.Equipped[spell] = l = new List<EquipReq>();
                    l.Add(new EquipReq { Class = SpellTables.Int(u, i, 1), InvMask = SpellTables.Int(u, i, 2), SubMask = SpellTables.Int(u, i, 3) });
                }
            }
            catch (Exception ex) { set = new EnchantSet { Problem = "could not be read: " + ex.Message }; }
            return set;
        }
    }

    public static class EnchantData
    {
        public enum State { NotStarted, Disabled, Unavailable, Ready }
        public static bool AutoStart = true;                 // off in the self-test (it must not read the real game)
        static readonly object gate = new object();
        static volatile EnchantSet data;
        static volatile State state = State.NotStarted;
        static string reason;
        public static State Status => state;
        public static string Reason => reason;
        public static EnchantSet Set => data;
        public static bool Ready => state == State.Ready && data != null;
        public static int Revision;                          // changes whenever the data does (part of the engine's cache key)

        public static void SetForTest(EnchantSet s, State st = State.Ready, string why = null) { lock (gate) { data = s; state = st; reason = why; Revision++; } }
        public static void ResetForTest() { lock (gate) { data = null; state = State.NotStarted; reason = null; Revision++; } }

        // read the tables once (blocking: the engine calls this on its worker); a later call returns at once
        public static bool Ensure()
        {
            if (state == State.Ready) return true;
            lock (gate)
            {
                if (state == State.Ready) return true;
                if (!AutoStart || state == State.Unavailable || state == State.Disabled) return false;
                bool on; try { on = ItemData.Enabled(); } catch { on = true; }
                if (!on) { state = State.Disabled; return false; }
                var root = ItemData.WowRoot();
                if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) { state = State.Unavailable; reason = "the WoW folder is not set"; return false; }
                try
                {
                    var flavor = ItemData.Flavor() ?? "_classic_beta_";
                    byte[] a = null, b = null;
                    using (var casc = CascStorage.Open(root, flavor))
                    {
                        casc.ZeroFillEncrypted = true;
                        var keys = casc.ResolveFileIds(new HashSet<int> { EnchantParse.EnchantDef.FileId, EnchantParse.EquippedDef.FileId });
                        if (keys.TryGetValue(EnchantParse.EnchantDef.FileId, out var k1)) a = casc.ReadByContentKey(k1);
                        if (keys.TryGetValue(EnchantParse.EquippedDef.FileId, out var k2)) b = casc.ReadByContentKey(k2);
                    }
                    var set = EnchantParse.All(a, b);
                    if (set.Problem.Length > 0) { state = State.Unavailable; reason = set.Problem; Util.Log("enchant data: " + set.Problem); return false; }
                    data = set; state = State.Ready; Revision++;
                    return true;
                }
                catch (Exception e) { state = State.Unavailable; reason = "could not be read: " + e.Message; Util.Log("enchant data: " + e); return false; }
            }
        }
    }
}
