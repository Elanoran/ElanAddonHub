using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "seendata": Atlas layer A3, what the player's client has seen (creaturecache.wdb / gameobjectcache.wdb / questcache.wdb).
    // Everything runs on WDB files built in the test (a small writer lives here) under the scratch folder. The real game cache is only touched by the last,
    // optional check (read-only, skipped when there is no WoW install).
    public partial class MainWindow
    {
        // ---- a tiny WDB writer (the container and record layouts the reader is written against)
        sealed class SdRec { public int Id; public byte[] Data; }

        static byte[] SdFile(string magic, int build, string locale, int recordVersion, IEnumerable<SdRec> recs, bool endMarker = true)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(magic.Reverse().Select(c => (byte)c).ToArray());
            w.Write(build);
            w.Write(locale.Reverse().Select(c => (byte)c).ToArray());
            w.Write(1234); w.Write(recordVersion); w.Write(0);
            foreach (var r in recs) { w.Write(r.Id); w.Write(r.Data.Length); w.Write(r.Data); }
            if (endMarker) { w.Write(0); w.Write(0); }
            return ms.ToArray();
        }

        // bits, most significant first
        sealed class SdBits
        {
            public List<byte> Bytes = new List<byte>(); int pos;
            public void Put(int value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    if ((pos & 7) == 0) Bytes.Add(0);
                    if (((value >> i) & 1) != 0) Bytes[Bytes.Count - 1] |= (byte)(0x80 >> (pos & 7));
                    pos++;
                }
            }
        }

        static byte[] SdCStr(string s) => Encoding.UTF8.GetBytes(s).Concat(new byte[] { 0 }).ToArray();

        static SdRec SdCreature(int id, string name, string title = "", int type = 7, int family = 0, int rank = 0, int[] displays = null, params int[] questItems)
        {
            displays = displays ?? new[] { 1000 + id };
            var bits = new SdBits();
            int tl = title.Length == 0 ? 0 : Encoding.UTF8.GetByteCount(title) + 1;
            bits.Put(tl, 11); bits.Put(0, 11); bits.Put(0, 6); bits.Put(0, 1);
            for (int i = 0; i < 4; i++) { bits.Put(i == 0 ? Encoding.UTF8.GetByteCount(name) + 1 : 0, 11); bits.Put(0, 11); }
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            var head = bits.Bytes.ToList(); while (head.Count < 15) head.Add(0);
            w.Write(head.ToArray());
            w.Write(SdCStr(name));
            w.Write(0x08000001u); w.Write(0u); w.Write(0u);          // flags
            w.Write((byte)type); w.Write((byte)family); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)rank); w.Write(new byte[8]);   // type, family, ..., rank at +17, proxy ids
            w.Write(displays.Length); w.Write(100f);
            foreach (var dsp in displays) { w.Write(dsp); w.Write(1f); w.Write(100f / displays.Length); }
            w.Write(1f); w.Write(1f);                                // health, energy multiplier
            w.Write(questItems.Length);
            w.Write(new byte[36]);
            if (tl > 0) w.Write(SdCStr(title));
            foreach (var q in questItems) w.Write(q);
            return new SdRec { Id = id, Data = ms.ToArray() };
        }

        static SdRec SdObject(int id, string name, int type = 3, int display = 500, int[] data = null, params int[] questItems)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(type); w.Write(display);
            w.Write(SdCStr(name)); w.Write(new byte[6]);             // names 2-4, icon, cast bar, unknown (all empty)
            for (int i = 0; i < WdbObject.Count; i++) w.Write(data != null && i < data.Length ? data[i] : 0);
            w.Write(1f);
            w.Write((byte)questItems.Length);
            foreach (var q in questItems) w.Write(q);
            w.Write(new byte[8]);
            return new SdRec { Id = id, Data = ms.ToArray() };
        }

        static SdRec SdQuest(int id, string title, string objectives, string description, int level = 20, int minLevel = 10, int sort = 12, int type = 2, int extraNumbers = 0)
        {
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(id); w.Write(type); w.Write(level); w.Write(0); w.Write(minLevel); w.Write(0); w.Write(sort);
            w.Write(new byte[4 * 4 + 43 * extraNumbers]);            // rewards, objectives ...: as long as the quest needs
            var t = Encoding.UTF8.GetBytes(title); var o = Encoding.UTF8.GetBytes(objectives); var d = Encoding.UTF8.GetBytes(description);
            var bits = new SdBits();
            bits.Put(t.Length, 9); bits.Put(o.Length, 12); bits.Put(d.Length, 12); bits.Put(0, 9); bits.Put(0, 10); bits.Put(0, 8); bits.Put(0, 10); bits.Put(0, 8); bits.Put(0, 11); bits.Put(0, 1);
            var head = bits.Bytes.ToList(); while (head.Count < 12) head.Add(0);
            w.Write(head.ToArray()); w.Write(t); w.Write(o); w.Write(d);
            return new SdRec { Id = id, Data = ms.ToArray() };
        }

        static void SdWriteSet(string dir, SdRec[] npcs, SdRec[] objs, SdRec[] quests)
        {
            Directory.CreateDirectory(dir);
            if (npcs != null) File.WriteAllBytes(Path.Combine(dir, "creaturecache.wdb"), SdFile("WMOB", 70338, "enUS", 4, npcs));
            if (objs != null) File.WriteAllBytes(Path.Combine(dir, "gameobjectcache.wdb"), SdFile("WGOB", 70338, "enUS", 4, objs));
            if (quests != null) File.WriteAllBytes(Path.Combine(dir, "questcache.wdb"), SdFile("WQST", 70338, "enUS", 12, quests));
        }

        async Task<string> SeenDataTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "seendata");
            var savedCacheFile = SeenData.CacheFile; var savedRoot = SeenData.WowRoot; var savedEnabled = SeenData.Enabled; var savedDebounce = SeenData.DebounceMs; var savedFlavor = SeenData.Flavor;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                Check("fixture: everything of this test is under the scratch folder (never the real game)", Path.GetFullPath(work).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                SeenData.ResetForTest();
                SeenData.CacheFile = Path.Combine(work, "cache", "seendata.bin");

                // ---- the container
                var good = SdFile("WMOB", 70338, "enUS", 4, new[] { SdCreature(10, "Alpha"), SdCreature(11, "Bravo"), SdCreature(10, "Alpha Two") });
                var f = Wdb.Parse(good, Wdb.CreatureMagic);
                Check("container: magic, build, locale and versions read from the header", f.Magic == "WMOB" && f.Build == 70338 && f.Locale == "enUS" && f.RecordVersion == 4 && f.CacheVersion == 0, f.Magic + " " + f.Build + " " + f.Locale);
                Check("container: 3 records of which one repeats an id = 2 entries, the last one wins, not truncated", f.Entries.Count == 2 && f.Duplicates == 1 && !f.Truncated && WdbSeen.Creatures(good).Items.First(c => c.Id == 10).Name == "Alpha Two");
                var cut = good.Take(good.Length - 8 - 30).ToArray();
                var fc = Wdb.Parse(cut, null);
                Check("container: a file cut off inside a record keeps the complete records and says so", fc.Truncated && fc.Entries.Count >= 1 && fc.Entries.Count < 3, fc.Entries.Count + "");
                var noEnd = SdFile("WMOB", 70338, "enUS", 4, new[] { SdCreature(10, "Alpha") }, false);
                Check("container: no end marker = truncated flag, the record is kept", Wdb.Parse(noEnd, null).Truncated && WdbSeen.Creatures(noEnd).Items.Count == 1);
                Check("container: a header only is an empty list", Wdb.Parse(SdFile("WMOB", 1, "enUS", 4, new SdRec[0]), null).Entries.Count == 0);
                bool threw1 = false, threw2 = false, threw3 = false;
                try { Wdb.Parse(new byte[10], null); } catch (Db2Exception) { threw1 = true; }
                try { Wdb.Parse(SdFile("WGOB", 1, "enUS", 4, new SdRec[0]), Wdb.CreatureMagic); } catch (Db2Exception) { threw2 = true; }
                try { WdbSeen.Quests(null); } catch (Db2Exception) { threw3 = true; }
                Check("container: too short, the wrong kind of file, no data = an error, not a crash", threw1 && threw2 && threw3);
                var rnd = new Random(7); var garbage = new byte[5000]; rnd.NextBytes(garbage);
                bool garbageOk = true;
                try { Wdb.Parse(garbage, null); } catch (Db2Exception) { }
                catch (Exception) { garbageOk = false; }
                var garbageBody = good.Take(24).Concat(garbage).ToArray();
                try { var r = WdbSeen.Creatures(garbageBody); garbageOk &= r.Items.Count == 0; } catch (Exception) { garbageOk = false; }
                var hugeLen = good.Take(24).Concat(BitConverter.GetBytes(5)).Concat(BitConverter.GetBytes(0x7FFFFFF0)).Concat(new byte[40]).ToArray();
                try { garbageOk &= Wdb.Parse(hugeLen, null).Entries.Count == 0; } catch (Exception) { garbageOk = false; }
                Check("garbage: random bytes, a random body behind a good header and an absurd record length never throw anything but a Db2Exception and yield nothing", garbageOk);

                // ---- creatures
                var cr = WdbSeen.Creatures(SdFile("WMOB", 70338, "enUS", 4, new[]
                {
                    SdCreature(3566, "Flatland Prowler", "", 1, 2, 0, new[] { 4562 }, 5203),
                    SdCreature(3671, "Lady Anacondra", "Fanglord", 7, 0, 1, new[] { 100, 200 }, 9739, 9740),
                    SdCreature(3057, "Cairne Bloodhoof", "Chief of the Bloodhoof", 7, 0, 3),
                    SdCreature(300, "Rare One", "", 1, 3, 4),
                    SdCreature(301, "Ünïcode Wolf")
                })).Items.ToDictionary(c => c.Id);
                Check("creature: name, type, family, rank, display id, quest items", cr[3566].Name == "Flatland Prowler" && cr[3566].Type == 1 && cr[3566].Family == 2 && cr[3566].Rank == 0 && cr[3566].DisplayId == 4562 && cr[3566].QuestItems.SequenceEqual(new[] { 5203 }));
                Check("creature: title (subname) and two quest items, two displays", cr[3671].Title == "Fanglord" && cr[3671].Rank == 1 && cr[3671].QuestItems.SequenceEqual(new[] { 9739, 9740 }) && cr[3671].DisplayId == 100);
                Check("creature: boss and rare rank, no quest items", cr[3057].Rank == 3 && cr[3057].Title == "Chief of the Bloodhoof" && cr[3057].QuestItems.Length == 0 && cr[300].Rank == 4 && cr[300].Family == 3);
                Check("creature: UTF-8 names", cr[301].Name == "Ünïcode Wolf");
                var c1 = SdCreature(1, "Bad Length");
                var bad = WdbSeen.Creatures(SdFile("WMOB", 70338, "enUS", 4, new[] { SdCreature(2, "Fine"), new SdRec { Id = 1, Data = c1.Data.Concat(new byte[] { 9 }).ToArray() }, new SdRec { Id = 3, Data = c1.Data.Take(40).ToArray() }, new SdRec { Id = 4, Data = new byte[5] }, SdCreature(5, "") }));
                Check("creature: a record that does not fit its computed length (extra byte, cut short, tiny, no name) is skipped, the good one stays", bad.Items.Count == 1 && bad.Items[0].Name == "Fine" && bad.Skipped == 4 && bad.Recognised == false, bad.Items.Count + "/" + bad.Skipped);

                // ---- game objects
                var ob = WdbSeen.Objects(SdFile("WGOB", 70338, "enUS", 4, new[]
                {
                    SdObject(19861, "Henrig Lonebrow's Journal", 3, 447, new[] { 43, 3214, 0, 1 }),
                    SdObject(111221, "Wooden Chair", 7, 39, new[] { 1, 1 }),
                    SdObject(667676, "Pristine Mattock", 3, 4401, new[] { 43 }, 278404, 278405)
                })).Items.ToDictionary(o => o.Id);
                Check("object: name, type, display id, data fields, chest loot id", ob[19861].Name == "Henrig Lonebrow's Journal" && ob[19861].Type == 3 && ob[19861].DisplayId == 447 && ob[19861].Data[0] == 43 && ob[19861].ChestLootId == 3214 && ob[111221].ChestLootId == 0 && ob[111221].Data[1] == 1);
                Check("object: quest items", ob[667676].QuestItems.SequenceEqual(new[] { 278404, 278405 }) && ob[19861].QuestItems.Length == 0);
                var ob2 = WdbSeen.Objects(SdFile("WGOB", 70338, "enUS", 4, new[] { SdObject(1, "Ok"), new SdRec { Id = 2, Data = SdObject(2, "Long").Data.Concat(new byte[3]).ToArray() }, new SdRec { Id = 3, Data = new byte[20] } }));
                Check("object: a record of the wrong length or without strings is skipped", ob2.Items.Count == 1 && ob2.Skipped == 2);

                // ---- quests
                var qs = WdbSeen.Quests(SdFile("WQST", 70338, "enUS", 12, new[]
                {
                    SdQuest(94222, "Craftsman's Writ: Iridescent Hammer", "Acquire the items requested by the customer.", "<Penned in faded ink>\r\nSecond line", 60, 1, 16941),
                    SdQuest(1136, "Frostmaw", "Bring Frostmaw's Mane to Melor Stonehoof in Thunder Bluff.", "$n, it is now time.", 37, 26, 1638, 2, 1),
                    SdQuest(5728, "Hidden Enemies", "Kill Bazzalan and Jergosh the Invoker before returning to Thrall.", "Hmm, leaders of the Searing Blade...", 16, 9, 2437, 2, 2),
                    SdQuest(77, "Négative sort", "Obj", "Desc", 5, 1, -81, 2, 5)
                })).Items.ToDictionary(q => q.Id);
                Check("quest: title, objectives text, level, minimum level, zone, whatever the number of rewards / objectives in front of the texts",
                    qs[94222].Title == "Craftsman's Writ: Iridescent Hammer" && qs[94222].Objectives == "Acquire the items requested by the customer." && qs[94222].Level == 60 && qs[94222].MinLevel == 1 && qs[94222].Sort == 16941
                    && qs[1136].Title == "Frostmaw" && qs[1136].Level == 37 && qs[1136].MinLevel == 26 && qs[1136].Sort == 1638
                    && qs[5728].Title == "Hidden Enemies" && qs[5728].Objectives.StartsWith("Kill Bazzalan") && qs[5728].Sort == 2437 && qs[5728].Level == 16, string.Join("|", qs.Values.Select(q => q.Title)));
                Check("quest: description kept as the game wrote it (line breaks, $n); a negative sort and UTF-8 title", qs[94222].Description == "<Penned in faded ink>\r\nSecond line" && qs[1136].Description == "$n, it is now time." && qs[77].Sort == -81 && qs[77].Title == "Négative sort");
                var qbad = WdbSeen.Quests(SdFile("WQST", 70338, "enUS", 12, new[] { SdQuest(1, "Ok", "o", "d"), new SdRec { Id = 2, Data = SdQuest(2, "Long", "o", "d").Data.Concat(new byte[] { 1 }).ToArray() }, new SdRec { Id = 4, Data = SdQuest(3, "Wrong id", "o", "d").Data }, new SdRec { Id = 5, Data = new byte[30] } }));
                Check("quest: an extra byte, an id that disagrees with the record's own, a tiny record = skipped", qbad.Items.Count == 1 && qbad.Skipped == 3);

                // ---- files, saved index, refresh
                var wow = Path.Combine(work, "wow");
                var wdb = Path.Combine(wow, "_classic_beta_", "Cache", "WDB", "enUS");
                var npcs1 = new[] { SdCreature(68, "Stormwind City Guard", "", 7, 0, 1), SdCreature(525, "Mangy Wolf", "", 1, 1), SdCreature(269139, "Grey Riding Kodo", "", 1, 0) };
                var objs1 = new[] { SdObject(2000, "Copper Vein", 3, 11, new[] { 1, 99 }), SdObject(2001, "Tin Vein", 3, 12) };
                var qsts1 = new[] { SdQuest(100, "Wolves of the Vale", "Kill 10 wolves.", "Please.", 8, 3, 12), SdQuest(101, "The Wolf's Den", "Find the den.", "Go.", 9, 3, 12) };
                SdWriteSet(wdb, npcs1, objs1, qsts1);
                var idx = Path.Combine(work, "idx", "seen.bin");
                var set = SeenData.Obtain(null, wdb, idx, out var from1);
                Check("read: 3 NPCs, 2 objects, 2 quests from the files; the build of the header; index saved", set.Ok && set.Npcs.Count == 3 && set.Objects.Count == 2 && set.Quests.Count == 2 && set.Build == 70338 && from1 == "game files" && File.Exists(idx), set.Problem);
                int len0 = (int)new FileInfo(idx).Length;
                var saved = SeenIndexFile.TryRead(idx);
                Check("saved index: reads back every field", saved != null && saved.Set.Npcs.Count == 3 && saved.Set.Objects.First(o => o.Id == 2000).Data[1] == 99 && saved.Set.Quests.First(q => q.Id == 100).Objectives == "Kill 10 wolves." && saved.Set.Npcs.First(n => n.Id == 68).Rank == 1 && saved.Set.Build == 70338);
                var set2 = SeenData.Obtain(saved, wdb, idx, out var from2);
                Check("unchanged files: the saved index is used, the files are not read", from2 == "cache file" && set2.Npcs.Count == 3);
                SdWriteSet(wdb, null, null, qsts1.Concat(new[] { SdQuest(102, "A New One", "o", "d", 12, 5, 12) }).ToArray());
                var set3 = SeenData.Obtain(saved, wdb, idx, out var from3);
                Check("a changed file (new size / time): the files are read again, the new quest is there, the index is saved again", from3 == "game files" && set3.Quests.Count == 3 && SeenIndexFile.TryRead(idx).Set.Quests.Count == 3);
                File.WriteAllBytes(idx, new byte[] { 1, 2, 3, 4, 5 });
                Check("a damaged index file reads as nothing, the files are read again and it is replaced", SeenIndexFile.TryRead(idx) == null && SeenData.Obtain(SeenIndexFile.TryRead(idx), wdb, idx, out var from4).Ok && from4 == "game files" && SeenIndexFile.TryRead(idx) != null);

                var lone = Path.Combine(work, "lone", "enUS");
                SdWriteSet(lone, npcs1, null, null);
                var setL = SeenData.ReadFiles(lone);
                Check("only one of the three files present: that one is used, the others are empty", setL.Ok && setL.Npcs.Count == 3 && setL.Objects.Count == 0 && setL.Quests.Count == 0);
                var empty = Path.Combine(work, "none", "enUS"); Directory.CreateDirectory(empty);
                Check("none of the files present: a problem that says the game has not written its cache", SeenData.ReadFiles(empty).Problem == "the game has not written its cache yet");
                var mixed = Path.Combine(work, "mixed", "enUS");
                SdWriteSet(mixed, npcs1, null, qsts1);
                File.WriteAllBytes(Path.Combine(mixed, "gameobjectcache.wdb"), SdFile("WGOB", 99999, "enUS", 9, Enumerable.Range(1, 6).Select(i => new SdRec { Id = i, Data = new byte[50 + i] })));
                var setM = SeenData.ReadFiles(mixed);
                Check("one file in a layout this version does not know: left out with a note, the other two are used", setM.Ok && setM.Npcs.Count == 3 && setM.Quests.Count == 2 && setM.Objects.Count == 0 && setM.Notes.Any(n => n.StartsWith("gameobjectcache.wdb: layout not recognised") && n.Contains("99999")), string.Join(";", setM.Notes));
                var allBad = Path.Combine(work, "allbad", "enUS"); Directory.CreateDirectory(allBad);
                File.WriteAllBytes(Path.Combine(allBad, "creaturecache.wdb"), garbage);
                var setB = SeenData.ReadFiles(allBad);
                Check("only damaged files: unavailable, with the reason; nothing is saved", !setB.Ok && setB.Problem != null && setB.Npcs.Count == 0);
                Check("the WDB folder: enUS preferred, otherwise the locale folder with the newest creature cache; none = null",
                    SeenData.WdbDir(wow, "_classic_beta_") == wdb && SeenData.WdbDir(Path.Combine(work, "nowhere"), "_classic_beta_") == null);
                var other = Path.Combine(work, "wow2", "_classic_beta_", "Cache", "WDB");
                SdWriteSet(Path.Combine(other, "deDE"), npcs1, null, null);
                Check("only a deDE folder: that one is used", SeenData.WdbDir(Path.Combine(work, "wow2"), "_classic_beta_") == Path.Combine(other, "deDE"));

                // ---- the snapshot and search
                var snap = new SeenSnapshot(set3.Npcs, set3.Objects, set3.Quests);
                Check("snapshot counts", snap.NpcCount == 3 && snap.ObjectCount == 2 && snap.QuestCount == 3);
                var rk = new SeenSnapshot(new[] { new WdbCreature { Id = 1, Name = "Timberwolf" }, new WdbCreature { Id = 2, Name = "Dire Wolf" }, new WdbCreature { Id = 3, Name = "Wolfgang" }, new WdbCreature { Id = 4, Name = "Cat" } }, null, null);
                Check("search: names that start with the query first, then a word that starts with it, then a match inside a word", string.Join("|", rk.Search("wolf", 10).Select(x => x.Name)) == "Wolfgang|Dire Wolf|Timberwolf", string.Join("|", rk.Search("wolf", 10).Select(x => x.Name)));
                var s2 = snap.Search("city guard", 10);
                Check("search: every word must occur, in any order of the name; kind, id and detail are filled in", s2.Count == 1 && s2[0].Kind == "npc" && s2[0].Id == 68 && s2[0].AtlasId == "npc:68" && s2[0].Detail == "elite" && s2[0].Source == "Forever", string.Join("|", s2.Select(x => x.AtlasId)));
                Check("search: by id with a prefix ('npc:525', 'obj:2000', 'quest:101'), a bare number finds all kinds with that id", snap.Search("npc:525", 5).Single().Name == "Mangy Wolf" && snap.Search("obj:2000", 5).Single().Name == "Copper Vein" && snap.Search("quest:101", 5).Single().Kind == "quest" && snap.Search("npc:2000", 5).Count == 0 && snap.Search("68", 5).Single().Id == 68);
                Check("search: limited to a kind, case-insensitive, empty query and max 0 find nothing", snap.Search("VEIN", 5, "obj").Count == 2 && snap.Search("vein", 5, "npc").Count == 0 && snap.Search("", 5).Count == 0 && snap.Search("vein", 0).Count == 0 && snap.Search("zzzz", 5).Count == 0);
                Check("search: max is respected", snap.Search("a", 2).Count == 2);
                Check("quest hits say the level; npc hits with a title say it", SeenSnapshot.Hit(set3.Quests.First(q => q.Id == 100)).Detail == "level 8" && SeenSnapshot.Hit(new WdbCreature { Id = 1, Name = "X", Title = "Weaponsmith", Rank = 4 }).Detail == "<Weaponsmith>, rare");

                // ---- the service: background read, status line, switch, refresh
                SeenData.Flavor = () => "_classic_beta_";
                SeenData.WowRoot = () => null; SeenData.Enabled = () => true;
                Check("status line: not read yet", SeenData.StatusLine == "Seen data: not read yet" && SeenData.Npc(68) == null && SeenData.Search("wolf", 3).Count == 0 && !SeenData.Ready);
                SeenData.Start();
                await WaitSeen(() => SeenData.Status == SeenData.State.Unavailable);
                Check("no WoW folder: unavailable, says so", SeenData.StatusLine == "Seen data: unavailable (the WoW folder is not set)", SeenData.StatusLine);
                SeenData.WowRoot = () => Path.Combine(work, "wow2empty");
                Directory.CreateDirectory(Path.Combine(work, "wow2empty"));
                SeenData.Start();
                await WaitSeen(() => SeenData.Reason != null && SeenData.Reason.Contains("cache"));
                Check("a WoW folder without a WDB cache: unavailable (the game has not written its cache yet)", SeenData.Status == SeenData.State.Unavailable && SeenData.StatusLine.Contains("has not written its cache"), SeenData.StatusLine);
                SeenData.WowRoot = () => wow;
                SeenData.DebounceMs = 250;
                SeenData.CacheFile = Path.Combine(work, "cache", "svc.bin");
                SdWriteSet(wdb, npcs1, objs1, qsts1);
                SeenData.Start();
                await WaitSeen(() => SeenData.Ready);
                Check("started: ready, 3 NPCs 2 objects 2 quests; status line; the API", SeenData.Ready && SeenData.StatusLine == "Seen data: 3 NPCs, 2 objects, 2 quests seen" && SeenData.Npc(525).Name == "Mangy Wolf" && SeenData.Object(2001).Name == "Tin Vein" && SeenData.Quest(100).Title == "Wolves of the Vale" && SeenData.Npc(99999) == null && SeenData.Quest(0) == null && SeenData.Search("tin", 5).Count == 1, SeenData.StatusLine);
                var src = AtlasObs.SeenSource;
                AtlasObs.SeenSource = SeenData.AsSource;
                Check("the Lodge hook (AtlasObs.SeenSource) lists what was seen", AtlasObs.SeenSource.Npcs().Count() == 3 && AtlasObs.SeenSource.Objects().Count() == 2 && AtlasObs.SeenSource.Quests().Count() == 2);
                AtlasObs.SeenSource = src;
                int before = SeenData.StartCountForTest;
                SdWriteSet(wdb, npcs1.Concat(new[] { SdCreature(300001, "Forever Newcomer", "the Seen") }).ToArray(), null, null);
                File.AppendAllText(Path.Combine(wdb, "pagetextcache.wdb"), "x");     // other files in the folder do not matter
                await WaitSeen(() => SeenData.NpcCount == 4);
                Check("the game rewrites the creature cache: one refresh after the quiet moment picks up the new NPC", SeenData.NpcCount == 4 && SeenData.Npc(300001).Title == "the Seen" && SeenData.StartCountForTest - before >= 1 && SeenData.StartCountForTest - before <= 2, SeenData.NpcCount + " / " + (SeenData.StartCountForTest - before));
                int mid = SeenData.StartCountForTest;
                File.AppendAllText(Path.Combine(wdb, "pagetextcache.wdb"), "y");
                await Task.Delay(700);
                Check("a change to another file in the folder is ignored", SeenData.StartCountForTest == mid);
                SeenData.Enabled = () => false;
                SeenData.Start();
                Check("switched off: Start() does not read, says 'off', drops the index, stops watching", SeenData.Status == SeenData.State.Disabled && SeenData.StatusLine == "Seen data: off" && SeenData.Snapshot == null && SeenData.Npc(68) == null);
                int off = SeenData.StartCountForTest;
                SdWriteSet(wdb, npcs1, null, null);
                await Task.Delay(700);
                Check("after it is off a rewrite of the cache is not looked at", SeenData.StartCountForTest == off && SeenData.Snapshot == null);
                SeenData.Enabled = () => true;
                SeenData.ResetForTest();
                SeenData.SetForTest(null, SeenData.State.Loading);
                Check("status line: reading", SeenData.StatusLine == "Seen data: reading your WoW cache...");
                SeenData.ResetForTest();

                // ---- optional: the real game cache, read-only
                SeenSmokeReal(Check, notes);
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                SeenData.StopWatching();
                SeenData.ResetForTest();
                SeenData.CacheFile = savedCacheFile; SeenData.WowRoot = savedRoot; SeenData.Enabled = savedEnabled; SeenData.DebounceMs = savedDebounce; SeenData.Flavor = savedFlavor;
            }
            notes.Insert(0, "seendata (Atlas A3): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }

        static async Task WaitSeen(Func<bool> done)
        {
            for (int i = 0; i < 100 && !done(); i++) await Task.Delay(50);
        }

        void SeenSmokeReal(CheckFn Check, List<string> notes)
        {
            var real = Environment.GetEnvironmentVariable("ELANSHUB_WOW");
            if (string.IsNullOrEmpty(real)) real = WowLocator.Find();
            var dir = string.IsNullOrEmpty(real) ? null : SeenData.WdbDir(real, "_classic_beta_");
            if (dir == null || !File.Exists(Path.Combine(dir, "creaturecache.wdb")))
            {
                notes.Add("skip real game cache: no WoW install with a Forever beta client cache on this PC");
                return;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var set = SeenData.ReadFiles(dir);       // opens the game's files shared, read-only; nothing is written there
                Check("real game cache: the three files are recognised and read", set.Ok && set.Notes.All(n => !n.Contains("not recognised")), string.Join(";", set.Notes) + set.Problem);
                if (!set.Ok) return;
                var snap = new SeenSnapshot(set.Npcs, set.Objects, set.Quests);
                Check("real game cache: NPCs, objects and quests were read (counts above zero)", snap.NpcCount > 0 && snap.ObjectCount > 0 && snap.QuestCount > 0, snap.NpcCount + "/" + snap.ObjectCount + "/" + snap.QuestCount);
                var guard = snap.Search("guard", 50, "npc");
                Check("real game cache: a city guard is among the NPCs seen (if this player has met one)", guard.Count > 0 || snap.NpcCount < 50, guard.Count + "");
                // well-known Classic ids, checked only when this player's client has met them
                if (snap.Npcs.TryGetValue(68, out var swg)) Check("real game cache: npc 68 is Stormwind City Guard, a humanoid", swg.Name == "Stormwind City Guard" && swg.Type == 7, swg.Name + " " + swg.Type);
                if (snap.Npcs.TryGetValue(525, out var wolf)) Check("real game cache: npc 525 is Mangy Wolf, a beast of the wolf family", wolf.Name == "Mangy Wolf" && wolf.Type == 1 && wolf.Family == 1 && wolf.Rank == 0, wolf.Name + " " + wolf.Type + "/" + wolf.Family);
                if (snap.Npcs.TryGetValue(3057, out var cairne)) Check("real game cache: npc 3057 is Cairne Bloodhoof, a boss", cairne.Name == "Cairne Bloodhoof" && cairne.Rank == 3, cairne.Name + " " + cairne.Rank);
                if (snap.Quests.TryGetValue(5728, out var he)) Check("real game cache: quest 5728 is Hidden Enemies, level 16", he.Title == "Hidden Enemies" && he.Level == 16 && he.Objectives.StartsWith("Kill Bazzalan"), he.Title + " " + he.Level);
                Check("real game cache: every quest has a title and every NPC a name", snap.Quests.Values.All(q => q.Title.Length > 0) && snap.Npcs.Values.All(n => n.Name.Length > 0));
                notes.Add("note real game cache: " + snap.NpcCount + " NPCs, " + snap.ObjectCount + " objects, " + snap.QuestCount + " quests, build " + set.Build + ", read in " + sw.ElapsedMilliseconds + " ms from " + dir + (set.Notes.Count > 0 ? "; " + string.Join("; ", set.Notes) : ""));
            }
            catch (Exception e) { notes.Add("skip real game cache: " + e.Message); }
        }
    }
}
