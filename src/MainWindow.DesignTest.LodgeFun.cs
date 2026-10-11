using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // ELANSHUB_DESIGN_SHOTS=lodgefun with --selftest <dir>: renders the Lodge 2.37 additions from synthetic frames - the grouped online list,
    // a "Looking for group" card, item chips and the search panel (lodge-fun-*.png). Item icons are synthetic squares written to the
    // scratch icon cache (nothing is read from or written to the WoW folder). Quits when done.
    public partial class MainWindow
    {
        async Task LodgeFunShots(string dir)
        {
            var s = settings;
            var hadCollapsed = s.LodgeGroupsCollapsed;
            try
            {
                Width = 1180; Height = 760;
                s.LodgeGroupsCollapsed = null;
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                await FunShotGroups(dir);
                await FunShotLfg(dir);
                await FunShotItems(dir);
                await FunShotSearch(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "lodge fun design shots done");
            }
            catch (Exception e) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "result.txt"), "design shots crashed: " + e); }
            finally { s.LodgeGroupsCollapsed = hadCollapsed; Session.Disconnect(); }
            Quit();
        }

        async Task FunShotSearch(string dir)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var phrases = new[] { "Deadmines tonight? We need one more DPS.", "Who is up for Deadmines after dinner?", "Finished Deadmines, the helm finally dropped!", "Anyone remember where the Deadmines entrance is?",
                "Deadmines on Saturday works for me", "I can tank Deadmines if someone heals", "The Deadmines run last week took forever", "Heading to Deadmines now, meet at the door", "Deadmines or Wailing Caverns first?", "Deadmines gear list is pinned" };
            var srv = new FunServer(this, 260, now, i => i % 25 == 0 ? phrases[(i / 25 - 1) % phrases.Length] : "just chatting, nothing to see " + i);
            Session.SentForTest = srv.Handle;
            Session.Disconnect();
            LodgePage.ShowChatForTest();
            Session.IsShownToUser = () => false;
            string Loot(string id, string text, int minsAgo) => "{\"t\":\"msg\",\"channel\":\"loot\",\"id\":" + Q(id) + ",\"at\":" + (now - minsAgo * 60000L) + ",\"from\":\"Tess\",\"fromId\":3,\"text\":" + Q(text) + "}";
            Session.FeedForTest(FunHistoryWelcome(srv, 100, Loot("L1", "selling a Deadmines recipe, 3 silver", 3)));
            Session.Select(Session.TextChannels.First(c => c.Id == "general"));
            await Task.Delay(700);
            LodgePage.ScrollToEndForTest();
            LodgePage.OpenSearch();
            LodgePage.SearchBoxTextForTest = "deadmines";
            await LodgePage.RunSearchForTest();
            await Task.Delay(700);
            // a popup is its own window and not part of the window's picture: the card is drawn on top at the place it has under the magnifier
            UpdateLayout();
            var origin = LodgePage.SearchCardOriginForTest(this);
            var card = LodgePage.SearchCardForTest;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // (a brush of an element with a shadow effect would be shifted by the effect's bounds: the view box pins it to the element's own rectangle)
                VisualBrush Pinned(Visual v, double w, double h) => new VisualBrush(v) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h), Stretch = Stretch.Fill };
                dc.DrawRectangle(Pinned(this, ActualWidth, ActualHeight), null, new Rect(0, 0, ActualWidth, ActualHeight));
                var shadow = new Rect(origin.X, origin.Y + 8, card.ActualWidth, card.ActualHeight);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, shadow, 12, 12);
                dc.DrawRectangle(Pinned(card, card.ActualWidth, card.ActualHeight), null, new Rect(origin.X, origin.Y, card.ActualWidth, card.ActualHeight));
            }
            var bmp = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "lodge-fun-search.png"))) enc.Save(fs);
            LodgePage.CloseSearchForTest();
            Session.SentForTest = null;
        }

        async Task FunShotItems(string dir)
        {
            ItemKnowledge.WowRoot = () => null;   // synthetic item data and icons only
            ItemKnowledge.Set(FunItemChars());
            var cols = new[] { Color.FromRgb(0xE8, 0xE4, 0xDA), Color.FromRgb(0xFF, 0x80, 0x00), Color.FromRgb(0x6C, 0xB4, 0xE0), Color.FromRgb(0xC8, 0xB4, 0x8C), Color.FromRgb(0x9C, 0x6C, 0xD8), Color.FromRgb(0xB0, 0x48, 0xF8) };
            for (int i = 0; i < cols.Length; i++) FunMakeIcon(1000001 + i, cols[i]);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string M(string id, string from, int fromId, string text, int minsAgo) =>
                "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + (now - minsAgo * 60000L) + ",\"from\":" + Q(from) + ",\"fromId\":" + fromId + ",\"text\":" + Q(text) + "}";
            var hist = string.Join(",",
                M("i1", "Bob", 2, "Who still needs [Runecloth]? I have a stack for the guild bank.", 8),
                M("i2", "Mia", 3, "Dropped in Stratholme: |cffa335ee|Hitem:12640::::::::60:::::::|h[Lionheart Helm]|h|r - want it?", 6),
                M("i3", "Kor", 6, "Thunderfury looks like this https://www.wowhead.com/classic/item=19019/thunderfury-blessed-blade-of-the-windseeker, and [Hearthstone] is in your bags", 3),
                M("i4", "Dax", 8, "Selling [Linen Cloth] and [Wool Cloth], also looking for a [Torch] and a [Moonwell Charm]", 1));
            Session.FeedForTest(FunWelcome("owner", FunPeople(false), hist));
            Session.Select(Session.TextChannels.First(c => c.Id == "general"));
            await Task.Delay(900);
            LodgePage.ScrollToEndForTest();
            await Task.Delay(300);
            Snapshot(System.IO.Path.Combine(dir, "lodge-fun-items.png"));
        }

        async Task FunShotLfg(string dir)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var start = DateTime.UtcNow.AddMinutes(95);
            string Sig(string n, string r) => "{\"n\":" + Q(n) + ",\"r\":" + Q(r) + "}";
            var open = FunLfgMsg("l1", "Bob", 2, now - 5 * 60000, FunLfgJson("The Deadmines", new DateTimeOffset(start).ToUnixTimeMilliseconds(), false, "Level 18-22, bring water, we will do the full run",
                "open", "Bob", 1, 1, 3, string.Join(",", Sig("Bob", "tank"), Sig("Mia", "healer"), Sig("Dax", "dps"), Sig("Kor", "maybe"))), "x");
            var mine = FunLfgMsg("l2", "Kor", 6, now - 2 * 60000, FunLfgJson("Molten Core", now + 3 * 3600000L * 24, false, "", "closed", "Kor", 2, 3, 12, ""), "x");
            string M(string id, string from, int fromId, string text, int minsAgo) =>
                "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + (now - minsAgo * 60000L) + ",\"from\":" + Q(from) + ",\"fromId\":" + fromId + ",\"text\":" + Q(text) + "}";
            Session.FeedForTest(FunWelcome("owner", FunPeople(false), string.Join(",", M("a1", "Bob", 2, "Deadmines in an hour? We still need a DPS.", 9), open)));
            Session.Select(Session.TextChannels.First(c => c.Id == "general"));
            await Task.Delay(700);
            LodgePage.ScrollToEndForTest();
            await Task.Delay(300);
            Snapshot(System.IO.Path.Combine(dir, "lodge-fun-lfg-card.png"));
            LodgePage.OpenLfgFormForTest();
            LodgePage.LfgTitleForTest = "Dead";
            LodgePage.LfgSetLaterForTest(true, 0, DateTime.Now.AddHours(2).Hour, 0);
            LodgePage.LfgNoteForTest = "Level 18-22, bring water";
            await Task.Delay(500);
            Snapshot(System.IO.Path.Combine(dir, "lodge-fun-lfg-form.png"));
            LodgePage.CloseLfgFormForTest();
            await Task.Delay(100);
        }

        async Task FunShotGroups(string dir)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string M(string id, string from, int fromId, string text, int minsAgo) =>
                "{\"t\":\"msg\",\"channel\":\"general\",\"id\":" + Q(id) + ",\"at\":" + (now - minsAgo * 60000L) + ",\"from\":" + Q(from) + ",\"fromId\":" + fromId + ",\"text\":" + Q(text) + "}";
            var history = string.Join(",", M("h1", "Bob", 2, "Deadmines tonight? We need one more DPS.", 12), M("h2", "Mia", 3, "I can heal, give me ten minutes to finish a quest", 10),
                M("h3", "Kor", 6, "Molten Core is on for Saturday, bring fire resistance", 4));
            Session.FeedForTest(FunWelcome("member", FunPeople(false), history));
            await Task.Delay(700);
            SnapshotElement(LodgePage.SidebarForTest, System.IO.Path.Combine(dir, "lodge-fun-groups.png"));
            Snapshot(System.IO.Path.Combine(dir, "lodge-fun-groups-window.png"));
        }
    }
}
