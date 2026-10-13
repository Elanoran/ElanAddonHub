using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas.Classic;

namespace ElansAddonHub
{
    // --selftest "atlasclassic": the Classic baseline of the Atlas (Services/Atlas/Classic). All input is synthetic SQL written here (no game data, nothing from
    // the real dump unless ELANSHUB_CLASSICDB points at a folder holding a downloaded dump + updates; then one extra check runs on it):
    //   parser: statement splitting (comments, ; inside strings), values (escapes, NULL, negative / decimal / exponent / hex), multi-row INSERT with and without a
    //           column list, REPLACE, UPDATE / DELETE with WHERE (AND, OR, NOT, IN, BETWEEN, LIKE, parentheses), @variables and arithmetic, things it must skip
    //   loot:   chance %, quest-only, group chance (explicit + equal share, capped at 100), references with gate and repeat count, the same item twice, a reference loop
    //   updates: numbered scripts above the dump's content version only, in order; skipped statements counted
    //   downloader (fake GitHub on 127.0.0.1): size shown before, progress, cancel, damaged / truncated / non-SQL dump refused, an earlier good set untouched by a
    //           failed download, no half files, source.json last, https only, safe names
    //   index: save / load round trip, damaged index = null, queries and the "Classic baseline" label on every result
    public partial class MainWindow
    {
        const string AcDump = @"
-- MySQL dump 10.13
/*!40101 SET NAMES utf8mb4 */;
/*!40014 SET UNIQUE_CHECKS=0 */;
DROP TABLE IF EXISTS `db_version`;
CREATE TABLE `db_version` (
  `version` varchar(120) DEFAULT NULL,
  `content_100_VDB-1_world` bit(1) DEFAULT NULL
) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3 COMMENT='Used DB version notes; with a semicolon';
CREATE TABLE `creature_template` (
  `Entry` mediumint unsigned NOT NULL DEFAULT '0',
  `Name` char(100) NOT NULL DEFAULT '',
  `SubName` char(100) DEFAULT NULL,
  `MinLevel` tinyint unsigned NOT NULL DEFAULT '1',
  `MaxLevel` tinyint unsigned NOT NULL DEFAULT '1',
  `Faction` smallint unsigned NOT NULL DEFAULT '0',
  `Family` tinyint NOT NULL DEFAULT '0',
  `CreatureType` tinyint unsigned NOT NULL DEFAULT '0',
  `NpcFlags` int unsigned NOT NULL DEFAULT '0',
  `Rank` tinyint unsigned NOT NULL DEFAULT '0',
  `LootId` mediumint unsigned NOT NULL DEFAULT '0',
  `PickpocketLootId` mediumint unsigned NOT NULL DEFAULT '0',
  `SkinningLootId` mediumint unsigned NOT NULL DEFAULT '0',
  `VendorTemplateId` mediumint unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`Entry`)
) ENGINE=MyISAM DEFAULT CHARSET=utf8mb3;
LOCK TABLES `creature_template` WRITE;
INSERT INTO `creature_template` VALUES (1,'Wolf; the ''Dire'' one','',5,6,14,1,1,0,0,1,0,1,0),(2,'Merchant \'Bob\'','Goods',10,10,35,0,7,129,0,0,5,0,7),(3,'Boss ""X""\\Y','',20,22,14,0,7,0,3,3,0,0,0),(4,'Loop Beast',NULL,1,1,14,0,1,0,0,4,0,0,0);
UNLOCK TABLES;
CREATE TABLE `creature` (
  `guid` int unsigned NOT NULL AUTO_INCREMENT,
  `id` mediumint unsigned NOT NULL DEFAULT '0',
  `map` smallint unsigned NOT NULL DEFAULT '0',
  `spawnMask` tinyint unsigned NOT NULL DEFAULT '1',
  `position_x` float NOT NULL DEFAULT '0',
  `position_y` float NOT NULL DEFAULT '0',
  `position_z` float NOT NULL DEFAULT '0',
  `orientation` float NOT NULL DEFAULT '0',
  `spawntimesecsmin` int unsigned NOT NULL DEFAULT '120',
  `spawntimesecsmax` int unsigned NOT NULL DEFAULT '120',
  PRIMARY KEY (`guid`)
) ENGINE=MyISAM;
INSERT INTO `creature` VALUES (1,1,0,1,-8949.95,-132.493,83.5312,0,300,300),(2,1,1,1,1.5e2,2,3,0,0,0),(3,0,0,1,10,20,30,0,60,60);
CREATE TABLE `creature_spawn_entry` (`guid` int unsigned NOT NULL, `entry` mediumint unsigned NOT NULL, PRIMARY KEY (`guid`,`entry`)) ENGINE=MyISAM;
INSERT INTO `creature_spawn_entry` VALUES (3,2),(3,3);
CREATE TABLE `creature_loot_template` (
  `entry` mediumint unsigned NOT NULL DEFAULT '0', `item` mediumint unsigned NOT NULL DEFAULT '0', `ChanceOrQuestChance` float NOT NULL DEFAULT '100',
  `groupid` tinyint unsigned NOT NULL DEFAULT '0', `mincountOrRef` mediumint NOT NULL DEFAULT '1', `maxcount` tinyint unsigned NOT NULL DEFAULT '1',
  `condition_id` mediumint unsigned NOT NULL DEFAULT '0', `comments` varchar(300) DEFAULT NULL, PRIMARY KEY (`entry`,`item`)
) ENGINE=MyISAM;
INSERT INTO `creature_loot_template` VALUES (1,100,50,0,1,2,0,'Wolf Pelt'),(1,101,-25,0,1,1,0,NULL),(1,200,30,1,1,1,0,NULL),(1,201,20,1,1,1,0,NULL),(1,202,0,1,1,1,0,NULL),(1,203,0,1,1,1,0,NULL),(1,300,10,0,-9000,2,0,'ref'),(1,402,10,0,1,1,0,NULL),(3,500,70,2,1,1,0,NULL),(3,501,50,2,1,1,0,NULL),(3,502,0,2,1,1,0,NULL),(4,0,100,0,-9001,1,0,NULL);
CREATE TABLE `reference_loot_template` (
  `entry` mediumint unsigned NOT NULL DEFAULT '0', `item` mediumint unsigned NOT NULL DEFAULT '0', `ChanceOrQuestChance` float NOT NULL DEFAULT '100',
  `groupid` tinyint unsigned NOT NULL DEFAULT '0', `mincountOrRef` mediumint NOT NULL DEFAULT '1', `maxcount` tinyint unsigned NOT NULL DEFAULT '1',
  `condition_id` mediumint unsigned NOT NULL DEFAULT '0', `comments` varchar(300) DEFAULT NULL, PRIMARY KEY (`entry`,`item`)
) ENGINE=MyISAM;
INSERT INTO `reference_loot_template` VALUES (9000,400,0,1,1,1,0,NULL),(9000,401,0,1,1,1,0,NULL),(9000,402,20,0,1,1,0,NULL),(9001,600,100,0,1,1,0,NULL),(9001,0,100,0,-9002,1,0,NULL),(9002,601,100,0,1,1,0,NULL),(9002,0,100,0,-9001,1,0,NULL);
CREATE TABLE `skinning_loot_template` (
  `entry` mediumint unsigned NOT NULL DEFAULT '0', `item` mediumint unsigned NOT NULL DEFAULT '0', `ChanceOrQuestChance` float NOT NULL DEFAULT '100',
  `groupid` tinyint unsigned NOT NULL DEFAULT '0', `mincountOrRef` mediumint NOT NULL DEFAULT '1', `maxcount` tinyint unsigned NOT NULL DEFAULT '1',
  `condition_id` mediumint unsigned NOT NULL DEFAULT '0', `comments` varchar(300) DEFAULT NULL, PRIMARY KEY (`entry`,`item`)
) ENGINE=MyISAM;
INSERT INTO `skinning_loot_template` VALUES (1,700,100,0,1,1,0,NULL);
INSERT INTO `pickpocketing_loot_template` (entry,item,ChanceOrQuestChance,groupid,mincountOrRef,maxcount) VALUES (5,710,15,0,1,3);
CREATE TABLE `npc_vendor_template` (`entry` mediumint unsigned NOT NULL, `item` mediumint unsigned NOT NULL, `maxcount` tinyint unsigned NOT NULL DEFAULT '0', `incrtime` int unsigned NOT NULL DEFAULT '0', `slot` tinyint NOT NULL DEFAULT '0', `condition_id` mediumint NOT NULL DEFAULT '0', `comments` text, PRIMARY KEY (`entry`,`item`)) ENGINE=MyISAM;
INSERT INTO `npc_vendor_template` VALUES (7,800,0,0,0,0,NULL),(7,801,5,3600,0,0,NULL),(7,802,1,1,0,0,NULL);
INSERT INTO `npc_vendor` (entry,item,maxcount,incrtime) VALUES (2,802,3,7200);
INSERT INTO `gameobject_template` (entry,type,displayId,name,data1) VALUES (50,3,1,'Old Chest',77),(51,2,1,'Banner',77);
INSERT INTO `gameobject_loot_template` (entry,item,ChanceOrQuestChance,groupid,mincountOrRef,maxcount) VALUES (77,900,40,0,1,1);
INSERT INTO `gameobject` (guid,id,map,position_x,position_y,position_z) VALUES (1,50,0,-5,6,7),(2,50,0,-8,9,10);
INSERT INTO `item_template` (entry,name,Quality,ItemLevel,RequiredLevel) VALUES (100,'Wolf Pelt',1,5,0),(101,'Wolf Tooth',0,1,0),(402,'Odd Trinket',3,22,15),(800,'Flour',1,1,0),(802,'Spices',1,1,0);
INSERT INTO `quest_template` (entry,Title,MinLevel,QuestLevel,ZoneOrSort,ReqItemId1,ReqItemCount1,RewItemId1,RewItemCount1,RewChoiceItemId1,RewChoiceItemId2) VALUES (10,'The ''Wolf'' Hunt',5,6,12,100,8,800,2,200,201);
INSERT INTO `creature_questrelation` (id,quest) VALUES (2,10);
INSERT INTO `gameobject_questrelation` (id,quest) VALUES (50,10);
-- trailing comment without a statement
";

