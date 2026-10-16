using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;

namespace ElansAddonHub
{
    // --selftest "menucolors" (Outpost 2.71.1): the section colours of the secondary menus - the Atlas side navigation, the Lodge sidebar, the
    // Characters list and the Settings menu. The tokens (Brush.Section.<Name> + .Tint, Brush.Section.Panel: every hue at least as readable as
    // Text.Secondary on the menu fill), each group's label in its hue, the rows of a group carrying it, the selected row filled with its group's
    // tint and marked by the 4 px bar in its hue, the faint menu fill behind every secondary menu. Also the Atlas Home without a search box of
    // its own (the title bar box and Ctrl+K are the one way to search) and Ctrl+K opening the palette from Home. Pictures with ELANSHUB_SHOTS=1.
    public partial class MainWindow
    {
        static readonly string[] SectionNames = { "Database", "World", "Character", "Lodge", "Neutral", "Calendar", "Files", "Ladder", "Channels", "Voice", "People", "General", "Addons", "Profile", "Privacy", "Overlay", "GuildMaster", "About" };

        static double RelLum(Color c)
        {
            double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        static double ContrastOf(Color a, Color b) { double x = RelLum(a), y = RelLum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
        // a (possibly see-through) brush drawn over an opaque colour
        static Color Over(Brush brush, Color bg)
        {
            var b = (SolidColorBrush)brush;
            double a = b.Color.A / 255.0 * b.Opacity;
            byte M(byte f, byte g) => (byte)Math.Round(f * a + g * (1 - a));
            return Color.FromRgb(M(b.Color.R, bg.R), M(b.Color.G, bg.G), M(b.Color.B, bg.B));
        }
        static bool SameBrush(Brush x, Brush y) => x is SolidColorBrush a && y is SolidColorBrush b && a.Color == b.Color && Math.Abs(a.Opacity - b.Opacity) < 0.001;

        async Task<string> MenuColorsTest(string dir)
        {
            var notes = new List<string>();
            bool ok = true;
            void Check(string what, bool cond, string detail = null) { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what + (!cond && detail != null ? " [" + detail + "]" : "")); ok &= cond; }
            string P(string n) => Path.Combine(dir, n);
            Brush R(string key) => (Brush)FindResource(key);
            Color C(string key) => ((SolidColorBrush)FindResource(key)).Color;
            string Hex(Brush b) => b is SolidColorBrush s ? s.Color + "@" + s.Opacity.ToString("0.00") : (b == null ? "null" : b.GetType().Name);
            var w0 = Width; var h0 = Height;
            var realSettings = settings;
            try
            {
                // ============================================================================================================ the tokens
                var panel = C("Brush.Section.Panel");
                var secondary = ContrastOf(C("Brush.Text.Secondary"), panel);
                var weak = SectionNames.Where(n => ContrastOf(C("Brush.Section." + n), panel) < secondary - 0.005).ToList();
                Check("tokens: every section has a hue and a .Tint (the same colour at 16 %, like Brush.Accent.Tint)",
                    SectionNames.All(n => TryFindResource("Brush.Section." + n) is SolidColorBrush h && TryFindResource("Brush.Section." + n + ".Tint") is SolidColorBrush t && t.Color == h.Color && Math.Abs(t.Opacity - 0.16) < 0.005),
                    string.Join(",", SectionNames.Where(n => !(TryFindResource("Brush.Section." + n + ".Tint") is SolidColorBrush))));
                Check("tokens: every hue reads at least as well as Text.Secondary on the menu fill (" + secondary.ToString("0.0") + ":1)", weak.Count == 0, string.Join(",", weak.Select(n => n + " " + ContrastOf(C("Brush.Section." + n), panel).ToString("0.0"))));
                var worstSel = SectionNames.Min(n => ContrastOf(C("Brush.Text.Primary"), Over(R("Brush.Section." + n + ".Tint"), panel)));
                Check("tokens: a selected row's name (Text.Primary) on every section tint over the menu fill stays >= 7:1 (" + worstSel.ToString("0.0") + ":1)", worstSel >= 7);
                var bg = C("Brush.Panel"); var s1 = C("Brush.Surface1");
                Check("tokens: the menu fill is a few % lighter than the page panel and still darker than a card (Surface1)",
                    panel.R > bg.R && panel.G > bg.G && panel.B > bg.B && panel.R - bg.R <= 12 && RelLum(panel) < RelLum(s1), panel + " vs " + bg + " / " + s1);
                var taken = new[] { "Brush.Accent", "Brush.Success", "Brush.Warn", "Brush.Danger", "Brush.Info" }.Select(C).ToList();
                Check("tokens: no section hue is the accent or a status colour", SectionNames.All(n => !taken.Contains(C("Brush.Section." + n)) || n == "Neutral"));

                // ============================================================================================================ the Atlas side navigation
                AtlasView.UseNavSettings(new Settings(), () => { });
                AtlasView.History.Clear();
                Width = 1300; Height = 900;
                ShowTab("atlas"); await Settle();
                await AtlasView.Open("browse:items"); await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Settle();
                var groups = AtlasView.NavGroupsForTest.ToList();
                var known = groups.Where(g => AtlasNav.Groups.Contains(g)).ToList();
                Check("atlas: the nav shows its groups (at least Database and World)", known.Contains(AtlasNav.Database) && known.Contains(AtlasNav.World), string.Join(",", groups));
                var badLabel = known.Where(g => !SameBrush(AtlasView.NavGroupLabelForTest(g)?.Foreground, R("Brush.Section." + g))).ToList();
                Check("atlas: each group's label is in its section hue (Database teal, World green, Character amber, Lodge violet)", badLabel.Count == 0, string.Join(",", badLabel.Select(g => g + "=" + Hex(AtlasView.NavGroupLabelForTest(g)?.Foreground))));
                Check("atlas: Recent is a neutral section (label in Brush.Section.Neutral)", groups.Contains("Recent") && SameBrush(AtlasView.NavGroupLabelForTest("Recent").Foreground, R("Brush.Section.Neutral")), string.Join(",", groups));
                var rowGroups = AtlasNav.Entries().Where(e => AtlasView.NavRowForTest(e.Key) != null).ToList();
                var badRows = rowGroups.Where(e => AtlasView.NavRowSectionForTest(e.Key) != ElansAddonHub.AtlasPage.SectionBrushKey(e.Group)).Select(e => e.Key + ":" + AtlasView.NavRowSectionForTest(e.Key)).ToList();
                Check("atlas: every row carries its group's section (" + rowGroups.Count + " rows); Home and Data sources are neutral", rowGroups.Count >= 6 && badRows.Count == 0
                    && AtlasView.NavRowSectionForTest("home") == "Brush.Section.Neutral" && AtlasView.NavRowSectionForTest("data") == "Brush.Section.Neutral", string.Join(",", badRows));
                Check("atlas: there is no Search row in the nav any more (the title bar box and Ctrl+K are the search)", AtlasView.NavRowForTest("search") == null && !AtlasView.NavRowsForTest.Contains("Search"), string.Join(" | ", AtlasView.NavRowsForTest));
                // the selected row: its group's tint + the 4 px bar in its hue
                string SelectedProblem(ContentControl row, string section)
                {
                    if (row == null) return "no row";
                    row.ApplyTemplate();
                    var ind = row.Template?.FindName("ind", row) as Border;
                    if (!SameBrush(row.Background, R(section + ".Tint"))) return "fill " + Hex(row.Background);
                    if (!SameBrush(row.BorderBrush, R(section))) return "bar " + Hex(row.BorderBrush);
                    if (ind == null || ind.ActualWidth < 3.5 || ind.ActualHeight < 15 || !SameBrush(ind.Background, R(section))) return "bar not drawn " + (ind == null ? "null" : ind.ActualWidth + "x" + ind.ActualHeight);
                    return null;
                }
                var selProblems = new List<string>();
                async Task Sel(string id, string key, string group)
                {
                    await AtlasView.Open(id); await Settle();
                    var p = SelectedProblem(AtlasView.NavRowForTest(key), ElansAddonHub.AtlasPage.SectionBrushKey(group));
                    if (p != null) selProblems.Add(id + " -> " + p);
                    var others = AtlasView.NavSelectedForTest.Where(k => k != key).ToList();
                    if (others.Count > 0) selProblems.Add(id + " also selected: " + string.Join(",", others));
                }
                await Sel("browse:items", "items", AtlasNav.Database);
                await Sel("browse:zones", "zones", AtlasNav.World);
                if (AtlasView.NavRowForTest("factions") != null) await Sel("browse:factions", "factions", AtlasNav.Character);
                if (AtlasView.NavRowForTest(AtlasNav.FindsKey) != null) await Sel(AtlasNav.FindsId, AtlasNav.FindsKey, AtlasNav.Lodge);
                await Sel(AtlasNav.DataId, "data", "Neutral");
                Check("atlas: the selected row is filled with its group's tint and marked by the 4 px bar in its hue (Items teal, Zones green, Factions amber, Finds violet, Data sources neutral)", selProblems.Count == 0, string.Join(" ; ", selProblems));
                await AtlasView.Open("browse:items"); await Settle();
                var unselected = AtlasView.NavRowForTest("zones");
                Check("atlas: a row that is not selected has no fill and no bar", unselected != null && !(unselected.Background is SolidColorBrush ub && ub.Color.A > 0 && ub.Opacity > 0) && !(unselected.BorderBrush is SolidColorBrush ubb && ubb.Color.A > 0), Hex(unselected?.Background) + " " + Hex(unselected?.BorderBrush));
                var back = AtlasView.NavBackdropForTest as Border;
                Check("atlas: the nav sits on the faint menu fill (Brush.Section.Panel) as tall as the nav", back != null && back.IsVisible && SameBrush(back.Background, R("Brush.Section.Panel")) && back.ActualHeight >= AtlasView.NavScroller_ForTest.ActualHeight, back == null ? "null" : back.ActualHeight + " vs " + AtlasView.NavScroller_ForTest.ActualHeight);
                await ShotWait(300); Snapshot(P("menucolors-atlas-items.png"));

                // ---- Home: no search box of its own, the sections keep their 24
                await AtlasView.GoStart(); await AtlasView.StartFillForTest; await Settle();
                var homeTexts = AtlasTexts(AtlasView.BodyForTest);
                Check("atlas home: no search box of its own (no 'Search items, creatures ...' row, no 'Ctrl K' chip)", !homeTexts.Any(t => t.StartsWith("Search items")) && !homeTexts.Contains("Ctrl K"), string.Join(" | ", homeTexts.Take(12)));
                var col = ((Panel)AtlasView.BodyForTest).Children.OfType<StackPanel>().FirstOrDefault();
                var shown = col == null ? new List<FrameworkElement>() : col.Children.OfType<FrameworkElement>().Where(e => e.Visibility == Visibility.Visible && e.ActualHeight > 0).ToList();
                string gap = "";
                if (shown.Count >= 2)
                {
                    var firstY = shown[0].TranslatePoint(new Point(0, 0), col).Y;
                    var isBanner = shown[0] is Border;
                    var nextY = shown[1].TranslatePoint(new Point(0, 0), col).Y;
                    gap = isBanner ? (nextY - (firstY + shown[0].ActualHeight)).ToString("0") : firstY.ToString("0");
                    Check("atlas home: the page starts with its first section at the top (or, under a Classic banner, 24 below it: the space the search box left is closed)", isBanner ? Math.Abs(nextY - (firstY + shown[0].ActualHeight) - 24) < 1 : Math.Abs(firstY) < 1, gap);
                }
                else Check("atlas home: the page has its sections", false, shown.Count.ToString());
                Check("atlas home: Continue and Browse are still there, Continue first", homeTexts.Contains("Continue") && homeTexts.Contains("Browse") && homeTexts.IndexOf("Continue") < homeTexts.IndexOf("Browse"));
                Check("atlas home: the Home row is selected in the neutral section", SelectedProblem(AtlasView.NavRowForTest("home"), "Brush.Section.Neutral") == null, SelectedProblem(AtlasView.NavRowForTest("home"), "Brush.Section.Neutral"));
                await ShotWait(300); Snapshot(P("menucolors-atlas-home.png"));
                var savedMods = AtlasView.ModifiersForTest;
                try
                {
                    AtlasView.ModifiersForTest = ModifierKeys.Control;
                    RaiseKey(AtlasView, Key.K, Keyboard.PreviewKeyDownEvent);
                }
                finally { AtlasView.ModifiersForTest = savedMods; }
                await AtlasView.PaletteOpenWaitForTest(); await Settle();
                Check("atlas home: Ctrl+K opens the palette from Home with the search box focused", AtlasView.PaletteOpen && AtlasView.PaletteBoxForTest.IsKeyboardFocused && AtlasView.IsStart);
                AtlasView.ClosePalette();
                TitleSearchForTest.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, TitleSearchForTest, "w")) { RoutedEvent = UIElement.PreviewTextInputEvent, Source = TitleSearchForTest });
                await AtlasView.PaletteOpenWaitForTest(); await AtlasView.PaletteWaitForTest();
                Check("atlas home: typing in the title bar box opens the palette with that letter (the one search box)", AtlasView.PaletteOpen && AtlasView.PaletteTextForTest == "w");
                AtlasView.ClosePalette();
                AtlasView.UseNavSettings(realSettings, () => SettingsStore.Save(realSettings));

