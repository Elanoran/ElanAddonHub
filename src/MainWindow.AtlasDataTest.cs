using System;
using Elans.Shared;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "atlasdata" (2.77): how the Atlas classifies objects (ObjectKinds: the lock first, an Alliance chest with a herb in it is a chest, real herbs and veins
    // stay herbs and ore), the Classic baseline's new fields (object locks, quest chains, enders; index format 2 and a format-1 file still read and rebuilt),
    // quest chains (QuestChains: step N of M, pre-quests, exclusive and "all of" groups, breadcrumbs, cycles and long chains guarded, Forever's own quest lines
    // and next-quest answers) with the chain card on the quest page, and the game version of a find (Toolkit "b" -> Lodge "gb", shown quietly, "unknown version"
    // for an old find). Invented data only; pictures with ELANSHUB_SHOTS=1.
    public partial class MainWindow
    {
        const int DtHerb = 2447, DtHerb2 = 765, DtOre = 2770, DtStone = 2835, DtGem = 774, DtSword = 200, DtCloth = 2589, DtPotion = 118, DtQuestItem = 5000;

        static ItemSnapshot AtlasDataItems() => new ItemSnapshot(new[]
        {
            CItem(DtHerb, "Peacebloom", 1, 5, 1, 0, 7, 9), CItem(DtHerb2, "Silverleaf", 1, 5, 1, 0, 7, 9), CItem(DtOre, "Copper Ore", 1, 10, 1, 0, 7, 7), CItem(DtStone, "Rough Stone", 1, 5, 1, 0, 7, 7),
            CItem(DtGem, "Malachite", 2, 7, 1, 0, 3, 7), CItem(DtSword, "Rusty Sword", 0, 10, 1, 13, 2, 7), CItem(DtCloth, "Linen Cloth", 1, 5, 1, 0, 7, 5), CItem(DtPotion, "Minor Healing Potion", 1, 5, 1, 0, 0, 1),
            CItem(DtQuestItem, "Strange Petal", 1, 1, 1, 0, 12, 0),
        }, new ClientItem[0]);

        // the locks of the invented game files: 29 / 31 Herbalism 1 / 70, 38 Mining 1, 57 a chest ("Open" and Pick Lock), 43 "Open" only
        static WorldLookups AtlasDataWorld()
        {
            var w = new WorldLookups();
            w.Locks[29] = new[] { new LockNeed { LockType = 2, Skill = 1 } };
            w.Locks[31] = new[] { new LockNeed { LockType = 2, Skill = 70 } };
            w.Locks[38] = new[] { new LockNeed { LockType = 3, Skill = 1 } };
            w.Locks[57] = new[] { new LockNeed { LockType = 5, Skill = 0 }, new LockNeed { LockType = 1, Skill = 0 } };
            w.Locks[43] = new[] { new LockNeed { LockType = 5, Skill = 0 } };
            w.LockTypes[1] = "Pick Lock"; w.LockTypes[2] = "Herbalism"; w.LockTypes[3] = "Mining"; w.LockTypes[5] = "Open";
            w.QuestLines[5] = new QuestLineInfo { Id = 5, Name = "Toxic Soil", Quests = new[] { 280, 281, 282 } };
            foreach (var q in w.QuestLines[5].Quests) w.LineOfQuest[q] = 5;
            return w;
        }

        static ClassicIndex AtlasDataClassic()
        {
            var idx = new ClassicIndex();
            idx.Provenance = new ClassicProvenance { DumpName = "ClassicDB_test.sql.gz", CommitSha = "abc", CommitDate = "2024-05-02T10:00:00Z", BuiltUtc = "2024-06-01T00:00:00Z", FormatVersion = ClassicIndex.FormatVersion };
            void Obj(int id, string name, int type, int lockId, params int[] loot)
            {
                idx.Objects[id] = new ClassicObject { Id = id, Name = name, Type = type, LockId = lockId, LootId = loot.Length > 0 ? id : 0, Spawns = new[] { Sp(0, 400 + id % 100, 900) } };
                foreach (var it in loot) idx.Add(it, new ClassicItemSource { Kind = ClassicSourceKind.ObjectLoot, SourceId = id, Chance = 20, MinCount = 1, MaxCount = 1 });
            }
            Obj(3715, "Alliance Chest", 3, 57, DtHerb, DtSword, DtCloth, DtPotion);        // the reported case: a herb drops from it
            Obj(1618, "Peacebloom", 3, 29, DtHerb);                                       // a herb only the baseline knows (lock from data0)
            Obj(1617, "Silverleaf", 3, 29, DtHerb2, DtQuestItem);                         // a herb with a quest item in it
            Obj(1731, "Copper Vein", 3, 38, DtOre, DtStone, DtGem);
            Obj(9001, "Withered Bundle", 3, 0, DtHerb, DtHerb2);                          // no lock known: only herbs = a herb
            Obj(9002, "Sturdy Crate", 3, 0, DtHerb, DtHerb2);                             // no lock known, only herbs, but a container's name
            Obj(9003, "Forgotten Supplies", 3, 0, DtHerb, DtSword);                       // no lock known: a weapon makes it a chest
            Obj(9004, "Dark Iron Deposit", 3, 0);                                         // nothing but a name
            Obj(9005, "Altar Lever", 10, 43, DtHerb);                                     // a goober with an Open lock and a herb in it: never a herb
            Obj(9006, "Oily Blackmouth School", 25, 0, DtCloth);
            idx.Npcs[2] = new ClassicNpc { Id = 2, Name = "Innkeeper Hale", MinLevel = 20, MaxLevel = 20, NpcFlags = 0x82, Spawns = new[] { Sp(0, 150, 200) } };
            idx.Npcs[3] = new ClassicNpc { Id = 3, Name = "Warden Fyl", MinLevel = 20, MaxLevel = 20, NpcFlags = 0x2, Spawns = new[] { Sp(0, 250, 300) } };
            ClassicQuest Q(int id, string title, int prev = 0, int next = 0, int nic = 0, int group = 0, int crumb = 0)
                => idx.Quests[id] = new ClassicQuest { Id = id, Title = title, MinLevel = 10, QuestLevel = 12, ZoneOrSort = 78, PrevQuestId = prev, NextQuestId = next, NextQuestInChain = nic, ExclusiveGroup = group, BreadcrumbFor = crumb, Givers = new[] { new ClassicQuestGiver { Id = 2 } } };
            for (int i = 201; i <= 215; i++) Q(i, "The Long Road, part " + (i - 200), i > 201 ? i - 1 : 0, 0, i < 215 ? i + 1 : 0);
            var q208 = idx.Quests[208];
            q208.RequiredSkill = 171; q208.RequiredSkillValue = 50; q208.RequiredMinRepFaction = 72; q208.RequiredMinRepValue = 9000;
            q208.Enders = new[] { new ClassicQuestGiver { Id = 3 } };
            Q(220, "A Letter for the Road", crumb: 201);
            Q(230, "Path of the Blade", group: 230); Q(231, "Path of the Staff", group: 230); Q(232, "Path of the Bow", group: 230);
            Q(240, "Gather the Shards", next: 242, group: -240); Q(241, "Gather the Gems", next: 242, group: -240); Q(242, "Forge the Key");
            Q(250, "Side Errand", prev: -208);
            Q(260, "Round One", nic: 261); Q(261, "Round Two", nic: 262); Q(262, "Round Three", nic: 260);
            Q(270, "Two Ways", nic: 271); Q(271, "The Baseline's Way"); Q(272, "Forever's Way");
            Q(280, "Bad Soil"); Q(281, "Worse Soil"); Q(282, "Clean Soil");
            Q(290, "The Fork"); Q(291, "Left Branch", prev: 290); Q(292, "Right Branch", prev: 290);
            Q(299, "Lonely Errand");
            idx.Finish();
            return idx;
        }

        static SeenSnapshot AtlasDataSeen()
        {
            var briar = new WdbObject { Id = 9100, Name = "Briarthorn", Type = 3 }; briar.Data[0] = 31;     // a herb only the client has seen: its lock from the cache
            var chest = new WdbObject { Id = 3715, Name = "Alliance Chest", Type = 3 }; chest.Data[0] = 57;
            return new SeenSnapshot(new WdbCreature[0], new[] { briar, chest },
                new[] { new WdbQuest { Id = 270, Title = "Two Ways", Level = 12, Full = true, NextQuest = 272 }, new WdbQuest { Id = 272, Title = "Forever's Way", Level = 12, Full = true } });
        }

        async Task<string> AtlasDataTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "atlasdata");
            var savedLodge = AtlasQuery.Context.Lodge; var savedNow = AtlasQuery.Context.NowUtc; var savedStamp = FindVersions.CurrentStamp; var savedWidth = Width;
            var savedTiles = MapRender.TileSourceForTest; var savedMapDir = MapRender.DirOverride;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                MapRender.DirOverride = Path.Combine(work, "maps");
                MapRender.TileSourceForTest = fdid => Solid(256, 256, 80, 100, 70);
                AtlasQuery.Context.NowUtc = () => AtUtc(AtNow);

                // ============================================================================================================ objects: the rule table
                LockNeed[] L(params int[] typeSkill) { var a = new LockNeed[typeSkill.Length / 2]; for (int i = 0; i < a.Length; i++) a[i] = new LockNeed { LockType = typeSkill[2 * i], Skill = typeSkill[2 * i + 1] }; return a; }
                List<int[]> Loot(params int[] cs) { var l = new List<int[]>(); for (int i = 0; i + 1 < cs.Length; i += 2) l.Add(new[] { cs[i], cs[i + 1] }); return l; }
                var chestLoot = Loot(7, 9, 2, 7, 7, 5, 0, 1, 4, 2);
                // the rule before 2.77: any herb in the loot made it a herb, whatever its lock or name
                string OldRule(ObjectKinds.Facts f) => f.Type == 25 ? "fishing" : f.Lock != null && f.Lock.Any(n => n.LockType == 2) ? "herbs" : f.Loot.Any(c => c[0] == 7 && c[1] == 9) ? "herbs" : "?";
                var table = new List<(string what, ObjectKinds.Facts f, string want)>
                {
                    ("Alliance Chest (lock: Open / Pick Lock, a herb among weapons and cloth)", new ObjectKinds.Facts { Type = 3, Name = "Alliance Chest", Lock = L(5, 0, 1, 0), Loot = chestLoot }, "chests"),
                    ("Alliance Chest with its lock unknown (the loot must not make it a herb)", new ObjectKinds.Facts { Type = 3, Name = "Alliance Chest", Loot = chestLoot }, "chests"),
                    ("a chest whose lock is known and whose loot is ONLY herbs", new ObjectKinds.Facts { Type = 3, Name = "Herbalist's Satchel", Lock = L(1, 25), Loot = Loot(7, 9, 7, 9) }, "chests"),
                    ("Peacebloom (lock: Herbalism 1)", new ObjectKinds.Facts { Type = 3, Name = "Peacebloom", Lock = L(2, 1), Loot = Loot(7, 9) }, "herbs"),
                    ("Silverleaf with a quest item (lock: Herbalism 1)", new ObjectKinds.Facts { Type = 3, Name = "Silverleaf", Lock = L(2, 1), Loot = Loot(7, 9, 12, 0) }, "herbs"),
                    ("a herb whose lock is unknown (herb + quest item)", new ObjectKinds.Facts { Type = 3, Name = "Gahz'ridian", Loot = Loot(7, 9, 12, 0) }, "herbs"),
                    ("Copper Vein (lock: Mining 1)", new ObjectKinds.Facts { Type = 3, Name = "Copper Vein", Lock = L(3, 1), Loot = Loot(7, 7, 7, 7, 3, 7) }, "ore"),
                    ("a vein whose lock is unknown (ore, stone and a gem)", new ObjectKinds.Facts { Type = 3, Name = "Small Thorium Vein", Loot = Loot(7, 7, 7, 7, 3, 7, 3, 7) }, "ore"),
                    ("a mining lock wins over a herb in the loot", new ObjectKinds.Facts { Type = 3, Name = "Ooze Covered Vein", Lock = L(3, 75), Loot = Loot(7, 7, 7, 9) }, "ore"),
                    ("a crate (name) whose loot is only herbs, lock unknown", new ObjectKinds.Facts { Type = 3, Name = "Sturdy Crate", Loot = Loot(7, 9, 7, 9) }, "chests"),
                    ("a deposit known only by its name", new ObjectKinds.Facts { Type = 3, Name = "Dark Iron Deposit" }, "ore"),
                    ("an 'Ore Box' is a container, not ore", new ObjectKinds.Facts { Type = 3, Name = "Ore Box", Loot = Loot(7, 7) }, "chests"),
                    ("a lever with an Open lock and a herb in it", new ObjectKinds.Facts { Type = 10, Name = "Altar Lever", Lock = L(5, 0), Loot = Loot(7, 9) }, "other"),
                    ("a fishing hole", new ObjectKinds.Facts { Type = 25, Name = "Oily Blackmouth School", Loot = Loot(7, 5) }, "fishing"),
                    ("a mailbox (nothing known)", new ObjectKinds.Facts { Type = 19, Name = "Mailbox" }, "other"),
                    ("a quest plant anyone can gather (lock: Herbalism 0) is not a herb", new ObjectKinds.Facts { Type = 3, Name = "Serpentbloom", Lock = L(2, 0), Loot = Loot(12, 0) }, "other"),
                    ("a beached sea creature (lock: Herbalism 0, nothing in it)", new ObjectKinds.Facts { Type = 2, Name = "Beached Sea Creature", Lock = L(2, 0) }, "other"),
                    ("a quest object with an Open lock and nothing but quest loot is not a chest", new ObjectKinds.Facts { Type = 3, Name = "Sealed Tome", Lock = L(5, 0), Loot = Loot(12, 0) }, "other"),
                    ("a quest dig site with a herb and a quest item (Open lock) is not a chest", new ObjectKinds.Facts { Type = 3, Name = "Gahz'ridian", Lock = L(5, 0), Loot = Loot(7, 9, 12, 0) }, "other"),
                };
                var wrong = table.Where(r => ObjectKinds.Classify(r.f).Kind != r.want).Select(r => r.what + " -> " + ObjectKinds.Classify(r.f)).ToList();
                Check("objects: the rule table (" + table.Count + " cases): chests with a herb in them stay chests, herbs and veins stay herbs and ore, a known non-gathering lock is never a herb, names only decide when nothing else does", wrong.Count == 0, string.Join(" | ", wrong));
                Check("objects: the old rule got the Alliance chest wrong (the regression this guards)", OldRule(table[0].f) == "herbs" && OldRule(table[1].f) == "herbs" && OldRule(table[2].f) == "herbs");
                Check("objects: the verdict says why (lock / loot / name)", ObjectKinds.Classify(table[0].f).Why.StartsWith("lock") && ObjectKinds.Classify(table[5].f).Why.StartsWith("loot") && ObjectKinds.Classify(table[10].f).Why == "name");

                // ============================================================================================================ objects: the live lists
                var classic = AtlasDataClassic();
                var spells = AtlasTestSpells(); spells.World = AtlasDataWorld();
                var items = AtlasDataItems();
                void Layers()
                {
                    ItemData.SetForTest(items, ItemData.State.Ready);
                    SpellData.SetForTest(spells);
                    SeenData.SetForTest(AtlasDataSeen());
                    MapData.SetForTest(new MapIndex(AtlasTestMapSet()));
                    ClassicAtlas.Use(classic);
                    AtlasQuery.Context.Lodge = () => null;
                    AtlasBrowse.ForgetForTest();
                }
                Layers();
                var groups = new Dictionary<int, string>();
                await Task.Run(() => { foreach (var id in new[] { 3715, 1618, 1617, 1731, 9001, 9002, 9003, 9004, 9005, 9006, 9100 }) groups[id] = AtlasBrowse.ObjectGroupOf(id); });
                string G(int id) => groups.TryGetValue(id, out var g) ? g : "?";
                Check("objects: the Alliance chest is listed under Chests, not Herbs (the lock from the client's cache: Open / Pick Lock)", G(3715) == "chests", G(3715));
                Check("objects: Peacebloom and Silverleaf (lock only in the Classic baseline) and Briarthorn (lock only in the client's cache) are herbs; the vein is ore",
                    G(1618) == "herbs" && G(1617) == "herbs" && G(9100) == "herbs" && G(1731) == "ore", string.Join(",", groups.Select(kv => kv.Key + "=" + kv.Value)));
                Check("objects: no lock known - a bundle of herbs is a herb, a crate of herbs a chest, a weapon makes a chest, a deposit by its name is ore, the lever is other, the school is fishing",
                    G(9001) == "herbs" && G(9002) == "chests" && G(9003) == "chests" && G(9004) == "ore" && G(9005) == "other" && G(9006) == "fishing", string.Join(",", groups.Select(kv => kv.Key + "=" + kv.Value)));
                var herbList = await Task.Run(() => AtlasBrowse.Run("browse:objects/herbs"));
                var herbIds = herbList.Rows.Select(r => r.Link ?? "").Where(l => l.StartsWith("obj:")).Select(l => int.Parse(l.Substring(4))).OrderBy(i => i).ToList();
                Check("objects: the Herbs list (Objects browse, the world map's herb layer and the gathering guide read the same groups) holds exactly the herbs", herbIds.SequenceEqual(new[] { 1617, 1618, 9001, 9100 }), string.Join(",", herbIds));
                var why = await Task.Run(() => AtlasBrowse.ObjectVerdictOf(3715));
                Check("objects: the audit can tell why an object is where it is (the chest: its lock)", why.Kind == "chests" && why.Why.StartsWith("lock"), why.ToString());
                var peace = await Task.Run(() => AtlasQuery.Build("obj:1618"));
                var briarP = await Task.Run(() => AtlasQuery.Build("obj:9100"));
                Check("object page: the herb only the baseline knows says 'Needs Herbalism 1' (Classic baseline), the one from the cache 'Herbalism 70' (Forever)",
                    peace.Facts.Any(f => f.Label == "Needs" && f.Value == "Herbalism 1" && f.Source == AtlasLabel.Classic) && briarP.Facts.Any(f => f.Label == "Needs" && f.Value == "Herbalism 70" && f.Source == AtlasLabel.Forever),
                    string.Join("; ", peace.Facts) + " || " + string.Join("; ", briarP.Facts));

                // ============================================================================================================ the Classic baseline's new fields
                var dump = string.Join("\n", new[]
                {
                    "INSERT INTO `gameobject_template` (entry,type,displayId,name,data0,data1) VALUES (3715,3,1,'Alliance Chest',57,3715),(1618,3,1,'Peacebloom',29,1618),(9005,10,1,'Altar Lever',43,0),(9006,25,1,'School',4,9006);",
                    "INSERT INTO `quest_template` (entry,Title,MinLevel,QuestLevel,ZoneOrSort,PrevQuestId,NextQuestId,ExclusiveGroup,BreadcrumbForQuestId,NextQuestInChain,RequiredSkill,RequiredSkillValue,RequiredMinRepFaction,RequiredMinRepValue,SrcItemId) VALUES "
                        + "(10,'First',1,2,12,0,11,0,0,11,0,0,0,0,0),(11,'Second',1,2,12,10,0,-5,0,0,171,50,72,9000,DtSrc),(12,'Crumb',1,2,12,0,0,0,10,0,0,0,0,0,0);".Replace("DtSrc", "5000"),
                    "INSERT INTO `creature_questrelation` (id,quest) VALUES (2,10);",
                    "INSERT INTO `creature_involvedrelation` (id,quest) VALUES (3,10),(2,11);",
                    "INSERT INTO `gameobject_involvedrelation` (id,quest) VALUES (1618,11);",
                });
                var built = ClassicImporter.BuildFromText(dump, new List<KeyValuePair<string, string>>());
                var b10 = built.QuestInfo(10); var b11 = built.QuestInfo(11); var b12 = built.QuestInfo(12);
                Check("import: an object's lock comes from data0 for chests and goobers only (not a fishing hole's data0)",
                    built.ObjectInfo(3715)?.LockId == 57 && built.ObjectInfo(1618)?.LockId == 29 && built.ObjectInfo(9005)?.LockId == 43 && built.ObjectInfo(9006)?.LockId == 0);
                Check("import: a quest's chain links, skill and reputation needs, given item and who takes it back are read",
                    b10 != null && b10.NextQuestId == 11 && b10.NextQuestInChain == 11 && b10.Enders.Length == 1 && b10.Enders[0].Id == 3 && b11.PrevQuestId == 10 && b11.ExclusiveGroup == -5 && b11.RequiredSkill == 171 && b11.RequiredSkillValue == 50
                    && b11.RequiredMinRepFaction == 72 && b11.RequiredMinRepValue == 9000 && b11.SourceItem == 5000 && b11.Enders.Length == 2 && b11.Enders.Any(e => e.IsObject && e.Id == 1618) && b12.BreadcrumbFor == 10);
                var v2 = Path.Combine(work, "index2.bin"); built.Save(v2);
                var back2 = ClassicIndex.Load(v2);
                Check("index: format 2 keeps the locks, the chain fields and the enders through a save", back2 != null && back2.Provenance.FormatVersion == 2 && back2.ObjectInfo(3715).LockId == 57 && back2.QuestInfo(11).PrevQuestId == 10 && back2.QuestInfo(11).ExclusiveGroup == -5
                    && back2.QuestInfo(11).RequiredMinRepValue == 9000 && back2.QuestInfo(12).BreadcrumbFor == 10 && back2.QuestInfo(10).Enders.Length == 1 && back2.QuestInfo(11).SourceItem == 5000);
                var v1 = Path.Combine(work, "index1.bin");
                using (var fs = new FileStream(v1, FileMode.Create)) using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest)) using (var bw = new BinaryWriter(gz, new System.Text.UTF8Encoding(false))) built.Write(bw, 1);
                var back1 = ClassicIndex.Load(v1);
                Check("index: a format-1 file (an earlier Outpost) still loads - same objects and quests, the new fields empty - and counts as outdated",
                    back1 != null && back1.Provenance.FormatVersion == 1 && back1.Objects.Count == built.Objects.Count && back1.Quests.Count == 3 && back1.ObjectInfo(3715).LockId == 0 && back1.QuestInfo(11).PrevQuestId == 0
                    && ClassicSetup.Outdated(back1) && !ClassicSetup.Outdated(back2));
                ClassicSetup.ResetUpgradeForTest();
                var fake = new FakeClassic { St = ClassicState.Ready, Idx = back1, Dir = work };
                var setup = new ClassicSetup(fake);
                bool started = setup.UpgradeIfOutdated(false);
                await WaitFor(() => !setup.Busy, 5000);
                bool again = setup.UpgradeIfOutdated(false);
                var realOnly = new ClassicSetup(new FakeClassic { St = ClassicState.Ready, Idx = back1, Dir = work });
                ClassicSetup.ResetUpgradeForTest();
                bool fakeSkipped = !realOnly.UpgradeIfOutdated();
                ClassicSetup.ResetUpgradeForTest();
                bool currentSkipped = !new ClassicSetup(new FakeClassic { St = ClassicState.Ready, Idx = back2, Dir = work }).UpgradeIfOutdated(false);
                Check("index: an outdated index is read again from the dump on this PC once (no download), a current one is left alone", started && fake.Builds == 1 && fake.Downloads == 0 && fake.Queries == 0 && !again && fakeSkipped && currentSkipped,
                    "started " + started + " builds " + fake.Builds + " downloads " + fake.Downloads + " again " + again + " fake " + fakeSkipped + " cur " + currentSkipped);
                ClassicSetup.ResetUpgradeForTest();

                // ============================================================================================================ quest chains: the builder
                QuestLink Ln(int id, int prev = 0, int next = 0, int nic = 0, int group = 0, int crumb = 0) => new QuestLink { Id = id, Title = "Q" + id, Prev = prev, Next = next, NextInChain = nic, Group = group, BreadcrumbFor = crumb };
                var links = new List<QuestLink>();
                for (int i = 1; i <= 7; i++) links.Add(Ln(i, i > 1 ? i - 1 : 0, 0, i < 7 ? i + 1 : 0));
                links.Add(Ln(20, group: 20)); links.Add(Ln(21, group: 20)); links.Add(Ln(22, group: 20));
                links.Add(Ln(30, next: 32, group: -30)); links.Add(Ln(31, next: 32, group: -30)); links.Add(Ln(32));
                links.Add(Ln(40, crumb: 1));
                links.Add(Ln(50, prev: -3));
                links.Add(Ln(60, nic: 61)); links.Add(Ln(61, nic: 62)); links.Add(Ln(62, nic: 60));
                links.Add(Ln(70)); links.Add(Ln(71, prev: 70)); links.Add(Ln(72, prev: 70));
                links.Add(Ln(80, nic: 80));                                                         // points at itself
                links.Add(Ln(90)); links.Add(Ln(91, prev: 90)); links.Add(new QuestLink { Id = 92, Title = "Q92", Prev = 90, Classes = 1 }); links.Add(new QuestLink { Id = 93, Title = "Q93", Prev = 90, Classes = 2 });
                for (int i = 1000; i < 1060; i++) links.Add(Ln(i, 0, 0, i + 1));                    // 60 steps
                var tab = QuestChains.Build(links);
                var c3 = QuestChains.For(tab, 3);
                Check("chains: the third quest of a seven-step chain is 'Step 3 of 7', its steps in order, only it marked current",
                    c3.Total == 7 && c3.Position == 3 && c3.StepText == "Step 3 of 7" && c3.Steps.Select(s => s.Id).SequenceEqual(new[] { 1, 2, 3, 4, 5, 6, 7 }) && c3.Steps.Count(s => s.Current) == 1 && c3.Requires.SequenceEqual(new[] { 2 }) && !c3.Cut, string.Join(",", c3.Steps));
                var c1 = QuestChains.For(tab, 1); var c7 = QuestChains.For(tab, 7);
                Check("chains: the first and the last step (1 of 7, 7 of 7); the first lists its breadcrumb, the third what can be taken while it is in the log",
                    c1.StepText == "Step 1 of 7" && c7.StepText == "Step 7 of 7" && c1.Breadcrumbs.SequenceEqual(new[] { 40 }) && c3.WhileActive.SequenceEqual(new[] { 50 }) && QuestChains.For(tab, 50).RequiresActive.SequenceEqual(new[] { 3 }) && QuestChains.For(tab, 40).BreadcrumbFor == 1);
                var c21 = QuestChains.For(tab, 21); var c32 = QuestChains.For(tab, 32); var c30 = QuestChains.For(tab, 30);
                Check("chains: an exclusive group lists the others (only one can be done); an 'all of' group opens its next quest only when all are done",
                    c21.ExclusiveWith.OrderBy(i => i).SequenceEqual(new[] { 20, 22 }) && c32.OpenedBy.OrderBy(i => i).SequenceEqual(new[] { 30, 31 }) && c32.OpenedByAll && c30.AllOfGroup.SequenceEqual(new[] { 31 }) && c30.Unlocks.Contains(32) && !c32.HasChain,
                    string.Join(",", c32.OpenedBy) + " all " + c32.OpenedByAll + " / " + string.Join(",", c30.Unlocks));
                var c61 = QuestChains.For(tab, 61); var c80 = QuestChains.For(tab, 80);
                Check("chains: a loop in the links stops where it would repeat (each quest once, marked cut), a quest pointing at itself is not a chain", c61.Steps.Count == 3 && c61.Steps.Select(s => s.Id).Distinct().Count() == 3 && c61.Cut && !c80.HasChain);
                var c70 = QuestChains.For(tab, 70);
                Check("chains: a fork has no single next step; both branches are listed as what it opens", !c70.HasChain && c70.Unlocks.OrderBy(i => i).SequenceEqual(new[] { 71, 72 }));
                var c90 = QuestChains.For(tab, 90);
                Check("chains: a quest that also opens class letters continues with the quest for everybody; the letters are what it opens", c90.Steps.Select(x => x.Id).SequenceEqual(new[] { 90, 91 }) && c90.Unlocks.OrderBy(i => i).SequenceEqual(new[] { 92, 93 }), string.Join(",", c90.Steps) + " / " + string.Join(",", c90.Unlocks));
                var cl = QuestChains.For(tab, 1000);
                Check("chains: a very long chain stops at " + QuestChains.MaxSteps + " steps each way (marked cut)", cl.Steps.Count == QuestChains.MaxSteps + 1 && cl.Cut && cl.Position == 1);
                var line = new QuestLineInfo { Id = 5, Name = "Toxic Soil", Quests = new[] { 3, 900, 901 } };
                var cline = QuestChains.For(tab, 900, line, id => "Line quest " + id);
                Check("chains: Forever's quest line gives the chain and its name (step 2 of 3, titles of quests the table does not have)", cline.FromLine && cline.Name == "Toxic Soil" && cline.StepText == "Step 2 of 3" && cline.Steps[2].Title == "Line quest 901");
                Check("chains: an unknown quest has nothing to show", !QuestChains.For(tab, 99999).Any);
                Check("chains: reputation standings", QuestChains.Standing(9000) == "Honored" && QuestChains.Standing(8999) == "Friendly" && QuestChains.Standing(42000) == "Exalted" && QuestChains.Standing(-3001) == "Hostile" && QuestChains.Standing(0) == "Neutral");

                // ============================================================================================================ quest chains: the live data and the page
                var live = await Task.Run(() => QuestChains.LiveFor(270));
                Check("live chains: Forever's own 'next quest' answer wins over the baseline's (Two Ways -> Forever's Way)", live.HasChain && live.Steps.Select(s => s.Id).SequenceEqual(new[] { 270, 272 }) && live.AnyForever, string.Join(",", live.Steps));
                var p208 = await Task.Run(() => AtlasQuery.Build("quest:208"));
                var ch = p208.Custom as QuestChainInfo;
                Check("quest page: the chain rides on the page (step 8 of 15) with a Chain fact, the skill and reputation needs, and who takes it back",
                    ch != null && ch.StepText == "Step 8 of 15" && p208.Facts.Any(f => f.Label == "Chain" && f.Value == "Step 8 of 15" && f.Source == AtlasLabel.Classic)
                    && p208.Facts.Any(f => f.Label == "Requires skill" && f.Value == "Alchemy 50") && p208.Facts.Any(f => f.Label == "Standing" && f.Value == "Honored with Stormwind" && f.Link == AtlasIds.Faction(72))
                    && p208.Section("enders")?.Rows.Any(r => r.Link == "npc:3" && r.Title == "Warden Fyl") == true && ch.WhileActive.Contains(250) && ch.Requires.SequenceEqual(new[] { 207 }),
                    string.Join("; ", p208.Facts));
                var p281 = await Task.Run(() => AtlasQuery.Build("quest:281"));
                Check("quest page: a quest on Forever's quest line shows the line's name and place (no extra Chain fact)", p281.Custom is QuestChainInfo l281 && l281.FromLine && l281.Name == "Toxic Soil" && l281.StepText == "Step 2 of 3"
                    && p281.Facts.Any(f => f.Label == "Quest line" && f.Value == "Toxic Soil (2 of 3)") && !p281.Facts.Any(f => f.Label == "Chain"));
                var p230 = await Task.Run(() => AtlasQuery.Build("quest:230"));
                Check("quest page: a quest with no chain but an exclusive group still gets the card", p230.Custom is QuestChainInfo x230 && !x230.HasChain && x230.ExclusiveWith.Count == 2);
                var p242 = await Task.Run(() => AtlasQuery.Build("quest:242")); var p290 = await Task.Run(() => AtlasQuery.Build("quest:290")); var p299 = await Task.Run(() => AtlasQuery.Build("quest:299"));
                Check("quest page: the 'all of' quest and the fork get the card (opened by all of / opens), a quest with nothing before or after gets none",
                    p242.Custom is QuestChainInfo x242 && !x242.HasChain && x242.OpenedByAll && p290.Custom is QuestChainInfo x290 && x290.Unlocks.Count == 2 && !p299.NotFound && p299.Custom == null);

                // the card in the window: a 15-step chain folds around this step, a click on a step opens it, "Show all" unfolds
                ShowTab("atlas");
                Width = Math.Max(Width, 1280);
                await AtlasView.Open("quest:208"); UpdateLayout();
                var tl = AtlasView.QuestChainTimelineForTest;
                var rows = tl?.Children.OfType<FrameworkElement>().ToList() ?? new List<FrameworkElement>();
                var tags = rows.Select(r => r.Tag as string ?? "").ToList();
                Check("chain card: the timeline shows step 1, steps 5 to 11 around this one (8, highlighted), a fold on each side and step 15",
                    tl != null && tags.Count(t => t == "chain-gap") == 2 && tags.Contains("chain-current") && tags.Contains("chain-step:201") && tags.Contains("chain-step:215") && tags.Contains("chain-step:205") && tags.Contains("chain-step:211") && !tags.Contains("chain-step:204") && !tags.Contains("chain-step:212"),
                    string.Join(",", tags));
                var tx = AtlasTexts(AtlasView);
                Check("chain card: it says 'Step 8 of 15', 'this quest', 3 more steps, and lists what can be taken with it",
                    tx.Contains("Step 8 of 15") && tx.Contains("this quest") && tx.Contains("3 more steps") && tx.Contains("Side Errand") && tx.Contains("Can be taken with it"), string.Join(" | ", tx.Where(t => t.Contains("Step") || t.Contains("more") || t.Contains("Road")).Take(12)));
                tl?.BringIntoView(); UpdateLayout();
                await ShotWait(400); Snapshot(Path.Combine(dir, "atlasdata-quest-chain.png"));
                var all = AtlasView.QuestChainAllForTest;
                if (all != null) ClickButton(all);
                UpdateLayout();
                var tagsAll = tl?.Children.OfType<FrameworkElement>().Select(r => r.Tag as string ?? "").ToList() ?? new List<string>();
                Check("chain card: 'Show all 15 steps' unfolds the whole chain", all != null && tagsAll.Count == 15 && !tagsAll.Contains("chain-gap"), string.Join(",", tagsAll));
                var step207 = AtlasWalk(tl).OfType<Button>().FirstOrDefault(b => b.Tag as string == "chain-open:207");
                if (step207 != null) ClickButton(step207);
                await WaitFor(() => AtlasView.Model?.Id == "quest:207", 5000);
                Check("chain card: a click on a step opens that quest (its own card marks it current)", AtlasView.CurrentId == "quest:207" && AtlasView.Model?.Custom is QuestChainInfo c207 && c207.Position == 7, AtlasView.CurrentId);
                await AtlasView.Open("quest:230"); UpdateLayout();
                tx = AtlasTexts(AtlasView);
                Check("chain card: a quest of an exclusive group says 'Only one of' with the others as links", tx.Contains("Only one of") && tx.Contains("Path of the Staff") && tx.Contains("Path of the Bow") && AtlasView.QuestChainTimelineForTest == null);
                AtlasWalk(AtlasView).OfType<FrameworkElement>().FirstOrDefault(e => e.Tag as string == "quest-chain")?.BringIntoView(); UpdateLayout();
                await ShotWait(300); Snapshot(Path.Combine(dir, "atlasdata-quest-exclusive.png"));

                // ============================================================================================================ finds: the game version
                FindVersions.CurrentStamp = () => "wow_classic_beta|1.60.1.70374|abc";
                var hub = new Dictionary<string, object>
                {
                    ["since"] = 1.0,
                    ["finds"] = new Dictionary<string, object>
                    {
                        ["1"] = new Dictionary<string, object> { ["i"] = (double)DtHerb, ["c"] = 1.0, ["src"] = "obj:1618", ["at"] = (double)(AtNow - 100), ["b"] = 70374.0 },
                        ["2"] = new Dictionary<string, object> { ["i"] = (double)DtSword, ["c"] = 1.0, ["src"] = "obj:3715", ["at"] = (double)(AtNow - 50) },
                        ["3"] = new Dictionary<string, object> { ["i"] = (double)DtCloth, ["c"] = 2.0, ["src"] = "obj:3715", ["at"] = (double)(AtNow - 40), ["b"] = "garbage" },
                    },
                };
                var snap = AtlasObs.FromTables(hub);
                var fs1 = snap.Finds.FirstOrDefault(f => f.Item == DtHerb); var fs2 = snap.Finds.FirstOrDefault(f => f.Item == DtSword); var fs3 = snap.Finds.FirstOrDefault(f => f.Item == DtCloth);
                Check("finds: the Toolkit's build is kept with the find; a find without one (or with garbage) is kept without", snap.Finds.Count == 3 && fs1?.Build == 70374 && fs2?.Build == 0 && fs3?.Build == 0);
                var batch = AtlasObs.MakeBatch(snap, null, new Dictionary<string, string>(), includeExtras: true);
                var sent = Wire(batch.Frame).List("finds").OfType<Dictionary<string, object>>().ToList();
                var sentHerb = sent.FirstOrDefault(o => Convert.ToInt32(o["i"]) == DtHerb); var sentSword = sent.FirstOrDefault(o => Convert.ToInt32(o["i"]) == DtSword);
                Check("finds: shared with the Lodge as \"gb\" only when known; the sent record's signature is unchanged (nothing is sent twice after the update)",
                    sentHerb != null && sentHerb.ContainsKey("gb") && Convert.ToInt32(sentHerb["gb"]) == 70374 && sentSword != null && !sentSword.ContainsKey("gb") && batch.Sigs.Values.All(v => v == "1"));
                var lodge = new LodgeAtlas();
                lodge.Apply(LodgeJson.FromPlain(new Dictionary<string, object>
                {
                    ["t"] = "atlas.data", ["epoch"] = "dt1", ["rev"] = 3.0, ["full"] = true,
                    ["finds"] = new List<object>
                    {
                        new Dictionary<string, object> { ["s"] = 1.0, ["i"] = (double)DtHerb, ["c"] = 1.0, ["src"] = "obj:1618", ["at"] = (double)(AtNow - 900), ["by"] = "Tess", ["gb"] = 70338.0 },
                        new Dictionary<string, object> { ["s"] = 2.0, ["i"] = (double)DtHerb, ["c"] = 1.0, ["src"] = "obj:1618", ["at"] = (double)(AtNow - 600), ["by"] = "Bob" },
                        new Dictionary<string, object> { ["s"] = 3.0, ["i"] = (double)DtHerb, ["c"] = 3.0, ["src"] = "obj:1618", ["at"] = (double)(AtNow - 300), ["by"] = "Ana", ["gb"] = 70374.0 },
                    },
                }) as System.Text.Json.Nodes.JsonObject);
                var lf = lodge.AllFinds;
                var round = new LodgeAtlas(); round.Apply(LodgeJson.Parse(LodgeJson.Serialize(lodge.ToCache())));
                Check("finds: the Lodge's finds carry their build, a find from an older Lodge has none, and the cache file keeps it",
                    lf.Count == 3 && lf.First(f => f.Seq == 1).Build == 70338 && lf.First(f => f.Seq == 2).Build == 0 && lf.First(f => f.Seq == 3).Build == 70374 && round.AllFinds.First(f => f.Seq == 1).Build == 70338 && round.AllFinds.First(f => f.Seq == 2).Build == 0);
                Check("finds: the words - this version 'game 1.60.1', an older build 'build 70338 (older game version)', none 'unknown version'",
                    FindVersions.Text(70374) == "game 1.60.1" && FindVersions.Text(70338) == "build 70338 (older game version)" && FindVersions.Text(0) == "unknown version" && FindVersions.Text(70400) == "build 70400"
                    && FindVersions.IsOlder(70338) && !FindVersions.IsOlder(70374) && !FindVersions.IsOlder(0) && FindVersions.Tip(0).Contains("not known"));
                FindVersions.CurrentStamp = () => "";
                Check("finds: without game data the build still shows (never a wrong 'older')", FindVersions.Text(70374) == "build 70374" && !FindVersions.IsOlder(70338));
                FindVersions.CurrentStamp = () => "wow_classic_beta|1.60.1.70374|abc";
                AtlasQuery.Context.Lodge = () => lodge;
                var herbPage = await Task.Run(() => AtlasQuery.Build("item:" + DtHerb));
                var fr = herbPage.Section("finds")?.Rows ?? new List<AtlasRow>();
                Check("finds: the item page's finds show their version quietly (second line) and the note counts the ones from an older game version",
                    fr.Count == 3 && fr.First(r => r.Title == "Ana").Extra == "game 1.60.1" && fr.First(r => r.Title == "Tess").Extra == "build 70338 (older game version)" && fr.First(r => r.Title == "Bob").Extra == "unknown version"
                    && herbPage.Section("finds").Note.Contains("1 of them in an older game version"), string.Join(" | ", fr.Select(r => r.Title + ":" + r.Extra)) + " / " + herbPage.Section("finds")?.Note);
                Check("finds: the strip's line ends with the version", ElansAddonHub.AtlasPage.FindFoot(lf.First(f => f.Seq == 3)).EndsWith("· game 1.60.1") && ElansAddonHub.AtlasPage.FindFoot(lf.First(f => f.Seq == 2)).EndsWith("· unknown version"));
                await AtlasView.Open("item:" + DtHerb + "?tab=lodge"); UpdateLayout();
                await ShotWait(300); Snapshot(Path.Combine(dir, "atlasdata-finds-version.png"));
            }
            catch (Exception e) { Check("atlasdata test crashed", false, e.ToString()); }
            finally
            {
                AtlasQuery.Context.Lodge = savedLodge; AtlasQuery.Context.NowUtc = savedNow; FindVersions.CurrentStamp = savedStamp; Width = savedWidth;
                MapRender.TileSourceForTest = savedTiles; MapRender.DirOverride = savedMapDir;
                ClassicSetup.ResetUpgradeForTest();
                ClassicAtlas.Use(null);
                ItemData.SetForTest(null, ItemData.State.Disabled, "off"); SpellData.SetForTest(null, SpellData.State.Disabled); SeenData.SetForTest(null, SeenData.State.Disabled); MapData.SetForTest(null, null, MapData.State.Disabled);
                AtlasBrowse.ForgetForTest();
                try { ResetAtlasForTest(); } catch (Exception) { }
            }
            return "atlasdata selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
