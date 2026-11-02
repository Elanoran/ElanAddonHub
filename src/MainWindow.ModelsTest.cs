using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "models" (3.6, Atlas 3D models step 1): which model a creature wears (ModelData). Everything runs on tables built here (SdBuild, the
    // spelldata section's small WDC5 writer) and on synthetic Classic SQL, under the scratch folder: the two tables in their 1.60.1.70707 layouts, an unknown
    // layout / foreign table / missing table switching only that table off with a reason, the display choice (the client's own display first, then the
    // highest chance, then the order), the humanoid flag, the saved index, and the Classic baseline's display ids (ModelId1..4 renamed DisplayId1..4 by an
    // update, the chances, index format 4, format-3 and format-2 files still read and outdated). 3.6 step 5: update z2823's display chance rule on a small
    // dump of its own (every branch of the six UPDATEs, the rule run at the file's place in the update order, not run when the dump already has it), and
    // the looks (which displays the card can switch between, the first one). The real game files are the modelsreal section's.
    public partial class MainWindow
    {
        // CreatureDisplayInfo in layout 7275F5F6: 28 fields, the id inline (field 0), CreatureModelScale float (4), TextureVariationFileDataID[4] (27)
        static byte[] MdDisplays(uint layout = 0x7275F5F6, uint table = 0xBFDAF9F1)
        {
            var fs = Enumerable.Range(0, 28).Select(i => i == 4 || i == 16 || i == 17 || i == 25 ? SdFl() : i == 27 ? SdI(4) : SdI()).ToArray();
            object[] R(int id, int model, float scale, int ext, params int[] tex) { var r = new object[28]; r[0] = id; r[1] = model; r[4] = scale; r[7] = ext; r[27] = tex; return r; }
            var rows = new[]
            {
                R(604, 43, 0.71f, 0, 126498, 126499, 0, 0),     // Timber Wolf
                R(503, 66, 0.6f, 0, 123096, 0, 0, 0),           // Stonetusk Boar
                R(3167, 7661, 1f, 346, 0, 0, 0, 0),             // Stormwind City Guard: a character model
                R(500, 77, 1f, 0, 0, 0, 0, 0),                  // its model row is missing
                R(8570, 1551, 1.8f, 0, 123466, 123467, 123468, 0),
            };
            return SdBuild(table, layout, fs, rows, rows.Select(r => (int)r[0]).ToArray(), idIndex: 0, idList: false);
        }

        // CreatureModelData in layout 0F5449F8: 34 fields, the id in the id list, GeoBox[6] (0), Flags (1), FileDataID (2), ModelScale float (21)
        static byte[] MdModels(uint layout = 0x0F5449F8)
        {
            var fs = Enumerable.Range(0, 34).Select(i => i == 0 ? SdFl(6) : i == 33 ? SdFl(2) : i == 21 ? SdFl() : SdI()).ToArray();
            object[] R(int file, int flags, float scale) { var r = new object[34]; r[0] = new float[6]; r[1] = flags; r[2] = file; r[21] = scale; r[33] = new float[2]; return r; }
            var rows = new[] { R(126487, 16, 1f), R(123090, 0, 1.25f), R(1011653, 0, 1f), R(123465, 0, 1f) };
            return SdBuild(ModelTables.ModelInfo.TableHash, layout, fs, rows, new[] { 43, 66, 7661, 1551 });
        }

        static SpellRaw MdRaw(byte[] displays = null, byte[] models = null, bool noDisplays = false)
        {
            var raw = new SpellRaw();
            if (!noDisplays) raw.Tables[ModelTables.DisplayInfo.Name] = displays ?? MdDisplays();
            raw.Tables[ModelTables.ModelInfo.Name] = models ?? MdModels();
            return raw;
        }

        // a z2815-style creature_template (ModelId1..4, no chances) and the z2823 update that renames them and adds the chances
        const string MdDump = @"CREATE TABLE `db_version` (
  `version` varchar(120) DEFAULT NULL,
  `content_100_VDB-1_world` bit(1) DEFAULT NULL
) ENGINE=MyISAM;
CREATE TABLE `creature_template` (
  `Entry` mediumint unsigned NOT NULL DEFAULT '0',
  `Name` char(100) NOT NULL DEFAULT '',
  `SubName` char(100) DEFAULT NULL,
  `MinLevel` tinyint unsigned NOT NULL DEFAULT '1',
  `MaxLevel` tinyint unsigned NOT NULL DEFAULT '1',
  `ModelId1` mediumint unsigned NOT NULL DEFAULT '0',
  `ModelId2` mediumint unsigned NOT NULL DEFAULT '0',
  `ModelId3` mediumint unsigned NOT NULL DEFAULT '0',
  `ModelId4` mediumint unsigned NOT NULL DEFAULT '0',
  `Faction` smallint unsigned NOT NULL DEFAULT '0',
  `CreatureType` tinyint unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`Entry`)
) ENGINE=MyISAM;
INSERT INTO `creature_template` VALUES (69,'Timber Wolf',NULL,2,2,604,0,0,0,32,1),(68,'Stormwind City Guard',NULL,55,55,3167,5446,0,0,11,7),(70,'Four Faces',NULL,1,1,11,0,12,13,14,7),(71,'Faceless',NULL,1,1,0,0,0,0,14,10),(4,'Four Again',NULL,1,1,21,22,23,24,14,7);";
        const string MdUpdate = @"ALTER TABLE db_version CHANGE COLUMN required_a required_z2823_01_mangos_displayid_probability bit;