                // ============================================================================================================ Settings
                ShowTab("settings"); await Settle();
                var nav = new (RadioButton b, string section, string show)[]
                {
                    (SettingsPage.NavGeneral, "General", "general"), (SettingsPage.NavAddons, "Addons", "addons"), (SettingsPage.NavLodge, "Lodge", "lodge"), (SettingsPage.NavProfile, "Profile", "profile"),
                    (SettingsPage.NavPrivacy, "Privacy", "privacy"), (SettingsPage.NavVoice, "Voice", "voice"), (SettingsPage.NavOverlay, "Overlay", "overlay"), (SettingsPage.NavGm, "GuildMaster", "gm"), (SettingsPage.NavAbout, "About", "about"),
                };
                var setProblems = new List<string>();
                foreach (var n in nav)
                {
                    var key = "Brush.Section." + n.section;
                    if (!SameBrush(n.b.BorderBrush, R(key)) || !SameBrush(n.b.Background, R(key + ".Tint"))) { setProblems.Add(n.section + " carries " + Hex(n.b.BorderBrush) + "/" + Hex(n.b.Background)); continue; }
                    n.b.ApplyTemplate();
                    if (!(n.b.Template.FindName("ic", n.b) is TextBlock ic) || !SameBrush(ic.Foreground, R(key))) setProblems.Add(n.section + " icon");
                    if (n.b.Visibility != Visibility.Visible) continue;
                    SettingsPage.Show(n.show); await Settle();
                    var bd = n.b.Template.FindName("bd", n.b) as Border; var ind = n.b.Template.FindName("ind", n.b) as Border;
                    if (n.b.IsChecked != true || bd == null || !SameBrush(bd.Background, R(key + ".Tint")) || ind == null || ind.Visibility != Visibility.Visible || !SameBrush(ind.Background, R(key)))
                        setProblems.Add(n.section + " selected: fill " + Hex(bd?.Background) + " bar " + ind?.Visibility);
                    var other = nav.FirstOrDefault(o => o.b != n.b && o.b.Visibility == Visibility.Visible).b;
                    var oind = other?.Template?.FindName("ind", other) as Border;
                    if (oind != null && oind.Visibility == Visibility.Visible) setProblems.Add(n.section + ": another category shows its bar too");
                }
                Check("settings: every category has its own section hue - the icon in it, and when chosen its tint as the fill and the 4 px bar in its hue (only one at a time)", setProblems.Count == 0, string.Join(" ; ", setProblems));
                Check("settings: the categories use more than one hue (" + nav.Select(n => C("Brush.Section." + n.section)).Distinct().Count() + " different)", nav.Select(n => C("Brush.Section." + n.section)).Distinct().Count() >= 7);
                Check("settings: the menu sits on the faint menu fill (Brush.Section.Panel)", SameBrush(SettingsPage.NavBox.Background, R("Brush.Section.Panel")));
                SettingsPage.Show("general"); await ShotWait(300); Snapshot(P("menucolors-settings.png"));

