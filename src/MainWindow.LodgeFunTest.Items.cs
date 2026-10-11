using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // "lodgefun", part 3: item links - the parser (brackets, Wowhead URLs, pasted game links, look-alikes, junk), the item data, the chips
    public partial class MainWindow
    {
        // a small synthetic icon in the (scratch) icon cache, so chips can show a picture without any game install
        static void FunMakeIcon(int fileId, Color c)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(c, Color.FromRgb((byte)(c.R / 3), (byte)(c.G / 3), (byte)(c.B / 3)), 60), null, new Rect(0, 0, 56, 56));
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3), new Rect(1.5, 1.5, 53, 53));
            }
            var bmp = new RenderTargetBitmap(56, 56, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            System.IO.Directory.CreateDirectory(IconStore.Dir);
            using (var fs = System.IO.File.Create(IconStore.PathFor(fileId))) enc.Save(fs);
        }

        // the item data of the self-test and the design shots: Bags-style characters with a few items
        internal static List<InvChar> FunItemChars()
        {
            InvItem It(int id, string name, int q, int icon) => new InvItem { Id = id, Name = name, Quality = q, Icon = icon, Count = 1 };
            return new List<InvChar>
            {
                new InvChar { Key = "Elanoran-Server", Name = "Elanoran", Realm = "Server", Bags = new List<InvItem>
                {
                    It(2589, "Linen Cloth", 1, 1000001), It(19019, "Thunderfury, Blessed Blade of the Windseeker", 5, 1000002), It(1, "Torch", 1, 0), It(2, "Torch", 1, 0),
                    It(6948, "Hearthstone", 1, 1000003), It(2592, "Wool Cloth", 1, 1000004), It(14047, "Runecloth", 1, 1000005), It(12640, "Lionheart Helm", 4, 1000006),
                } },
            };
        }

        async Task FunItems(string dir, Action<string, bool> check)
        {
            var hadRoot = ItemKnowledge.WowRoot;
            try
            {
                ItemKnowledge.WowRoot = () => null;   // nothing is read from any game folder in the self-test
                ItemKnowledge.ResetForTest();

                // ---- the parser: brackets
                var known = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Linen Cloth"] = 2589, ["Thunderfury, Blessed Blade of the Windseeker"] = 19019 };
                Func<string, int> idOf = n => known.TryGetValue(n, out var i) ? i : 0;
                var r = ItemLinks.Find("anyone has [Linen Cloth] left?", idOf);
                check("parse: [Item Name] is found with its text range and, when the data knows exactly one, its id", r.Count == 1 && r[0].Kind == ItemRefKind.Bracket && r[0].Id == 2589 && r[0].Name == "Linen Cloth"
                    && "anyone has [Linen Cloth] left?".Substring(r[0].Start, r[0].Length) == "[Linen Cloth]");
                r = ItemLinks.Find("[Mystery Thing] and [linen cloth]", idOf);
                check("parse: an unknown [Name] is a plain chip (no id); names match case-insensitively", r.Count == 2 && r[0].Id == 0 && r[0].Name == "Mystery Thing" && r[1].Id == 2589);
                check("parse: a long name with a comma and apostrophes is fine", ItemLinks.Find("[Thunderfury, Blessed Blade of the Windseeker] [Hand of Edward the Odd] [Ashkandi, Greatsword of the Brotherhood]", idOf).Count == 3);

                // ---- look-alikes and junk
                string[] notItems = { "[x]", "[ ]", "[]", "[1]", "[12345]", "[link](http://example.com)", "[[Linen Cloth]]", "foo[Linen Cloth]", "[ Linen Cloth ]", "[Looking for group]", "[edited]",
                    "[a/b/c]", "[@everyone]", "[Linen Cloth", "Linen Cloth]", "[this is a very long bracket that is nothing like an item name at all, really]", "[one two three four five six seven eight nine]",
                    "{Linen Cloth}", "(Linen Cloth)", "[<b>bold</b>]", "[ok]", "[--]", "[...]" };
                check("parse: look-alikes are ignored (" + string.Join(" ", notItems.Where(t => ItemLinks.Find(t, idOf).Count > 0)) + ")", notItems.All(t => ItemLinks.Find(t, idOf).Count == 0));
                check("parse: junk input never throws and finds nothing", ItemLinks.Find(null).Count == 0 && ItemLinks.Find("").Count == 0 && ItemLinks.Find(new string('[', 3000)).Count == 0
                    && ItemLinks.Find("|||Hitem:|h[|h|||r |c |Hitem:99999999999|h[Big]|h").Count == 0 && ItemLinks.Find(new string('x', 5000) + "[Linen Cloth]", idOf).Count == 0);

                // ---- Wowhead URLs
                var u1 = ItemLinks.Find("see https://www.wowhead.com/classic/item=2589/linen-cloth for details");
                check("parse: a Wowhead classic item URL gives the id and a readable name from the slug", u1.Count == 1 && u1[0].Kind == ItemRefKind.Wowhead && u1[0].Id == 2589 && u1[0].Name == "Linen Cloth");
                string[] wowheads = { "https://classic.wowhead.com/item=2589", "http://wowhead.com/classic/item=2589", "https://www.wowhead.com/tbc/item=2589/linen-cloth#comments", "https://www.wowhead.com/es/classic/item=2589",
                    "(https://www.wowhead.com/item=2589)", "https://www.wowhead.com/classic/item=2589?x=1&y=2", "https://www.wowhead.com/item=2589." };
                check("parse: other Wowhead forms (subdomain, locale, flavour, query, fragment, brackets and a trailing dot around it)", wowheads.All(t => { var f = ItemLinks.Find(t); return f.Count == 1 && f[0].Id == 2589; }));
                string[] fakeUrls = { "https://wowhead.com.evil.example/item=2589", "https://notwowhead.com/classic/item=2589", "https://www.wowhead.com/classic/spell=2589", "https://www.wowhead.com/classic/item=abc",
                    "https://www.wowhead.com/classic/items/armor", "https://example.com/?u=https://www.wowhead.com/item=1", "https://wowhead.com@evil.example/item=1", "https://www.wowhead.com:8080/item=1",
                    "https://evil.example/wowhead.com/item=1", "https://www.wowhead.com/classic/item=0", "ftp://www.wowhead.com/item=1", "https://www.wowhead.com/item=12345678901" };
                check("parse: look-alike URLs are not item links (" + string.Join(" ", fakeUrls.Where(t => ItemLinks.Find(t).Count > 0)) + ")", fakeUrls.All(t => ItemLinks.Find(t).Count == 0));

                // ---- pasted game links
                var cl = ItemLinks.Find("look |cffa335ee|Hitem:19019::::::::60:::::::|h[Thunderfury, Blessed Blade of the Windseeker]|h|r !");
                check("parse: a pasted game link gives the id, the name and the quality colour (epic)", cl.Count == 1 && cl[0].Kind == ItemRefKind.ChatLink && cl[0].Id == 19019 && cl[0].Quality == 4 && cl[0].Name.StartsWith("Thunderfury"));
                var cl2 = ItemLinks.Find("|Hitem:2589:0:0:0|h[Linen Cloth]|h");
                check("parse: a game link without colour codes works; its quality is unknown", cl2.Count == 1 && cl2[0].Id == 2589 && cl2[0].Quality == -1);
                check("parse: other game links (spells) and broken item links are not items", ItemLinks.Find("|cff71d5ff|Hspell:133|h[Fireball]|h|r").Count == 0 && ItemLinks.Find("|Hitem:abc|h[Linen Cloth]|h").Count == 0
                    && ItemLinks.Find("|Hitem:2589|h[Linen Cloth]").Count == 0);
                check("display: a game link reads [Name]; ordinary text is untouched", ItemLinks.Display("x |cffa335ee|Hitem:19019::|h[Thunderfury]|h|r y") == "x [Thunderfury] y" && ItemLinks.Display("plain [Linen Cloth]") == "plain [Linen Cloth]" && ItemLinks.Display(null) == null);

                // ---- one chip per item, in order, capped
                var mix = ItemLinks.Find("[Linen Cloth] twice [Linen Cloth] and https://www.wowhead.com/classic/item=2589 and |cff1eff00|Hitem:2589::|h[Linen Cloth]|h|r then [Wool Cloth]", idOf);
                check("parse: the same item mentioned four ways is one chip; the next item follows", mix.Count == 2 && mix[0].Id == 2589 && mix[1].Name == "Wool Cloth");
                var many = ItemLinks.Find(string.Join(" ", Enumerable.Range(1, 20).Select(i => "[Thing Number " + i + "]")));
                check("parse: a flood of brackets is capped at 6 chips", many.Count == ItemLinks.MaxPerMessage);
                var order = ItemLinks.Find("[Zed Item] https://www.wowhead.com/item=5 [Alpha Item]");
                check("parse: chips come in reading order", order.Count == 3 && order[0].Name == "Zed Item" && order[1].Id == 5 && order[2].Name == "Alpha Item");
                check("url: known items open Wowhead classic by id (the pasted URL is never reused); unknown names search it",
                    ItemLinks.UrlFor(new ItemRef { Id = 2589 }) == "https://www.wowhead.com/classic/item=2589" && ItemLinks.UrlFor(new ItemRef { Name = "Mystery Thing" }) == "https://www.wowhead.com/classic/search?q=Mystery%20Thing"
                    && BrowserUrl.IsAllowedScheme(ItemLinks.UrlFor(new ItemRef { Name = "a b&c=d" })) && !ItemLinks.UrlFor(new ItemRef { Name = "a b&c=d" }).Contains("&c=d"));

                // ---- the item data: unique names only
                ItemKnowledge.Set(FunItemChars());
                check("data: names from the Bags data; a name two items share is ambiguous (no id)", ItemKnowledge.IdOf("Linen Cloth") == 2589 && ItemKnowledge.IdOf("linen cloth") == 2589 && ItemKnowledge.IdOf("Torch") == 0
                    && ItemKnowledge.IdOf("Not an item") == 0 && ItemKnowledge.Get(19019).Quality == 5 && ItemKnowledge.Get(2589).Icon == 1000001);
                ItemKnowledge.Set(new List<InvChar> { new InvChar { Name = "Alt", Bags = new List<InvItem> { new InvItem { Id = 77, Name = "Item #77" } } } });
                check("data: a placeholder name ('Item #77') is not a name", ItemKnowledge.IdOf("Item #77") == 0 && ItemKnowledge.Get(77) != null && ItemKnowledge.Get(77).Name == null);
                ItemKnowledge.Set(FunItemChars());

                // ---- chips in the chat
                foreach (var (id, col) in new[] { (1000001, Color.FromRgb(0xE8, 0xE4, 0xDA)), (1000002, Color.FromRgb(0xFF, 0x80, 0x00)), (1000003, Color.FromRgb(0x6C, 0xB4, 0xE0)) }) FunMakeIcon(id, col);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var text = "Anyone have [Linen Cloth]? Bob dropped |cffff8000|Hitem:19019::::::::60:::::::|h[Thunderfury, Blessed Blade of the Windseeker]|h|r - https://www.wowhead.com/classic/item=2589/linen-cloth - also [Torch] and [Mystery Thing]";
                Session.FeedForTest(FunWelcome("owner", FunPeople(false),
                    "{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"i1\",\"at\":" + now + ",\"from\":\"Bob\",\"fromId\":2,\"text\":" + Q(text) + "}"));
                Session.Select(Session.TextChannels.First(c => c.Id == "general"));
                await Task.Delay(500);
                var vm = Session.MessagesOf("general").First(m => m.Id == "i1");
                var chips = vm.ItemChips.ToList();
                check("chips: one per distinct item, in reading order (Linen Cloth, Thunderfury, Torch, Mystery Thing)", chips.Count == 4 && chips[0].Name == "Linen Cloth" && chips[1].Name.StartsWith("Thunderfury") && chips[2].Name == "Torch" && chips[3].Name == "Mystery Thing");
                check("chips: the name is in the item's quality colour (epic/legendary known, common white), unknown stays quiet", chips[1].Quality == 5 && chips[0].Quality == 1 && chips[3].Quality == -1
                    && ((SolidColorBrush)chips[1].NameBrush).Color == (Color)ColorConverter.ConvertFromString("#FF8000") && ((SolidColorBrush)chips[0].NameBrush).Color == Colors.White);
                check("chips: the icon comes from the local icon cache by the Bags data's file id; items without one show a quiet tile", chips[0].Icon != null && chips[0].IconVisibility == Visibility.Visible && chips[1].Icon != null
                    && chips[2].Icon == null && chips[2].TileVisibility == Visibility.Visible && chips[3].Icon == null);
                check("chips: an ambiguous name and an unknown name are plain chips (no id); the known ones carry the id", chips[0].Ref.Id == 2589 && chips[1].Ref.Id == 19019 && chips[2].Ref.Id == 0 && chips[3].Ref.Id == 0 && !chips[3].Known);
                check("chips: the text shows [Name] for the pasted link, no escape codes", vm.ShownText.Contains("[Thunderfury, Blessed Blade of the Windseeker]") && !vm.ShownText.Contains("|H") && vm.Text.Contains("|Hitem:19019"));
                check("chips: the tooltip says what it is and where a click goes", chips[1].TipLine.StartsWith("Legendary") && chips[1].TipLine.Contains("19019") && chips[1].TipHint.Contains("Wowhead Classic") && chips[3].TipHint.Contains("search"));

                await Task.Delay(300);
                LodgePage.UpdateLayout();
                var shown = VisualsOf<TextBlock>(LodgePage.ChatListForTest).Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();
                var boxes = VisualsOf<TextBox>(LodgePage.ChatListForTest).Select(t => t.Text).ToList();
                check("chips: they are drawn under the message", shown.Contains("Linen Cloth") && shown.Contains("Mystery Thing") && shown.Any(t => t.StartsWith("Thunderfury")) && shown.Contains("Torch"));
                check("chips: the message text box shows the clean text", boxes.Any(t => t.Contains("[Thunderfury, Blessed Blade of the Windseeker]") && !t.Contains("|Hitem")));

                // ---- a click opens the item through the chat-link path
                var urls = new List<string>();
                LodgePage.OpenLinkOverrideForTest = u => urls.Add(u);
                LodgePage.ItemChipClickForTest(chips[0]);
                LodgePage.ItemChipClickForTest(chips[3]);
                check("click: a known item opens Wowhead classic by id; an unknown name searches", urls.Count == 2 && urls[0] == "https://www.wowhead.com/classic/item=2589" && urls[1] == "https://www.wowhead.com/classic/search?q=Mystery%20Thing");
                LodgePage.OpenLinkOverrideForTest = null;
                var field = typeof(LodgeView).GetField("OpenLink", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var del = field?.GetValue(LodgePage) as Delegate;
                check("click: without an override the chip uses the same OpenLink event as every chat link (the window's OpenChatLink: Settings > Privacy > Outpost browser decides where)",
                    del != null && del.GetInvocationList().Any(d => d.Method.Name == "OpenChatLink"));

                // ---- the data arrives later: chips update, names resolve
                ItemKnowledge.ResetForTest();
                Session.FeedForTest("{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"i2\",\"at\":" + now + ",\"from\":\"Tess\",\"fromId\":3,\"text\":\"selling [Runecloth] and [Lionheart Helm]\"}");
                var late = Session.MessagesOf("general").First(m => m.Id == "i2");
                check("late data: before the item data is there the names are plain chips", late.ItemChips.Count == 2 && late.ItemChips.All(c => c.Ref.Id == 0 && c.Icon == null));
                ItemKnowledge.Set(FunItemChars());
                Session.RefreshItemChips();
                check("late data: when it arrives the chips resolve (id, quality colour, icon)", late.ItemChips.Count == 2 && late.ItemChips[0].Ref.Id == 14047 && late.ItemChips[1].Ref.Id == 12640 && late.ItemChips[1].Quality == 4);

                // ---- messages without items, sign-up cards and editing
                Session.FeedForTest("{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"i3\",\"at\":" + now + ",\"from\":\"Bob\",\"fromId\":2,\"text\":\"nothing to see [x] here\"}");
                check("none: ordinary text has no chips", Session.MessagesOf("general").First(m => m.Id == "i3").ItemChips.Count == 0 && Session.MessagesOf("general").First(m => m.Id == "i3").ItemChipsVisibility == Visibility.Collapsed);
                Session.FeedForTest("{\"t\":\"edited\",\"channel\":\"general\",\"id\":\"i3\",\"text\":\"now with [Wool Cloth]\"}");
                check("edit: an edit that adds an item adds the chip", Session.MessagesOf("general").First(m => m.Id == "i3").ItemChips.Count == 1 && Session.MessagesOf("general").First(m => m.Id == "i3").ItemChips[0].Ref.Id == 2592);
                Session.FeedForTest("{\"t\":\"edited\",\"channel\":\"general\",\"id\":\"i3\",\"text\":\"never mind\"}");
                check("edit: and removing it takes the chip away", Session.MessagesOf("general").First(m => m.Id == "i3").ItemChips.Count == 0);
                Session.FeedForTest(FunLfgMsg("i4", "Bob", 2, now, FunLfgJson("[Linen Cloth] run", now + 3600000, false, "", "open", "Bob", 1, 1, 3, ""), "[Looking for group] [Linen Cloth] run - ..."));
                check("lfg: a sign-up card has no item chips (its fallback text is not shown)", Session.MessagesOf("general").First(m => m.Id == "i4").ItemChips.Count == 0);
                check("2.8 server: item chips are purely client side, so they work on any server", Session.MessagesOf("general").First(m => m.Id == "i1").ItemChips.Count == 4);
            }
            finally { ItemKnowledge.WowRoot = hadRoot; ItemKnowledge.ResetForTest(); LodgePage.OpenLinkOverrideForTest = null; }
        }
    }
}