ALTER TABLE `creature_template` CHANGE `ModelId1` `DisplayId1` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;
ALTER TABLE `creature_template` CHANGE `ModelId2` `DisplayId2` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;
ALTER TABLE `creature_template` CHANGE `ModelId3` `DisplayId3` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;
ALTER TABLE `creature_template` CHANGE `ModelId4` `DisplayId4` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;
ALTER TABLE `creature_template` ADD COLUMN `DisplayIdProbability1` SMALLINT UNSIGNED NOT NULL DEFAULT 0 AFTER `DisplayId4`;
UPDATE creature_template SET DisplayIdProbability1=100 WHERE DisplayId1!=0;
UPDATE creature_template SET DisplayIdProbability1=50,DisplayIdProbability2=50 WHERE DisplayId1!=0 AND DisplayId2!=0 AND EXISTS(SELECT modelid FROM creature_model_info WHERE creature_template.DisplayId2=creature_model_info.modelid AND modelid_other_team > 0);
UPDATE creature_template SET DisplayIdProbability1=25,DisplayIdProbability2=25,DisplayIdProbability3=25,DisplayIdProbability4=25 WHERE DisplayId1!=0 AND DisplayId2!=0 AND DisplayId3!=0 AND DisplayId4!=0;";
        const string MdUpdate2 = "INSERT INTO `creature_template` (`Entry`,`Name`,`DisplayId1`,`DisplayId2`,`DisplayIdProbability1`,`DisplayIdProbability2`) VALUES (72,'Late Pair',31,32,30,70);\n"
            // 3.6 step 5: the looks of the card (part 3): two usable looks at 30 / 70; one usable look beside a character model
            + "INSERT INTO `creature_template` (`Entry`,`Name`,`DisplayId1`,`DisplayId2`,`DisplayIdProbability1`,`DisplayIdProbability2`) VALUES (73,'Two Looks',604,605,30,70),(74,'One Look',604,3167,50,50);";

        // 3.6 step 5: update z2823's rule on a dump of its own. creature_model_info: 20 has an other-team model, 21 has none (0), 22 has no row.
        const string MdRuleDump = @"CREATE TABLE `db_version` (
  `version` varchar(120) DEFAULT NULL,
  `content_4482_TDB-0250_i13920` bit(1) DEFAULT NULL
) ENGINE=MyISAM;
CREATE TABLE `creature_model_info` (
  `modelid` mediumint unsigned NOT NULL DEFAULT '0',
  `bounding_radius` float NOT NULL DEFAULT '0',
  `combat_reach` float NOT NULL DEFAULT '0',
  `SpeedWalk` float NOT NULL DEFAULT '1',
  `SpeedRun` float NOT NULL DEFAULT '1.14286',
  `gender` tinyint unsigned NOT NULL DEFAULT '2',
  `modelid_other_gender` mediumint unsigned NOT NULL DEFAULT '0',
  `modelid_other_team` mediumint unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`modelid`)
) ENGINE=MyISAM;
INSERT INTO `creature_model_info` VALUES (10,0.3,1.5,1,1.14286,2,0,0),(20,0.3,1.5,1,1.14286,2,0,77),(21,0.3,1.5,1,1.14286,0,22,0),(30,0.3,1.5,1,1.14286,2,0,0),(40,0.3,1.5,1,1.14286,2,0,0);
CREATE TABLE `creature_template` (
  `Entry` mediumint unsigned NOT NULL DEFAULT '0',
  `Name` char(100) NOT NULL DEFAULT '',
  `ModelId1` mediumint unsigned NOT NULL DEFAULT '0',
  `ModelId2` mediumint unsigned NOT NULL DEFAULT '0',
  `ModelId3` mediumint unsigned NOT NULL DEFAULT '0',
  `ModelId4` mediumint unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`Entry`)
) ENGINE=MyISAM;
INSERT INTO `creature_template` VALUES (101,'One',10,0,0,0),(102,'Pair Other',10,20,0,0),(103,'Pair',10,21,0,0),(104,'Pair No Row',10,22,0,0),(105,'Three Other',10,20,30,0),(106,'Three',10,21,30,0),(107,'Four',10,20,30,40),(108,'No First',0,20,0,0),(109,'Gap',10,0,30,0);";

        // update 4620_z2823_01_mangos_displayid_probability.sql of cmangos/classic-db as it is (the file the importer finds in its updates folder)
        const string MdRuleUpdate = @"ALTER TABLE db_version CHANGE COLUMN required_z2822_01_mangos_creature_cls_stats required_z2823_01_mangos_displayid_probability bit;