                // ============================================================================================================ Characters
                var root = Path.Combine(dir, "menuwow");
                if (Directory.Exists(root)) Util.DeleteDir(root);
                var sv = Path.Combine(root, "_classic_beta_", "WTF", "Account", "TESTACC", "SavedVariables");
                Directory.CreateDirectory(sv);
                Directory.CreateDirectory(Path.Combine(root, "_classic_beta_", "Interface", "AddOns"));
                File.WriteAllText(Path.Combine(sv, "ElansBags.lua"), BagsLua(3), new UTF8Encoding(false));
                try
                {
                    InventoryView.Init(() => root);
                    ShowTab("inventory");
                    await Until(() => InventoryView.CharCountForTest == 2, 8000); await Settle();
                    var rows = AtlasWalk(InventoryView).OfType<RadioButton>().Where(r => r.DataContext is InventoryPage.CharRow).ToList();
                    var on = rows.FirstOrDefault(r => r.IsChecked == true);
                    if (on == null && rows.Count > 0)
                    {
                        // an earlier section may have left "All characters" chosen: pick a character the way a click does
                        rows[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, rows[0])); await Settle();
                        rows = AtlasWalk(InventoryView).OfType<RadioButton>().Where(r => r.DataContext is InventoryPage.CharRow).ToList();
                        on = rows.FirstOrDefault(r => r.IsChecked == true);
                    }
                    string charProblem = null;
                    if (on == null) charProblem = "no selected character (" + rows.Count + " rows)";
                    else
                    {
                        var cr = (InventoryPage.CharRow)on.DataContext;
                        on.ApplyTemplate();
                        var bd = on.Template.FindName("bd", on) as Border; var ind = on.Template.FindName("ind", on) as Border;
                        var cls = ((SolidColorBrush)cr.ClassBrush).Color;
                        if (!(bd?.Background is SolidColorBrush f) || f.Color != Color.FromArgb(0x29, cls.R, cls.G, cls.B)) charProblem = "fill " + Hex(bd?.Background) + " class " + cls;
                        else if (ind == null || ind.Visibility != Visibility.Visible || !(ind.Background is SolidColorBrush ib) || ib.Color != cls || ind.ActualWidth < 3.5) charProblem = "bar " + Hex(ind?.Background);
                        else if (rows.Any(r => r != on && r.Template?.FindName("ind", r) is Border oi && oi.Visibility == Visibility.Visible)) charProblem = "another row shows a bar";
                    }
                    Check("characters: the selected character is filled with its class colour at 16 % and marked by a 4 px bar in its class colour (the class is this list's section colour)", charProblem == null, charProblem);
                    Check("characters: the list sits on the faint menu fill (Brush.Section.Panel)", InventoryView.ListBackdrop.IsVisible && SameBrush(InventoryView.ListBackdrop.Background, R("Brush.Section.Panel")) && InventoryView.ListBackdrop.ActualHeight > 100);
                    await ShotWait(300); Snapshot(P("menucolors-characters.png"));
                }
                finally { InventoryView.Init(() => settings.WowRoot); }