        static readonly string[] AcUpdates =
        {
            // "100_old.sql" is at the content version: already in the dump, must NOT run
            "100_old.sql", "DELETE FROM creature_template WHERE entry = 1;",
            "101_b.sql", "UPDATE creature_loot_template SET ChanceOrQuestChance = 60 WHERE entry = 1 AND item = 100;\n" +
                         "DELETE FROM creature_loot_template WHERE entry = 1 AND item IN (202, 203);\n" +
                         "INSERT INTO creature_loot_template (entry,item,ChanceOrQuestChance,groupid,mincountOrRef,maxcount) VALUES (1, 205, 2.5, 0, 1, 1);\n" +
                         "SET @G = 1000;\n" +
                         "INSERT INTO creature (guid,id,map,position_x,position_y,position_z) VALUES (@G+1, 2, 0, -1.5, 2.25, -3), (@G+2, 2, 0, 4, 5, 6);\n" +
                         "UPDATE creature_template SET MaxLevel = MaxLevel + 2 WHERE entry = 2 OR (entry = 3 AND NOT Rank = 0);\n" +
                         "UPDATE creature_template SET Name = 'Merchant Bob', UnitFlags = 4 WHERE entry = 2;\n" +
                         "REPLACE INTO npc_vendor (entry,item,maxcount,incrtime) VALUES (2, 802, 9, 100);\n" +
                         "UPDATE creature_template SET Faction = (SELECT 1) WHERE entry = 1;\n" +
                         "SELECT 1;\n",
            // 99_late sorts by NUMBER: 99 is below the content version, 102 runs after 101
            "99_early.sql", "DELETE FROM creature_template WHERE entry = 2;",
            "102_c.1,2.sql", "UPDATE quest_template SET QuestLevel = 7 WHERE entry BETWEEN 9 AND 11 AND Title LIKE 'The %Hunt';\n" +
                         "DELETE FROM npc_vendor_template WHERE entry = 7 AND item = 800;\n" +
                         "UPDATE creature_template SET Rank = 4 WHERE Name <> 'Loop Beast' AND NpcFlags > 100 AND MinLevel IN (10, 11);\n" +
                         "UPDATE creature_template SET Name = CONCAT(Name, 'x') WHERE entry = 4;\n",
        };

