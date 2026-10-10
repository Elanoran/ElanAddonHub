using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    public partial class MainWindow
    {
        // WoW writes SavedVariables like this (tabs, ["key"] = value, "-- [n]" comments)
        static string BagsLua(int measleyCloth)
        {
            string Item(int i, int id, int c, int q, string name, string color) =>
                "\t\t\t\t{\n\t\t\t\t\t[\"i\"] = " + id + ",\n\t\t\t\t\t[\"c\"] = " + c + ",\n\t\t\t\t\t[\"q\"] = " + q + ",\n\t\t\t\t\t[\"l\"] = \"|cff" + color + "|Hitem:" + id + "::::::::|h[" + name + "]|h|r\",\n\t\t\t\t}, -- [" + i + "]\n";
            string Char(string key, string name, string cls, int level, string faction, long money, long updated, long bankAt, string bags, string bank, string equipped, bool hidden = false) =>
                "\t\t[\"" + key + "\"] = {\n\t\t\t[\"name\"] = \"" + name + "\",\n\t\t\t[\"realm\"] = \"Forever\",\n\t\t\t[\"class\"] = \"" + cls + "\",\n\t\t\t[\"level\"] = " + level + ",\n\t\t\t[\"faction\"] = \"" + faction + "\",\n\t\t\t[\"money\"] = " + money + ",\n\t\t\t[\"updated\"] = " + updated + ",\n\t\t\t[\"bagsAt\"] = " + updated + ",\n"
                + (bankAt > 0 ? "\t\t\t[\"bankAt\"] = " + bankAt + ",\n" : "") + (hidden ? "\t\t\t[\"hidden\"] = true,\n" : "")
                + "\t\t\t[\"bags\"] = {\n" + bags + "\t\t\t},\n" + (bank != null ? "\t\t\t[\"bank\"] = {\n" + bank + "\t\t\t},\n" : "") + "\t\t\t[\"equipped\"] = {\n" + equipped + "\t\t\t},\n\t\t},\n";
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sb = new StringBuilder("ElansBagsDB = {\n\t[\"replaceBags\"] = true,\n\t[\"view\"] = \"all\",\n\t[\"chars\"] = {\n");
            sb.Append(Char("Forever-Elan", "Elan", "HUNTER", 25, "Horde", 123456, now - 120, now - 3 * 86400,
                Item(1, 100, 20, 1, "Linen Cloth", "ffffff") + Item(2, 101, 5, 1, "Minor Healing Potion", "ffffff") + Item(3, 102, 1, 3, "Iron Sword", "0070dd") + Item(4, 106, 800, 1, "Rough Arrow", "ffffff"),
                Item(1, 100, 10, 1, "Linen Cloth", "ffffff") + Item(2, 107, 2, 2, "Bright Gem", "1eff00"),
                Item(1, 109, 1, 1, "White Cloak", "ffffff")));
            sb.Append(Char("Forever-Measley", "Measley", "PALADIN", 22, "Alliance", 2503000, now - 7200, 0,
                Item(1, 100, measleyCloth, 1, "Linen Cloth", "ffffff") + Item(2, 108, 1, 4, "Epic Hammer of Testing", "a335ee"),
                null,
                Item(1, 109, 1, 1, "White Cloak", "ffffff")));
            sb.Append(Char("Forever-Hidden", "Hidden", "MAGE", 10, "Horde", 5, now - 99999, 0, Item(1, 100, 99, 1, "Linen Cloth", "ffffff"), null, "", true));
            sb.Append("\t},\n}\n");
            return sb.ToString();
        }

        static string DirSignature(string root) => string.Join("|", Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f)
            .Select(f => f + "@" + new FileInfo(f).LastWriteTimeUtc.Ticks + ":" + new FileInfo(f).Length));

        // 2.22.0: Inventory page + the Elan's Bags card, from a synthetic ElansBags.lua in a fake WoW folder
        async Task<string> InventoryTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond) { notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            try
            {
                var root = Path.Combine(dir, "invwow");
                if (Directory.Exists(root)) Directory.Delete(root, true);
                var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "TESTACC", "SavedVariables");
                Directory.CreateDirectory(sv);
                Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
                var file = Path.Combine(sv, "ElansBags.lua");

                InventoryView.Init(() => root);
                TabInventory.IsChecked = true;
                await Task.Delay(400);
                Check("rail item opens the Inventory page", InventoryView.IsVisible && !AddonsPage.IsVisible);
                Check("empty state when the addon saved nothing yet", InventoryView.EmptyTitleForTest == "No inventory data yet" && InventoryView.EmptyTextForTest.Contains("Install Elan's Bags and log in once"));
                Snapshot(P("17-inventory-empty.png"));
                Check("a watcher is set up on the SavedVariables folder", InventoryView.WatcherCountForTest == 1);

                // WoW writes the file: the page notices (FileSystemWatcher + debounce)
                File.WriteAllText(file, BagsLua(3), new UTF8Encoding(false));
                for (int i = 0; i < 30 && InventoryView.CharCountForTest != 2; i++) await Task.Delay(200);
                Check("page re-reads when the file appears (2 visible characters, hidden one left out)", InventoryView.CharCountForTest == 2);
                await Task.Delay(200);
                Snapshot(P("17-inventory.png"));

                InventoryView.SetSearchForTest("");
                Check("no search: every item is listed", InventoryView.ResultCountForTest == 7);
                InventoryView.SetSearchForTest("linen");
                var r = InventoryView.ResultTextsForTest();
                Check("search 'linen': one result", r.Count == 1);
                Check("grouped per item with per-character counts and locations", r.Count == 1 && r[0].Contains("× 33") && r[0].Contains("Elan bags 20 · bank 10") && r[0].Contains("Measley bags 3"));
                Check("hidden character's items are not counted", r.Count == 1 && !r[0].Contains("Hidden") && !r[0].Contains("× 132"));
                InventoryView.SetSearchForTest("cloak");
                r = InventoryView.ResultTextsForTest();
                Check("worn items are searched too (both characters wear the cloak)", r.Count == 1 && r[0].Contains("Elan worn") && r[0].Contains("Measley worn"));
                InventoryView.SetSearchForTest("epic");
                r = InventoryView.ResultTextsForTest();
                Check("quality word search finds the epic on the paladin", r.Count == 1 && r[0].Contains("Epic Hammer") && r[0].Contains("Measley bags 1"));
                InventoryView.SetSearchForTest("gem");
                r = InventoryView.ResultTextsForTest();
                Check("bank-only item shows its location", r.Count == 1 && r[0].Contains("Elan bank 2"));
                InventoryView.SetSearchForTest("nothing like this");
                Check("no match is handled", InventoryView.ResultCountForTest == 0);
                InventoryView.SetSearchForTest("");
                InventoryView.FilterForTest("Forever-Measley");
                Check("clicking a character filters to it", InventoryView.ResultCountForTest == 3);
                InventoryView.FilterForTest(null);
                InventoryView.SetSearchForTest("linen");
                await Task.Delay(300);
                Snapshot(P("17-inventory-search.png"));

                // the file changes while the page is open (WoW writes on /reload): the numbers follow
                File.WriteAllText(file, BagsLua(5), new UTF8Encoding(false));
                for (int i = 0; i < 30 && !InventoryView.ResultTextsForTest().Any(t => t.Contains("Measley bags 5")); i++) await Task.Delay(200);
                Check("page follows a changed file", InventoryView.ResultTextsForTest().Any(t => t.Contains("× 35") && t.Contains("Measley bags 5")));

                // a file WoW still has open for writing is readable (FileShare.ReadWrite) and nothing is ever written back
                var before = DirSignature(root);
                using (var lockedFs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    InventoryView.Reload();
                    Check("file held open by the game can still be read", InventoryView.ResultCountForTest == 1);
                }
                InventoryView.Reload();
                Check("the hub never writes to the WoW folder", DirSignature(root) == before);

                // the parser, directly
                var parsed = InventoryReader.ParseText(BagsLua(3));
                Check("parser: 3 characters, hidden flag, bank only where seen", parsed.Count == 3 && parsed.First(c => c.Name == "Hidden").Hidden
                    && parsed.First(c => c.Name == "Elan").Bank != null && parsed.First(c => c.Name == "Measley").Bank == null);
                Check("parser: names and qualities come from the stored links", parsed.First(c => c.Name == "Measley").Bags.Any(i => i.Name == "Epic Hammer of Testing" && i.Quality == 4));
                Check("parser: bad file does not throw", InventoryReader.ParseText("this is { not lua").Count == 0);
                Check("time wording", InventoryReader.Ago(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3 * 86400) == "3 days ago" && InventoryReader.Ago(0) == "never");

                // the Elan's Bags card (optional addon, no class restriction) next to the others
                manifest = new Manifest
                {
                    Hub = new HubInfo { Version = App.Version },
                    Addons = new List<AddonInfo>
                    {
                        new AddonInfo { Id = "ElansHunterHelper", Name = "Elan's Hunter Helper", Description = "The hunter toolkit for WoW Forever", Version = "1.23.0", Flavor = "_classic_beta_", FlavorName = "WoW Forever (Classic beta)", Folders = new List<string> { "ElansHunterHelper" }, Changelog = new List<ChangeEntry>() },
                        new AddonInfo { Id = "ElansPaladinHelper", Name = "Elan's Paladin Helper", Description = "Paladin helper", Version = "0.6.2", Flavor = "_classic_beta_", FlavorName = "WoW Forever (Classic beta)", Folders = new List<string> { "ElansPaladinHelper" }, Classes = new List<string> { "PALADIN" }, Changelog = new List<ChangeEntry>() },
                        new AddonInfo { Id = "ElansBags", Name = "Elan's Bags", Description = "One bag window with search, categories and junk seller, plus an inventory of all your characters", Version = "0.1.0", Flavor = "_classic_beta_", FlavorName = "WoW Forever (Classic beta)", Folders = new List<string> { "ElansBags" },
                            Changelog = new List<ChangeEntry> { new ChangeEntry { Version = "0.1.0", Text = "First version: one bag window, search, categories, junk seller, alt inventory." } } },
                    },
                };
                cards.Clear();
                RebuildCards();
                var bagsCard = cards.FirstOrDefault(c => c.Info.Id == "ElansBags");
                Check("Elan's Bags card exists, optional and not class bound", bagsCard != null && !bagsCard.Info.Required && (bagsCard.Info.Classes == null || bagsCard.Info.Classes.Count == 0) && !bagsCard.RecommendedForYou);
                Check("card uses the new vector icon", bagsCard != null && ReferenceEquals(bagsCard.Logo, System.Windows.Application.Current.TryFindResource("Icon.Bags")));
                Check("card offers Install", bagsCard != null && bagsCard.ButtonEnabled && bagsCard.State == CardState.NotInstalled);
                if (bagsCard != null) bagsCard.Expanded = true;
                ShowTab("addons");
                await Task.Delay(500);
                Snapshot(P("18-card-bags.png"));
                if (bagsCard != null) bagsCard.Expanded = false;
                cards.Clear();
                manifest = null;
            }
            catch (Exception e) { ok = false; notes.Add("FAIL exception: " + e); }
            finally
            {
                InventoryView.Init(() => settings.WowRoot);
                ShowTab("addons");
            }
            notes.Insert(0, "inventory (2.22.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