                // ============================================================================================================ the Lodge sidebar
                try
                {
                    Session.Disconnect();
                    TabLodge.IsChecked = true;
                    LodgePage.ShowChatForTest();
                    Session.SentForTest = o => { };
                    Session.FeedForTest(FunWelcome("owner", FunPeople(false), "", "\"reply\",\"react\",\"react2\",\"pin\",\"profile\",\"displayname\",\"lfg\",\"history2\",\"search\",\"calendar\",\"files\",\"badges\"", "2.17.0"));
                    await Until(() => LodgePage.CalendarRowVisibleForTest && Session.Selected != null, 6000); await Settle();
                    var side = LodgePage.SidebarForTest;
                    var tx = AtlasWalk(side).OfType<TextBlock>().ToList();
                    TextBlock Head(string t) => tx.FirstOrDefault(x => x.Text == t);
                    Check("lodge: Text channels and Voice rooms headers are in their section hues (Channels blue, Voice rose)", SameBrush(Head("Text channels")?.Foreground, R("Brush.Section.Channels")) && SameBrush(Head("Voice rooms")?.Foreground, R("Brush.Section.Voice")),
                        Hex(Head("Text channels")?.Foreground) + " " + Hex(Head("Voice rooms")?.Foreground));
                    var groupTitles = AtlasWalk(side).OfType<TextBlock>().Where(t => t.DataContext is MemberGroupVM g && t.Text == g.Title).ToList();
                    Check("lodge: the people groups' headers are in the People hue (" + groupTitles.Count + " groups)", groupTitles.Count > 0 && groupTitles.All(t => SameBrush(t.Foreground, R("Brush.Section.People"))), string.Join(",", groupTitles.Select(t => t.Text + "=" + Hex(t.Foreground))));
                    var hashes = tx.Where(t => t.Text == "#").ToList();
                    Check("lodge: the channel '#' icons are in the Channels hue, the Calendar / Files / Ladder icons in theirs", hashes.Count >= 2 && hashes.All(t => SameBrush(t.Foreground, R("Brush.Section.Channels")))
                        && AtlasWalk(LodgePage.CalendarRow).OfType<TextBlock>().Any(t => SameBrush(t.Foreground, R("Brush.Section.Calendar")))
                        && AtlasWalk(LodgePage.FilesRow).OfType<TextBlock>().Any(t => SameBrush(t.Foreground, R("Brush.Section.Files")))
                        && AtlasWalk(LodgePage.LadderRow).OfType<TextBlock>().Any(t => SameBrush(t.Foreground, R("Brush.Section.Ladder"))));
                    ContentControl ChannelRow(ChannelVM c) => AtlasWalk(side).OfType<ContentControl>().FirstOrDefault(cc => ReferenceEquals(cc.Tag, c) && cc.Style == FindResource("Side.Row"));
                    var selCh = Session.Selected;
                    var chRow = selCh == null ? null : ChannelRow(selCh);
                    Check("lodge: the selected channel is filled with the Channels tint and marked by the 4 px bar in its hue", chRow != null && SelectedProblem(chRow, "Brush.Section.Channels") == null, chRow == null ? "no row" : SelectedProblem(chRow, "Brush.Section.Channels"));
                    var otherCh = Session.TextChannels.FirstOrDefault(c => c != selCh);
                    var otherRow = otherCh == null ? null : ChannelRow(otherCh);
                    Check("lodge: another channel has no fill and no bar", otherRow != null && SameBrush(otherRow.Background, Brushes.Transparent) && SameBrush(otherRow.BorderBrush, Brushes.Transparent), Hex(otherRow?.Background));
                    Check("lodge: the sidebar sits on the faint menu fill (Brush.Section.Panel)", side is Border sb && SameBrush(sb.Background, R("Brush.Section.Panel")));
                    await ShotWait(300); Snapshot(P("menucolors-lodge.png"));
                    LodgePage.ShowCalendarForTest(); await Settle();
                    Check("lodge: with the Calendar open its row takes the Calendar tint and bar, the channel lets go of its own", SelectedProblem(LodgePage.CalendarRow, "Brush.Section.Calendar") == null && SameBrush(chRow?.Background, Brushes.Transparent) && SameBrush(chRow?.BorderBrush, Brushes.Transparent),
                        SelectedProblem(LodgePage.CalendarRow, "Brush.Section.Calendar") + " / " + Hex(chRow?.Background));
                    await ShotWait(300); Snapshot(P("menucolors-lodge-calendar.png"));
                    LodgePage.HideCalendarForTest(); await Settle();
                    Check("lodge: back to the channel: the Calendar row lets go, the channel is marked again", SameBrush(LodgePage.CalendarRow.BorderBrush, Brushes.Transparent) && SelectedProblem(ChannelRow(selCh), "Brush.Section.Channels") == null);
                }
                finally { Session.Disconnect(); Session.SentForTest = null; }
            }
            catch (Exception e) { Check("menucolors checks ran to the end", false, e.ToString()); }
            finally
            {
                try { AtlasView.UseNavSettings(realSettings, () => SettingsStore.Save(realSettings)); AtlasView.ClosePalette(); Width = w0; Height = h0; ShowTab("addons"); } catch (Exception) { }
            }
            return string.Join("\r\n", notes) + "\r\nmenucolors: " + (ok ? "PASSED" : "FAILED");
        }
    }
}
