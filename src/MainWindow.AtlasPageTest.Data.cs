using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // --selftest "atlaspage", the invented world the checks run on. Nothing here is real game data: a small continent (from the mapdata test), a handful of
    // creatures / objects / quests / items, a Classic index built in memory, a Lodge pool applied from one atlas.data page, the client's caches as snapshots.
    public partial class MainWindow
    {
        const long AtNow = 1_760_000_000L;      // "now" of the Atlas test (2025-10-09); the Lodge data is a few hours older

        static ClassicSpawn Sp(int map, double x, double y) => new ClassicSpawn { Map = map, X = (float)x, Y = (float)y };

        // The map set with area ids on the zones (so quests sorted under an area land on a map)
        static MapSet AtlasTestMapSet()
        {
            var s = SynthMapSet();
            s.Assignments.First(a => a.UiMapId == 11).AreaId = 78;       // Greenvale = area 78
            s.Assignments.First(a => a.UiMapId == 13).AreaId = 79;       // Highkeep = area 79
            s.Areas.Add(new AreaInfo { Id = 79, Name = "Highkeep", ParentId = 77 });
            return s;
        }

        static ClassicIndex AtlasTestClassic(bool manyWolves = true)
        {
            var idx = new ClassicIndex();
            idx.Provenance = new ClassicProvenance { DumpName = "ClassicDB_test.sql.gz", CommitSha = "abc123", CommitDate = "2024-05-02T10:00:00Z", BuiltUtc = "2024-06-01T00:00:00Z", FormatVersion = 1 };
            void It(int id, string name, int q, int il, int rl) => idx.Items[id] = new ClassicItemInfo { Id = id, Name = name, Quality = q, ItemLevel = il, RequiredLevel = rl };
            It(100, "Gnarlfang's Claw", 3, 14, 10); It(101, "Wolf Pelt", 1, 5, 1); It(102, "Linen Cloth", 1, 5, 1); It(103, "Dusty Cloak", 0, 3, 1);
            It(104, "Crypt Blade", 3, 26, 22); It(105, "Ruby Ring", 4, 28, 24); It(107, "Wanderer's Coin", 1, 1, 1); It(108, "Heavy Linen Bandage", 1, 20, 1);
            void Npc(int id, string name, string sub, int min, int max, int rank, int type, int faction, int flags, params ClassicSpawn[] sp)
                => idx.Npcs[id] = new ClassicNpc { Id = id, Name = name, SubName = sub, MinLevel = min, MaxLevel = max, Rank = rank, Type = type, FactionId = faction, NpcFlags = flags, Spawns = sp };
            Npc(1, "Gnarlfang", "Fanglord", 12, 12, 3, 1, 14, 0, Sp(0, 500, 1500));
            Npc(2, "Innkeeper Hale", "Innkeeper", 20, 20, 0, 7, 14, 0x82, Sp(0, 150, 200));
            var wolves = new List<ClassicSpawn>();
            if (manyWolves) for (int i = 0; i < 60; i++) wolves.Add(Sp(0, 50 + (i * 13) % 900, 60 + (i * 31) % 1880));      // all inside Greenvale, none in the city
            wolves.Add(Sp(0, -500, 0));                                                                                  // one in Redmoor
            Npc(3, "Plains Wolf", "", 5, 6, 0, 1, 14, 0, wolves.ToArray());
            Npc(4, "Crypt Lord", "", 24, 24, 3, 6, 14, 0, Sp(99, 50, 50));
            Npc(5, "Deep Hall Warden", "", 40, 40, 3, 3, 14, 0, Sp(98, 10, 10));
            Npc(6, "Crypt Skeleton", "", 20, 22, 0, 6, 14, 0, Sp(99, 20, 20), Sp(99, 30, 30));
            for (int i = 10; i < 40; i++) Npc(i, "Wanderer " + i, "", 1 + i, 3 + i, 0, 7, 14, 0, Sp(1, i * 10, i * 10));   // continent 1 has no map assignment: these never get a marker
            idx.Objects[50] = new ClassicObject { Id = 50, Name = "Ancient Chest", Type = 3, LootId = 1, Spawns = new[] { Sp(0, 600, 900) } };
            idx.Quests[7] = new ClassicQuest
            {
                Id = 7, Title = "The Lost Pup", MinLevel = 4, QuestLevel = 6, ZoneOrSort = 78, RequiredRaces = 0, RequiredClasses = 4 | 8,
                RewardItems = new[] { 100 }, RewardCounts = new[] { 1 }, ChoiceItems = new[] { 101, 102 }, ChoiceCounts = new[] { 1, 5 }, ObjectiveItems = new[] { 101 }, ObjectiveCounts = new[] { 4 },
                Givers = new[] { new ClassicQuestGiver { Id = 2 } },
            };
            idx.Quests[9] = new ClassicQuest { Id = 9, Title = "Classic Only Errand", MinLevel = 1, QuestLevel = 3, ZoneOrSort = 78, Givers = new[] { new ClassicQuestGiver { Id = 50, IsObject = true } } };
            void Src(int item, ClassicSourceKind k, int src, double chance, int min = 1, int max = 1, int stock = 0, bool qo = false)
                => idx.Add(item, new ClassicItemSource { Kind = k, SourceId = src, Chance = chance, MinCount = min, MaxCount = max, QuestOnly = qo });
            Src(100, ClassicSourceKind.NpcDrop, 1, 10); Src(100, ClassicSourceKind.NpcDrop, 3, 0.5);
            Src(101, ClassicSourceKind.NpcDrop, 1, 45); Src(101, ClassicSourceKind.NpcDrop, 3, 33.3, 1, 2); Src(101, ClassicSourceKind.NpcSkin, 3, 50);
            Src(102, ClassicSourceKind.Vendor, 2, 0, 0, 0);
            Src(104, ClassicSourceKind.NpcDrop, 4, 25); Src(105, ClassicSourceKind.NpcDrop, 4, 3); Src(105, ClassicSourceKind.NpcDrop, 5, 2); Src(105, ClassicSourceKind.ObjectLoot, 50, 15);
            for (int i = 10; i < 40; i++) Src(100, ClassicSourceKind.NpcDrop, i, 0.01 + (i - 10) * 0.003);               // 30 world-drop sources far under 0.1 %
            for (int i = 10; i < 18; i++) Src(103, ClassicSourceKind.NpcDrop, i, 0.02 + (i - 10) * 0.004);                // an item with nothing but tiny sources
            Src(100, ClassicSourceKind.QuestReward, 7, 0); Src(101, ClassicSourceKind.QuestChoice, 7, 0); Src(102, ClassicSourceKind.QuestChoice, 7, 0, 5, 5);
            Src(101, ClassicSourceKind.QuestObjective, 7, 0, 4, 4);
            idx.Finish();
            return idx;
        }

        // one atlas.data page, as the Lodge server sends it (see LodgeAtlas.Apply)
        static LodgeAtlas AtlasTestLodge()
        {
            long at = AtNow - 3 * 3600;
            var json = "{\"t\":\"atlas.data\",\"epoch\":\"e1\",\"rev\":7,\"at\":" + (AtNow - 600) + ",\"full\":true,"
                + "\"kills\":{\"1\":{\"n\":340,\"m\":3,\"items\":{\"100\":{\"n\":41,\"c\":41},\"102\":{\"n\":20,\"c\":30}},\"z\":{\"11\":340}}},"
                + "\"objects\":{\"50\":{\"n\":50,\"m\":2,\"items\":{\"105\":{\"n\":5,\"c\":5}},\"z\":{\"11\":50}}},"
                + "\"fishing\":{\"11\":{\"n\":30,\"m\":1,\"items\":{\"101\":{\"n\":6,\"c\":6}}}},"
                + "\"vendors\":{\"2\":{\"at\":" + at + ",\"z\":13,\"x\":0.667,\"y\":0.75,\"items\":{\"100\":{\"p\":12050,\"s\":1,\"l\":-1},\"102\":{\"p\":25,\"s\":20,\"l\":-1}}}},"
                + "\"quests\":{\"7\":{\"at\":" + at + ",\"rewards\":[100],\"choices\":[101]}},"
                + "\"spawns\":{\"1\":[{\"z\":11,\"x\":0.3,\"y\":0.4},{\"z\":11,\"x\":0.32,\"y\":0.43}]},"
                + "\"items\":{\"555\":{\"n\":\"Mystery Ring\",\"q\":4,\"il\":20,\"rl\":15,\"cls\":4,\"sub\":0,\"slot\":11,\"st\":[[3,5],[7,4]]}},"
                + "\"finds\":["
                + "{\"i\":100,\"c\":1,\"at\":" + (AtNow - 3 * 3600) + ",\"src\":\"npc:1\",\"z\":11,\"x\":0.31,\"y\":0.41,\"by\":\"Tess\",\"s\":3},"
                + "{\"i\":100,\"c\":1,\"at\":" + (AtNow - 2 * 86400) + ",\"src\":\"vendor:2\",\"z\":13,\"x\":0.66,\"y\":0.75,\"by\":\"Bob\",\"s\":1},"
                + "{\"i\":105,\"c\":1,\"at\":" + (AtNow - 600) + ",\"src\":\"obj:50\",\"z\":11,\"x\":0.5,\"y\":0.5,\"by\":\"Tess\",\"s\":4},"
                + "{\"i\":101,\"c\":2,\"at\":" + (AtNow - 90000) + ",\"src\":\"fish\",\"z\":11,\"x\":0.8,\"y\":0.2,\"by\":\"Bob\",\"s\":2}]}";
            var l = new LodgeAtlas();
            if (!l.Apply(Json.Obj(json))) throw new InvalidOperationException("the invented atlas.data page was refused");
            return l;
        }

        static ClientItem CItem(int id, string name, int q, int il, int rl, int inv, int cls, int sub, long sell = 0, int icon = 0, string desc = "")
            => new ClientItem { Id = id, Name = name, Quality = q, ItemLevel = il, RequiredLevel = rl, InventoryType = inv, ClassId = cls, SubclassId = sub, SellPrice = sell, IconId = icon, Description = desc, Bonding = q >= 3 ? 1 : 0 };

        static ItemSnapshot AtlasTestItems()
            => new ItemSnapshot(new[]
            {
                CItem(100, "Gnarlfang's Claw", 3, 14, 10, 13, 2, 7, 12050, 0, "Still warm."), CItem(101, "Wolf Pelt", 1, 5, 1, 0, 7, 0, 30), CItem(102, "Linen Cloth", 1, 5, 1, 0, 7, 5, 13),
                CItem(103, "Dusty Cloak", 0, 3, 1, 16, 4, 1), CItem(104, "Crypt Blade", 3, 26, 22, 13, 2, 7), CItem(105, "Ruby Ring", 4, 28, 24, 11, 4, 0),
                CItem(108, "Heavy Linen Bandage", 1, 20, 1, 0, 0, 1),
            }, new ClientItem[0]);

        static AtlasSet AtlasTestSpells()
        {
            var spells = new[]
            {
                new SpellInfo { Id = 133, Name = "Fireball", Rank = "Rank 1", Desc = "Hurls a fiery ball.", CastMs = 1500, DurationMs = 0, RangeMax = 35 },
                new SpellInfo { Id = 4000, Name = "Heavy Linen Bandage", Rank = "Rank 2", Desc = "Heals the target.", CastMs = 0, Effects = new[] { new EffectInfo { Index = 0, Effect = EffectInfo.CreateItem, ItemType = 108 } } },
            };
            var skills = new[] { new SkillInfo { Id = 129, Name = "First Aid", Category = SkillInfo.CatSecondary, Description = "Bandages and anti-venoms." }, new SkillInfo { Id = 171, Name = "Alchemy", Category = SkillInfo.CatProfession } };
            var abilities = new[] { new SkillAbility { Id = 1, Skill = 129, Spell = 4000, MinRank = 40, Green = 60, Gray = 80 } };
            var reagents = new Dictionary<int, int[]> { [4000] = new[] { 102, 2 } };
            var set = new AtlasSet
            {
                Spells = new SpellSnapshot(spells, skills, abilities, reagents),
                Factions = new FactionSnapshot(new[] { new FactionInfo { Id = 72, Name = "Stormwind", ParentId = 469, Description = "The human kingdom." }, new FactionInfo { Id = 469, Name = "Alliance" } },
                    new[] { new FactionTemplateInfo { Id = 14, FactionId = 72, Group = FactionTemplateInfo.GroupMonster } }),
                Details = new ItemDetails(),
            };
            return set;
        }

        static SeenSnapshot AtlasTestSeen()
            => new SeenSnapshot(
                new[]
                {
                    new WdbCreature { Id = 1, Name = "Gnarlfang", Title = "Fanglord", Type = 1, Rank = 3 },
                    new WdbCreature { Id = 2, Name = "Innkeeper Hale", Title = "Innkeeper", Type = 7, Flags0 = 0x82 },
                    new WdbCreature { Id = 60, Name = "Forever Only Boar", Type = 1 },
                },
                new[] { new WdbObject { Id = 50, Name = "Ancient Chest", Type = 3 } },
                new[]
                {
                    new WdbQuest { Id = 7, Title = "The Lost Pup", Level = 6, MinLevel = 4, Sort = 78, Objectives = "Bring four Wolf Pelts to Innkeeper Hale.", Description = "A pup has gone missing in the vale." },
                    new WdbQuest { Id = 8, Title = "A Forever Only Errand", Level = 9, Sort = 78 },
                });

        static Dictionary<int, InvItemTotals> AtlasTestOwned()
        {
            var elan = new InvChar { Key = "Forever-Elan", Name = "Elan", Realm = "Forever", Class = "HUNTER", Level = 60 };
            return new Dictionary<int, InvItemTotals>
            {
                [100] = new InvItemTotals { Id = 100, Chars = { new InvCount { Char = elan, Bags = 1, Bank = 1 } }, Guilds = { new InvGuildCount { Guild = new InvGuild { Key = "g1", Name = "Nasferatu", Realm = "Forever" }, Count = 3 } } },
            };
        }

        // a fake of the Classic downloader + importer behind the card
        sealed class FakeClassic : IClassicBackend
        {
            public ClassicState St = ClassicState.NotDownloaded;
            public ClassicIndex Idx;
            public string Dir;
            public int Queries, Downloads, Builds, Deletes;
            public Exception FailQuery, FailDownload, FailBuild;
            public Action<CancellationToken> OnDownload;
            public ClassicState State() => St;
            public ClassicIndex Index() => St == ClassicState.Ready ? Idx : null;
            public Task<ClassicRemote> Query(CancellationToken ct)
            {
                Queries++;
                if (FailQuery != null) return Task.FromException<ClassicRemote>(FailQuery);
                var r = new ClassicRemote { DumpName = "ClassicDB_test.sql.gz", DumpSize = 19_000_000, CommitSha = "abc123", CommitDate = "2024-05-02T10:00:00Z" };
                r.Updates.Add(new KeyValuePair<string, long>("z1_a.sql", 2000)); r.Updates.Add(new KeyValuePair<string, long>("z2_b.sql", 3000));
                return Task.FromResult(r);
            }
            public async Task<ClassicDownloadResult> Download(ClassicRemote remote, IProgress<ClassicDownloadProgress> progress, CancellationToken ct)
            {
                Downloads++;
                if (FailDownload != null) throw FailDownload;
                for (int i = 1; i <= 4; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ClassicDownloadProgress { Phase = "Downloading the dump", File = remote.DumpName, BytesDone = i * 1000, BytesTotal = 4000 });
                    OnDownload?.Invoke(ct);
                    await Task.Delay(15, ct).ConfigureAwait(false);
                }
                St = ClassicState.Downloaded;
                return new ClassicDownloadResult { Dir = Dir, DumpName = remote.DumpName };
            }
            public async Task<ClassicImportReport> BuildIndex(IProgress<ClassicProgress> progress, CancellationToken ct)
            {
                Builds++;
                if (FailBuild != null) throw FailBuild;
                for (int i = 1; i <= 3; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ClassicProgress { Phase = "Resolving loot", Fraction = i / 3.0 });
                    await Task.Delay(15, ct).ConfigureAwait(false);
                }
                St = ClassicState.Ready;
                return new ClassicImportReport();
            }
            public void Delete() { Deletes++; St = ClassicState.NotDownloaded; }
            public string Folder() => Dir;
        }
    }
}
