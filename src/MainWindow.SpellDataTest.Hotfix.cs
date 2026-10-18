using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub
{
    // --selftest "spelldata", the server's hotfixes beyond items: synthetic DBCache.bin rows for every table ServerHotfixes reads (SpellName, Spell, SpellMisc,
    // SpellEffect, QuestV2, Lock, FactionTemplate) laid over the synthetic shipped tables of SpellDataTest; the rows are written in the layouts of build
    // 1.60.1.70374 exactly as the client stores them (natural widths, the relationship column last).
    public partial class MainWindow
    {
        sealed class HfRow
        {
            readonly MemoryStream ms = new MemoryStream(); readonly BinaryWriter w;
            public HfRow() { w = new BinaryWriter(ms); }
            public HfRow I32(params int[] v) { foreach (var x in v) w.Write(x); return this; }
            public HfRow I16(params int[] v) { foreach (var x in v) w.Write((short)x); return this; }
            public HfRow U8(params int[] v) { foreach (var x in v) w.Write((byte)x); return this; }
            public HfRow F32(params float[] v) { foreach (var x in v) w.Write(x); return this; }
            public HfRow Str(params string[] v) { foreach (var s in v) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); } return this; }
            public byte[] Bytes { get { w.Flush(); return ms.ToArray(); } }
        }

        // SpellMisc 434B3607: Attributes[17], DifficultyID i16, CastingTimeIndex, DurationIndex, PvPDurationIndex, RangeIndex u16, SchoolMask u8, 3 floats, 6 ints, SpellID
        static byte[] HfMisc(int spell, int diff, int cast, int dur, int range, int icon) =>
            new HfRow().I32(new int[17]).I16(diff, cast, dur, 0, range).U8(1).F32(0, 0, 0).I32(icon, 0, 0, 0, 0, 0).I32(spell).Bytes;

        // SpellEffect 5362E3D4 (136 bytes)
        static byte[] HfEffect(int spell, int diff, int index, int effect, float points, int aura = 0, int period = 0, int item = 0, int radiusIdx = 0)
        {
            var r = new HfRow().I16(aura, diff).I32(index, effect).F32(0).I32(0, period).F32(0, 1).I32(0, item, 0).F32(0, 0, 0).I32(0).F32(0, 1, 0, 0, 0, 1, points)
                .I32(0, 0).I32(0, 0).I32(radiusIdx, 0).I32(0, 0, 0, 0).I16(0, 0).I32(spell);
            return r.Bytes;
        }

        static byte[] HfLock(int[] index, int[] skill, int[] type)
        {
            var r = new HfRow().I32(0);
            for (int e = 0; e < 8; e++) r.I32(e < index.Length ? index[e] : 0);
            for (int e = 0; e < 8; e++) r.I16(e < skill.Length ? skill[e] : 0);
            for (int e = 0; e < 8; e++) r.U8(e < type.Length ? type[e] : 0);
            return r.U8(new byte[8].Select(b => (int)b).ToArray()).Bytes;
        }

        static byte[] HfTemplate(int faction, int group, int friendGroup, int enemyGroup)
            => new HfRow().I16(faction).I32(0).U8(group, friendGroup, enemyGroup).I16(new int[16]).Bytes;

        static DbEntry Hf(SpellTables.Def t, int id, byte[] data, byte status = 1) => new DbEntry { Table = t.TableHash, Id = id, Status = status, Data = data ?? new byte[0] };

        static List<DbEntry> HfEntries()
        {
            var e = new List<DbEntry>
            {
                Hf(SpellTables.SpellName, 133, new HfRow().Str("Fireball").Bytes),                       // the same name: not a change
                Hf(SpellTables.SpellName, 424242, new HfRow().Str("Teleport to Elwynn").Bytes),      // a new spell
                Hf(SpellTables.SpellName, 424243, new HfRow().Str("Gone Again").Bytes),
                Hf(SpellTables.SpellName, 424243, null, 3),                                              // invalid after a row: that row no longer counts
                Hf(SpellTables.SpellName, 9000, null, 3),                                                // invalid for a shipped id: the shipped row stands
                Hf(SpellTables.Spell, 424242, new HfRow().Str("", "Teleports you $s1 times.", "").Bytes),
                Hf(SpellTables.Spell, 772, null, 2),                                                      // Rend's texts are removed
                Hf(SpellTables.SpellMisc, 50, HfMisc(424242, 0, 1, 4, 3, 136235)),
                Hf(SpellTables.SpellMisc, 1, HfMisc(133, 0, 1, 4, 3, 135812)),                           // Fireball's own row: the duration index 2 -> 4 (4 s -> 9 s)
                Hf(SpellTables.SpellMisc, 2, null, 2),                                                    // Rend's row removed
                Hf(SpellTables.SpellMisc, 51, HfMisc(133, 5, 9, 9, 9, 999999)),                          // another difficulty: not used
                Hf(SpellTables.SpellEffect, 60, HfEffect(424242, 0, 0, 252, 3f)),
                Hf(SpellTables.SpellEffect, 1, HfEffect(133, 0, 0, 2, 25f)),                             // Fireball's direct damage 18 -> 25
                Hf(SpellTables.SpellEffect, 2, null, 2),                                                  // Fireball's periodic effect removed
                Hf(SpellTables.SpellEffect, 61, HfEffect(133, 6, 0, 2, 777f)),                           // another difficulty: not used
                Hf(SpellTables.SpellEffect, 62, HfEffect(8000, 0, 0, 10, 1f).Take(135).ToArray()),       // a byte short of its layout: skipped, not half-read
                Hf(WorldTables.QuestV2, 93000, new HfRow().I32(93000, 66000, 0).Bytes),
                Hf(WorldTables.QuestV2, 25, new HfRow().I32(25, 0, 0).Bytes),                            // a shipped quest again: not new
                Hf(WorldTables.QuestV2, 498, null, 2),
                Hf(WorldTables.QuestV2, 93001, new HfRow().I32(1, 0, 0).Bytes),                          // the inline id is not the entry's id: skipped
                Hf(WorldTables.Lock, 31, HfLock(new[] { 2 }, new[] { 85 }, new[] { 2 })),                // Briarthorn: Herbalism 70 -> 85
                Hf(WorldTables.Lock, 4588, HfLock(new[] { 0, 5 }, new[] { 0, 0 }, new[] { 0, 2 })),      // a new lock, "Open" without a level
                Hf(WorldTables.Lock, 39, null, 2),
                Hf(SpellTables.FactionTemplate, 100, HfTemplate(99, 8, 0, 0)),                           // the monsters are no longer anyone's enemy
                Hf(SpellTables.FactionTemplate, 3561, HfTemplate(2741, 8, 0, 1)),
                new DbEntry { Table = 0x021826BB, Id = 3425, Data = new HfRow().Str("What are you looking for?", "").I32(new int[9]).Bytes },   // BroadcastText: not used, noted
                new DbEntry { Table = 0xC9D6B6B3, Id = 1, Status = 3, Data = new byte[0] },             // Creature, no data: nothing to note
            };
            return e;
        }

        void SdHotfixChecks(string work, AtlasSet set, CheckFn Check)
        {
            const string Build = "wow_classic_beta|1.60.1.70374|abc";
            Check("hotfix: the game build number comes from the version (70374); none = 0", ServerHotfixes.BuildNumber(Build) == 70374 && ServerHotfixes.BuildNumber(null) == 0 && ServerHotfixes.BuildNumber("x|y|z") == 0);
            var file = DbCacheFile(9, 70374, HfEntries());
            var tables = DbCache.ReadTables(file, new HashSet<uint> { SpellTables.SpellName.TableHash, SpellTables.SpellEffect.TableHash });
            var names = tables.Get(SpellTables.SpellName.TableHash);
            Check("DBCache tables: the last entry of an id wins - a row, then 'invalid' = no row and no removal (424243); 'invalid' for a shipped id = nothing (9000); other tables not asked for are not kept",
                names.Rows.Count == 2 && names.Rows.ContainsKey(424242) && !names.Rows.ContainsKey(424243) && names.Removed.Count == 0 && tables.Get(SpellTables.SpellEffect.TableHash).Removed.SetEquals(new[] { 2 }) && tables.Get(WorldTables.Lock.TableHash) == null);

            var d = ServerHotfixes.Decode(file, set.LayoutsSeen, 70374);
            Check("hotfix decode: 15 rows of the seven tables read, 2 skipped (a SpellEffect row a byte short, a QuestV2 row whose inline id is another); BroadcastText noted as not used, the data-less Creature entry not",
                d.Rows == 15 && d.Dropped == 2 && d.Notes.Any(n => n.Contains("BroadcastText")) && !d.Notes.Any(n => n.Contains("Creature")), d.Rows + " rows, " + d.Dropped + " dropped; " + string.Join(" | ", d.Notes));
            Check("hotfix decode: the fields at their widths - SpellMisc 50 (spell 424242, cast 1, duration 4, range 3, icon 136235), SpellEffect 60 (spell 424242, index 0, effect 252, 3 points), FactionTemplate 3561 (faction 2741, enemy group 1)",
                d.Misc[50].SpellId == 424242 && d.Misc[50].CastIndex == 1 && d.Misc[50].DurationIndex == 4 && d.Misc[50].RangeIndex == 3 && d.Misc[50].IconId == 136235
                && d.Effects[60].SpellId == 424242 && d.Effects[60].Effect.Effect == 252 && d.Effects[60].Effect.Points == 3f && d.Templates[3561].FactionId == 2741 && d.Templates[3561].EnemyGroup == 1);

            var hs = ServerHotfixes.Apply(set, d);
            var hi = hs.Hotfixes;
            var nw = hs.Spells.Get(424242);
            Check("hotfix spells: a new spell from the server's rows alone - name, description, icon, cast 1.5 s, duration 9 s, range 35 yd, one effect; marked new",
                nw != null && nw.Name == "Teleport to Elwynn" && nw.Desc == "Teleports you $s1 times." && nw.IconId == 136235 && nw.CastMs == 1500 && nw.DurationMs == 9000 && Math.Abs(nw.RangeMax - 35f) < 0.01 && nw.Effects.Length == 1
                && (hi.Spell(424242) & HotfixParts.New) != 0, nw == null ? "missing" : nw.IconId + " " + nw.CastMs + " " + nw.DurationMs);
            var fb = hs.Spells.Get(133);
            Check("hotfix spells: Fireball's own SpellMisc / SpellEffect rows replace the shipped ones (9 s, 25 damage), the removed periodic effect is gone, the other difficulty's rows are not used; its name is unchanged (not marked)",
                fb.DurationMs == 9000 && fb.Effects.Length == 1 && fb.Effects[0].Points == 25f && fb.IconId == 135812 && hi.Spell(133) == (HotfixParts.Numbers | HotfixParts.Effects), fb.DurationMs + " " + fb.Effects.Length + " " + hi.Spell(133));
            var rend = hs.Spells.Get(772);
            Check("hotfix spells: a removed Spell row clears Rend's texts, a removed SpellMisc row its icon; 'invalid' entries leave Poison Cloud (9000) and add nothing (424243)",
                rend.Name == "Rend" && rend.Desc == "" && rend.Rank == "" && rend.IconId == 0 && hs.Spells.Get(9000)?.Name == "Poison Cloud" && hs.Spells.Get(424243) == null);
            Check("hotfix spells: the shipped set is not changed (Fireball still 4 s with 2 effects, Rend still has its text, no spell 424242)",
                set.Spells.Get(133).DurationMs == 4000 && set.Spells.Get(133).Effects.Length == 2 && set.Spells.Get(772).Desc.Length > 0 && set.Spells.Get(424242) == null && set.Hotfixes == null);
            var w = hs.World;
            Check("hotfix quests: 93000 is new (in the client's quest list and marked), 25 was shipped already (not new), the removed 498 is gone; the shipped list is unchanged",
                w.QuestIds.Contains(93000) && w.QuestIds.Contains(25) && !w.QuestIds.Contains(498) && hi.NewQuests.SetEquals(new[] { 93000 }) && set.World.QuestIds.Count == 3 && w.SortName(-304) == "Cooking", string.Join(",", w.QuestIds));
            Check("hotfix locks: Briarthorn (31) now needs Herbalism 85, the removed Tin Vein lock (39) needs nothing, the new lock 4588 has no skill need; the shipped lookups unchanged",
                w.SkillNeed(31)?.Skill == 85 && w.SkillNeed(31)?.LockType == 2 && w.SkillNeed(39) == null && w.SkillNeed(4588) == null && w.SkillNeed(2)?.Skill == 25 && set.World.SkillNeed(31)?.Skill == 70 && set.World.SkillNeed(39)?.Skill == 65 && hi.Locks.Contains(31) && hi.Locks.Contains(39));
            Check("hotfix faction templates: 100 (monsters) is no longer hostile to the Alliance, 3561 is new; the shipped templates unchanged",
                hs.Factions.ToAlliance(100) == Standing.Neutral && set.Factions.ToAlliance(100) == Standing.Hostile && hs.Factions.Template(3561)?.FactionId == 2741 && hi.Templates.SetEquals(new[] { 100, 3561 }));
            Check("hotfix: the recipes are worked out again on the new spells (the 4 shipped recipes stay)", hs.Spells.RecipeCount == 4 && hs.Spells.Producing(1001).Count == 1);

            // the rules that keep a wrong layout out
            var layouts = new Dictionary<string, uint>(set.LayoutsSeen) { [SpellTables.SpellMisc.Name] = 0x12345678 };
            var dl = ServerHotfixes.Decode(file, layouts, 70374);
            Check("hotfix: a game whose SpellMisc layout is not the verified one - its hotfix rows are not used (noted), the other tables still are", dl.Misc.Count == 0 && dl.Effects.Count > 0 && dl.Notes.Any(n => n.Contains("SpellMisc") && n.Contains("not the one")), string.Join(" | ", dl.Notes));
            var noLayouts = ServerHotfixes.Decode(file, new Dictionary<string, uint>(), 70374);
            Check("hotfix: tables the game files did not give (no layout to compare): nothing used", noLayouts.Rows == 0);
            var other = ServerHotfixes.Decode(DbCacheFile(9, 70338, HfEntries()), set.LayoutsSeen, 70374);
            Check("hotfix: a DBCache.bin of another build than the game: nothing used, said so", other.Rows == 0 && other.Notes.Any(n => n.Contains("70338") && n.Contains("not used")));
            var cut = ServerHotfixes.Decode(DbCacheFile(9, 70374, HfEntries().Take(2), truncateLast: true), set.LayoutsSeen, 70374);
            Check("hotfix: a cache cut in the middle of an entry (the game writing it): the rows before it are used, noted", cut.Rows == 2 && cut.Names.ContainsKey(424242) && cut.Notes.Any(n => n.Contains("middle")));
            bool threw = false; try { ServerHotfixes.Decode(DbCacheFile(8, 70374, HfEntries()), set.LayoutsSeen, 70374); } catch (Db2Exception) { threw = true; }
            Check("hotfix: a cache version this reader does not know is refused (the caller keeps the shipped tables)", threw);

            // the service: the overlay from a file, and the saved index keeps what the overlay needs
            var hf = Path.Combine(work, "hotfix", "DBCache.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(hf));
            File.WriteAllBytes(hf, file);
            var viaFile = SpellData.Overlay(set, hf, Build);
            Check("SpellData.Overlay: the shipped set with DBCache.bin's rows (15), no file = the shipped tables with no hotfixes, a broken file = the same with the reason",
                viaFile.Hotfixes.Rows == 15 && viaFile.Spells.Get(424242) != null && SpellData.Overlay(set, Path.Combine(work, "nope.bin"), Build).Hotfixes.Rows == 0
                && SpellData.Overlay(set, WriteJunk(work), Build) is AtlasSet bad && bad.Hotfixes.Rows == 0 && bad.Spells.Get(424242) == null && bad.Hotfixes.Notes.Any(n => n.StartsWith("DBCache.bin")));
            var idx = Path.Combine(work, "hotfix", "spelldata.bin");
            SpellIndexFile.TryWrite(idx, Build, set);
            var back = SpellIndexFile.TryRead(idx)?.Set;
            var again = back == null ? null : ServerHotfixes.Apply(back, ServerHotfixes.Decode(file, back.LayoutsSeen, 70374));
            Check("index file: the lookups the overlay needs (cast times, durations, ranges, which SpellMisc / SpellEffect row is whose) survive the round trip - the same overlay from the saved index",
                again != null && again.Spells.Get(424242)?.CastMs == 1500 && again.Spells.Get(133).Effects.Length == 1 && again.Spells.Get(772).IconId == 0 && back.Spells.Lookups.MiscRows.Count == set.Spells.Lookups.MiscRows.Count && back.Spells.Lookups.EffectRows.Count == set.Spells.Lookups.EffectRows.Count);

            // what the Atlas shows
            SpellData.SetForTest(hs);
            var view = SpellData.Spell(424242);
            Check("SpellData.Spell: the new spell with its description filled in from the hotfix effect ('Teleports you 3 times.') and its hotfix parts", view != null && view.Description == "Teleports you 3 times." && (view.Hotfix & HotfixParts.New) != 0 && SpellData.Spell(6001).Hotfix == HotfixParts.None, view?.Description);
            var page = AtlasQuery.Build("spell:424242");
            Check("spell page of a new spell: 'Source: A spell the server added', its facts and description labelled Server hotfix",
                page.Facts.Any(f => f.Label == "Source" && f.Source == AtlasLabel.Hotfix && f.Value.Contains("server added")) && page.Facts.First(f => f.Label == "Cast time").Source == AtlasLabel.Hotfix && page.Texts.Any(t => t.Title == "Description" && t.Source == AtlasLabel.Hotfix), string.Join("; ", page.Facts));
            var fbPage = AtlasQuery.Build("spell:133");
            Check("spell page of a changed spell: the changed parts are 'Server hotfix' (duration, description through the effects), the rank stays Forever; the source names what changed",
                fbPage.Facts.First(f => f.Label == "Duration").Source == AtlasLabel.Hotfix && fbPage.Facts.First(f => f.Label == "Rank").Source == AtlasLabel.Forever && fbPage.Texts.First(t => t.Title == "Description").Source == AtlasLabel.Hotfix
                && fbPage.Facts.Any(f => f.Label == "Source" && f.Value == "The server changed its icon, cast time, duration or range and effects"), string.Join("; ", fbPage.Facts));
            Check("spell page of an untouched spell: no Source fact, everything Forever", !AtlasQuery.Build("spell:6001").Facts.Any(f => f.Label == "Source" || f.Source == AtlasLabel.Hotfix));
            var tip = AtlasTip.For(page);
            Check("the spell's tooltip carries the source in its footer", tip.Footer.Any(l => l.StartsWith("Source: A spell the server added")), string.Join(" | ", tip.Footer));
            var hit = AtlasQuery.Search("teleport to elwynn").All.FirstOrDefault(h => h.Id == "spell:424242");
            Check("search: the new spell is found, labelled Server hotfix", hit != null && hit.Label == AtlasLabel.Hotfix, hit?.ToString());
            var q = AtlasQuery.Build("quest:93000");
            Check("quest page of a quest only a server hotfix names: the game knows it from a server hotfix, said so", q.NotFound && q.Message.Contains("server hotfix"), q.Message);
            SpellData.SetForTest(set);
        }

        static string WriteJunk(string work)
        {
            var p = Path.Combine(work, "hotfix", "junk.bin");
            File.WriteAllBytes(p, Encoding.ASCII.GetBytes("not a hotfix cache at all, just text"));
            return p;
        }
    }
}
