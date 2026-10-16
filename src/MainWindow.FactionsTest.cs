using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
    // --selftest "factions" (Outpost 2.77): the Atlas Factions list and the faction page.
    //   - sides: which side a faction belongs to (the client's own groups, renamed groups, a cycle, the named exceptions), the All | Alliance | Horde | Shared switch
    //     (browse id side=..., counts that add up and follow the other filters, the list partitioned), in the window (segmented choice, a click narrows the list)
    //   - "Your characters": the reputations Elan's Hub 1.16.0 saves, read from a fixture SavedVariables file (junk entries dropped, a hidden character left out,
    //     best standing first, the bar inside the standing), the line when no Hub data has reputations yet, a group page's best standing per faction
    //   - "Your lodge": the Armory's feature `reputations` with a fake server (frames fed in, frames sent collected): what goes into a snapshot (only with both switches
    //     on and a server that takes it; never names), the question per faction, the answer (junk dropped, my own rows left to the section above), and no section on an
    //     older server, for a guest or after the owner switched the feature off
    //   - icons: faction and dungeon rows get the game's own pictures where the game has them (first candidate that exists, race portrait, side banner, generic
    //     dungeon / raid picture), the glyph where it has none; the audit over the lists
    // The real WoW folder is never touched: the SavedVariables live under the scratch folder and icon lookups use a fake resolver.
    public partial class MainWindow
    {
        static FactionSnapshot FxFactions()
        {
            FactionInfo F(int id, string name, int parent, bool rep = true) => new FactionInfo { Id = id, Name = name, ParentId = parent, ReputationIndex = rep ? id % 64 : -1 };
            return new FactionSnapshot(new[]
            {
                F(469, "Alliance", 0, false), F(72, "Stormwind", 469), F(47, "Ironforge", 469), F(54, "Gnomeregan Exiles", 469),
                F(891, "Alliance Forces", 0, false), F(730, "Stormpike Guard", 891),
                F(67, "Horde", 0, false), F(76, "Orgrimmar", 67), F(530, "Darkspear Trolls", 67),
                F(892, "Horde Forces", 0, false), F(729, "Frostwolf Clan", 892),
                F(169, "Steamwheedle Cartel", 0, false), F(21, "Booty Bay", 169), F(369, "Gadgetzan", 169),
                F(529, "Argent Dawn", 0), F(589, "Wintersaber Trainers", 0),
                F(5000, "Alliance", 0, false), F(5001, "New Haven", 5000),                 // a patch that renumbered the group: found by its name
                F(9001, "Loop A", 9002), F(9002, "Loop B", 9001),                          // a broken tree must not hang
                F(7, "Creature", 0, false),                                                 // a plain creature faction: shared, no reputation bar, no icon
            }, new FactionTemplateInfo[0]);
        }

        static string FxRepsLua(long now)
        {
            string Mod(string name, long at, string data) => "['" + name + "']={v=1,at=" + at + ",by='ElansHub',ver='1.16.0',data={" + data + "}},";
            string Core(string name, string cls, string clsFile, int level) => "name='" + name + "',realm='Forever',class='" + cls + "',classFile='" + clsFile + "',race='Human',raceFile='Human',sex=2,faction='Alliance',level=" + level + ",money=1,updated=" + now;
            var s = new StringBuilder("ElansHubData = {\n['schema']=1,\n['chars']={\n");
            // Aria: Revered with Stormwind (21000..42000, 4200 into it), Friendly with Ironforge; junk entries that must be dropped
            s.Append("['Forever-Aria']={at=" + now + ",mods={\n" + Mod("core", now, Core("Aria", "Hunter", "HUNTER", 60)) + Mod("professions", now, "at=" + now + ",list={{name='Cooking',skill=10,max=75,kind='secondary'}}")
                + Mod("reps", now, "at=" + now + ",list={{id=72,s=7,v=25200,min=21000,max=42000,n='Stormwind'},{id=47,s=5,v=4200,min=3000,max=9000},{id=0,s=5,v=1},{id=-3,s=5,v=1},{id=72.5,s=5,v=1},{id=530,s=9,v=1},{id=76,s=0,v=1},'junk',{id=21,s=4,v=0}}") + "}},\n");
            // Bo: Exalted with Stormwind, the read was partial (a collapsed header)
            s.Append("['Forever-Bo']={at=" + now + ",mods={\n" + Mod("core", now, Core("Bo", "Warrior", "WARRIOR", 58))
                + Mod("reps", now, "at=" + now + ",partial=true,list={{id=72,s=8,v=42999,min=42000,max=43000},{id=529,s=4,v=100,min=0,max=3000}}") + "}},\n");
            // Cy: hidden by the user; Exalted with Stormwind too, must never show
            s.Append("['Forever-Cy']={at=" + now + ",mods={\n" + Mod("core", now, Core("Cy", "Mage", "MAGE", 60))
                + Mod("reps", now, "at=" + now + ",list={{id=72,s=8,v=42500,min=42000,max=43000}}") + "}},\n");
            // Dee: an older Hub without the reps module
            s.Append("['Forever-Dee']={at=" + now + ",mods={\n" + Mod("core", now, Core("Dee", "Priest", "PRIEST", 20)) + "}},\n");
            s.Append("}}\n");
            return s.ToString();
        }

        async Task<string> FactionsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            var work = Path.Combine(dir, "factions");
            var savedChars = AtlasQuery.Context.Characters; var savedStandings = AtlasQuery.Context.Standings; var savedLodge = AtlasQuery.Context.Lodge;
            var sent = new List<Dictionary<string, object>>();
            var session = Session;
            var hadClock = session.Clock; var hadOn = settings.ArmoryShareOn; var hadReps = settings.ArmoryShareReps; var hadChars = settings.ArmoryChars; var hadRoot = settings.WowRoot;
            var hadSent = session.SentForTest;
            try
            {
                if (Directory.Exists(work)) Util.DeleteDir(work);
                Directory.CreateDirectory(work);
                var fs = FxFactions();
                SpellData.SetForTest(new AtlasSet { Spells = new SpellSnapshot(new SpellInfo[0], new SkillInfo[0], new SkillAbility[0], new Dictionary<int, int[]>()), Factions = fs, Details = new ItemDetails() });
                ClassData.SetForTest(null, ClassData.State.NotStarted);
                ClassicAtlas.Use(null);
                AtlasQuery.Context.Lodge = () => null;
                AtlasQuery.Context.Standings = null;
                AtlasBrowse.ForgetForTest();

                // ============================================================================================================ sides
                string Side(int id) => FactionData.SideOf(fs, id);
                Check("sides: a faction under the Alliance or Alliance Forces group is the Alliance's (Stormwind, Gnomeregan Exiles, Stormpike Guard; the groups themselves too)",
                    Side(72) == "alliance" && Side(54) == "alliance" && Side(730) == "alliance" && Side(469) == "alliance" && Side(891) == "alliance");
                Check("sides: the Horde likewise (Orgrimmar, Darkspear Trolls, Frostwolf Clan, the groups)", Side(76) == "horde" && Side(530) == "horde" && Side(729) == "horde" && Side(67) == "horde" && Side(892) == "horde");
                Check("sides: everything else is shared (Steamwheedle Cartel and its towns, Argent Dawn, a creature faction)", Side(169) == "shared" && Side(21) == "shared" && Side(529) == "shared" && Side(7) == "shared");
                Check("sides: a group renamed by a patch is still found by its name; a named exception (Wintersaber Trainers) is the Alliance's; a broken tree (a loop) ends as shared; an unknown id is shared",
                    Side(5001) == "alliance" && Side(589) == "alliance" && Side(9001) == "shared" && Side(123456) == "shared");

                // ============================================================================================================ the side switch (browse)
                var all = AtlasBrowse.Run("browse:factions");
                int Cnt(BrowseResult r, string side) => r.Sides.FirstOrDefault(x => x.Slug == side)?.Count ?? -1;
                Check("switch: the Factions list carries All | Alliance | Horde | Shared with counts; All is the whole list, the three add up to it",
                    all.Sides.Select(x => x.Title).SequenceEqual(new[] { "All", "Alliance", "Horde", "Shared" }) && Cnt(all, "") == all.Total && Cnt(all, "alliance") + Cnt(all, "horde") + Cnt(all, "shared") == all.Total && all.Sides[0].Current,
                    string.Join(",", all.Sides.Select(x => x.Title + "=" + x.Count)) + " total " + all.Total);
                Check("switch: the counts (Alliance 9 with the renamed group and its town and the Wintersaber Trainers, Horde 5, the other 7 shared)", Cnt(all, "alliance") == 9 && Cnt(all, "horde") == 5 && Cnt(all, "shared") == 7 && all.Total == 21, string.Join(",", all.Sides.Select(x => x.Title + "=" + x.Count)));
                var horde = AtlasBrowse.Run("browse:factions?side=horde");
                Check("switch: side=horde lists only the Horde's factions (Orgrimmar yes, Stormwind no) and marks Horde as the current choice; the id keeps the side",
                    horde.Total == 5 && horde.Rows.All(r => FactionData.SideOf(fs, int.Parse(r.Key.Split(':')[1])) == "horde") && horde.Rows.Any(r => r.Title == "Orgrimmar") && !horde.Rows.Any(r => r.Title == "Stormwind")
                    && horde.Sides.Single(x => x.Current).Slug == "horde" && horde.Id == "browse:factions?side=horde", horde.Id + " " + string.Join(",", horde.Rows.Select(r => r.Title)));
                var parts = new[] { "alliance", "horde", "shared" }.SelectMany(sd => AtlasBrowse.Run("browse:factions?side=" + sd).Rows.Select(r => r.Key)).ToList();
                Check("switch: the three sides partition the list (every faction in exactly one)", parts.Count == all.Total && parts.Distinct().Count() == all.Total && new HashSet<string>(parts).SetEquals(all.Rows.Select(r => r.Key)));
                var named = AtlasBrowse.Run("browse:factions?name=s&side=alliance");
                Check("switch: the counts follow the other filters (name contains 's': All, Alliance ... count only those) and the side still narrows",
                    Cnt(named, "") == AtlasBrowse.Run("browse:factions?name=s").Total && Cnt(named, "alliance") == named.Total && named.Rows.All(r => r.Title.ToLowerInvariant().Contains("s")), string.Join(",", named.Sides.Select(x => x.Title + "=" + x.Count)));
                var withBar = AtlasBrowse.Run("browse:factions/reputations?side=shared");
                Check("switch: it combines with 'With a reputation bar' (no header groups, no creature faction)", withBar.Total > 0 && !withBar.Rows.Any(r => r.Title == "Steamwheedle Cartel" || r.Title == "Creature"), string.Join(",", withBar.Rows.Select(r => r.Title)));
                BrowseQuery.TryParse("browse:factions?side=sideways", out var bad);
                Check("switch: an unknown side is ignored; side is a filter (Clear filters removes it) and survives the id round trip",
                    bad.Side == "" && BrowseQuery.TryParse("browse:factions?side=HORDE", out var up) && up.Side == "horde" && up.HasFilters && up.ToId() == "browse:factions?side=horde");
                Check("switch: other lists have no side switch", AtlasBrowse.Run("browse:skills").Sides.Count == 0 && AtlasBrowse.Run("browse:spells").Sides.Count == 0);

                // ============================================================================================================ icons
                var rowsAll = all.Rows.ToDictionary(r => r.Title + ":" + r.Key, r => r);
                AtlasRow Row(int id) => all.Rows.First(r => r.Key == "faction:" + id);
                Check("icons: a faction with its own pictures gets an icon id (Stormwind, Argent Dawn, the Alliance group); a side's faction without one gets the side's banner (New Haven); a creature faction keeps the glyph",
                    Row(72).IconId == FactionIcons.Base + 72 && Row(529).IconId == FactionIcons.Base + 529 && Row(469).IconId == FactionIcons.Base + 469 && Row(5001).IconId == FactionIcons.Base + 5001 && Row(7).IconId == 0 && Row(7).ShowGlyph && !Row(72).ShowGlyph);
                var audit = all.Rows.Where(r => (r.IconId > 0) != (FactionIcons.Candidates(int.Parse(r.Key.Split(':')[1])).Count > 0)).Select(r => r.Title).ToList();
                Check("icon audit: in the Factions list every faction that has a picture to try shows it (no glyph placeholder), and only those carry an icon id; the search uses the same id",
                    audit.Count == 0 && AtlasQuery.Search("stormwind").Groups.SelectMany(g => g.Hits).First(h => h.Id == "faction:72").IconId == FactionIcons.Base + 72, string.Join(",", audit));
                Check("icons: what is tried, best first (Stormwind: the mage portal, then the human portrait, then the Alliance banner; New Haven: the banner only; a loop: nothing)",
                    FactionIcons.Candidates(72).SequenceEqual(new[] { "Spell_Arcane_TeleportStormWind", "race:Human", FactionIcons.AllianceBanner }) && FactionIcons.Candidates(5001).SequenceEqual(new[] { FactionIcons.AllianceBanner }) && FactionIcons.Candidates(9001).Count == 0,
                    string.Join(",", FactionIcons.Candidates(72)));
                // the pick, with a fake resolver standing in for the game's file list
                var have = new Dictionary<string, int> { ["Spell_Arcane_TeleportOrgrimmar"] = 135759, [FactionIcons.AllianceBanner] = 132486, ["INV_Jewelry_Talisman_07"] = 133440 };
                int asked = 0;
                Dictionary<ulong, int> Fake(HashSet<ulong> want) { asked++; var d = new Dictionary<ulong, int>(); foreach (var kv in have) { var h = CascStorage.NameHash(NamedIcons.PathOf(kv.Key)); if (want.Contains(h)) d[h] = kv.Value; } return d; }
                var pick = NamedIcons.Pick(new[] { FactionIcons.Base + 76, FactionIcons.Base + 72, FactionIcons.Base + 529, FactionIcons.Base + 5001, FactionIcons.Base + 9001, 133440 }, Fake);
                Check("icons: the first picture the game has wins (Orgrimmar: the portal), a missing portal falls back to the race portrait (Stormwind), the banner for New Haven; one look-up for the whole batch; ids without a picture and plain file ids are left out",
                    pick[FactionIcons.Base + 76] == 135759 && pick[FactionIcons.Base + 72] == RaceIcons.For("Human", 2) && pick[FactionIcons.Base + 529] == 133440 && pick[FactionIcons.Base + 5001] == 132486 && !pick.ContainsKey(FactionIcons.Base + 9001) && !pick.ContainsKey(133440) && asked == 1,
                    string.Join(",", pick.Select(kv => kv.Key - FactionIcons.Base + "=" + kv.Value)));
                Check("icons: a resolver that fails leaves only the race portraits (no exception)", NamedIcons.Pick(new[] { FactionIcons.Base + 72, FactionIcons.Base + 76 }, w => throw new IOException("closed")).Count == 2);
                Check("icons: names go to Interface\\Icons\\<name>.blp; a full path is taken as it is; the made-up ranges never meet a FileDataID or the race portraits",
                    NamedIcons.PathOf("INV_Misc_Coin_01") == @"Interface\Icons\INV_Misc_Coin_01.blp" && NamedIcons.PathOf(@"Interface\LFGFrame\LFGIcon-Raid.blp") == @"Interface\LFGFrame\LFGIcon-Raid.blp"
                    && !NamedIcons.IsIcon(135759) && !NamedIcons.IsIcon(RaceIcons.Base) && !RaceIcons.IsCrop(FactionIcons.Base) && FactionIcons.Base > RaceIcons.Base + 1000 && InstanceIcons.Base > FactionIcons.Base + FactionIcons.MaxFaction && (long)InstanceIcons.Base + InstanceIcons.MaxMap < int.MaxValue);
                Check("icons: a racial skill line without an icon of its own takes the race's portrait (Racial - Troll, Racial - Night Elf); a skill's own icon wins; any other skill without one keeps the glyph",
                    SkillIcons.For(new SkillInfo { Id = 733, Name = "Racial - Troll" }) == RaceIcons.For("Troll", 2) && RaceIcons.For("Troll", 2) > 0 && SkillIcons.For(new SkillInfo { Name = "Racial - Night Elf" }) == RaceIcons.For("NightElf", 2)
                    && SkillIcons.For(new SkillInfo { Name = "Racial - Troll", IconId = 5 }) == 5 && SkillIcons.For(new SkillInfo { Name = "Companions" }) == 0 && SkillIcons.For(null) == 0);
                // dungeons and raids
                Check("icons: a dungeon tries its own picture by its name ('The Deadmines' -> LFGIcon-Deadmines), then the generic dungeon picture; a raid the raid picture; an alias where the game names it differently (Zul'Farrak -> ZulFarak); a battleground without a picture of its own gets none",
                    InstanceIcons.Candidates("The Deadmines", 1).First() == InstanceIcons.Lfg + "Deadmines.blp" && InstanceIcons.Candidates("The Deadmines", 1).Last() == InstanceIcons.Dungeon
                    && InstanceIcons.Candidates("Molten Core", 2).First() == InstanceIcons.Lfg + "MoltenCore.blp" && InstanceIcons.Candidates("Molten Core", 2).Last() == InstanceIcons.Raid
                    && InstanceIcons.Candidates("Zul'Farrak", 1).First() == InstanceIcons.Lfg + "ZulFarak.blp" && !InstanceIcons.Candidates("Alterac Valley", 3).Contains(InstanceIcons.Dungeon),
                    string.Join(",", InstanceIcons.Candidates("Zul'Farrak", 1)));
                int dm = InstanceIcons.For(36, "The Deadmines", 1);
                var dmPick = NamedIcons.Pick(new[] { dm }, w => { var d = new Dictionary<ulong, int>(); var h = CascStorage.NameHash(InstanceIcons.Dungeon); if (w.Contains(h)) d[h] = 136334; return d; });
                Check("icons: a dungeon row's made-up id remembers what to try; when the game lacks its own picture the generic dungeon picture is used", dm == InstanceIcons.Base + 36 && dmPick.TryGetValue(dm, out var dmReal) && dmReal == 136334);

                // ============================================================================================================ your characters (fixture SavedVariables)
                var root = Path.Combine(work, "wow");
                var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "ACC1", "SavedVariables");
                Directory.CreateDirectory(sv);
                Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                File.WriteAllText(Path.Combine(sv, "ElansHub.lua"), FxRepsLua(now), new UTF8Encoding(false));
                Check("fixture: the WoW folder is under the scratch folder (never the real game)", Path.GetFullPath(root).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));
                HubData.ResetCache();
                var hub = HubData.Read(root);
                var chars = CharacterMerge.Merge(hub, null).Chars.Where(c => c.Hub != null).ToList();
                foreach (var c in chars) if (c.Name == "Cy") c.Hidden = true;
                AtlasQuery.Context.Characters = () => chars;
                var aria = HubModules.RepsOf(chars.First(c => c.Name == "Aria").Hub);
                Check("hub: the reps module is read from the SavedVariables (Aria: Stormwind Revered 25200 in 21000..42000, Ironforge, Booty Bay; junk ids, standings out of 1..8 and non-tables dropped)",
                    aria != null && aria.ById.Count == 3 && aria.ById[72].Standing == 7 && aria.ById[72].Value == 25200 && aria.ById[72].Min == 21000 && aria.ById[72].Max == 42000 && aria.ById[72].Name == "Stormwind" && aria.ById.ContainsKey(47) && aria.ById.ContainsKey(21) && !aria.Partial,
                    aria == null ? "null" : string.Join(",", aria.ById.Keys));
                Check("hub: a partial read is flagged; a character of an older Hub has no reps module (null, not empty)", HubModules.RepsOf(chars.First(c => c.Name == "Bo").Hub).Partial && HubModules.RepsOf(chars.First(c => c.Name == "Dee").Hub) == null);
                Check("standing words and the bar: Revered 4,200 / 21,000 at 20 %; Exalted 999 / 1,000; no bounds = the word alone, no bar",
                    Reputation.Line(7, 25200, 21000, 42000) == "Revered 4,200 / 21,000" && Math.Abs(Reputation.Progress(25200, 21000, 42000) - 0.2) < 1e-9 && Reputation.Line(8, 42999, 42000, 43000) == "Exalted 999 / 1,000"
                    && Reputation.Line(4, 0, 0, 0) == "Neutral" && Reputation.Progress(5, 0, 0) < 0 && Reputation.StandingName(1) == "Hated" && Reputation.StandingName(9) == "Unknown");

                var sw = AtlasQuery.Build("faction:72");
                var mine = sw.Section(AtlasQuery.StandingMineKey);
                Check("faction page: 'Your characters' lists who has a reputation with Stormwind, best first (Bo Exalted, then Aria Revered); the hidden character (Cy) and characters without one (Dee) are not listed",
                    mine != null && mine.Rows.Select(r => r.Title).SequenceEqual(new[] { "Bo", "Aria" }), mine == null ? "no section" : string.Join(",", mine.Rows.Select(r => r.Title)));
                var ariaRow = mine?.Rows.FirstOrDefault(r => r.Title == "Aria");
                Check("faction page: a row says the standing and the progress like the game's bar (Revered 4,200 / 21,000), carries the bar (0.2) and the level and class",
                    ariaRow != null && ariaRow.Detail == "Revered 4,200 / 21,000" && Math.Abs(ariaRow.Progress - 0.2) < 1e-9 && ariaRow.HasProgress && ariaRow.Extra.Contains("Level 60") && ariaRow.Extra.Contains("Hunter"), ariaRow?.Detail + " | " + ariaRow?.Extra);
                Check("faction page: the note says a collapsed header can hide factions (Bo's read was partial); a Side fact where 'Part of' does not already say it (Stormpike Guard: Alliance, Argent Dawn: Both sides; not on Stormwind); the faction's icon; the section is on the Overview tab",
                    mine.Note.Contains("collapsed") && !sw.Facts.Any(f => f.Label == "Side") && AtlasQuery.Build("faction:730").Facts.Any(f => f.Label == "Side" && f.Value == "Alliance") && AtlasQuery.Build("faction:529").Facts.Any(f => f.Label == "Side" && f.Value == "Both sides") && sw.IconId == FactionIcons.Base + 72 && AtlasTabs.OfSection(AtlasQuery.StandingMineKey) == AtlasTabs.Overview && AtlasTabs.OfSection(AtlasQuery.StandingLodgeKey) == AtlasTabs.Lodge);
                var ad = AtlasQuery.Build("faction:529").Section(AtlasQuery.StandingMineKey);
                var org = AtlasQuery.Build("faction:76").Section(AtlasQuery.StandingMineKey);
                Check("faction page: Argent Dawn shows Bo (Neutral); Orgrimmar - nobody has it: the calm 'none of your characters' line (no rows)",
                    ad != null && ad.Rows.Count == 1 && ad.Rows[0].Title == "Bo" && org != null && org.Rows.Count == 0 && org.Empty.StartsWith("None of your characters"), org?.Empty);
                Check("faction page: a group without a reputation of its own (Steamwheedle Cartel) has no standing sections; its factions show the best own standing (Booty Bay: Neutral (Aria))",
                    AtlasQuery.Build("faction:169") is var stc && stc.Section(AtlasQuery.StandingMineKey) == null && stc.Section("members").Rows.First(r => r.Title == "Booty Bay").Detail == "Neutral (Aria)" && stc.Section("members").Rows.First(r => r.Title == "Gadgetzan").Detail == "",
                    string.Join(" | ", AtlasQuery.Build("faction:169").Section("members").Rows.Select(r => r.Title + "=" + r.Detail)));
                var deeOnly = chars.Where(c => c.Name == "Dee").ToList();
                AtlasQuery.Context.Characters = () => deeOnly;
                var none = AtlasQuery.Build("faction:72").Section(AtlasQuery.StandingMineKey);
                Check("faction page: with only an older Hub's data the section says how to get reputations (Elan's Hub 1.16.0, log in or /reload)", none != null && none.Rows.Count == 0 && none.Empty.Contains("1.16.0"), none?.Empty);
                AtlasQuery.Context.Characters = () => new List<InvChar>();
                Check("faction page: no characters at all = no section", AtlasQuery.Build("faction:72").Section(AtlasQuery.StandingMineKey) == null);
                AtlasQuery.Context.Characters = () => chars;

                // ============================================================================================================ your lodge (fake server)
                var clock = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
                session.Clock = () => clock;
                session.SentForTest = o => sent.Add(Json.Obj(Json.Write(o)));
                session.ArmoryResetForTest();
                settings.WowRoot = root;
                settings.ArmoryShareOn = true; settings.ArmoryShareReps = false; settings.ArmoryChars = new List<string> { chars.First(c => c.Name == "Aria").Key };
                session.ArmorySourceForTest = () => chars.Where(c => c.Name == "Aria").ToList();
                AtlasQuery.Context.Standings = f => session.StandingsFor(f);
                string feats(bool reps) => "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"armory\",\"armory2\"" + (reps ? ",\"reputations\"" : "");

                // a server without the feature
                sent.Clear();
                session.FeedForTest(FunWelcome("member", FunPeople(false), "", feats(false), "2.26.0"));
                var oldPut = AryFrames(sent, "armory.put");
                Check("older server: the snapshot goes without reputations; the faction page has no Lodge section and nothing is asked",
                    !session.SupportsReps && oldPut.Count == 1 && !oldPut[0].Child("char").ContainsKey("reps") && AtlasQuery.Build("faction:72").Section(AtlasQuery.StandingLodgeKey) == null && AryFrames(sent, "armory.standing").Count == 0);

                // a server with it, the member's switch off
                session.ArmoryResetForTest();
                session.ArmorySourceForTest = () => chars.Where(c => c.Name == "Aria").ToList();
                sent.Clear();
                session.FeedForTest(FunWelcome("member", FunPeople(false), "", feats(true), "2.26.0"));
                var put1 = AryFrames(sent, "armory.put");
                Check("switch off (the default): even a server with the feature gets no reputations", session.SupportsReps && !settings.ArmoryShareReps && put1.Count == 1 && !put1[0].Child("char").ContainsKey("reps") && !session.RepsSharedForTest);
                // the member switches it on: the character goes again, now with its reputations (ids, standings, values - never names)
                session.FeedForTest("{\"t\":\"armory.saved\",\"ok\":true,\"key\":\"" + chars.First(c => c.Name == "Aria").Key + "\",\"stored\":true}");
                sent.Clear();
                settings.ArmoryShareReps = true;
                session.ArmorySettingsChanged();
                var put2 = AryFrames(sent, "armory.put");
                var reps = put2.Count == 1 ? put2[0].Child("char").Child("reps") : null;
                var list = reps?.List("list").OfType<Dictionary<string, object>>().ToList();
                Check("switch on: the character is uploaded again with its reputations: faction id, standing, value and bounds, sorted by id, no names",
                    list != null && list.Select(e => e.Int("id")).SequenceEqual(new[] { 21, 47, 72 }) && list.First(e => e.Int("id") == 72).Int("s") == 7 && list.First(e => e.Int("id") == 72).Int("min") == 21000 && !Json.Write(put2[0]).Contains("Stormwind") && reps.Long("at") == now,
                    put2.Count + " " + (reps == null ? "no reps" : Json.Write(reps)));
                Check("the signature includes the reputations (switching on uploads again) and ignores the read's time", ArmoryShare.Signature(ArmoryShare.Build(chars.First(c => c.Name == "Aria"), false, true, true)) != ArmoryShare.Signature(ArmoryShare.Build(chars.First(c => c.Name == "Aria"), false, true, false)) && !Json.Write(ArmoryShare.Build(chars.First(c => c.Name == "Aria"), false, true, true)).Contains("\"at\""));

                // asking where the Lodge stands
                sent.Clear();
                int changes = 0; Action onCh = () => changes++;
                session.StandingsChanged += onCh;
                try
                {
                    var p1 = AtlasQuery.Build("faction:72");
                    var lodge1 = p1.Section(AtlasQuery.StandingLodgeKey);
                    var q = AryFrames(sent, "armory.standing");
                    Check("ask: the page asks once for the faction (armory.standing {faction: 72}) and says it is asking", q.Count == 1 && q[0].Int("faction") == 72 && lodge1 != null && lodge1.Rows.Count == 0 && lodge1.Empty.StartsWith("Asking"));
                    AtlasQuery.Build("faction:72");
                    Check("ask: drawing the page again before the answer does not repeat the question", AryFrames(sent, "armory.standing").Count == 1);
                    string SRow(object faction, string name, int s, string extra = "") => "{\"faction\":" + faction + ",\"owner\":\"mia\",\"by\":\"Mia\",\"key\":\"Forever-" + name + "\",\"name\":\"" + name + "\",\"class\":\"PALADIN\",\"level\":60,\"s\":" + s + ",\"v\":30000,\"min\":21000,\"max\":42000,\"at\":5" + extra + "}";
                    session.FeedForTest("{\"t\":\"armory.standing\",\"faction\":72,\"who\":[" + SRow(72, "Lia", 7) + "," + SRow(72, "Mine", 8, ",\"mine\":true") + "," + SRow(76, "Elsewhere", 6) + "," + SRow(72, "Bad", 9) + ",5,\"x\"," + SRow(72, "Zed", 5).Replace("\"level\":60", "\"level\":900") + "]}");
                    var st = session.StandingsFor(72);
                    Check("answer: junk rows are dropped (another faction, a standing of 9, non-objects), the level is capped, the event fired once",
                        changes == 1 && !st.Pending && st.Rows.Count == 3 && st.Rows.Any(r => r.Name == "Zed" && r.Level == 100) && st.Rows.Any(r => r.Mine), changes + " " + string.Join(",", st.Rows.Select(r => r.Name)));
                    var lodge2 = AtlasQuery.Build("faction:72").Section(AtlasQuery.StandingLodgeKey);
                    Check("faction page: 'Your lodge' lists the other members' characters with the member's name, standing and bar, best first; my own shared character is left to 'Your characters'",
                        lodge2 != null && lodge2.Rows.Select(r => r.Title).SequenceEqual(new[] { "Lia (Mia)", "Zed (Mia)" }) && lodge2.Rows[0].Detail == "Revered 9,000 / 21,000" && lodge2.Rows[0].HasProgress && lodge2.Note.Contains("chose to share"),
                        lodge2 == null ? "none" : string.Join(",", lodge2.Rows.Select(r => r.Title + "=" + r.Detail)));
                    changes = 0;
                    session.FeedForTest("{\"t\":\"armory.standing\",\"faction\":81,\"who\":[" + SRow(81, "Nobody", 5) + "]}");
                    Check("answer: an answer to a question that was never asked is ignored", changes == 0);
                    int before = AryFrames(sent, "armory.standing").Count;
                    clock = clock.AddMinutes(3);
                    session.StandingsFor(72);
                    Check("ask: after two minutes the question is sent again (the old rows still shown meanwhile)", AryFrames(sent, "armory.standing").Count == before + 1 && session.StandingsFor(72).Rows.Count == 3);

                    // the owner switched reputations off on the server
                    session.FeedForTest("{\"t\":\"error\",\"text\":\"Reputations are switched off on this lodge\",\"code\":\"noreps\"}");
                    sent.Clear();
                    Check("owner switched it off (error noreps): no Lodge section any more, nothing asked, and the next upload goes without reputations",
                        AtlasQuery.Build("faction:72").Section(AtlasQuery.StandingLodgeKey) == null && AryFrames(sent, "armory.standing").Count == 0 && !session.RepsSharedForTest);

                    // a guest
                    session.ArmoryResetForTest();
                    sent.Clear();
                    session.FeedForTest(FunWelcome("guest", FunPeople(false), "", feats(true), "2.26.0"));
                    sent.Clear();
                    Check("guest: no Lodge section and nothing is asked", AtlasQuery.Build("faction:72").Section(AtlasQuery.StandingLodgeKey) == null && AryFrames(sent, "armory.standing").Count == 0 && !session.StandingsFor(72).Available);
                    Check("Settings: the switch 'Also share their reputations' is off by default in a new settings file", !new Settings().ArmoryShareReps);
                }
                finally { session.StandingsChanged -= onCh; }

                // ============================================================================================================ the window
                session.ArmoryResetForTest();
                session.FeedForTest(FunWelcome("member", FunPeople(false), "", feats(true), "2.26.0"));
                session.FeedForTest("{\"t\":\"armory.standing\",\"faction\":72,\"who\":[]}");
                AtlasView.FilterDebounceMsForTest = 20;
                ShowTab("atlas");
                AtlasView.History.Clear();
                await AtlasView.Open("browse:factions"); await AtlasView.WaitForTest(); await Task.Delay(120); UpdateLayout();
                var btns = AtlasView.SideButtonsForTest;
                Check("window: the Factions list shows the side switch (All, Alliance, Horde, Shared) with counts; All is chosen",
                    btns.Count == 4 && btns[""].IsChecked == true && AtlasView.SideCountForTest("horde") == "5" && AtlasView.SideCountForTest("") == all.Total.ToString(), string.Join(",", btns.Keys));
                Snapshot(Path.Combine(dir, "factions-all.png"));
                btns["horde"].IsChecked = true;
                await WaitFor(() => AtlasView.BrowseModel?.Query.Side == "horde" && AtlasView.BrowseModel.Total == 5, 5000); await AtlasView.BrowseWaitForTest(); await Task.Delay(80); UpdateLayout();
                Check("window: choosing Horde narrows the list to the 5 Horde factions, the id follows (side=horde) and the switch stays (no redraw)",
                    AtlasView.BrowseModel?.Total == 5 && AtlasView.CurrentId == "browse:factions?side=horde" && ReferenceEquals(btns["horde"], AtlasView.SideButtonsForTest["horde"]) && AtlasView.BrowseListForTest.Items.Count == 5, AtlasView.CurrentId);
                Snapshot(Path.Combine(dir, "factions-horde.png"));
                var nameBox = (TextBox)AtlasView.BrowseControlForTest("name");
                nameBox.Text = "or";
                await WaitFor(() => AtlasView.BrowseModel?.Query.Name == "or", 5000); await AtlasView.BrowseWaitForTest(); await Task.Delay(80); UpdateLayout();
                Check("window: typing a name updates the counts on the switch in place", AtlasView.SideCountForTest("horde") == AtlasBrowse.Run("browse:factions?name=or&side=horde").Total.ToString() && AtlasView.SideCountForTest("") == AtlasBrowse.Run("browse:factions?name=or").Total.ToString(),
                    AtlasView.SideCountForTest("horde") + "/" + AtlasView.SideCountForTest(""));
                await AtlasView.Open("faction:72"); await AtlasView.WaitForTest(); await Task.Delay(150); UpdateLayout();
                var tx = AtlasTexts(AtlasView.BodyForTest);
                Check("window: the Stormwind page shows 'Your characters' with Bo and Aria and the standings", tx.Contains("Your characters") && tx.Contains("Revered 4,200 / 21,000") && tx.Any(t => t.StartsWith("Exalted")), string.Join(" | ", tx.Take(40)));
                Snapshot(Path.Combine(dir, "faction-page.png"));
            }
            catch (Exception e) { Check("factions test ran without an exception", false, e.ToString()); }
            finally
            {
                AtlasQuery.Context.Characters = savedChars; AtlasQuery.Context.Standings = savedStandings; AtlasQuery.Context.Lodge = savedLodge;
                session.Clock = hadClock; session.SentForTest = hadSent; session.ArmorySourceForTest = null; session.ForgetStandingsForTest(); session.ArmoryResetForTest();
                settings.ArmoryShareOn = hadOn; settings.ArmoryShareReps = hadReps; settings.ArmoryChars = hadChars; settings.WowRoot = hadRoot;
                SpellData.ResetForTest(); ClassData.ResetForTest(); AtlasBrowse.ForgetForTest(); HubData.ResetCache();
                AtlasView.History.Clear();
            }
            return "factions: " + (ok ? "OK" : "FAILED") + "\r\n" + string.Join("\r\n", notes);
        }
    }
}
