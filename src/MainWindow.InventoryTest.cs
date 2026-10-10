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
        // WoW writes SavedVariables like this (tabs, ["key"] = value, "-- [n]" comments). Elan has the 0.2.0 layout + icons + a guild bank,
        // Measley was saved by 0.1.x (summed lists only), Hidden is hidden.
        static string BagsLua(int measleyCloth)
        {
            string Item(int i, int id, int c, int q, string name, string color, int ic = 0) =>
                "\t\t\t\t{\n\t\t\t\t\t[\"i\"] = " + id + ",\n\t\t\t\t\t[\"c\"] = " + c + ",\n\t\t\t\t\t[\"q\"] = " + q + ",\n" + (ic > 0 ? "\t\t\t\t\t[\"ic\"] = " + ic + ",\n" : "") + "\t\t\t\t\t[\"l\"] = \"|cff" + color + "|Hitem:" + id + "::::::::|h[" + name + "]|h|r\",\n\t\t\t\t}, -- [" + i + "]\n";
            string Slot(int slot, int id, int c, int q, int ic) =>
                "\t\t\t\t\t\t[" + slot + "] = {\n\t\t\t\t\t\t\t[\"i\"] = " + id + ",\n\t\t\t\t\t\t\t[\"c\"] = " + c + ",\n\t\t\t\t\t\t\t[\"q\"] = " + q + ",\n" + (ic > 0 ? "\t\t\t\t\t\t\t[\"ic\"] = " + ic + ",\n" : "") + "\t\t\t\t\t\t},\n";
            string Cont(string bag, int size, string slots, int bagId = 0, int bagIc = 0, string bagLink = null, int fam = 0) =>
                "\t\t\t\t[\"" + bag + "\"] = {\n\t\t\t\t\t[\"n\"] = " + size + ",\n" + (fam > 0 ? "\t\t\t\t\t[\"k\"] = " + fam + ",\n" : "")
                + (bagId > 0 ? "\t\t\t\t\t[\"id\"] = " + bagId + ",\n\t\t\t\t\t[\"ic\"] = " + bagIc + ",\n\t\t\t\t\t[\"q\"] = 2,\n\t\t\t\t\t[\"l\"] = \"" + bagLink + "\",\n" : "")
                + "\t\t\t\t\t[\"s\"] = {\n" + slots + "\t\t\t\t\t},\n\t\t\t\t},\n";
            string Char(string key, string name, string cls, int level, string faction, long money, long updated, long bankAt, string bags, string bank, string equipped, bool hidden = false, string extra = "") =>
                "\t\t[\"" + key + "\"] = {\n\t\t\t[\"name\"] = \"" + name + "\",\n\t\t\t[\"realm\"] = \"Forever\",\n\t\t\t[\"class\"] = \"" + cls + "\",\n\t\t\t[\"level\"] = " + level + ",\n\t\t\t[\"faction\"] = \"" + faction + "\",\n\t\t\t[\"money\"] = " + money + ",\n\t\t\t[\"updated\"] = " + updated + ",\n\t\t\t[\"bagsAt\"] = " + updated + ",\n"
                + (bankAt > 0 ? "\t\t\t[\"bankAt\"] = " + bankAt + ",\n" : "") + (hidden ? "\t\t\t[\"hidden\"] = true,\n" : "") + extra
                + "\t\t\t[\"bags\"] = {\n" + bags + "\t\t\t},\n" + (bank != null ? "\t\t\t[\"bank\"] = {\n" + bank + "\t\t\t},\n" : "") + "\t\t\t[\"equipped\"] = {\n" + equipped + "\t\t\t},\n\t\t},\n";
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const int IcCloth = 132593, IcPotion = 134328, IcSword = 133633, IcArrow = 134400, IcGem = 133784, IcCloak = 133003;
            // the 0.2.0 layout of Elan: backpack 16 (4 filled), a 6 slot bag (2 filled), bank 28 (2 filled) + one bank bag
            var elanExtra = "\t\t\t[\"guild\"] = \"Test Guild\",\n"
                + "\t\t\t[\"bagsCont\"] = {\n"
                + Cont("0", 16, Slot(1, 101, 5, 1, IcPotion) + Slot(2, 100, 20, 1, IcCloth) + Slot(3, 102, 1, 3, IcSword) + Slot(16, 107, 2, 2, IcGem))
                + Cont("1", 6, Slot(1, 106, 800, 1, IcArrow) + Slot(4, 106, 20, 1, 0), 3000, IcSword, "|cff1eff00|Hitem:3000::::::::|h[Linen Bag]|h|r", 2)
                + "\t\t\t},\n"
                + "\t\t\t[\"bankCont\"] = {\n"
                + Cont("-1", 28, Slot(1, 100, 10, 1, IcCloth) + Slot(7, 107, 2, 2, IcGem))
                + Cont("5", 8, Slot(2, 109, 1, 1, IcCloak), 3001, IcCloth, "|cff0070dd|Hitem:3001::::::::|h[Rare Satchel]|h|r")
                + "\t\t\t},\n";
            var sb = new StringBuilder("ElansBagsDB = {\n\t[\"replaceBags\"] = true,\n\t[\"view\"] = \"all\",\n"
                + "\t[\"client\"] = {\n\t\t[\"build\"] = \"1.60.1\",\n\t\t[\"guildBank\"] = {\n\t\t\t[\"GetGuildBankItemInfo\"] = true,\n\t\t\t[\"C_GuildBank\"] = false,\n\t\t},\n\t},\n"
                + "\t[\"guilds\"] = {\n\t\t[\"Forever-Test Guild\"] = {\n\t\t\t[\"name\"] = \"Test Guild\",\n\t\t\t[\"realm\"] = \"Forever\",\n\t\t\t[\"money\"] = 1234567,\n\t\t\t[\"updated\"] = " + (now - 600) + ",\n\t\t\t[\"numTabs\"] = 3,\n\t\t\t[\"tabs\"] = {\n"
                + "\t\t\t\t[\"1\"] = {\n\t\t\t\t\t[\"name\"] = \"Consumables\",\n\t\t\t\t\t[\"icon\"] = " + IcPotion + ",\n\t\t\t\t\t[\"at\"] = " + (now - 600) + ",\n\t\t\t\t\t[\"n\"] = 98,\n\t\t\t\t\t[\"s\"] = {\n"
                + "\t\t\t\t\t\t[1] = { [\"i\"] = 101, [\"c\"] = 40, [\"q\"] = 1, [\"ic\"] = " + IcPotion + ", [\"l\"] = \"|cffffffff|Hitem:101::::::::|h[Minor Healing Potion]|h|r\" },\n"
                + "\t\t\t\t\t\t[15] = { [\"i\"] = 102, [\"c\"] = 1, [\"q\"] = 3, [\"ic\"] = " + IcSword + ", [\"l\"] = \"|cff0070dd|Hitem:102::::::::|h[Iron Sword]|h|r\" },\n"
                + "\t\t\t\t\t},\n\t\t\t\t},\n\t\t\t},\n\t\t},\n\t},\n\t[\"chars\"] = {\n");
            sb.Append(Char("Forever-Elan", "Elan", "HUNTER", 25, "Horde", 123456, now - 120, now - 3 * 86400,
                Item(1, 100, 20, 1, "Linen Cloth", "ffffff", IcCloth) + Item(2, 101, 5, 1, "Minor Healing Potion", "ffffff", IcPotion) + Item(3, 102, 1, 3, "Iron Sword", "0070dd", IcSword) + Item(4, 106, 820, 1, "Rough Arrow", "ffffff", IcArrow) + Item(5, 107, 2, 2, "Bright Gem", "1eff00", IcGem),
                Item(1, 100, 10, 1, "Linen Cloth", "ffffff", IcCloth) + Item(2, 107, 2, 2, "Bright Gem", "1eff00", IcGem) + Item(3, 109, 1, 1, "White Cloak", "ffffff", IcCloak),
                Item(1, 109, 1, 1, "White Cloak", "ffffff", IcCloak), false, elanExtra));
            sb.Append(Char("Forever-Measley", "Measley", "PALADIN", 22, "Alliance", 2503000, now - 7200, 0,
                Item(1, 100, measleyCloth, 1, "Linen Cloth", "ffffff") + Item(2, 108, 1, 4, "Epic Hammer of Testing", "a335ee"),
                null,
                Item(1, 109, 1, 1, "White Cloak", "ffffff")));
            sb.Append(Char("Forever-Hidden", "Hidden", "MAGE", 10, "Horde", 5, now - 99999, 0, Item(1, 100, 99, 1, "Linen Cloth", "ffffff"), null, "", true));
            sb.Append("\t},\n}\n");
            return sb.ToString();
        }

        // a small solid PNG standing in for an extracted icon (the colour depends on the id)
        static byte[] TestIconPng(int id)
        {
            var px = new byte[64 * 64 * 4];
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    int o = (y * 64 + x) * 4;
                    bool edge = x < 3 || y < 3 || x > 60 || y > 60;
                    px[o] = (byte)(60 + id % 160); px[o + 1] = (byte)(90 + (id / 7) % 140); px[o + 2] = (byte)(50 + (id / 13) % 180); px[o + 3] = 255;
                    if (edge) { px[o] = px[o + 1] = px[o + 2] = 20; }
                    else if (x + y > 70 && x + y < 80) { px[o] = px[o + 1] = px[o + 2] = 235; }
                }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, 64 * 4);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        static string DirSignature(string root) => string.Join("|", Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f)
            .Select(f => f + "@" + new FileInfo(f).LastWriteTimeUtc.Ticks + ":" + new FileInfo(f).Length));

        // 2.22.0/2.23.0: Inventory page + the Elan's Bags card, from a synthetic ElansBags.lua in a fake WoW folder
        async Task<string> InventoryTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
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

                // ---- the in-game look: one character, Bags / Bank / Guild bank tabs
                Check("default view: the character saved last (Elan), Bags tab, tab strip shown", InventoryView.SelectedKeyForTest == "Forever-Elan" && InventoryView.TitleForTest == "Elan" && InventoryView.TabStripVisibleForTest);
                var panels = InventoryView.PanelsForTest();
                Check("bags tab: backpack 4 of 16 used + Linen Bag 2 of 6", panels.SequenceEqual(new[] { "Backpack|4|16", "Linen Bag|2|6" }));
                Check("every slot is drawn (22), 6 hold items", InventoryView.SlotsDrawnForTest == 22 && InventoryView.SlotsFilledForTest == 6);
                Check("free-slot info is shown", InventoryView.ViewInfoForTest.StartsWith("16 free of 22 slots"));
                // the fake WoW folder has no game data: icons cannot be read, the page says so and shows coloured tiles
                for (int i = 0; i < 40 && InventoryView.IconNoteForTest == null; i++) await Task.Delay(100);
                Check("no icons available: placeholders for every item + a note", InventoryView.SlotsWithImageForTest == 0 && InventoryView.SlotsPlaceholderForTest == 6 && (InventoryView.IconNoteForTest ?? "").Contains("not available"));
                await Task.Delay(200);
                Snapshot(P("17-inventory-bags-placeholders.png"));
                // icons that are already cached on disk are used at once
                Directory.CreateDirectory(IconStore.Dir);
                foreach (var ic in new[] { 134400, 133633, 132593, 134328, 133784, 133003 }) File.WriteAllBytes(IconStore.PathFor(ic), TestIconPng(ic));
                IconStore.ForgetMemory();
                InventoryView.RefreshIconsForTest();
                Check("cached icons show as images (all 6; the slot saved without an icon id takes it from the summed list)", InventoryView.SlotsWithImageForTest == 6 && InventoryView.SlotsPlaceholderForTest == 0, InventoryView.SlotsWithImageForTest + "/" + InventoryView.SlotsPlaceholderForTest);
                await Task.Delay(200);
                Snapshot(P("17-inventory-bags.png"));
                InventoryView.SelectTabForTest("bank");
                panels = InventoryView.PanelsForTest();
                Check("bank tab: main bank 2 of 28 + Rare Satchel 1 of 8", panels.SequenceEqual(new[] { "Bank|2|28", "Rare Satchel|1|8" }));
                await Task.Delay(200);
                Snapshot(P("17-inventory-bank.png"));
                Check("guild bank tab appears for a character whose guild bank was saved", InventoryView.GuildTabVisibleForTest);
                InventoryView.SelectTabForTest("guild");
                panels = InventoryView.PanelsForTest();
                Check("guild tab: Consumables, 2 of 98", panels.SequenceEqual(new[] { "Consumables|2|98" }));
                await Task.Delay(200);
                Snapshot(P("17-inventory-guild.png"));
                // a character saved by the older addon: one pile, bank never seen, no guild tab
                InventoryView.FilterForTest("Forever-Measley");
                Check("old data: guild tab hidden, falls back to the Bags tab", !InventoryView.GuildTabVisibleForTest);
                panels = InventoryView.PanelsForTest();
                Check("old data without layout: shown as one pile with a note to update", panels.Count == 1 && panels[0].StartsWith("Items|2|") && (InventoryView.IconNoteForTest ?? "").Contains("0.2.0"));
                await Task.Delay(200);
                Snapshot(P("17-inventory-oldaddon.png"));
                InventoryView.SelectTabForTest("bank");
                Check("bank never opened: says so", (InventoryView.BagsMessageForTest ?? "").Contains("has not been opened"));
                InventoryView.SelectTabForTest("bags");
                InventoryView.FilterForTest("Forever-Elan");
                InventoryView.SetSearchForTest("linen");
                Check("typing a search switches to results (tab strip hidden)", !InventoryView.TabStripVisibleForTest && InventoryView.ResultCountForTest == 1);
                InventoryView.SetSearchForTest("");
                Check("clearing the search brings the bags back", InventoryView.TabStripVisibleForTest);
                InventoryView.FilterForTest(null);

                // opt-in (ELANSHUB_TEST_REALICONS=1): read the real icons from the local game data, cached in the test data folder
                if (Environment.GetEnvironmentVariable("ELANSHUB_TEST_REALICONS") == "1")
                {
                    var real = WowLocator.Find();
                    foreach (var ic in new[] { 134400, 133633, 132593, 134328, 133784, 133003 }) { try { File.Delete(IconStore.PathFor(ic)); } catch { } }
                    IconStore.ForgetMemory();
                    IconStore.WowRoot = () => real;
                    InventoryView.FilterForTest("Forever-Elan");
                    InventoryView.SelectTabForTest("bags");
                    for (int i = 0; i < 80 && InventoryView.SlotsWithImageForTest < 6; i++) await Task.Delay(250);
                    Check("real icons: extracted from the local game data and cached as PNG", InventoryView.SlotsWithImageForTest == 6 && File.Exists(IconStore.PathFor(134400)) && IconStore.Problem == null, InventoryView.SlotsWithImageForTest + " " + IconStore.Problem);
                    await Task.Delay(300);
                    Snapshot(P("17-inventory-realicons.png"));
                    InventoryView.SelectTabForTest("bank");
                    await Task.Delay(1200);
                    Snapshot(P("17-inventory-realicons-bank.png"));
                    Check("real icons: second read comes from the disk cache", IconStore.TryGet(134400) != null);
                    InventoryView.SelectTabForTest("bags");
                    IconStore.WowRoot = () => root;
                    InventoryView.FilterForTest(null);
                }

                InventoryView.SetSearchForTest("");
                Check("no search: every item is listed", InventoryView.ResultCountForTest == 7);
                InventoryView.SetSearchForTest("linen");
                var r = InventoryView.ResultTextsForTest();
                Check("search 'linen': one result", r.Count == 1);
                Check("grouped per item with per-character counts and locations", r.Count == 1 && r[0].Contains("× 33") && r[0].Contains("Elan bags 20 · bank 10") && r[0].Contains("Measley bags 3"));
                Check("hidden character's items are not counted", r.Count == 1 && !r[0].Contains("Hidden") && !r[0].Contains("× 132"));
                InventoryView.SetSearchForTest("cloak");
                r = InventoryView.ResultTextsForTest();
                Check("worn items are searched too (both characters wear the cloak)", r.Count == 1 && r[0].Contains("Elan bank 1 · worn") && r[0].Contains("Measley worn"));
                InventoryView.SetSearchForTest("epic");
                r = InventoryView.ResultTextsForTest();
                Check("quality word search finds the epic on the paladin", r.Count == 1 && r[0].Contains("Epic Hammer") && r[0].Contains("Measley bags 1"));
                InventoryView.SetSearchForTest("gem");
                r = InventoryView.ResultTextsForTest();
                Check("bank-only item shows its location", r.Count == 1 && r[0].Contains("Elan bags 2 · bank 2"));
                InventoryView.SetSearchForTest("nothing like this");
                Check("no match is handled", InventoryView.ResultCountForTest == 0);
                InventoryView.SetSearchForTest("");
                InventoryView.ResultsForTest("Forever-Measley");
                Check("a character's search results are limited to it", InventoryView.ResultCountForTest == 3);
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
                IconStore.ForgetMemory();
                InventoryView.Init(() => settings.WowRoot);
                ShowTab("addons");
            }
            notes.Insert(0, "inventory (2.23.0): " + (ok ? "all ok" : "FAILURES"));
            return string.Join("\r\n", notes);
        }
    }
}