ALTER TABLE `creature_template` CHANGE `ModelId1` `DisplayId1` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;
ALTER TABLE `creature_template` CHANGE `ModelId2` `DisplayId2` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;
ALTER TABLE `creature_template` CHANGE `ModelId3` `DisplayId3` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;
ALTER TABLE `creature_template` CHANGE `ModelId4` `DisplayId4` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;
ALTER TABLE `creature_template` ADD COLUMN `DisplayIdProbability1` SMALLINT UNSIGNED NOT NULL DEFAULT 0 AFTER `DisplayId4`;
ALTER TABLE `creature_template` ADD COLUMN `DisplayIdProbability2` SMALLINT UNSIGNED NOT NULL DEFAULT 0 AFTER `DisplayIdProbability1`;
ALTER TABLE `creature_template` ADD COLUMN `DisplayIdProbability3` SMALLINT UNSIGNED NOT NULL DEFAULT 0 AFTER `DisplayIdProbability2`;
ALTER TABLE `creature_template` ADD COLUMN `DisplayIdProbability4` SMALLINT UNSIGNED NOT NULL DEFAULT 0 AFTER `DisplayIdProbability3`;

SET sql_safe_updates=0;
-- setting probabilities to exactly replicate previous behaviour
UPDATE creature_template SET DisplayIdProbability1=100 WHERE DisplayId1!=0;
UPDATE creature_template SET DisplayIdProbability1=50,DisplayIdProbability2=50 WHERE DisplayId1!=0 AND DisplayId2!=0 AND EXISTS(SELECT modelid FROM creature_model_info WHERE creature_template.DisplayId2=creature_model_info.modelid AND modelid_other_team > 0);
UPDATE creature_template SET DisplayIdProbability1=0,DisplayIdProbability2=100 WHERE DisplayId1!=0 AND DisplayId2!=0 AND NOT EXISTS(SELECT modelid FROM creature_model_info WHERE creature_template.DisplayId2=creature_model_info.modelid AND modelid_other_team > 0);
UPDATE creature_template SET DisplayIdProbability1=33,DisplayIdProbability2=33,DisplayIdProbability3=33 WHERE DisplayId1!=0 AND DisplayId2!=0 AND DisplayId3!=0 AND EXISTS(SELECT modelid FROM creature_model_info WHERE creature_template.DisplayId2=creature_model_info.modelid AND modelid_other_team > 0);
UPDATE creature_template SET DisplayIdProbability1=0,DisplayIdProbability2=0,DisplayIdProbability3=100 WHERE DisplayId1!=0 AND DisplayId2!=0 AND DisplayId3!=0 AND NOT EXISTS(SELECT modelid FROM creature_model_info WHERE creature_template.DisplayId2=creature_model_info.modelid AND modelid_other_team > 0);
UPDATE creature_template SET DisplayIdProbability1=25,DisplayIdProbability2=25,DisplayIdProbability3=25,DisplayIdProbability4=25 WHERE DisplayId1!=0 AND DisplayId2!=0 AND DisplayId3!=0 AND DisplayId4!=0;
SET sql_safe_updates=1;
";
        // the next update renames modelid_other_team; a later one gives 101 a second display (the rule ran before it: 101 keeps 100 / 0, as in the real database)
        const string MdRuleRename = "ALTER TABLE db_version CHANGE COLUMN required_z2823_01_mangos_displayid_probability required_z2824_01_mangos_model_unification bit;\n"
            + "ALTER TABLE `creature_model_info` CHANGE `modelid_other_team` `modelid_alternative` MEDIUMINT UNSIGNED NOT NULL DEFAULT 0;";
        const string MdRuleLater = "UPDATE `creature_template` SET `DisplayId2`=21 WHERE `Entry`=101;\nUPDATE `creature_model_info` SET `modelid_alternative`=5 WHERE `modelid`=21;";

        // 3.6 step 5: the rule's dump read with its updates; contentVersion = the db_version content column's number (4620 = the rule is in the dump already)
        static ClassicIndex MdRuleIndex(ClassicImportReport rep, int contentVersion = 4482)
        {
            var dump = MdRuleDump.Replace("content_4482_TDB-0250_i13920", "content_" + contentVersion + "_x");
            return ClassicImporter.BuildFromText(dump, new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("4620_z2823_01_mangos_displayid_probability.sql", MdRuleUpdate),
                new KeyValuePair<string, string>("4621_z2824_01_mangos_model_unification.sql", MdRuleRename),
                new KeyValuePair<string, string>("4700_later.sql", MdRuleLater),
            }, rep);
        }

        async Task<string> ModelsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "models");
            var savedCache = ModelData.CacheFile; var savedAuto = ModelData.AutoStart; var savedSeen = SeenData.Snapshot; var savedSeenSt = SeenData.Status;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                ModelData.AutoStart = false; ModelData.ResetForTest(); ModelData.CacheFile = Path.Combine(work, "cache", "modeldata.bin");
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                Check("service: with AutoStart off Start() reads nothing", await Task.Run(() => { ModelData.Start(); return ModelData.Status == ModelData.State.NotStarted; }));

                // ---- the tables
                var set = ModelSet.Parse(MdRaw());
                Check("parse: both tables in the 1.60.1.70707 layouts, no problems (5 displays, 4 models)", set.Displays?.Count == 5 && set.Models?.Count == 4 && set.Problems.Count == 0
                    && set.LayoutsSeen[ModelTables.DisplayInfo.Name] == 0x7275F5F6 && set.LayoutsSeen[ModelTables.ModelInfo.Name] == 0x0F5449F8, string.Join("; ", set.Problems.Values));
                var d604 = set.Displays?.GetValueOrDefault(604);
                Check("parse: the inline id, ModelID, the scale float and the 4 texture variations of a display; FileDataID, Flags and ModelScale of a model",
                    d604 != null && d604.ModelId == 43 && Math.Abs(d604.Scale - 0.71f) < 1e-5 && d604.Textures.SequenceEqual(new[] { 126498, 126499, 0, 0 }) && set.Displays[8570].Textures[2] == 123468
                    && set.Models[43].FileId == 126487 && set.Models[43].Flags == 16 && Math.Abs(set.Models[66].Scale - 1.25f) < 1e-5);

                // ---- resolution
                List<NpcDisplay> L(params (int d, int c)[] a) => a.Select(x => new NpcDisplay { DisplayId = x.d, Chance = x.c, From = "classic" }).ToList();
                var wolf = ModelData.Resolve(set, L((604, 100)), 69);
                Check("resolve: display 604 -> model 43 -> file 126487, 2 texture variations, scale 0.71 x 1, not humanoid, Ok",
                    wolf.Ok && wolf.ModelId == 43 && wolf.ModelFileId == 126487 && wolf.TextureCount == 2 && wolf.Textures[1] == 126499 && Math.Abs(wolf.Scale - 0.71f) < 1e-5 && !wolf.Humanoid && wolf.Reason == null && wolf.From == "classic");
                var boar = ModelData.Resolve(set, L((503, 0)), 113);
                Check("resolve: the scale is the display's times the model's (0.6 x 1.25)", boar.Ok && Math.Abs(boar.Scale - 0.75f) < 1e-5 && boar.ModelFileId == 123090);
                var guard = ModelData.Resolve(set, L((3167, 100)), 68);
                Check("resolve: ExtendedDisplayInfoID != 0 = a humanoid: flagged, 'character model, not yet shown', not Ok (the model file is still named)",
                    guard.Humanoid && guard.Reason == ModelData.HumanoidReason && !guard.Ok && guard.ModelFileId == 1011653, guard.Reason);
                var lost = ModelData.Resolve(set, L((999, 100)), 1);
                var noModel = ModelData.Resolve(set, L((500, 100)), 1);
                var none = ModelData.Resolve(set, L(), 1);
                Check("resolve: a display the game does not know, a model row that is missing, no display id at all - each a reason, nothing guessed",
                    !lost.Ok && lost.Reason.Contains("999") && lost.Reason.Contains("not in the game's CreatureDisplayInfo") && !noModel.Ok && noModel.Reason.Contains("model 77") && noModel.ModelFileId == 0
                    && !none.Ok && none.Reason.StartsWith("no display id"), lost.Reason + " | " + noModel.Reason + " | " + none.Reason);
                var hi = ModelData.Resolve(set, L((503, 25), (604, 75)), 1);
                var tie = ModelData.Resolve(set, L((503, 0), (604, 0)), 1);
                var skip = ModelData.Resolve(set, L((999, 90), (503, 10)), 1);
                Check("choice: the highest chance wins (604 at 75% over 503 at 25%), equal chances keep the order (503 first), a display the game lacks passes to the next",
                    hi.DisplayId == 604 && tie.DisplayId == 503 && skip.DisplayId == 503 && skip.Ok && hi.Considered.Count == 2, hi.DisplayId + " " + tie.DisplayId + " " + skip.DisplayId);
                var noData = ModelData.Resolve(null, L((604, 100)), 1);
                Check("resolve: without the model tables the reason says so", !noData.Ok && noData.Reason.StartsWith("the model tables are not read"), noData.Reason);

                // ---- unknown layout / foreign table / missing table: only that table off
                var newLayout = ModelSet.Parse(MdRaw(MdDisplays(0x12345678)));
                var r1 = ModelData.Resolve(newLayout, L((604, 100)), 1);
                Check("layout: an unknown CreatureDisplayInfo layout switches that table off with 'game updated' (the model table still reads), and a lookup gives that reason",
                    newLayout.Displays == null && newLayout.Models?.Count == 4 && newLayout.Problems.TryGetValue("CreatureDisplayInfo", out var p1) && p1.StartsWith("game updated") && p1.Contains("12345678")
                    && !newLayout.Problems.ContainsKey("all") && !r1.Ok && r1.Reason.StartsWith("game updated"), string.Join("; ", newLayout.Problems.Values) + " | " + r1.Reason);
                var newModels = ModelSet.Parse(MdRaw(null, MdModels(0xCAFEBABE)));
                var r2 = ModelData.Resolve(newModels, L((604, 100)), 1);
                Check("layout: an unknown CreatureModelData layout keeps the displays; the lookup finds the display and says why there is no model file",
                    newModels.Displays?.Count == 5 && newModels.Models == null && r2.DisplayId == 604 && r2.ModelFileId == 0 && r2.Reason != null && r2.Reason.StartsWith("game updated"), r2.Reason);
                var foreign = ModelSet.Parse(MdRaw(MdDisplays(0x7275F5F6, 0x11111111)));
                var missing = ModelSet.Parse(MdRaw(noDisplays: true));
                var both = ModelSet.Parse(new SpellRaw());
                Check("layout: a foreign table and a table that is not in the install are off with their own reason; nothing at all = 'all' unavailable",
                    foreign.Displays == null && foreign.Problems["CreatureDisplayInfo"].Contains("not the table") && missing.Displays == null && missing.Problems["CreatureDisplayInfo"].Contains("not in this game install")
                    && both.Problems.ContainsKey("all"), string.Join("; ", foreign.Problems.Values.Concat(missing.Problems.Values)));

                // ---- the saved index
                var cf = ModelData.CacheFile;
                bool wrote = ModelIndexFile.TryWrite(cf, "wowf|1.60.1.70707|abc", set);
                var back = ModelIndexFile.TryRead(cf);
                Check("index: the two tables survive a save (displays, textures, scales, models, layouts) for the build they were read from",
                    wrote && back != null && back.Build == "wowf|1.60.1.70707|abc" && back.Format == ModelData.FormatVersion && back.Set.Displays.Count == 5 && back.Set.Displays[8570].Textures.SequenceEqual(set.Displays[8570].Textures)
                    && back.Set.Models[43].FileId == 126487 && Math.Abs(back.Set.Displays[604].Scale - 0.71f) < 1e-6 && back.Set.LayoutsSeen.Count == 2 && ModelData.Resolve(back.Set, L((604, 1)), 1).ModelFileId == 126487);
                int reads = 0;
                var same = ModelData.Obtain(back, "wowf|1.60.1.70707|abc", cf, () => { reads++; return set; }, out var from1);
                var other = ModelData.Obtain(back, "wowf|1.60.1.70800|def", cf, () => { reads++; return newLayout; }, out var from2);
                Check("index: the same build reads the saved file, another build reads the game files (and saves them)", reads == 1 && from1 == "cache file" && from2 == "game files" && ModelIndexFile.TryRead(cf)?.Build == "wowf|1.60.1.70800|def");
                File.WriteAllBytes(cf, new byte[] { 1, 2, 3 });
                Check("index: a damaged file reads as none", ModelIndexFile.TryRead(cf) == null);

                // ---- the Classic baseline's display ids
                var rep = new ClassicImportReport();
                var ci = ClassicImporter.BuildFromText(MdDump, new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("4620_z2823_01_mangos_displayid_probability.sql", MdUpdate), new KeyValuePair<string, string>("4700_late.sql", MdUpdate2) }, rep);
                var cw = ci.NpcInfo(69); var cg = ci.NpcInfo(68); var c4 = ci.NpcInfo(4); var c0 = ci.NpcInfo(71); var c72 = ci.NpcInfo(72); var c70 = ci.NpcInfo(70);
                Check("classic import: ModelId1..4 of the dump and DisplayId1..4 after the rename land in the same columns; empty slots are left out (70 keeps 11, 12, 13)",
                    cw != null && cw.DisplayIds.SequenceEqual(new[] { 604 }) && cg.DisplayIds.SequenceEqual(new[] { 3167, 5446 }) && c70.DisplayIds.SequenceEqual(new[] { 11, 12, 13 }) && c0.DisplayIds.Length == 0
                    && c72 != null && c72.DisplayIds.SequenceEqual(new[] { 31, 32 }), string.Join(" ", ci.Npcs.Values.Select(n => n.Id + ":" + string.Join("/", n.DisplayIds))));
                Check("classic import: the chances the updates give (100 for one display, 25 each for four, 30 / 70 from an insert); the EXISTS update the SQL reader skips is the importer's own rule (3.6 step 5), so a pair without creature_model_info rows reads 0 / 100",
                    cw.DisplayChances.SequenceEqual(new[] { 100 }) && c4.DisplayChances.SequenceEqual(new[] { 25, 25, 25, 25 }) && c72.DisplayChances.SequenceEqual(new[] { 30, 70 }) && cg.DisplayChances.SequenceEqual(new[] { 0, 100 })
                    && c70.DisplayChances.SequenceEqual(new[] { 100, 0, 0 }) && rep.SkipReasons.Keys.Any(k => k.Contains("sub-select") || k.Contains("EXISTS")) && rep.DisplayChanceRuleRan && rep.DisplayChanceChanged == 1,
                    string.Join(" ", ci.Npcs.Values.Select(n => n.Id + ":" + string.Join("/", n.DisplayChances))) + " | " + string.Join("; ", rep.SkipReasons.Keys) + " | changed " + rep.DisplayChanceChanged);

                // ---- 3.6 step 5: update z2823's rule, every branch, on a dump with creature_model_info
                {
                    var rr = new ClassicImportReport();
                    var ri = MdRuleIndex(rr);
                    int[] Ch(int id) => ri.NpcInfo(id)?.DisplayChances ?? new int[0];
                    string all = string.Join(" ", ri.Npcs.Values.OrderBy(n => n.Id).Select(n => n.Id + ":" + string.Join("/", n.DisplayIds) + "@" + string.Join("/", n.DisplayChances)));
                    Check("display chance rule: one display 100; a pair whose 2nd display has an other-team model 50 / 50, without one (modelid_other_team 0, or no creature_model_info row) 0 / 100",
                        Ch(101).SequenceEqual(new[] { 100, 0 }) && Ch(102).SequenceEqual(new[] { 50, 50 }) && Ch(103).SequenceEqual(new[] { 0, 100 }) && Ch(104).SequenceEqual(new[] { 0, 100 }), all);
                    Check("display chance rule: three displays 33 / 33 / 33 with an other-team model, else 0 / 0 / 100; four displays 25 each (the last UPDATE overrides the 33s)",
                        Ch(105).SequenceEqual(new[] { 33, 33, 33 }) && Ch(106).SequenceEqual(new[] { 0, 0, 100 }) && Ch(107).SequenceEqual(new[] { 25, 25, 25, 25 }), all);
                    Check("display chance rule: no first display = no statement matches (0); a gap in DisplayId2 = only the first UPDATE (100 / 0)",
                        ri.NpcInfo(108).DisplayIds.SequenceEqual(new[] { 20 }) && Ch(108).SequenceEqual(new[] { 0 }) && ri.NpcInfo(109).DisplayIds.SequenceEqual(new[] { 10, 30 }) && Ch(109).SequenceEqual(new[] { 100, 0 }), all);
                    Check("display chance rule: run where the update sits in the order - a display a later update adds (101 gets 21) keeps the chances the rule gave (100 / 0)",
                        ri.NpcInfo(101).DisplayIds.SequenceEqual(new[] { 10, 21 }) && rr.DisplayChanceRuleRan && rr.DisplayChanceRows == 8 && rr.DisplayChanceChanged == 5 && rr.UpdateFilesApplied == 3,
                        all + " | rows " + rr.DisplayChanceRows + " changed " + rr.DisplayChanceChanged + " files " + rr.UpdateFilesApplied + " | " + string.Join("; ", rr.Notes.Where(n => n.Contains("z2823"))));
                    var rn = new ClassicImportReport();
                    var rin = MdRuleIndex(rn, 4620);
                    Check("display chance rule: a dump that already has update 4620 (content 4620) does not run it again",
                        !rn.DisplayChanceRuleRan && rn.UpdateFilesAlreadyInDump == 1 && rin.NpcInfo(103) != null, "ran " + rn.DisplayChanceRuleRan + " in dump " + rn.UpdateFilesAlreadyInDump);
                    var d = new long[] { 10, 20, 30, 0 }; var pr = new long[] { 7, 7, 7, 7 };
                    var d0 = new long[] { 0, 20, 0, 0 }; var p0 = new long[] { 7, 7, 7, 7 };
                    Check("display chance rule: the six statements on one row (three displays, other team: 33 / 33 / 33, the 4th chance untouched; no first display: nothing changes)",
                        ClassicDisplayChances.Rule(d, true, pr) && pr.SequenceEqual(new long[] { 33, 33, 33, 7 }) && !ClassicDisplayChances.Rule(d0, true, p0) && p0.SequenceEqual(new long[] { 7, 7, 7, 7 })
                        && ClassicDisplayChances.IsRuleFile("4620_z2823_01_mangos_displayid_probability.sql") && !ClassicDisplayChances.IsRuleFile("4621_z2824_01_mangos_model_unification.sql"));
                }

                // ---- 3.6 step 5: the looks the card can switch between
                {
                    var two = ModelData.Looks(set, L((604, 25), (503, 25), (3167, 25), (999, 25)), 1);
                    var zero = ModelData.Looks(set, L((604, 0), (503, 100)), 1);
                    var noChance = ModelData.Looks(set, L((604, 0), (503, 0)), 1);
                    var dup = ModelData.Looks(set, L((604, 50), (604, 50)), 1);
                    var uneven = ModelData.Looks(set, L((604, 30), (503, 70)), 1);
                    Check("looks: the displays that resolve to a model that can be shown, in their order (604, 503; the character model 3167 and the unknown 999 left out)",
                        two.Select(x => x.DisplayId).SequenceEqual(new[] { 604, 503 }) && two.All(x => x.Model.Ok && x.Chance == 25), string.Join(", ", two));
                    Check("looks: a display with chance 0 beside one with a chance is never worn (one look); with no chances at all both stay; the same display twice is one look (chances added)",
                        zero.Count == 1 && zero[0].DisplayId == 503 && noChance.Count == 2 && noChance.All(x => x.Chance == 0) && dup.Count == 1 && dup[0].Chance == 100, string.Join(", ", zero) + " | " + string.Join(", ", noChance) + " | " + string.Join(", ", dup));
                    Check("looks: the first one shown is the highest chance (503 at 70), equal chances the first, none = -1",
                        ModelData.DefaultLook(uneven) == 1 && ModelData.DefaultLook(two) == 0 && ModelData.DefaultLook(new List<NpcLook>()) == -1);
                }
                var v3 = Path.Combine(work, "classic-v4.bin"); ci.Save(v3);
                var b3 = ClassicIndex.Load(v3);
                Check("classic index: format 4 keeps the display ids and chances through a save", b3 != null && b3.Provenance.FormatVersion == 4 && ClassicIndex.FormatVersion == 4 && b3.NpcInfo(68).DisplayIds.SequenceEqual(new[] { 3167, 5446 })
                    && b3.NpcInfo(72).DisplayChances.SequenceEqual(new[] { 30, 70 }) && b3.NpcInfo(68).DisplayChances.SequenceEqual(new[] { 0, 100 }) && b3.NpcInfo(71).DisplayIds.Length == 0 && b3.NpcInfo(69).Name == "Timber Wolf");
                var v3old = Path.Combine(work, "classic-v3.bin");
                using (var fs = new FileStream(v3old, FileMode.Create)) using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest)) using (var bw = new BinaryWriter(gz, new System.Text.UTF8Encoding(false))) ci.Write(bw, 3);
                var b3old = ClassicIndex.Load(v3old);
                Check("classic index: a format-3 file (chances before update z2823's rule) still reads with its display ids, and counts as outdated (read again from the dump)",
                    b3old != null && b3old.Provenance.FormatVersion == 3 && b3old.NpcInfo(68).DisplayIds.SequenceEqual(new[] { 3167, 5446 }) && ClassicSetup.Outdated(b3old));
                var v2 = Path.Combine(work, "classic-v2.bin");
                using (var fs = new FileStream(v2, FileMode.Create)) using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest)) using (var bw = new BinaryWriter(gz, new System.Text.UTF8Encoding(false))) ci.Write(bw, 2);
                var b2 = ClassicIndex.Load(v2);
                Check("classic index: a format-2 file (Outpost 3.5) still reads - same npcs, no display ids - and counts as outdated (read again from the dump on this PC)",
                    b2 != null && b2.Provenance.FormatVersion == 2 && b2.Npcs.Count == ci.Npcs.Count && b2.NpcInfo(69).Name == "Timber Wolf" && b2.Npcs.Values.All(n => n.DisplayIds.Length == 0) && ClassicSetup.Outdated(b2) && !ClassicSetup.Outdated(b3));

                // ---- ForNpc: the client's own display first, then the Classic baseline
                ModelData.SetForTest(set);
                ClassicAtlas.Use(b3);
                SeenData.SetForTest(new SeenSnapshot(new[] { new WdbCreature { Id = 69, Name = "Timber Wolf", DisplayId = 503 }, new WdbCreature { Id = 900, Name = "Seen Only", DisplayId = 604 } }, null, null));
                var f69 = ModelData.ForNpc(69); var f900 = ModelData.ForNpc(900); var f68 = ModelData.ForNpc(68); var f71 = ModelData.ForNpc(71);
                Check("for npc: the display the client saw wins over the Classic baseline's (69 seen as 503), a creature only the client met resolves, a humanoid is flagged",
                    f69.DisplayId == 503 && f69.From == "seen" && f69.Considered.Count == 1 && f69.ModelFileId == 123090 && f900.Ok && f900.ModelFileId == 126487 && f68.Humanoid && f68.From == "classic" && f68.DisplayId == 3167,
                    f69.DisplayId + "/" + f69.From + " " + f900.Reason + " " + f68.Reason);
                SeenData.SetForTest(null, SeenData.State.NotStarted);
                var c69 = ModelData.ForNpc(69);
                Check("for npc: without the client's cache the Classic baseline's display is used (604 -> 126487, 2 skins); a creature with no display says so",
                    c69.Ok && c69.DisplayId == 604 && c69.From == "classic" && c69.ModelFileId == 126487 && c69.TextureCount == 2 && !f71.Ok && ModelData.ForNpc(71).Reason.StartsWith("no display id"), c69.Reason);
                ClassicAtlas.Use(b2);
                var old = ModelData.ForNpc(69);
                Check("for npc: with a format-2 Classic index the reason names it (no guess)", !old.Ok && old.Reason.Contains("older than format 3") && ClassicAtlas.Displays(69).Count == 0, old.Reason);
                ClassicAtlas.Use(null);
                Check("for npc: no Classic baseline and no cache = 'no display id' with that said", ModelData.ForNpc(69).Reason.Contains("no Classic baseline"), ModelData.ForNpc(69).Reason);

                // ---- 3.6 step 2: the model build (MainWindow.ModelsTest.Build.cs)
                try { await ModelBuildChecks(work, Check); } catch (Exception e) { Check("model build checks crashed", false, e.ToString()); }

                // ---- 3.6 steps 3 + 4: the viewer and the NPC page's Model card (MainWindow.ModelsTest.View.cs)
                try { await ModelViewChecks(work, Check, b3, notes); } catch (Exception e) { Check("model view checks crashed", false, e.ToString()); }
            }
            catch (Exception e) { Check("models test crashed", false, e.ToString()); }
            finally
            {
                ModelData.ResetForTest(); ModelData.CacheFile = savedCache; ModelData.AutoStart = savedAuto;
                SeenData.SetForTest(savedSeen, savedSeenSt);
                ClassicAtlas.Use(null);
            }
            return (ok ? "models: ok" : "models: FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