        static List<KeyValuePair<string, string>> AcUpdateList()
        {
            var l = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < AcUpdates.Length; i += 2) l.Add(new KeyValuePair<string, string>(AcUpdates[i], AcUpdates[i + 1]));
            // given out of order on purpose
            l.Reverse();
            return l;
        }

        // ---- fake GitHub: API listing + raw files on 127.0.0.1
        sealed class AcFakeGitHub
        {
            readonly HttpListener l = new HttpListener();
            public int Port;
            public byte[] Dump;                 // served for the dump name
            public string DumpName = "ClassicDB_1_12_1_z1.sql.gz";
            public long ListedDumpSize = -1;    // -1 = Dump.Length
            public bool Slow;                   // dribble the dump out so a cancel lands in the middle
            public int Requests;
            public readonly Dictionary<string, string> Updates = new Dictionary<string, string>();
            public volatile bool StopServing;

            public AcFakeGitHub()
            {
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    Port = 20000 + new Random().Next(20000);
                    var t = new HttpListener();
                    t.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                    try { t.Start(); l = t; break; } catch { t.Close(); }
                }
                Task.Run(async () =>
                {
                    while (l.IsListening)
                    {
                        HttpListenerContext c;
                        try { c = await l.GetContextAsync(); } catch { return; }
                        var cc = c;
                        _ = Task.Run(() => { try { Serve(cc); } catch { try { cc.Response.Abort(); } catch { } } });
                    }
                });
            }

            public string Api => "http://127.0.0.1:" + Port + "/api";
            public string Raw => "http://127.0.0.1:" + Port + "/raw";
            public void Stop() { try { l.Stop(); l.Close(); } catch { } }

            static void Text(HttpListenerContext c, string s, int status = 200)
            {
                var b = Encoding.UTF8.GetBytes(s);
                c.Response.StatusCode = status; c.Response.ContentLength64 = b.Length;
                c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
            }

            void Serve(HttpListenerContext c)
            {
                Interlocked.Increment(ref Requests);
                var p = c.Request.Url.AbsolutePath;
                if (p == "/api/contents/Full_DB")
                    Text(c, "[{\"name\":\".gitignore\",\"size\":10,\"type\":\"file\"},{\"name\":\"" + DumpName + "\",\"size\":" + (ListedDumpSize >= 0 ? ListedDumpSize : Dump.Length) + ",\"type\":\"file\"}]");
                else if (p == "/api/contents/Updates")
                    Text(c, "[" + string.Join(",", Updates.Select(u => "{\"name\":\"" + u.Key + "\",\"size\":" + Encoding.UTF8.GetByteCount(u.Value) + ",\"type\":\"file\"}").Concat(new[] { "{\"name\":\"Instances\",\"size\":0,\"type\":\"dir\"}", "{\"name\":\"notes.txt\",\"size\":3,\"type\":\"file\"}" })) + "]");
                else if (p == "/api/commits/master")
                    Text(c, "{\"sha\":\"0123456789abcdef0123456789abcdef01234567\",\"commit\":{\"committer\":{\"date\":\"2026-09-22T18:45:58Z\"}}}");
                else if (p == "/raw/Full_DB/" + DumpName)
                {
                    var d = Dump;
                    c.Response.ContentLength64 = d.Length;
                    if (!Slow) { c.Response.OutputStream.Write(d, 0, d.Length); c.Response.Close(); return; }
                    for (int i = 0; i < d.Length; i += 16384)
                    {
                        if (StopServing) { c.Response.Abort(); return; }
                        c.Response.OutputStream.Write(d, i, Math.Min(16384, d.Length - i));
                        c.Response.OutputStream.Flush();
                        Thread.Sleep(25);
                    }
                    c.Response.Close();
                }
                else if (p.StartsWith("/raw/Updates/") && Updates.TryGetValue(p.Substring("/raw/Updates/".Length), out var txt)) Text(c, txt);
                else if (p == "/raw/LICENSE.md") Text(c, "GNU GENERAL PUBLIC LICENSE (test copy)");
                else if (p == "/raw/COPYRIGHT.md") Text(c, "Copyright (test copy)");
                else if (p == "/raw/AUTHORS") Text(c, "Authors (test copy)");
                else Text(c, "not found", 404);
            }
        }

        sealed class AcProgress : IProgress<ClassicDownloadProgress>
        {
            public readonly List<ClassicDownloadProgress> Items = new List<ClassicDownloadProgress>();
            public Action<ClassicDownloadProgress> On;
            public void Report(ClassicDownloadProgress p)
            {
                lock (Items) Items.Add(p);
                On?.Invoke(p);
            }
            public List<ClassicDownloadProgress> Snapshot() { lock (Items) return Items.ToList(); }
        }

        static byte[] AcGzip(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        static string AcSha(string path) { using (var s = File.OpenRead(path)) return BitConverter.ToString(SHA256.Create().ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }

        static bool DumpMonotonic(List<ClassicDownloadProgress> pl)
        {
            var d = pl.Where(p => p.Phase.StartsWith("Downloading the dump")).ToList();
            return d.Count >= 2 && d.Zip(d.Skip(1), (a, b) => b.BytesDone >= a.BytesDone).All(x => x);
        }

        static bool AcNear(double a, double b, double eps = 0.0002) => Math.Abs(a - b) <= eps;

        async Task<string> AtlasClassicTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var work = Path.Combine(dir, "atlasclassic");
            string savedApi = ClassicDownloader.ApiBase, savedRaw = ClassicDownloader.RawBase;
            AcFakeGitHub gh = null;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                check("fixture: the work folder is under the scratch folder (never the real data folder)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                check("label: every value is tagged \"Classic baseline\"", ClassicLabel.Source == "Classic baseline" && ClassicAtlas.Source == "Classic baseline");

                // ================= statement reader =================
                {
                    var sql = "SELECT ';' -- c;\n; /* a;b */ SELECT 2 # x;\n;;SELECT 'it''s; \\' ok'; /*!40101 SET x=1 */; SELECT `a;b`;\r\n  INSERT INTO t\r\n VALUES (1)  ;SELECT 'unterminated-at-end'";
                    var rd = new ClassicSqlReader(new StringReader(sql));
                    var got = new List<string>();
                    while (rd.Next(null, out var s, out var skipped)) got.Add(s);
                    check("reader: ; inside strings / backticks and in comments does not end a statement; comments (-- # /* */ /*! */) are dropped; empty statements vanish",
                        got.Count == 6 && got[0] == "SELECT ';'" && got[1] == "SELECT 2" && got[2] == "SELECT 'it''s; \\' ok'" && got[3] == "SELECT `a;b`" && got[4] == "INSERT INTO t VALUES (1)" && got[5] == "SELECT 'unterminated-at-end'");
                    var rd2 = new ClassicSqlReader(new StringReader("INSERT INTO other VALUES (1,'x;y');\nINSERT INTO creature_template VALUES (1);\nSET @a = 1;"));
                    var kept = new List<string>(); int skip = 0;
                    while (rd2.Next(ClassicSqlExec.WantHead, out var s, out var sk)) { if (sk) skip++; else kept.Add(s); }
                    check("reader: a statement for a table we do not keep is swallowed unread (counted), the others come through",
                        skip == 1 && kept.Count == 2 && kept[0].StartsWith("INSERT INTO creature_template") && kept[1] == "SET @a = 1" && rd2.Skipped == 1);
                    var big = new StringBuilder("INSERT INTO t VALUES ");
                    for (int i = 0; i < 20000; i++) big.Append(i > 0 ? "," : "").Append("(" + i + ",'abc;def')");
                    big.Append(";SELECT 1;");
                    var rd3 = new ClassicSqlReader(new StringReader(big.ToString()));
                    rd3.Next(null, out var bs, out _); rd3.Next(null, out var bs2, out _);
                    check("reader: a long multi-row statement (> 64 KB read block) comes through whole", bs.Length > 200000 && bs.EndsWith("(19999,'abc;def')") && bs2 == "SELECT 1");
                }

                // ================= values and statements =================
                {
                    var ex = new ClassicSqlExec();
                    var T = ex.Tables["item_template"];
                    ex.Execute("INSERT INTO item_template (entry,name,quality,itemlevel,requiredlevel) VALUES (1,'a\\nb\\'c''d\\\\e',NULL,-3,1.5e1),(2,\"dq\\\"x\",2,0x1F,-2.25),(3,'',+4,10 DIV 3,7 % 4 * 2 + (1 << 2))", "t");
                    var r1 = T.Rows.Count > 0 ? T.Rows[0] : null;
                    check("values: string escapes (\\n \\' '' \\\\), NULL, negative, exponent decimal", r1 != null && (string)r1[T.Idx["name"]] == "a\nb'c'd\\e" && r1[T.Idx["quality"]] == null && (long)r1[T.Idx["itemlevel"]] == -3 && (double)r1[T.Idx["requiredlevel"]] == 15.0);
                    var r2 = T.Rows.Count > 1 ? T.Rows[1] : null;
                    check("values: double-quoted string, hex number, negative decimal", r2 != null && (string)r2[T.Idx["name"]] == "dq\"x" && (long)r2[T.Idx["itemlevel"]] == 31 && (double)r2[T.Idx["requiredlevel"]] == -2.25);
                    var r3 = T.Rows.Count > 2 ? T.Rows[2] : null;
                    check("values: empty string, unary plus, DIV, %, <<, precedence and parentheses", r3 != null && (string)r3[T.Idx["name"]] == "" && (long)r3[T.Idx["quality"]] == 4 && (long)r3[T.Idx["itemlevel"]] == 3 && (long)r3[T.Idx["requiredlevel"]] == 7 % 4 * 2 + (1 << 2));
                    ex.Execute("INSERT INTO item_template (entry,name) VALUES (1,'dup')", "t");
                    check("insert: a duplicate primary key is refused (statement skipped and counted), the old row stays", ex.Skipped == 1 && T.Rows.Count == 3 && (string)T.Rows[0][T.Idx["name"]] == "a\nb'c'd\\e");
                    ex.Execute("REPLACE INTO item_template (entry,name) VALUES (1,'new'),(9,'nine')", "t");
                    check("replace: replaces by key and inserts new ones", T.Rows.Count == 4 && (string)T.Rows[0][T.Idx["name"]] == "new" && T.ByPk.ContainsKey("9"));
                    ex.Execute("INSERT IGNORE INTO item_template (entry,name) VALUES (1,'ignored'),(10,'ten')", "t");
                    check("insert ignore: duplicates are dropped silently, the rest goes in", T.Rows.Count == 5 && (string)T.Rows[0][T.Idx["name"]] == "new" && ex.Skipped == 1);
                    ex.Execute("UPDATE item_template SET quality = quality + 10, name = 'U' WHERE (entry IN (2, 3) OR name LIKE 'n%') AND NOT entry = 3 AND entry BETWEEN 1 AND 9", "t");
                    var names = T.Rows.ToDictionary(r => (long)r[T.Idx["entry"]], r => (string)r[T.Idx["name"]]);
                    check("update: AND / OR / NOT / IN / LIKE / BETWEEN / parentheses pick exactly rows 1, 2 and 9", names[1] == "U" && names[2] == "U" && names[9] == "U" && names[3] == "" && names[10] == "ten");
                    ex.Execute("UPDATE item_template SET entry = 20 WHERE entry = 10", "t");
                    check("update: changing a primary key moves the key (REPLACE / duplicate checks still right)", T.ByPk.ContainsKey("20") && !T.ByPk.ContainsKey("10"));
                    ex.Execute("DELETE FROM item_template WHERE name IS NULL OR entry > 15", "t");
                    check("delete: IS NULL / > removes the right rows", T.Rows.Count == 4 && !T.ByPk.ContainsKey("20"));
                    ex.Execute("DELETE FROM item_template LIMIT 1", "t");
                    check("delete: LIMIT", T.Rows.Count == 3);
                    long before = ex.Skipped;
                    ex.Execute("UPDATE item_template SET quality = (SELECT 1)", "t");
                    ex.Execute("UPDATE item_template i JOIN creature_template c ON c.entry = i.entry SET i.quality = 1", "t");
                    ex.Execute("DELETE ct FROM item_template ct JOIN creature_template c ON 1", "t");
                    ex.Execute("INSERT INTO item_template (entry) SELECT entry FROM creature_template", "t");
                    ex.Execute("INSERT INTO item_template (entry,name) VALUES (50,'a') ON DUPLICATE KEY UPDATE name = 'b'", "t");
                    ex.Execute("UPDATE item_template SET name = UPPER(name)", "t");
                    check("unsupported: sub-select, join, multi-table delete, INSERT ... SELECT, ON DUPLICATE KEY, functions are skipped and counted, never thrown; the table is untouched", ex.Skipped - before == 6 && T.Rows.Count == 3);
                    ex.Execute("UPDATE item_template SET description = 'x' WHERE nonexistent = 5", "t");
                    ex.Execute("ALTER TABLE item_template ADD COLUMN foo int", "t");
                    ex.Execute("INSERT INTO item_template VALUES (77,'x')", "t");
                    check("columns we do not keep: an UPDATE of only those is a no-op; an INSERT without column list after ALTER is refused", T.Rows.Count == 3 && ex.Skipped - before == 7);
                    ex.Execute("SET @a = 5, @b := @a * 2 + 1, @c = (SELECT 1)", "t");
                    ex.Execute("INSERT INTO item_template (entry,name) VALUES (@b, 'b'), (@a+100, 'c'), (@c, 'bad')", "t");
                    check("variables: SET @a / := with arithmetic feed later VALUES; one with a sub-select is 'unknown' and makes its statement skipped", !T.ByPk.ContainsKey("11") && T.Rows.Count == 3);
                    ex.Execute("INSERT INTO item_template (entry,name) VALUES (@b, 'b'), (@a+100, 'c')", "t");
                    check("variables: values 11 and 105 inserted", T.ByPk.ContainsKey("11") && T.ByPk.ContainsKey("105"));
                    ex.Execute("INSERT INTO item_template (entry,name) VALUES (@x := 300 + 1, 'v'), (@x + 1, 'w')", "t");
                    check("variables: @x := inside a value list is assigned and visible to the next value", T.ByPk.ContainsKey("301") && T.ByPk.ContainsKey("302"));
                }

                // ================= dump + updates -> index =================
                var rep = new ClassicImportReport();
                var idx = ClassicImporter.BuildFromText(AcDump, AcUpdateList(), rep, new ClassicProvenance { DumpName = "d.sql.gz", CommitSha = "abc", CommitDate = "2026-01-01" });
                check("dump: content version read from db_version (100); update 100_old and 99_early are in the dump already, 101 and 102 applied", rep.ContentVersion == 100 && rep.UpdateFilesAlreadyInDump == 2 && rep.UpdateFilesApplied == 2 && rep.UpdateFiles == 4);
                foreach (var n in rep.Notes.Where(x => x.StartsWith("dump:")).Take(5)) notes.Add("info " + n);
                check("dump: every statement of the synthetic dump was understood (nothing failed, the table-less INSERTs carry column lists)", rep.DumpStatementsFailed == 0 && rep.DumpStatementsApplied >= 10);
                notes.Add("info dump statements applied " + rep.DumpStatementsApplied + ", failed " + rep.DumpStatementsFailed + ", unwanted " + rep.DumpStatementsSkippedUnwanted);
                check("dump: tables loaded (4 npcs, 3 + 2 creature rows, 1 object chest + banner), skipped update statements counted and sampled", rep.RowsAfterDump["creature_template"] == 4 && rep.RowsAfterDump["creature"] == 3 && rep.RowsAfterDump["gameobject_template"] == 2 && rep.UpdateStatementsSkipped >= 2 && rep.SkipSamples.Count >= 2);
                check("updates: 100_old (DELETE npc 1) and 99_early (DELETE npc 2) did NOT run", idx.NpcInfo(1) != null && idx.NpcInfo(2) != null);

                var wolf = idx.NpcInfo(1);
                check("npc: name with quotes and ; , levels, rank, faction, flags", wolf != null && wolf.Name == "Wolf; the 'Dire' one" && wolf.MinLevel == 5 && wolf.MaxLevel == 6 && wolf.FactionId == 14 && wolf.Rank == 0 && wolf.Type == 1 && wolf.Family == 1 && wolf.Source == "Classic baseline");
                var bob = idx.NpcInfo(2);
                check("npc update: MaxLevel + 2, new name, vendor flag, sub name", bob.Name == "Merchant Bob" && bob.MaxLevel == 12 && bob.MinLevel == 10 && bob.SubName == "Goods" && bob.IsVendor && !bob.IsQuestGiver);
                check("npc update: NOT / AND / OR picked npc 3 (rank 3 boss) only for MaxLevel; Rank = 4 only for the vendor (flags > 100, level 10, name <> Loop Beast)", idx.NpcInfo(3).MaxLevel == 24 && bob.Rank == 4 && idx.NpcInfo(3).Rank == 3 && idx.NpcInfo(3).Name == "Boss \"X\"\\Y" && idx.NpcInfo(4).Name == "Loop Beast");
                IReadOnlyList<ClassicItemSource> S(int item) => idx.SourcesOf(item);
                ClassicItemSource One(int item, ClassicSourceKind k, int src) => S(item).FirstOrDefault(s => s.Kind == k && s.SourceId == src);
                var s100 = One(100, ClassicSourceKind.NpcDrop, 1);
                check("loot: plain row, chance changed by an update to 60 %, count 1-2", AcNear(s100.Chance, 60) && s100.MinCount == 1 && s100.MaxCount == 2 && !s100.QuestOnly && s100.Source == "Classic baseline");
                var s101 = One(101, ClassicSourceKind.NpcDrop, 1);
                check("loot: negative chance = quest-only, 25 %", AcNear(s101.Chance, 25) && s101.QuestOnly);
                check("loot: group with explicit 30 + 20 (the equal-share rows were deleted by an update) -> 30 and 20", AcNear(One(200, ClassicSourceKind.NpcDrop, 1).Chance, 30) && AcNear(One(201, ClassicSourceKind.NpcDrop, 1).Chance, 20) && One(202, ClassicSourceKind.NpcDrop, 1).SourceId == 0);
                check("loot: a row inserted by an update (2.5 %)", AcNear(One(205, ClassicSourceKind.NpcDrop, 1).Chance, 2.5));
                // reference 9000 gate 10 %, rolled twice: 400/401 are one equal group (50 % per pass) -> 0.10 * (1 - 0.5^2) = 7.5 %; 402 is 20 % per pass -> 0.10 * (1 - 0.8^2) = 3.6 %, combined with its own 10 % row
                check("loot: reference gated by the row's chance, repeated MaxCount times, equal-chance group inside the reference -> 7.5 %", AcNear(One(400, ClassicSourceKind.NpcDrop, 1).Chance, 7.5) && AcNear(One(401, ClassicSourceKind.NpcDrop, 1).Chance, 7.5));
                check("loot: the same item from a reference (3.6 %) and a direct row (10 %) combines as independent rolls -> 13.24 %", AcNear(One(402, ClassicSourceKind.NpcDrop, 1).Chance, 13.24, 0.001));
                check("loot: explicit 70 + 50 in one group is capped at 100 (70 + 30) and a zero-chance row gets nothing", AcNear(One(500, ClassicSourceKind.NpcDrop, 3).Chance, 70) && AcNear(One(501, ClassicSourceKind.NpcDrop, 3).Chance, 30) && S(502).Count == 0);
                check("loot: a reference loop does not hang; both sides resolve (100 %) and it is counted", AcNear(One(600, ClassicSourceKind.NpcDrop, 4).Chance, 100) && AcNear(One(601, ClassicSourceKind.NpcDrop, 4).Chance, 100) && rep.ReferenceCycles >= 1);
                check("loot: skinning and pickpocketing tables are separate source kinds (pickpocket: INSERT with a column list and no CREATE TABLE seen)", AcNear(One(700, ClassicSourceKind.NpcSkin, 1).Chance, 100) && AcNear(One(710, ClassicSourceKind.NpcPickpocket, 2).Chance, 15) && One(710, ClassicSourceKind.NpcPickpocket, 2).MaxCount == 3 && S(700).Count == 1);
                var obj = idx.ObjectInfo(50);
                check("object: chest loot id from data1 (type 3) -> 40 % from the object; the banner (type 2) has none", AcNear(One(900, ClassicSourceKind.ObjectLoot, 50).Chance, 40) && obj.LootId == 77 && idx.ObjectInfo(51).LootId == 0 && obj.Name == "Old Chest");

                var vend = idx.VendorItems(2);
                check("vendor: npc rows plus the vendor template; the npc's own row wins (stock 9 after REPLACE, restock 100); the template row deleted by an update is gone", vend.Count == 2 && vend.Any(v => v.ItemId == 802 && v.MaxCount == 9 && v.RestockSeconds == 100) && vend.Any(v => v.ItemId == 801 && v.MaxCount == 5 && v.RestockSeconds == 3600) && !vend.Any(v => v.ItemId == 800) && vend[0].Source == "Classic baseline");
                check("vendor: an item lists the vendor as a source", One(801, ClassicSourceKind.Vendor, 2).MaxCount == 5 && idx.VendorItems(99).Count == 0);

                var q = idx.QuestInfo(10);
                check("quest: title with quotes, levels (QuestLevel 7 from an update), zone, reward / choice / objective items with counts, givers (npc 2 and object 50)",
                    q != null && q.Title == "The 'Wolf' Hunt" && q.MinLevel == 5 && q.QuestLevel == 7 && q.ZoneOrSort == 12 && q.RewardItems.SequenceEqual(new[] { 800 }) && q.RewardCounts[0] == 2 && q.ChoiceItems.SequenceEqual(new[] { 200, 201 }) && q.ObjectiveItems.SequenceEqual(new[] { 100 }) && q.ObjectiveCounts[0] == 8
                    && q.Givers.Length == 2 && q.Givers.Any(g => !g.IsObject && g.Id == 2) && q.Givers.Any(g => g.IsObject && g.Id == 50));
                check("quest: QuestsRewarding covers fixed rewards and choices, QuestsNeeding the objectives", idx.QuestsRewarding(800).Count == 1 && idx.QuestsRewarding(201).Count == 1 && idx.QuestsRewarding(100).Count == 0 && idx.QuestsNeeding(100).Count == 1 && One(201, ClassicSourceKind.QuestChoice, 10).SourceId == 10 && One(800, ClassicSourceKind.QuestReward, 10).MinCount == 2);

                var sp1 = idx.Spawns(1);
                check("spawns: raw map + world x / y / z, negative and exponent decimals, respawn", sp1.Count == 2 && sp1[0].Map == 0 && AcNear(sp1[0].X, -8949.95, 0.01) && AcNear(sp1[0].Y, -132.493, 0.01) && AcNear(sp1[0].Z, 83.5312, 0.01) && sp1[0].RespawnSeconds == 300 && sp1[1].Map == 1 && AcNear(sp1[1].X, 150));
                check("spawns: a shared spawn (creature_spawn_entry) counts for each of its npcs; rows added by an update use @variables (npc 2: 1 shared + 2 added)", idx.Spawns(2).Count == 3 && idx.Spawns(3).Count == 1 && idx.Spawns(2).Any(s => AcNear(s.X, -1.5) && AcNear(s.Y, 2.25) && AcNear(s.Z, -3)) && idx.ObjectSpawns(50).Count == 2 && idx.Spawns(999).Count == 0);
                var fp = new ClassicSpawn { Map = 1, X = 100, Y = 50 };
                fp.ToFraction(200, 0, 100, 0, out var fx, out var fy);
                check("map model: world X (north) / Y (west) -> picture fractions (x from the west edge, y from the north edge)", AcNear(fx, 0.5) && AcNear(fy, 0.5) && AcNear(Frac(new ClassicSpawn { X = 200, Y = 100 }), 0) && AcNear(Frac(new ClassicSpawn { X = 0, Y = 0 }), 2));

                // search
                var hits = idx.Search("wolf", 10);
                check("search: npc, two items and a quest with Wolf in the name found, case-insensitive, each tagged; the exact / prefix hit ranks before a hit in the middle", hits.Count == 4 && hits.All(h => h.Source == "Classic baseline") && hits.Any(h => h.Kind == ClassicEntityKind.Npc && h.Id == 1) && hits.Any(h => h.Kind == ClassicEntityKind.Item && h.Id == 100) && hits.Any(h => h.Kind == ClassicEntityKind.Quest && h.Id == 10));
                check("search: kinds filter, limit, ranking (starts-with before contains), empty text", idx.Search("WOLF", 10, ClassicEntityKind.Item).Count == 2 && idx.Search("wolf", 1).Count == 1 && idx.Search("pelt", 5)[0].Id == 100 && idx.Search("", 5).Count == 0 && idx.Search("zzzz", 5).Count == 0 && idx.Search("wolf", 10)[0].Name.StartsWith("Wolf", StringComparison.Ordinal));
                var look = idx.LootOf(ClassicSourceKind.NpcDrop, 1);
                check("lookup: LootOf lists an npc's drops best chance first", look.Count >= 8 && look[0].Source.Chance >= look[1].Source.Chance && look[0].ItemId == 100);

                // ================= index file =================
                var ipath = Path.Combine(work, "index.bin");
                idx.Save(ipath);
                var back = ClassicIndex.Load(ipath);
                check("index: saved atomically (no .part left) and loaded", back != null && File.Exists(ipath) && !File.Exists(ipath + ".part") && new FileInfo(ipath).Length > 200);
                check("index round trip: counts, npc, object, quest, item info, provenance", back.Npcs.Count == idx.Npcs.Count && back.Objects.Count == idx.Objects.Count && back.Quests.Count == idx.Quests.Count && back.Items.Count == idx.Items.Count
                    && back.NpcInfo(2).Name == "Merchant Bob" && back.NpcInfo(2).Spawns.Length == 3 && back.ObjectInfo(50).LootId == 77 && back.QuestInfo(10).Title == "The 'Wolf' Hunt" && back.ItemInfo(402).Name == "Odd Trinket" && back.ItemInfo(402).Quality == 3 && back.ItemInfo(402).ItemLevel == 22 && back.ItemInfo(402).RequiredLevel == 15
                    && back.Provenance.CommitSha == "abc" && back.Provenance.DumpName == "d.sql.gz" && back.Provenance.UpdatesApplied == 2);
                bool same = true;
                foreach (var kv in idx.ItemSources)
                {
                    var b = back.SourcesOf(kv.Key);
                    same &= b.Count == kv.Value.Count;
                    for (int i = 0; same && i < kv.Value.Count; i++) same &= b[i].Kind == kv.Value[i].Kind && b[i].SourceId == kv.Value[i].SourceId && AcNear(b[i].Chance, kv.Value[i].Chance, 0.001) && b[i].QuestOnly == kv.Value[i].QuestOnly && b[i].MinCount == kv.Value[i].MinCount && b[i].MaxCount == kv.Value[i].MaxCount && b[i].RestockSeconds == kv.Value[i].RestockSeconds;
                }
                check("index round trip: every item's sources identical (kind, id, chance, quest-only, counts, restock)", same && back.ItemSources.Count == idx.ItemSources.Count);
                check("index round trip: queries on the loaded copy (vendor, quests, search, loot)", back.VendorItems(2).Count == 2 && back.QuestsRewarding(800).Count == 1 && back.Search("pelt", 3)[0].Id == 100 && back.LootOf(ClassicSourceKind.NpcDrop, 1).Count == look.Count);
                var bytes = File.ReadAllBytes(ipath);
                File.WriteAllBytes(Path.Combine(work, "trunc.bin"), bytes.Take(bytes.Length / 2).ToArray());
                var flipped = (byte[])bytes.Clone(); flipped[flipped.Length / 2] ^= 0xFF; flipped[flipped.Length / 2 + 1] ^= 0xFF;
                File.WriteAllBytes(Path.Combine(work, "flip.bin"), flipped);
                File.WriteAllText(Path.Combine(work, "text.bin"), "this is not an index");
                File.WriteAllBytes(Path.Combine(work, "empty.bin"), new byte[0]);
                check("index: truncated, bit-flipped, wrong and empty files load as null (no exception)", ClassicIndex.Load(Path.Combine(work, "trunc.bin")) == null && ClassicIndex.Load(Path.Combine(work, "flip.bin")) == null && ClassicIndex.Load(Path.Combine(work, "text.bin")) == null && ClassicIndex.Load(Path.Combine(work, "empty.bin")) == null && ClassicIndex.Load(Path.Combine(work, "missing.bin")) == null);

                // ================= facade without data =================
                ClassicAtlas.Use(null);
                check("facade: with no index every query answers 'nothing' (not null lists), state follows the files (Use(null) forgets the loaded index)", ClassicAtlas.SourcesOf(100).Count == 0 && ClassicAtlas.NpcInfo(1) == null && ClassicAtlas.Search("wolf").Count == 0 && ClassicAtlas.VendorItems(2).Count == 0);
                ClassicAtlas.Use(idx);
                check("facade: Use(index) makes the static queries work", ClassicAtlas.IsReady && ClassicAtlas.SourcesOf(100).Count > 0 && ClassicAtlas.NpcInfo(1).Name.StartsWith("Wolf") && ClassicAtlas.State() == ClassicState.Ready);
                ClassicAtlas.Use(null);

                // ================= downloader =================
                gh = new AcFakeGitHub();
                var good = AcGzip(Encoding.UTF8.GetBytes(AcDump));
                gh.Dump = good;
                foreach (var u in AcUpdateList()) gh.Updates[u.Key] = u.Value;
                ClassicDownloader.ApiBase = gh.Api; ClassicDownloader.RawBase = gh.Raw;
                var dl = Path.Combine(work, "dl");

                var remote = await ClassicDownloader.Query(CancellationToken.None);
                check("query: dump name, size and update list with sizes are known before anything is downloaded (directories / other files ignored)", remote.DumpName == gh.DumpName && remote.DumpSize == good.Length && remote.Updates.Count == 4 && remote.UpdatesSize > 100 && remote.TotalSize == remote.DumpSize + remote.UpdatesSize + ClassicRemote.LicenseEstimate && !Directory.Exists(dl));
                check("query: commit and date for the credit, licence notice names GPL-3.0 and the label", remote.CommitSha.StartsWith("0123456789abcdef") && remote.CommitDate == "2026-09-22T18:45:58Z" && remote.Notice.Contains("GPL-3.0") && remote.Notice.Contains("Classic baseline") && remote.Repo == "https://github.com/cmangos/classic-db");

                var prog = new AcProgress();
                var res = await ClassicDownloader.Download(remote, prog, CancellationToken.None, dl);
                var pl = prog.Snapshot();
                check("download: dump, licence files, updates and source.json are in place; staging and .part files are gone",
                    File.Exists(Path.Combine(dl, gh.DumpName)) && File.Exists(Path.Combine(dl, "LICENSE.md")) && File.Exists(Path.Combine(dl, "COPYRIGHT.md")) && File.Exists(Path.Combine(dl, "AUTHORS"))
                    && Directory.GetFiles(Path.Combine(dl, "updates"), "*.sql").Length == 4 && File.Exists(ClassicPaths.SourceFile(dl)) && !Directory.Exists(Path.Combine(dl, ".staging")) && Directory.GetFiles(dl, "*.part", SearchOption.AllDirectories).Length == 0);
                check("download: progress reported (dump phase, updates phase), bytes never go backwards, reach at least dump + updates", pl.Count >= 5 && pl.Any(p => p.Phase.StartsWith("Downloading the dump")) && pl.Any(p => p.Phase.StartsWith("Downloading updates")) && DumpMonotonic(pl) && pl.Max(p => p.BytesDone) >= remote.DumpSize + remote.UpdatesSize && pl.All(p => p.BytesTotal == remote.TotalSize));
                var src = ClassicDownloader.ReadSource(dl);
                check("download: source.json records repo, licence, credit, label, dump name, size, SHA-256 (matches the file), commit, date, update count",
                    src != null && src.Str("repo") == remote.Repo && src.Str("license") == "GPL-3.0" && src.Str("label") == "Classic baseline" && src.Str("dump") == gh.DumpName && src.Long("dumpSize") == good.Length
                    && src.Str("sha256") == AcSha(Path.Combine(dl, gh.DumpName)) && src.Str("commit") == remote.CommitSha && src.Str("commitDate") == remote.CommitDate && src.Int("updates") == 4 && res.Sha256 == src.Str("sha256"));
                check("download: licence text is kept next to the data", File.ReadAllText(Path.Combine(dl, "LICENSE.md")).Contains("GNU GENERAL PUBLIC LICENSE") && ClassicDownloader.IsGzipValid(Path.Combine(dl, gh.DumpName), out _));

                // build the index from what was downloaded
                var brep = await ClassicAtlas.BuildIndex(null, CancellationToken.None, dl);
                var built = ClassicIndex.Load(ClassicPaths.IndexFile(dl));
                check("build: the downloaded set imports into index.bin next to it, credited with the commit", brep != null && built != null && built.SourcesOf(100).Count > 0 && built.Provenance.CommitSha == remote.CommitSha && built.Provenance.DumpName == gh.DumpName && brep.IndexBytes == new FileInfo(ClassicPaths.IndexFile(dl)).Length && brep.Seconds >= 0);
                var goodHash = AcSha(Path.Combine(dl, gh.DumpName));
                var goodSource = File.ReadAllText(ClassicPaths.SourceFile(dl));
                var goodUpdate = File.ReadAllText(Path.Combine(dl, "updates", "101_b.sql"));

                Func<Task<Exception>> tryDownload = async () =>
                {
                    try { var rm = await ClassicDownloader.Query(CancellationToken.None); await ClassicDownloader.Download(rm, null, CancellationToken.None, dl); return null; }
                    catch (Exception e) { return e; }
                };
                Func<bool> untouched = () => File.Exists(Path.Combine(dl, "ClassicDB_1_12_1_z1.sql.gz")) && AcSha(Path.Combine(dl, "ClassicDB_1_12_1_z1.sql.gz")) == goodHash && File.ReadAllText(ClassicPaths.SourceFile(dl)) == goodSource
                    && File.ReadAllText(Path.Combine(dl, "updates", "101_b.sql")) == goodUpdate && Directory.GetFiles(dl, "ClassicDB_*.sql.gz").Length == 1 && !Directory.Exists(Path.Combine(dl, ".staging")) && Directory.GetFiles(dl, "*.part", SearchOption.AllDirectories).Length == 0;

                // damaged dump (bytes flipped in the middle of the compressed data)
                var damaged = (byte[])good.Clone();
                for (int i = damaged.Length / 2; i < damaged.Length / 2 + 6; i++) damaged[i] ^= 0x5A;
                gh.DumpName = "ClassicDB_1_12_1_z2.sql.gz"; gh.Dump = damaged;
                var e1 = await tryDownload();
                check("corrupt gzip: refused with a message, the earlier good set is byte-identical, nothing half-written", e1 is ClassicDownloadException && e1.Message.Contains("not usable") && untouched());

                // truncated, size consistent with the listing (so only the gzip check can catch it)
                gh.Dump = good.Take(good.Length - 12).ToArray();
                var e2 = await tryDownload();
                check("truncated gzip (listing agrees with the cut file): refused, earlier set untouched", e2 is ClassicDownloadException && untouched());

                // size differs from what GitHub listed
                gh.Dump = good; gh.ListedDumpSize = good.Length + 500;
                var e3 = await tryDownload();
                check("size not what the listing says: refused, earlier set untouched", e3 is ClassicDownloadException && e3.Message.Contains("not what GitHub listed") && untouched());
                gh.ListedDumpSize = -1;

                // valid gzip, but not a SQL dump
                gh.Dump = AcGzip(Encoding.UTF8.GetBytes("hello, this is not a database dump at all"));
                var e4 = await tryDownload();
                check("valid gzip that is not an SQL dump: refused, earlier set untouched", e4 is ClassicDownloadException && e4.Message.Contains("SQL") && untouched());

                // an update file missing on the server -> the whole set is refused
                gh.Dump = good; gh.Updates["103_gone.sql"] = "SELECT 1;";
                var rmGone = await ClassicDownloader.Query(CancellationToken.None);
                gh.Updates.Remove("103_gone.sql");
                Exception e5 = null;
                try { await ClassicDownloader.Download(rmGone, null, CancellationToken.None, dl); } catch (Exception e) { e5 = e; }
                check("a missing update file: the new set is refused as a whole, earlier set untouched", e5 is ClassicDownloadException && untouched());

                // cancel in the middle of the dump
                var bigRaw = new byte[600 * 1024];
                new Random(7).NextBytes(bigRaw);
                var bigSql = Encoding.UTF8.GetBytes(AcDump + "-- " + Convert.ToBase64String(bigRaw) + "\n");
                gh.Dump = AcGzip(bigSql); gh.Slow = true;
                var cts = new CancellationTokenSource();
                var cp = new AcProgress();
                cp.On = p => { if (p.BytesDone > 40000) cts.Cancel(); };
                var sw = Stopwatch.StartNew();
                Exception e6 = null;
                try { var rm6 = await ClassicDownloader.Query(CancellationToken.None); await ClassicDownloader.Download(rm6, cp, cts.Token, dl); } catch (Exception e) { e6 = e; }
                gh.StopServing = true; gh.Slow = false;
                check("cancel: stops in the middle of the dump (OperationCanceledException, well before it would have finished), earlier set untouched, staging cleaned",
                    e6 is OperationCanceledException && sw.Elapsed.TotalSeconds < 12 && cp.Snapshot().Max(p => p.BytesDone) < gh.Dump.Length && untouched());
                gh.StopServing = false;
                gh.DumpName = "ClassicDB_1_12_1_z1.sql.gz"; gh.Dump = good;

                // a newer good dump replaces the old set (old dump file removed, new source.json)
                gh.DumpName = "ClassicDB_1_12_1_z3.sql.gz";
                var rm7 = await ClassicDownloader.Query(CancellationToken.None);
                await ClassicDownloader.Download(rm7, null, CancellationToken.None, dl);
                check("a newer dump replaces the old one (one dump file left, source.json names the new one, no .part)", Directory.GetFiles(dl, "ClassicDB_*.sql.gz").Length == 1 && File.Exists(Path.Combine(dl, "ClassicDB_1_12_1_z3.sql.gz")) && ClassicDownloader.ReadSource(dl).Str("dump") == "ClassicDB_1_12_1_z3.sql.gz" && Directory.GetFiles(dl, "*.part", SearchOption.AllDirectories).Length == 0);

                // safety rules
                ClassicDownloader.RawBase = "http://example.com/raw";
                Exception e8 = null;
                try { await ClassicDownloader.Download(rm7, null, CancellationToken.None, Path.Combine(work, "dl2")); } catch (Exception e) { e8 = e; }
                check("https only: a plain http address that is not this PC is refused before any request", e8 is ClassicDownloadException && e8.Message.Contains("https") && !Directory.Exists(Path.Combine(work, "dl2", "updates")));
                ClassicDownloader.RawBase = gh.Raw; ClassicDownloader.ApiBase = "ftp://example.com/api";
                Exception e9 = null;
                try { await ClassicDownloader.Query(CancellationToken.None); } catch (Exception e) { e9 = e; }
                check("https only: any other scheme is refused too", e9 is ClassicDownloadException);
                ClassicDownloader.ApiBase = gh.Api;
                var evil = new ClassicRemote { DumpName = "..\\..\\evil.sql.gz", DumpSize = 1 };
                Exception e10 = null;
                try { await ClassicDownloader.Download(evil, null, CancellationToken.None, Path.Combine(work, "dl3")); } catch (Exception e) { e10 = e; }
                var evil2 = new ClassicRemote { DumpName = "ClassicDB_1.sql.gz", DumpSize = 1 };
                evil2.Updates.Add(new KeyValuePair<string, long>("..\\x.sql", 1));
                Exception e11 = null;
                try { await ClassicDownloader.Download(evil2, null, CancellationToken.None, Path.Combine(work, "dl3")); } catch (Exception e) { e11 = e; }
                check("safe names: a dump or update name with a path in it is refused, nothing written outside the folder", e10 is ClassicDownloadException && e11 is ClassicDownloadException && !File.Exists(Path.Combine(work, "evil.sql.gz")) && !Directory.Exists(Path.Combine(work, "dl3", ".staging")));

                // the facade state follows the files (test data folder)
                check("state: Downloaded once source.json exists, NotDownloaded after Delete", ClassicDownloader.ReadSource(dl) != null);
                ClassicAtlas.Delete(dl);
                check("delete: the whole folder (dump, licence, updates, index) is removed", !Directory.Exists(dl));

                // ================= real data (optional) =================
                var real = Environment.GetEnvironmentVariable("ELANSHUB_CLASSICDB");
                if (!string.IsNullOrEmpty(real) && Directory.Exists(real))
                {
                    var gz = Directory.GetFiles(real, "*.sql.gz").FirstOrDefault();
                    if (gz != null)
                    {
                        var ri = Path.Combine(work, "real-index.bin");
                        var rr = ClassicImporter.Import(gz, Path.Combine(real, "updates"), ri, new ClassicProvenance { DumpName = Path.GetFileName(gz) }, null, CancellationToken.None);
                        var rx = ClassicIndex.Load(ri);
                        notes.Add("info real dump: " + rr.ToText().Replace("\r\n", " | ").Replace("\n", " | "));
                        check("real dump: sizes in the expected range (npcs, items, objects, quests, spawns, items with a source) and imported in under two minutes", rx != null && rr.Npcs > 9000 && rr.Items > 15000 && rr.Objects > 8000 && rr.Quests > 3500 && rr.NpcSpawns > 50000 && rr.ObjectSpawns > 30000 && rr.ItemsWithSource > 9000 && rr.Seconds < 120);
                        var hogger = rx?.NpcInfo(448);
                        var claw = rx?.SourcesOf(1931).FirstOrDefault(s => s.Kind == ClassicSourceKind.NpcDrop && s.SourceId == 448) ?? default(ClassicItemSource);
                        check("real dump: Hogger exists with the quest-only 100 % claw, spawns on Eastern Kingdoms, and every source is tagged", hogger != null && hogger.Name == "Hogger" && AcNear(claw.Chance, 100) && claw.QuestOnly && hogger.Spawns.Length > 0 && hogger.Spawns.All(s => s.Map == 0) && claw.Source == "Classic baseline");
                    }
                }
                else notes.Add("skip real-dump check (set ELANSHUB_CLASSICDB to a folder with the dump .sql.gz and an updates folder)");
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                ClassicDownloader.ApiBase = savedApi; ClassicDownloader.RawBase = savedRaw;
                gh?.Stop();
                ClassicAtlas.Use(null);
            }
            return "atlasclassic selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes);
        }

        static double Frac(ClassicSpawn s) { s.ToFraction(200, 0, 100, 0, out var fx, out var fy); return fx + fy; }
    }
}
