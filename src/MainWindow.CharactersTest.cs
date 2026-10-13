using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "characters": the Characters page's Hub side. Fixture folders under the scratch directory only (never the real WoW folder).
    // Parsing of the six new Hub modules (bags, bank, guildbank, gear, talents, professions; v1) from sample text, a module newer than the app
    // understands = TooNew with no data (every module), the merge of Hub and Elan's Bags characters (Hub primary, Bags fills in, the newer record wins,
    // "Realm-Name" exact so same names on two realms never mix), the conversion into the inventory model (counts across characters), the talent
    // grouping on the shape of the real data (several trees side by side, a bogus 102800 position), and the page itself: Overview, Gear, Talents,
    // Professions and the empty / too-new states, fed by an ElansHub.lua and an ElansBags.lua together.
    public partial class MainWindow
    {
        // WoW-style Lua of ElansHubData: Elan (every module, bags newer than the Bags addon's save unless elanBagsAt says otherwise), Measley (bags too new,
        // gear), Other-Measley (another realm, same name as a Bags character), Newbie (core only), Future (every module newer than this app), Other-Elan (same
        // name as Elan on another realm, core only); the guild bank of "Forever-Test Guild" as an account module.
        static string HubCharsLua(long now, long elanBagsAt, string elanLevel = "60")
        {
            string Link(int id, string name, string hex) => "[" + id + "]='|cff" + hex + "|Hitem:" + id + "::::::::|h[" + name + "]|h|r'";
            string It(int slot, int id, int c, int q, int ic, int il = 0) => "[" + slot + "]={i=" + id + ",c=" + c + ",q=" + q + ",ic=" + ic + (il > 0 ? ",il=" + il : "") + "}";
            string Mod(string name, int v, long at, string data) => "['" + name + "']={v=" + v + ",at=" + at + ",by='ElansHub',ver='1.12.0',data={" + data + "}},";
            string Core(string name, string realm, string cls, string clsFile, string race, int sex, string faction, string level, long money, string guild, long updated, string extra = "") =>
                "name='" + name + "',realm='" + realm + "',class='" + cls + "',classFile='" + clsFile + "',race='" + race + "',raceFile='" + race + "',sex=" + sex + ",faction='" + faction + "',level=" + level
                + ",xp=1500,xpMax=4500,rested=300,money=" + money + ",guild='" + guild + "',zone='Orgrimmar',subzone='Valley of Strength',updated=" + updated + extra;
            // talents: three trees side by side (columns 600 apart, 2400 between trees), plus a node at an absurd position and one without any
            var nodes = new StringBuilder();
            int nid = 1;
            foreach (var x0 in new[] { 1800, 5400, 9000 })
                for (int col = 0; col < 3; col++)
                    for (int row = 0; row < 3; row++, nid++)
                        nodes.Append("[" + nid + "]={id=" + nid + ",r=" + (nid % 3) + ",m=3,x=" + (x0 + col * 600) + ",y=" + (600 + row * 600) + ",s=" + (1000 + nid) + (nid % 2 == 0 ? ",n='Talent " + nid + "'" : "") + (nid % 4 == 0 ? ",ic=" + (135000 + nid) : nid == 5 ? ",ic='junk'" : "") + "},\n");
            nodes.Append("[" + nid++ + "]={id=900,r=1,m=1,x=102800,y=600,s=5,n='Bogus'},\n");
            nodes.Append("[" + nid++ + "]={id=901,r=1,m=1,s=6,n='Nowhere'},\n");
            var s = new StringBuilder("ElansHubData = {\n['schema']=1,\n['chars']={\n");
            s.Append("['Forever-Elan']={at=" + (now - 10) + ",mods={\n");
            s.Append(Mod("core", 1, now - 10, Core("Elan", "Forever", "Hunter", "HUNTER", "Orc", 2, "Horde", elanLevel, 123456789, "Test Guild", now - 50)));
            s.Append(Mod("bags", 1, elanBagsAt, "at=" + elanBagsAt + ",cont={\n[0]={n=16,k='backpack',s={" + It(1, 100, 20, 1, 132593) + "," + It(2, 101, 5, 1, 134328) + "," + It(3, 102, 1, 3, 133633) + "," + It(16, 107, 2, 2, 133784) + "}},\n"
                + "[1]={n=6,k='bag',id=3000,ic=133633,s={" + It(1, 106, 800, 1, 134400) + "}},\n"
                + "[5]={n=8,k='reagent',id=3002,ic=132593,s={" + It(1, 100, 15, 1, 132593) + "," + It(3, 107, 3, 2, 133784) + "}}},\n"
                + "links={" + Link(100, "Linen Cloth", "ffffff") + "," + Link(101, "Minor Healing Potion", "ffffff") + "," + Link(102, "Iron Sword", "0070dd") + "," + Link(106, "Rough Arrow", "ffffff") + ","
                + Link(107, "Bright Gem", "1eff00") + "," + Link(3000, "Linen Bag", "1eff00") + "," + Link(3002, "Light Leather Reagent Bag", "0070dd") + "}"));
            s.Append(Mod("bank", 1, now - 3600, "at=" + (now - 3600) + ",cont={\n[-1]={n=28,k='bank',s={" + It(1, 100, 10, 1, 132593) + "," + It(7, 107, 2, 2, 133784) + "}},\n"
                + "[6]={n=8,k='bankbag',id=3001,ic=132593,s={" + It(2, 109, 1, 1, 133003) + "}}},\n"
                + "links={" + Link(100, "Linen Cloth", "ffffff") + "," + Link(107, "Bright Gem", "1eff00") + "," + Link(109, "White Cloak", "ffffff") + "," + Link(3001, "Rare Satchel", "0070dd") + "}"));
            s.Append(Mod("gear", 1, now - 300, "at=" + (now - 300) + ",ilvl=58.5,slots={" + It(1, 200, 1, 3, 133003, 62) + "," + It(5, 201, 1, 2, 133003, 55) + "," + It(15, 109, 1, 1, 133003, 10) + "," + It(16, 202, 1, 4, 133633, 70) + "},"
                + "links={" + Link(200, "Helm of Testing", "0070dd") + "," + Link(201, "Plain Chestguard", "1eff00") + "," + Link(202, "Epic Blade", "a335ee") + "," + Link(109, "White Cloak", "ffffff") + "}"));
            s.Append(Mod("talents", 1, now - 300, "at=" + (now - 300) + ",api='traits',spent=35,unspent=2,nodes={\n" + nodes + "}"));
            s.Append(Mod("professions", 1, now - 300, "at=" + (now - 300) + ",list={{name='Mining',skill=225,max=300,ic=136248,kind='primary'},{name='Herbalism',skill=300,max=300,ic=136065,kind='primary'},{name='Cooking',skill=75,max=150,ic=133971,kind='secondary'}}"));
            // hunter.pets v1 as Hunter Helper 1.26.5 writes it, plus odd entries a reader must skip (a bare ability id, a number where a table goes, a text junk pet)
            s.Append(Mod("hunter.pets", 1, now - 200, "at=" + (now - 200) + ",active='Rex:Bear',pets={['Rex:Bear']={name='Rex',family='Bear',familyId='bear',level=40,loyalty=5,ic=132127,tpTotal=80,tpSpent=30,happiness=3,"
                + "abilities={{id='bite',name='Bite',rank=4},{id='growl',name='Growl',rank=3},{id='bogus'},7}},['Spot:Wolf']={name='Spot',family='Wolf',familyId='wolf',level=22,loyaltyText='Faithful',stable=2,abilities={{id='bite',name='Bite',rank=2}}},"
                + "['Odd:Cat']='junk'},known={bite=6,claw=4,fasterattack=2,weird='x',zero=0}"));
            s.Append("}},\n");
            s.Append("['Forever-Measley']={at=" + (now - 5000) + ",mods={\n");
            s.Append(Mod("core", 1, now - 5000, Core("Measley", "Forever", "Paladin", "PALADIN", "Human", 2, "Alliance", "22", 2503000, "", now - 5000)));
            s.Append(Mod("bags", 2, now, "at=" + now + ",cont={}"));
            s.Append(Mod("gear", 1, now - 5000, "at=" + (now - 5000) + ",slots={" + It(1, 203, 1, 2, 133003, 20) + "},links={" + Link(203, "Leather Cap", "1eff00") + "}"));
            s.Append("}},\n");
            s.Append("['Other-Measley']={at=" + (now - 500) + ",mods={\n");
            s.Append(Mod("core", 1, now - 500, Core("Measley", "Other", "Warrior", "WARRIOR", "Orc", 3, "Horde", "5", 77, "", now - 500)));
            s.Append(Mod("bags", 1, now - 500, "at=" + (now - 500) + ",cont={[0]={n=16,k='backpack',s={" + It(1, 100, 7, 1, 132593) + "}}},links={" + Link(100, "Linen Cloth", "ffffff") + "}"));
            s.Append("}},\n");
            s.Append("['Other-Elan']={at=" + (now - 900) + ",mods={\n");
            s.Append(Mod("core", 1, now - 900, Core("Elan", "Other", "Mage", "MAGE", "Human", 3, "Alliance", "9", 5, "", now - 900)));
            s.Append("}},\n");
            s.Append("['Forever-Newbie']={at=" + (now - 700) + ",mods={\n");
            s.Append(Mod("core", 1, now - 700, Core("Newbie", "Forever", "Rogue", "ROGUE", "Gnome", 2, "Alliance", "5", 12, "", now - 700)));
            s.Append("}},\n");
            s.Append("['Forever-Future']={at=" + (now - 800) + ",mods={\n");
            s.Append(Mod("core", 1, now - 800, Core("Future", "Forever", "Priest", "PRIEST", "Dwarf", 3, "Alliance", "30", 99, "", now - 800)));
            foreach (var m in new[] { "bags", "bank", "gear", "talents", "professions", "hunter.pets" }) s.Append(Mod(m, 2, now - 800, "surprise=true"));
            s.Append("}},\n");
            s.Append("},\n['account']={['mods']={\n");
            s.Append(Mod("guildbank", 1, now - 30, "guilds={['Forever-Test Guild']={name='Test Guild',realm='Forever',money=999000,at=" + (now - 30) + ",tabs={[1]={name='Consumables',ic=134328,at=" + (now - 30)
                + ",s={" + It(1, 101, 41, 1, 134328) + "}}}}},links={" + Link(101, "Minor Healing Potion", "ffffff") + "}"));
            s.Append("}}}\n");
            return s.ToString();
        }

        async Task<string> CharactersTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var root = Path.Combine(dir, "charswow");
            try
            {
                if (Directory.Exists(root)) Util.DeleteDir(root);
                HubData.ResetCache();
                Check("fixture: the WoW folder of this test is under the scratch folder (never the real game)", Path.GetFullPath(root).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase));

                // ---- the six modules, parsed from sample text
                var hub = HubData.Parse(HubCharsLua(now, now - 60));
                var elan = hub.Char("Forever-Elan");
                Check("supported table lists the six new modules at v1", new[] { "bags", "bank", "guildbank", "gear", "talents", "professions" }.All(m => HubData.Supported[m] == 1));
                var bags = HubModules.BagsOf(elan);
                Check("bags v1: 3 containers in index order (backpack 16, bag 6, reagent 8)", bags != null && bags.Cont.Select(c => c.Index + ":" + c.N + ":" + c.K).SequenceEqual(new[] { "0:16:backpack", "1:6:bag", "5:8:reagent" }), bags == null ? "null" : string.Join(",", bags.Cont.Select(c => c.Index + ":" + c.N + ":" + c.K)));
                Check("bags v1: items {i,c,q,ic}, names + quality colours from the links table, bag item named", bags != null && bags.Cont[0].S[1].I == 100 && bags.Cont[0].S[1].C == 20 && bags.Cont[0].S[1].Ic == 132593 && bags.Cont[0].S[1].Name == "Linen Cloth"
                    && bags.Cont[0].S[3].Q == 3 && bags.Cont[1].BagName == "Linen Bag" && bags.Cont[1].BagQuality == 2 && bags.Cont[2].BagName == "Light Leather Reagent Bag" && bags.At == now - 60);
                var bank = HubModules.BankOf(elan);
                Check("bank v1: main bank (index -1) and a bank bag", bank != null && bank.Cont.Select(c => c.Index + ":" + c.K).SequenceEqual(new[] { "-1:bank", "6:bankbag" }) && bank.Cont[1].S[2].I == 109 && bank.Cont[1].BagName == "Rare Satchel");
                var gear = HubModules.GearOf(elan);
                Check("gear v1: average item level, 4 slots with item level, names from links", gear != null && Math.Abs(gear.Ilvl - 58.5) < 0.001 && gear.Slots.Keys.SequenceEqual(new[] { 1, 5, 15, 16 }) && gear.Slots[1].Il == 62 && gear.Slots[1].Name == "Helm of Testing" && gear.Slots[16].Q == 4);
                var tal = HubModules.TalentsOf(elan);
                Check("talents v1: api, spent / unspent, 29 nodes (27 placed + a bogus one + one without a position)", tal != null && tal.Api == "traits" && tal.Spent == 35 && tal.Unspent == 2 && tal.Nodes.Count == 29 && tal.Nodes.Count(n => n.HasPos) == 28 && tal.Nodes[1].N == "Talent 2", tal == null ? "null" : tal.Nodes.Count.ToString());
                Check("talents v1: ic parsed as a number (0 when missing or not a number)", tal != null && tal.Nodes[3].Ic == 135004 && tal.Nodes[7].Ic == 135008 && tal.Nodes[0].Ic == 0 && tal.Nodes[4].Ic == 0 && tal.Nodes.Count(n => n.Ic > 0) == 6, tal == null ? "null" : string.Join(",", tal.Nodes.Select(n => n.Ic)));
                var prof = HubModules.ProfessionsOf(elan);
                Check("professions v1: name, skill / max, icon, kind", prof != null && prof.List.Count == 3 && prof.List[0].Name == "Mining" && prof.List[0].Skill == 225 && prof.List[0].Max == 300 && prof.List[0].Ic == 136248 && prof.List[2].Kind == "secondary");
                // ---- hunter.pets v1 (Hunter Helper 1.26.5)
                Check("supported table lists hunter.pets at v1", HubData.Supported["hunter.pets"] == 1);
                var pets = HubModules.PetsOf(elan);
                Check("hunter.pets v1: active key, 2 pets (the text junk one skipped), with you first, the stable after", pets != null && pets.Active == "Rex:Bear" && pets.Pets.Select(p => p.Key).SequenceEqual(new[] { "Rex:Bear", "Spot:Wolf" })
                    && pets.WithYou.Count() == 1 && pets.Stabled.Single().Key == "Spot:Wolf" && pets.At == now - 200, pets == null ? "null" : string.Join(",", pets.Pets.Select(p => p.Key)));
                var rex = pets?.Pets[0]; var spot = pets?.Pets[1];
                Check("hunter.pets v1: pet fields - name, family, familyId, level, loyalty 5 (Faithful), icon, training points, happiness",
                    rex != null && rex.Name == "Rex" && rex.Family == "Bear" && rex.FamilyId == "bear" && rex.Level == 40 && rex.Loyalty == 5 && HubModules.LoyaltyNames[rex.Loyalty - 1] == "Faithful" && rex.Ic == 132127 && rex.TpTotal == 80 && rex.TpSpent == 30 && rex.Happiness == 3 && !rex.InStable);
                Check("hunter.pets v1: abilities in order with ranks (a bare id gets its name from the id, rank 0; a number row is skipped)", rex != null && rex.Abilities.Select(a => a.Name + ":" + a.Rank).SequenceEqual(new[] { "Bite:4", "Growl:3", "Bogus:0" }), rex == null ? "null" : string.Join(",", rex.Abilities.Select(a => a.Name + ":" + a.Rank)));
                Check("hunter.pets v1: stabled pet - slot 2, loyalty from its text, no icon, no training points", spot != null && spot.Stable == 2 && spot.InStable && spot.Loyalty == 0 && spot.LoyaltyText == "Faithful" && spot.Ic == 0 && spot.TpTotal == -1 && spot.Happiness == 0);
                Check("hunter.pets v1: known abilities {id = rank}; non-numbers and rank 0 dropped", pets != null && pets.Known.Count == 3 && pets.Known["bite"] == 6 && pets.Known["claw"] == 4 && pets.Known["fasterattack"] == 2 && HubModules.PetAbilityName("fasterattack") == "Faster Attack" && HubModules.PetAbilityName("newthing") == "Newthing");
                var futPets = hub.Char("Forever-Future");
                Check("hunter.pets v2: TooNew, no data exposed, the character's other modules unaffected", futPets.Modules["hunter.pets"].TooNew && futPets.Modules["hunter.pets"].Data == null && HubModules.StateOf(futPets, "hunter.pets") == HubModState.TooNew && HubModules.PetsOf(futPets) == null);
                Check("hunter.pets missing: Missing, no data", HubModules.StateOf(hub.Char("Forever-Newbie"), "hunter.pets") == HubModState.Missing && HubModules.PetsOf(hub.Char("Forever-Newbie")) == null && HubModules.PetsOf(null) == null);
                bool petJunkOk = true; HubPetsModule pj = null, pbig = null;
                try
                {
                    var pjHub = HubData.Parse("ElansHubData = { chars = { ['A-B'] = { mods = { ['hunter.pets'] = { v = 1, data = { active = 7, pets = { 5, { name = 5 }, { name = 'X', level = 'a', stable = -3, loyalty = 1e999, ic = 'q', abilities = 4 }, { name = 'Y', abilities = { 3, { id = {}, rank = 'x' }, { name = 'Z', rank = -2 } } } }, known = { 'x', a = {}, b = -1 } } } } } } }");
                    pj = HubModules.PetsOf(pjHub.Char("A-B"));
                    var sb = new StringBuilder("ElansHubData = { chars = { ['A-B'] = { mods = { ['hunter.pets'] = { v = 1, data = { pets = {");
                    for (int i = 1; i <= 60; i++)
                    {
                        sb.Append("['P" + i + "']={name='Pet" + i + "',stable=" + i + ",abilities={");
                        for (int j = 1; j <= 60; j++) sb.Append("{id='a" + j + "',rank=" + j + "},");
                        sb.Append("}},");
                    }
                    sb.Append("},known={");
                    for (int i = 1; i <= 150; i++) sb.Append("k" + i + "=" + i + ",");
                    sb.Append("}}}}}}}");
                    pbig = HubModules.PetsOf(HubData.Parse(sb.ToString()).Char("A-B"));
                }
                catch (Exception) { petJunkOk = false; }
                Check("hunter.pets: odd values (numbers for tables, text for numbers, 1e999, negative slot, unnamed rows) are skipped or zeroed without throwing",
                    petJunkOk && pj != null && pj.Active == null && pj.Pets.Count == 2 && pj.Pets[0].Name == "X" && pj.Pets[0].Level == 0 && pj.Pets[0].Stable == 0 && pj.Pets[0].Loyalty == 0 && pj.Pets[0].Ic == 0 && pj.Pets[0].Abilities.Count == 0
                    && pj.Pets[1].Abilities.Count == 1 && pj.Pets[1].Abilities[0].Name == "Z" && pj.Pets[1].Abilities[0].Rank == 0 && pj.Known.Count == 0, pj == null ? "null" : pj.Pets.Count + " pets");
                Check("hunter.pets: caps - at most 40 pets, 40 abilities per pet, 100 known abilities", petJunkOk && pbig != null && pbig.Pets.Count == HubModules.MaxPets && pbig.Pets.All(p => p.Abilities.Count == HubModules.MaxPetAbilities) && pbig.Known.Count == HubModules.MaxKnown, pbig == null ? "null" : pbig.Pets.Count + "/" + pbig.Known.Count);

                var gb = HubModules.GuildBankOf(hub);
                Check("guildbank v1 (account): guild, money, tab with name / icon / items", gb != null && gb.Guilds.Count == 1 && gb.Guilds[0].Key == "Forever-Test Guild" && gb.Guilds[0].Money == 999000 && gb.Guilds[0].Tabs.Count == 1 && gb.Guilds[0].Tabs[0].Name == "Consumables" && gb.Guilds[0].Tabs[0].S[1].C == 41 && gb.Guilds[0].Tabs[0].S[1].Name == "Minor Healing Potion");

                // ---- too new, per module
                var fut = hub.Char("Forever-Future");
                var tooNewOk = new[] { "bags", "bank", "gear", "talents", "professions" }.All(m => fut.Modules[m].TooNew && fut.Modules[m].Data == null && HubModules.StateOf(fut, m) == HubModState.TooNew);
                Check("v2 of bags / bank / gear / talents / professions: TooNew, no data exposed", tooNewOk);
                Check("v2 modules give no typed data (every accessor is null), the core next to them still reads", HubModules.BagsOf(fut) == null && HubModules.BankOf(fut) == null && HubModules.GearOf(fut) == null && HubModules.TalentsOf(fut) == null && HubModules.ProfessionsOf(fut) == null && fut.Core?.Level == 30);
                var gbNew = HubData.Parse(HubCharsLua(now, now).Replace("['guildbank']={v=1", "['guildbank']={v=2"));
                Check("guildbank v2 (account): TooNew, no data, StateOf says so", HubModules.StateOf(gbNew, "guildbank") == HubModState.TooNew && HubModules.GuildBankOf(gbNew) == null && gbNew.AccountModules["guildbank"].Data == null);
                Check("missing module is Missing, not TooNew; a character not in the Hub has none", HubModules.StateOf(hub.Char("Forever-Newbie"), "gear") == HubModState.Missing && HubModules.StateOf((HubChar)null, "bags") == HubModState.Missing && HubModules.StateOf(hub.Char("Forever-Elan"), "bags") == HubModState.Ok);
                var junk = HubData.Parse("ElansHubData = { chars = { ['A-B'] = { mods = { bags = { v = 1, data = { cont = { [0] = 5, [1] = { n = 'x', s = { a = 1, [2] = { i = -3 }, [3] = { i = 1e999, c = 'q' } } } }, links = 7 } },"
                    + " talents = { v = 1, data = { nodes = { 5, { x = 'a', y = {} } } } }, professions = { v = 1, data = { list = { 3, { skill = 4 } } } }, gear = { v = 1, data = { slots = { x = 1, [1] = { i = 5 } } } } } } } }");
                bool junkOk = true;
                try
                {
                    var jc = junk.Char("A-B");
                    junkOk = HubModules.BagsOf(jc) != null && HubModules.TalentsOf(jc).Nodes.Count == 1 && HubModules.ProfessionsOf(jc).List.Count == 0 && HubModules.GearOf(jc).Slots.Count == 1;
                }
                catch (Exception) { junkOk = false; }
                Check("odd values (strings where numbers go, negative ids, 1e999, non-table rows) are skipped without throwing", junkOk);

                // ---- merge: Hub primary, Bags fills in, newer wins, Realm-Name exact
                var legacy = InventoryReader.ParseAll(BagsLua(3));
                var merged = CharacterMerge.Merge(hub, legacy);
                var byKey = merged.Chars.ToDictionary(c => c.Key);
                Check("merge: 7 characters - 5 Hub (Elan, Measley, Other-Measley, Other-Elan, Newbie, Future = 6) + the Bags-only Hidden one", merged.Chars.Count == 7 && byKey.ContainsKey("Forever-Hidden") && byKey.ContainsKey("Other-Elan"), string.Join(",", merged.Chars.Select(c => c.Key)));
                var e = byKey["Forever-Elan"];
                Check("merge: Hub character takes its identity from core (level 60 not the Bags 25, Orc, male), keeps the Bags guild name when core has one", e.Level == 60 && e.RaceFile == "Orc" && e.Sex == 2 && e.Class == "HUNTER" && e.Guild == "Test Guild" && e.Core != null && e.Hub != null && e.Money == 123456789);
                Check("merge: Hub bags (a minute old) beat the Bags save (two minutes old) - source Elan's Hub, 3 containers", e.BagsFrom == CharacterMerge.FromHub && e.BagsCont.Select(c => c.BagId).SequenceEqual(new[] { 0, 1, 5 }) && e.BagsAt == now - 60);
                Check("merge: the Hub bank (an hour old) beats the Bags bank (3 days old)", e.BankFrom == CharacterMerge.FromHub && e.BankCont.Select(c => c.BagId).SequenceEqual(new[] { -1, 6 }) && e.BankAt == now - 3600);
                Check("merge: reagent container becomes a Reagent special bag (free space counted apart), others general", e.BagsCont.First(c => c.BagId == 5).Special?.Key == "reagent" && e.BagsCont.Where(c => c.BagId != 5).All(c => c.Special == null) && e.BankCont.All(c => c.Special == null));
                Check("merge: slot names from links, bag title from the bag item, backpack / bank titles", e.BagsCont[0].Title == "Backpack" && e.BagsCont[1].Title == "Linen Bag" && e.BankCont[0].Title == "Bank" && e.BagsCont[0].Slots[3].Name == "Iron Sword" && e.BagsCont[0].Slots[3].Quality == 3);
                Check("merge: worn items from the Hub gear (4), summed lists filled (search / old tooltips)", e.Equipped.Count == 4 && e.Equipped.Any(i => i.Name == "Epic Blade" && i.Quality == 4) && e.Bags.Single(i => i.Id == 100).Count == 35 && e.Bank.Single(i => i.Id == 100).Count == 10);
                var m = byKey["Forever-Measley"];
                Check("merge: bags module too new -> the Bags save fills in (source Elan's Bags), identity from the Hub core (Human, Lv 22)", m.BagsFrom == CharacterMerge.FromBags && m.Bags.Count == 2 && m.RaceFile == "Human" && m.Level == 22 && m.BankFrom == null && m.Bank == null);
                Check("merge: gear still comes from the Hub next to Bags bags (1 worn item from the Hub)", m.Equipped.Count == 1 && m.Equipped[0].Name == "Leather Cap");
                var hid = byKey["Forever-Hidden"];
                Check("merge: a character only the Bags addon knows stays as it was (hidden flag, source Elan's Bags, no Hub record)", hid.Hidden && hid.Hub == null && hid.Core == null && hid.BagsFrom == CharacterMerge.FromBags);
                var om = byKey["Other-Measley"];
                Check("merge: same name on another realm is its own character, never mixed (Other-Measley: Orc, 7 cloth; Forever-Measley: Human, 3 cloth)",
                    om.Realm == "Other" && om.RaceFile == "Orc" && om.Bags.Single().Count == 7 && m.Realm == "Forever" && m.Bags.Single(i => i.Id == 100).Count == 3);
                var oe = byKey["Other-Elan"];
                Check("merge: Elan on another realm gets none of Forever-Elan's bags or Bags-addon data", oe.BagsCont == null && oe.Bags.Count == 0 && oe.BagsFrom == null && oe.Level == 9 && oe.Class == "MAGE");
                var nj = CharacterMerge.Merge(HubData.Parse(HubCharsLua(now, now).Replace("['Forever-Measley']", "['Zone-Measley']")), legacy);
                Check("merge: no name-only join - a Hub character under another key is not joined to the Bags Measley (3 Measleys; the Bags one has no race)",
                    nj.Chars.Count(c => c.Name == "Measley") == 3 && nj.Chars.First(c => c.Key == "Forever-Measley").RaceFile == null && nj.Chars.First(c => c.Key == "Forever-Measley").BagsFrom == CharacterMerge.FromBags);
                var nb = byKey["Forever-Newbie"];
                Check("merge: a Hub character with core only is listed, with no bags and no bank", nb.BagsCont == null && nb.BagsFrom == null && nb.BankFrom == null && nb.Level == 5 && nb.RaceFile == "Gnome");
                var fu = byKey["Forever-Future"];
                Check("merge: modules too new and nothing else -> listed without data", fu.BagsFrom == null && fu.BagsCont == null && fu.Level == 30);
                // newer wins both ways
                var oldHub = CharacterMerge.Merge(HubData.Parse(HubCharsLua(now, now - 86400)), legacy);
                var e2 = oldHub.Chars.First(c => c.Key == "Forever-Elan");
                Check("merge: Hub bags older than the Bags save -> the Bags save wins (a day old vs two minutes old)", e2.BagsFrom == CharacterMerge.FromBags && e2.BagsCont.Count == 2 && e2.BagsAt == legacy.Chars.First(c => c.Name == "Elan").BagsAt);
                Check("merge: the identity still comes from the Hub when only the bags fall back", e2.Level == 60 && e2.BankFrom == CharacterMerge.FromHub);
                var hubOnly = CharacterMerge.Merge(HubData.Parse(HubCharsLua(now, now)), new InvData());
                Check("merge: no Bags addon at all (empty legacy) - Hub characters alone, 6", hubOnly.Chars.Count == 6 && hubOnly.Chars.All(c => c.Hub != null));
                var bagsOnly = CharacterMerge.Merge(new HubModel(), legacy);
                Check("merge: no Hub at all - the Bags characters exactly as before (3, all source Elan's Bags, not the same objects)", bagsOnly.Chars.Count == 3 && bagsOnly.Chars.All(c => c.Hub == null) && legacy.Chars.All(l => l.BagsFrom == null), string.Join(",", bagsOnly.Chars.Select(c => c.Key)));
                Check("merge: null inputs are fine", CharacterMerge.Merge(null, null).Chars.Count == 0);

                // ---- guild banks: per tab, the newer scan wins
                var g = merged.Guilds.Single();
                Check("guild merge: one guild, newer tab 1 from the Hub (41 potions), tab 2 'Mats' kept from the Bags save, 3 tabs known, Hub money", g.Tabs.Select(t => t.Index).SequenceEqual(new[] { 1, 2 }) && g.Tabs[0].Slots[1].Count == 41 && g.Tabs[1].Slots.Count == 2
                    && g.Meta.Count == 3 && g.NumTabs == 3 && g.Money == 999000);
                var lgOnly = CharacterMerge.Merge(HubData.Parse(HubCharsLua(now, now).Replace("['guildbank']={v=1", "['guildbank']={v=2")), legacy);
                Check("guild merge: guildbank too new -> the Bags guild bank is still shown", lgOnly.Guilds.Count == 1 && lgOnly.Guilds[0].Tabs.Count == 2 && lgOnly.Guilds[0].Tabs[0].Slots[1].Count == 40);

                // ---- conversion: the cross-character counts work from either source
                var index = InventoryReader.BuildIndex(merged.Chars.Where(c => !c.Hidden), merged.Guilds);
                var lines = InventoryReader.TipFor(index, 100, e).Lines();
                Check("counts: linen cloth - Elan from the Hub (bags 20 + reagent bag 15, bank 10), Measley from the Bags save, Other-Measley on its realm", lines.SequenceEqual(new[] { "This: Elan Bags 35 · Bank 10 · Total 45", "Other: Measley Bags 7", "Other: Measley Bags 3", "All: 55" }), string.Join(" | ", lines));
                lines = InventoryReader.TipFor(index, 109, e).Lines();
                Check("counts: the cloak - bank bag 1 + worn 1 (Hub gear) on Elan, guild bank 2 (Bags tab kept); Measley's Hub gear (a cap) replaces the Bags list of what she wears", lines.SequenceEqual(new[] { "This: Elan Bank 1 · Worn 1 · Total 2", "Guild: Test Guild 2", "All: 4" }), string.Join(" | ", lines));
                lines = InventoryReader.TipFor(index, 101, e).Lines();
                Check("counts: potions - Elan 5 in bags, the newer Hub guild tab (41)", lines.SequenceEqual(new[] { "This: Elan Bags 5", "Guild: Test Guild 41", "All: 46" }), string.Join(" | ", lines));
                Check("counts: the hidden character is not counted", InventoryReader.BuildIndex(merged.Chars.Where(c => !c.Hidden), merged.Guilds)[100].Chars.All(k => k.Char.Name != "Hidden"));
                var search = InventoryReader.Search(merged.Chars, "epic", null, merged.Guilds);
                Check("search finds Hub-worn items too (Epic Blade on Elan) and Bags-only ones (Epic Hammer on Measley)", search.Count == 2 && search.Any(r => r.Name == "Epic Blade" && r.Holdings.Any(h => h.Char.Key == "Forever-Elan" && h.Equipped > 0)));

                // ---- talents grouping
                var lay = TalentGrid.Group(tal.Nodes);
                Check("talents: 3 trees side by side by the big gaps in x, 3 columns x 3 rows each, 9 nodes each", lay.Trees.Count == 3 && lay.Trees.All(t => t.Cols == 3 && t.Rows == 3 && t.Cells.Sum(c => c.Nodes.Count) == 9), string.Join(";", lay.Trees.Select(t => t.Cols + "x" + t.Rows + "/" + t.Cells.Sum(c => c.Nodes.Count))));
                Check("talents: the bogus 102800 position and the node without one are loose, not drawn in a tree and not lost", lay.Loose.Count == 2 && lay.Loose.Any(n => n.Id == 900) && lay.Loose.Any(n => n.Id == 901) && lay.Trees.Sum(t => t.Cells.Sum(c => c.Nodes.Count)) + lay.Loose.Count == tal.Nodes.Count);
                Check("talents: trees run left to right (first tree has the x=1800 nodes), cells are ordered top-left to bottom-right", lay.Trees[0].Cells[0].Nodes.Any(n => n.X == 1800 && n.Y == 600) && lay.Trees[2].Cells.Last().Nodes.Any(n => n.X == 10200 && n.Y == 1800) && lay.Trees.Select(t => t.Index).SequenceEqual(new[] { 1, 2, 3 }));
                Check("talents: points per tree are the sum of ranks", lay.Trees.Sum(t => t.Points) == tal.Nodes.Where(n => n.HasPos && n.X < 40000).Sum(n => n.R));
                HubTalentNode N(double x, double y, int r = 1) => new HubTalentNode { Id = 1, R = r, M = 3, X = x, Y = y, HasPos = true };
                bool threw = false; TalentLayout l1 = null, l2 = null, l3 = null, l4 = null, l5 = null;
                try
                {
                    l1 = TalentGrid.Group(new[] { N(1800, 100), N(5400, 100), N(9000, 100) });                          // the real data's three x values only
                    l2 = TalentGrid.Group(null);
                    l3 = TalentGrid.Group(new[] { N(5, 5), N(5, 5), N(5, 5) });                                          // all on one spot
                    l4 = TalentGrid.Group(Enumerable.Range(0, 80).Select(i => N(i * 137.0, (i * 59) % 4000)));           // far more distinct positions than the grid has cells
                    l5 = TalentGrid.Group(new[] { N(double.NaN, 1), N(1, double.PositiveInfinity), N(-5, -5), N(1e12, 1), N(0, 0) });
                }
                catch (Exception) { threw = true; }
                Check("talents: odd inputs (three lone x values, null, everything on one spot, 80 scattered, NaN / infinity / huge) never throw", !threw);
                Check("talents: three lone x values stay in the grid; one spot is one cell with 3 nodes; 80 scattered fit 12 x 14 at most; NaN / infinity / 1e12 are loose",
                    !threw && l1.Trees.Sum(t => t.Cells.Count) == 3 && l2.Trees.Count == 0 && l3.Trees.Count == 1 && l3.Trees[0].Cells.Count == 1 && l3.Trees[0].Cells[0].Nodes.Count == 3
                    && l4.Trees.All(t => t.Cols <= TalentGrid.MaxCols && t.Rows <= TalentGrid.MaxRows) && l4.Trees.Sum(t => t.Cells.Sum(c => c.Nodes.Count)) == 80 && l5.Loose.Count == 3 && l5.Trees.Sum(t => t.Cells.Sum(c => c.Nodes.Count)) == 2);

                // ---- the page, fed by an ElansHub.lua and an ElansBags.lua together
                var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "CHARS", "SavedVariables");
                Directory.CreateDirectory(sv);
                Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
                var hubFile = Path.Combine(sv, "ElansHub.lua");
                File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), BagsLua(3), new UTF8Encoding(false));
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60), new UTF8Encoding(false));
                HubData.ResetCache();
                InventoryView.Init(() => root);
                TabInventory.IsChecked = true;
                for (int i = 0; i < 40 && InventoryView.CharCountForTest != 6; i++) await Task.Delay(200);
                Check("page lists the merged characters (6 visible, the hidden Bags one left out)", InventoryView.CharCountForTest == 6, InventoryView.CharCountForTest.ToString());
                Check("two watchers per SavedVariables folder: ElansBags.lua and ElansHub.lua", InventoryView.WatcherCountForTest == 2, InventoryView.WatcherCountForTest.ToString());
                Check("first look: the character saved last (Elan on Forever), Overview tab", InventoryView.SelectedKeyForTest == "Forever-Elan" && InventoryView.TabForTest == "overview", InventoryView.SelectedKeyForTest + " " + InventoryView.TabForTest);
                var rows = InventoryView.CharRowsForTest();
                Check("character list: name, level + class + realm, the two Elans told apart by the realm", rows.Count == 6 && rows[0].StartsWith("Elan|Lv 60 Hunter · Forever|") && rows[1].StartsWith("Elan|Lv 9 Mage · Other|") && rows[3].StartsWith("Measley|Lv 22 Paladin · Forever|") && rows[4].StartsWith("Measley|Lv 5 Warrior · Other|"), string.Join(" ; ", rows));
                Check("race portraits come from the Hub core (Elan: orc male = 236452; Other-Measley: orc female = 236451; Measley: human male = 236448)", rows[0].Split('|')[2] == "236452" && rows[4].Split('|')[2] == "236451" && rows[3].Split('|')[2] == "236448", string.Join(" ; ", rows));
                InventoryView.FilterForTest("Forever-Elan");
                InventoryView.SelectTabForTest("overview");
                var ov = InventoryView.CharTabLinesForTest();
                Check("overview: level, race, class", ov.Contains("identity|Level 60 Orc Hunter"), string.Join(" | ", ov));
                Check("overview: realm, faction, guild, zone", ov.Contains("where|Forever · Horde|Test Guild|Orgrimmar · Valley of Strength"), string.Join(" | ", ov));
                Check("overview: experience and rested", ov.Contains("xp|Experience " + 1500.ToString("N0") + " / " + 4500.ToString("N0") + " (33 %) · Rested 300"), string.Join(" | ", ov));
                Check("overview: gold", ov.Contains("gold|" + InventoryReader.Money(123456789)));
                Check("overview: free bag space counts general bags only (22 slots, 5 used; the reagent bag is apart), bank items, item level, talents, professions",
                    ov.Contains("stat|Bag space|17 free of 22") && ov.Contains("stat|Bank|13 items") && ov.Contains("stat|Item level|58.5") && ov.Contains("stat|Talents|35 spent · 2 unspent") && ov.Contains("stat|Professions|Mining, Herbalism, Cooking"), string.Join(" | ", ov));
                Check("overview: the age line names the source", InventoryView.ViewInfoForTest.StartsWith("Updated ") && InventoryView.ViewInfoForTest.EndsWith("Elan's Hub"), InventoryView.ViewInfoForTest);
                await Task.Delay(300);
                Snapshot(P("20-characters-overview.png"));

                InventoryView.SelectTabForTest("gear");
                var gl = InventoryView.CharTabLinesForTest();
                Check("gear: slots in paper-doll order (head, back, chest, main hand) with names and item levels", gl.Where(x => x.StartsWith("slot|")).SequenceEqual(new[] { "slot|1|Head|Helm of Testing|62", "slot|15|Back|White Cloak|10", "slot|5|Chest|Plain Chestguard|55", "slot|16|Main hand|Epic Blade|70" }), string.Join(" | ", gl));
                Check("gear: average item level and slot count in the age line, with the source", gl.Contains("gearinfo|58.5|4") && InventoryView.ViewInfoForTest.StartsWith("Average item level 58.5 · 4 items worn · Updated ") && InventoryView.ViewInfoForTest.EndsWith("Elan's Hub"), InventoryView.ViewInfoForTest);
                await Task.Delay(300);
                Snapshot(P("20-characters-gear.png"));

                InventoryView.SelectTabForTest("talents");
                var tl = InventoryView.CharTabLinesForTest();
                Check("talents: 35 spent, 2 unspent, 3 trees, 2 loose nodes", tl.Contains("talentinfo|35|2|3|2") && InventoryView.ViewInfoForTest.StartsWith("35 points spent · 2 unspent"), string.Join(" | ", tl));
                Check("talents: three 3x3 trees of 9 nodes each, the bogus and the position-less node drawn apart", tl.Count(x => x.StartsWith("tree|")) == 3 && tl.All(x => !x.StartsWith("tree|") || x.Contains("|3x3|")) && tl.Contains("loose|2"), string.Join(" | ", tl));
                await Task.Delay(300);
                Check("talents: a node with an icon id draws an icon image (letter tile underneath until it loads), the others keep the letter tile", InventoryView.TalentIconsForTest == 6 && InventoryView.TalentNodesForTest == 29, InventoryView.TalentIconsForTest + " icons of " + InventoryView.TalentNodesForTest);
                Snapshot(P("20-characters-talents.png"));

                InventoryView.SelectTabForTest("professions");
                var pl = InventoryView.CharTabLinesForTest();
                Check("professions: Mining 225 / 300, Herbalism 300 / 300, Cooking 75 / 150 (secondary)", pl.Where(x => x.StartsWith("prof|")).SequenceEqual(new[] { "prof|Mining|225|300|primary", "prof|Herbalism|300|300|primary", "prof|Cooking|75|150|secondary" }), string.Join(" | ", pl));
                await Task.Delay(300);
                Snapshot(P("20-characters-professions.png"));

                // the Pet tab: hunters only
                Check("Pet tab is shown for the hunter (classFile HUNTER)", InventoryView.PetTabVisibleForTest);
                InventoryView.SelectTabForTest("pet");
                var pt = InventoryView.CharTabLinesForTest();
                Check("pet: with you - Rex, Bear, level 40, Faithful, training points, abilities with ranks", pt.Contains("pet|with you|Rex|Bear|40|Faithful|Training points 30 / 80|Bite 4, Growl 3, Bogus"), string.Join(" | ", pt));
                Check("pet: stabled - Spot in slot 2, loyalty from its text, no training points", pt.Contains("pet|stable 2|Spot|Wolf|22|Faithful||Bite 2"), string.Join(" | ", pt));
                Check("pet: 'Your hunter can teach' lists the known abilities by name with ranks", pt.Contains("known|Bite 6, Claw 4, Faster Attack 2"), string.Join(" | ", pt));
                Check("pet: counts line (1 with you, 1 stabled, 3 known) and the age line names Hunter Helper", pt.Contains("petinfo|1|1|3") && InventoryView.ViewInfoForTest.StartsWith("2 pets · Updated ") && InventoryView.ViewInfoForTest.EndsWith("Hunter Helper"), InventoryView.ViewInfoForTest);
                await Task.Delay(300);
                Snapshot(P("20-characters-pet.png"));
                InventoryView.FilterForTest("Forever-Measley");
                Check("Pet tab is hidden for a Paladin, and the page falls back to Overview", !InventoryView.PetTabVisibleForTest && InventoryView.TabForTest == "overview", InventoryView.TabForTest);
                InventoryView.FilterForTest("Forever-Elan");
                Check("back on the hunter the tab is there again", InventoryView.PetTabVisibleForTest);

                InventoryView.SelectTabForTest("bags");
                var panels = InventoryView.PanelsForTest();
                Check("bags from the Hub: backpack 4/16, Linen Bag 1/6, reagent bag 2/8; the reagent bag has its chip, free space apart",
                    panels.SequenceEqual(new[] { "Backpack|4|16", "Linen Bag|1|6", "Light Leather Reagent Bag|2|8" }) && InventoryView.ChipsForTest().SequenceEqual(new[] { "Light Leather Reagent Bag|Reagents only" })
                    && InventoryView.ViewInfoForTest.StartsWith("17 free of 22 slots · Reagents 6 free") && InventoryView.ViewInfoForTest.EndsWith("Elan's Hub"), string.Join(",", panels) + " / " + InventoryView.ViewInfoForTest);
                await Task.Delay(300);
                Snapshot(P("20-characters-bags.png"));
                InventoryView.SelectTabForTest("bank");
                panels = InventoryView.PanelsForTest();
                Check("bank from the Hub: main bank 2/28 and Rare Satchel 1/8", panels.SequenceEqual(new[] { "Bank|2|28", "Rare Satchel|1|8" }), string.Join(",", panels));
                Check("guild bank tab is there (Hub guild bank + the Bags save's other tabs)", InventoryView.GuildTabVisibleForTest);
                InventoryView.SelectTabForTest("guild");
                var gt = InventoryView.GuildTabsForTest();
                Check("guild bank: Consumables (Hub), Mats (Bags save), Officers (never seen)", gt.Count == 3 && gt[0].StartsWith("1|Consumables|seen|") && gt[1].StartsWith("2|Mats|seen|") && gt[2].StartsWith("3|Officers|unseen|"), string.Join(" ; ", gt));
                Check("guild bank: the Hub's money in the footer", InventoryView.ViewInfoForTest.Contains(InventoryReader.Money(999000)), InventoryView.ViewInfoForTest);
                var tip = InventoryView.ItemTipForTest(100, "Forever-Elan");
                Check("tooltip counts across characters work on the merged list", tip.SequenceEqual(new[] { "This: Elan Bags 35 · Bank 10 · Total 45", "Other: Measley Bags 7", "Other: Measley Bags 3", "All: 55" }), string.Join(" | ", tip));
                InventoryView.SelectTabForTest("overview");

                // empty states: core only, and everything too new
                InventoryView.FilterForTest("Forever-Newbie");
                InventoryView.SelectTabForTest("gear");
                Check("gear, module missing: friendly line", InventoryView.CharTabLinesForTest().SequenceEqual(new[] { "empty|No gear saved for Newbie yet" }), string.Join(" | ", InventoryView.CharTabLinesForTest()));
                InventoryView.SelectTabForTest("talents");
                Check("talents, module missing", InventoryView.CharTabLinesForTest().SequenceEqual(new[] { "empty|No talents saved for Newbie yet" }));
                InventoryView.SelectTabForTest("professions");
                Check("professions, module missing", InventoryView.CharTabLinesForTest().SequenceEqual(new[] { "empty|No professions saved for Newbie yet" }));
                InventoryView.SelectTabForTest("bags");
                Check("bags, module missing: says to log in with Elan's Hub 1.12.0 or newer", (InventoryView.BagsMessageForTest ?? "").Contains("No bags saved for Newbie yet") && InventoryView.BagsMessageForTest.Contains("Elan's Hub 1.12.0 or newer"), InventoryView.BagsMessageForTest);
                InventoryView.SelectTabForTest("bank");
                Check("bank, never opened: says to visit a banker once", (InventoryView.BagsMessageForTest ?? "").Contains("has not been opened") && InventoryView.BagsMessageForTest.Contains("Visit a banker once"), InventoryView.BagsMessageForTest);
                InventoryView.FilterForTest("Forever-Future");
                foreach (var t in new[] { "gear", "talents", "professions" })
                {
                    InventoryView.SelectTabForTest(t);
                    Check(t + ", module too new: 'Update Elan's Outpost to show " + t + ".'", InventoryView.CharTabLinesForTest().SequenceEqual(new[] { "empty|Update Elan's Outpost to show " + t + "." }), string.Join(" | ", InventoryView.CharTabLinesForTest()));
                }
                InventoryView.SelectTabForTest("bags");
                Check("bags, module too new", InventoryView.BagsMessageForTest == "Update Elan's Outpost to show the bags.", InventoryView.BagsMessageForTest);
                InventoryView.SelectTabForTest("bank");
                Check("bank, module too new", InventoryView.BagsMessageForTest == "Update Elan's Outpost to show the bank.", InventoryView.BagsMessageForTest);
                InventoryView.SelectTabForTest("gear");
                await Task.Delay(300);
                Snapshot(P("20-characters-toonew.png"));
                InventoryView.SelectTabForTest("overview");
                Check("overview of a character whose other modules are too new still works (core v1)", InventoryView.CharTabLinesForTest().Contains("identity|Level 30 Dwarf Priest"), string.Join(" | ", InventoryView.CharTabLinesForTest()));

                // search over the merged list
                InventoryView.FilterForTest(null);
                InventoryView.SetSearchForTest("linen");
                var rs = InventoryView.ResultTextsForTest();
                Check("search 'linen' across Hub and Bags characters: 55 in all, Elan's split from the Hub", rs.Count == 1 && rs[0].Contains("× 55") && rs[0].Contains("Elan bags 35 · bank 10") && rs[0].Contains("Measley bags 7") && rs[0].Contains("Measley bags 3"), string.Join(" | ", rs));
                InventoryView.SetSearchForTest("");

                // two more hunters: one with no pets module yet (the empty state), one whose pets module is newer than this app (TooNew)
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60).Replace("class='Rogue',classFile='ROGUE'", "class='Hunter',classFile='HUNTER'").Replace("class='Priest',classFile='PRIEST'", "class='Hunter',classFile='HUNTER'"), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(hubFile, DateTime.UtcNow.AddSeconds(3));
                InventoryView.FilterForTest("Forever-Newbie");
                InventoryView.SelectTabForTest("pet");
                for (int i = 0; i < 40 && !InventoryView.PetTabVisibleForTest; i++) { await Task.Delay(200); InventoryView.FilterForTest("Forever-Newbie"); }
                Check("Pet tab shows for a hunter with no pets module too", InventoryView.PetTabVisibleForTest);
                InventoryView.SelectTabForTest("pet");
                Check("pet, module missing: 'No pets saved' (the hint says to log in with Hunter Helper 1.26.5)", InventoryView.CharTabLinesForTest().SequenceEqual(new[] { "empty|No pets saved for Newbie yet" }), string.Join(" | ", InventoryView.CharTabLinesForTest()));
                await Task.Delay(300);
                Snapshot(P("20-characters-pet-empty.png"));
                InventoryView.FilterForTest("Forever-Future");
                InventoryView.SelectTabForTest("pet");
                Check("pet, module too new: 'Update Elan's Outpost to show pets.'", InventoryView.CharTabLinesForTest().SequenceEqual(new[] { "empty|Update Elan's Outpost to show pets." }), string.Join(" | ", InventoryView.CharTabLinesForTest()));
                InventoryView.SelectTabForTest("overview");

                // the Hub file changes while the page is open: the page follows (watcher + debounce)
                InventoryView.FilterForTest("Forever-Elan");
                InventoryView.SelectTabForTest("overview");
                File.WriteAllText(hubFile, HubCharsLua(now, now - 60, "61"), new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(hubFile, DateTime.UtcNow.AddSeconds(5));
                for (int i = 0; i < 40 && !InventoryView.CharTabLinesForTest().Contains("identity|Level 61 Orc Hunter"); i++) await Task.Delay(200);
                Check("ElansHub.lua changed: Elan is level 61 within a moment", InventoryView.CharTabLinesForTest().Contains("identity|Level 61 Orc Hunter"), string.Join(" | ", InventoryView.CharTabLinesForTest()));

                // the Hub file goes away (an old setup): the Bags save alone is shown, as before
                File.Delete(hubFile);
                for (int i = 0; i < 40 && InventoryView.CharCountForTest != 2; i++) await Task.Delay(200);
                Check("without ElansHub.lua only the two visible Bags characters are left", InventoryView.CharCountForTest == 2, InventoryView.CharCountForTest.ToString());
                InventoryView.FilterForTest("Forever-Elan");
                InventoryView.SelectTabForTest("overview");
                ov = InventoryView.CharTabLinesForTest();
                Check("legacy-only overview: level / class from the Bags save, a note to log in with the Hub, no experience bar", ov.Contains("identity|Level 25 Hunter") && ov.Contains("note|no core") && !ov.Any(x => x.StartsWith("xp|")), string.Join(" | ", ov));
                Check("legacy-only overview: the age line names Elan's Bags", InventoryView.ViewInfoForTest.EndsWith("Elan's Bags"), InventoryView.ViewInfoForTest);
                await Task.Delay(300);
                Snapshot(P("20-characters-legacy-overview.png"));
                InventoryView.SelectTabForTest("gear");
                Check("legacy-only gear tab: friendly empty state", InventoryView.CharTabLinesForTest().SequenceEqual(new[] { "empty|No gear saved for Elan yet" }));
                InventoryView.SelectTabForTest("bags");
                Check("legacy-only bags tab as before: backpack 4/16 + Linen Bag 2/6, source Elan's Bags", InventoryView.PanelsForTest().SequenceEqual(new[] { "Backpack|4|16", "Linen Bag|2|6" }) && InventoryView.ViewInfoForTest.EndsWith("Elan's Bags"), InventoryView.ViewInfoForTest);
                Check("the Outpost wrote nothing into the WoW folder (only the test's own ElansBags.lua is left)", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)).SequenceEqual(new[] { "ElansBags.lua" }));
            }
            catch (Exception ex) { ok = false; notes.Add("FAIL exception: " + ex); }
            finally
            {
                HubData.ResetCache();
                IconStore.ForgetMemory();
                InventoryView.ResetViewForTest();
                InventoryView.Init(() => settings.WowRoot);
                ShowTab("addons");
            }
            notes.Insert(0, "characters (2.46.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
