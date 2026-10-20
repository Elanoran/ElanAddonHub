using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "chatlist": the Lodge's message list as a whole - the behaviour contract of the list (where a channel opens, what new messages,
    // sending, scrolling up, jumps, the unread line, edits / deletes / reactions, pictures and video cards that arrive late, the keyboard,
    // the context menu, selection, the typing line, accessibility) in a long channel (600 messages), and the numbers of a 5000-message
    // channel (time to open, rows built, memory, the cost of a scroll step). All from synthetic frames: no server.
    public partial class MainWindow
    {
        static readonly string[] ChatWho = { "Bob", "Tess", "Mia" };

        static string ChatJ(string s) => Q(s).Replace("\r", "").Replace("\n", "\\n");

        // message i of n in a mixed channel: runs of three from one person 45 s apart (grouping), every 37th mine, pictures, YouTube links,
        // mentions, long multi-line ones, replies and reactions
        static string ChatHistoryMsg(int i, int n, long now, Func<int, string> textFor = null, string pre = "c")
        {
            string id = pre + i.ToString("D5");
            string from; int fid;
            if (i % 37 == 0) { from = "Elan"; fid = 1; }
            else { from = ChatWho[((i - 1) / 3) % 3]; fid = from == "Bob" ? 2 : from == "Tess" ? 4 : 3; }
            long at = now - (long)(n + 1 - i) * 45000L;
            string text, extra = "";
            var custom = textFor?.Invoke(i);
            if (custom != null) text = custom;
            else if (i % 50 == 7) { text = ""; extra = ",\"file\":{\"id\":\"img" + i + "\",\"name\":\"shot" + i + ".png\",\"size\":20000,\"mime\":\"image/png\"}"; }
            else if (i % 50 == 21) text = "look at this pull https://www.youtube.com/watch?v=chatlistV" + (i % 10).ToString("D2");
            else if (i % 20 == 11) text = "@Elan are you around for Deadmines " + i + "?";
            else if (i % 13 == 0) text = "a longer one " + i + "\nwith a second line\nand a third line that goes on for a while, so that it wraps when the window is narrow enough to need it";
            else text = "chatter number " + i;
            if (i % 10 == 3 && i > 5) extra += ",\"replyTo\":{\"id\":\"" + pre + (i - 5).ToString("D5") + "\",\"by\":\"Bob\",\"snippet\":\"chatter number " + (i - 5) + "\"}";
            if (i % 10 == 5) extra += ",\"reactions\":{\"ready\":[\"Bob\",\"Tess\"],\"lol\":[\"Elan\"]}";
            return "{\"channel\":\"long\",\"id\":" + Q(id) + ",\"at\":" + at + ",\"from\":" + Q(from) + ",\"fromId\":" + fid + ",\"text\":" + ChatJ(text) + extra + "}";
        }

        static string ChatLive(string id, string from, int fid, string text, string extra = "", string channel = "long") =>
            "{\"t\":\"msg\",\"channel\":" + Q(channel) + ",\"id\":" + Q(id) + ",\"at\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"from\":" + Q(from) + ",\"fromId\":" + fid + ",\"text\":" + ChatJ(text) + extra + "}";

        string ChatWelcome(int n, long now, Func<int, string> textFor = null, string pre = "c")
        {
            var hist = string.Join(",", Enumerable.Range(1, n).Select(i => ChatHistoryMsg(i, n, now, textFor, pre)));
            var loot = string.Join(",", Enumerable.Range(1, 20).Select(i => "{\"channel\":\"loot\",\"id\":\"L" + i + "\",\"at\":" + (now - (21 - i) * 60000L) + ",\"from\":\"Tess\",\"fromId\":4,\"text\":\"loot line " + i + "\"}"));
            // a channel of its own, "Long" (General keeps what the other Lodge sections left in it)
            return FunWelcome("owner", FunPeople(false), "").Replace("\"loot\":[]}", "\"loot\":[" + loot + "],\"long\":[" + hist + "]}")
                .Replace("\"channels\":[", "\"channels\":[{\"id\":\"long\",\"name\":\"Long\",\"type\":\"text\",\"minRole\":\"guest\"},");
        }

        static BitmapSource ChatPicture(int w, int h, byte seed)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(seed, 140, 200), Color.FromRgb(220, 90, seed), 30), null, new Rect(0, 0, w, h));
            var rt = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rt.Render(dv);
            rt.Freeze();
            return rt;
        }

        static byte[] ChatPng(int w, int h)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(ChatPicture(w, h, 60)));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        // a history page answer: nothing older than what the welcome sent
        void ChatPager(Dictionary<string, object> o)
        {
            if (o.Str("t") == "history:before")
                Session.FeedForTest("{\"t\":\"history:page\",\"channel\":" + Q(o.Str("channel")) + ",\"before\":" + Q(o.Str("before")) + ",\"messages\":[],\"hasMore\":false}");
        }

        async Task<string> ChatListTest(string dir)
        {
            var notes = new List<string>();
            var perf = new List<string>();
            bool ok = true;
            Action<string, bool> check = (what, cond) => { SelfTestClock.Mark(what); notes.Add((cond ? "ok   " : "FAIL ") + what); ok &= cond; };
            var hadShown = Session.IsShownToUser; var hadThumb = YouTube.ThumbSource; var hadMeta = YouTube.MetaSource; var hadAuto = settings.VideoPreviewsAuto; var hadLast = settings.LastChannel;
            try
            {
                Session.Disconnect();
                TabLodge.IsChecked = true;
                LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => true;
                settings.VideoPreviewsAuto = true;   // the video cards of this channel load by themselves (click to load is the video test)
                YouTube.ClearCaches();
                var late = new TaskCompletionSource<byte[]>();
                var late2 = new TaskCompletionSource<byte[]>();
                YouTube.ThumbSource = id => id == "chatlistLAT" ? late.Task : id == "chatlistLA2" ? late2.Task : Task.FromResult(FakeThumb((byte)(id[id.Length - 1] * 5)));
                YouTube.MetaSource = id => Task.FromResult<VideoMeta>(null);
                Session.SentForTest = ChatPager;
                await ChatContract(dir, check, late, late2);
                if (Environment.GetEnvironmentVariable("ELANSHUB_CHATLIST_QUICK") != "1") await ChatPerf(dir, check, perf);   // (=1: the contract only, while working on the list)
            }
            catch (Exception e) { ok = false; notes.Add("crashed: " + e); }
            finally
            {
                Session.SentForTest = null;
                Session.IsShownToUser = hadShown; YouTube.ThumbSource = hadThumb; YouTube.MetaSource = hadMeta; settings.VideoPreviewsAuto = hadAuto; settings.LastChannel = hadLast;
                YouTube.ClearCaches();
                Session.Disconnect();
            }
            try { File.WriteAllLines(Path.Combine(dir, "chatlist-perf.txt"), perf); } catch { }
            return "chat list selftest: " + (ok ? "PASSED" : "FAILED") + "\r\n  " + string.Join("\r\n  ", notes.Concat(perf));
        }

        async Task ChatSettle() { await Settle(); await Settle(); }

        async Task ChatContract(string dir, Action<string, bool> check, TaskCompletionSource<byte[]> late, TaskCompletionSource<byte[]> late2)
        {
            const int N = 600;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var L = LodgePage;
            async Task Feed(string json) { Session.FeedForTest(json); await ChatSettle(); }
            ChannelVM Long() => Session.TextChannels.First(c => c.Id == "long");
            ChannelVM Loot() => Session.TextChannels.First(c => c.Id == "loot");
            MessageVM Msg(string id) => Session.MessagesOf("long").FirstOrDefault(m => m.Id == id);
            bool OnScreen(string id) { var y = L.OffsetOfForTest(id); return !double.IsNaN(y) && y >= -1 && y < L.ViewportForTest; }
            (string id, double y) Anchor() { var id = L.FirstVisibleForTest(out var y); return (id, y); }
            bool Same((string id, double y) a, out string what)
            {
                var y = a.id == null ? double.NaN : L.OffsetOfForTest(a.id);
                what = a.id + " " + a.y.ToString("0.0") + " -> " + y.ToString("0.0");
                return a.id != null && !double.IsNaN(y) && Math.Abs(y - a.y) < 1.5;
            }
            // the whole row is on screen, its bottom edge at the bottom of the view (measured on the drawn row, not the scroll numbers)
            bool EndOnScreen(string id) { var y = L.OffsetOfForTest(id); var r = L.RowForTest(id); if (double.IsNaN(y) || r == null) return false; var bottom = y + r.ActualHeight; return bottom <= L.ViewportForTest + 1 && bottom >= L.ViewportForTest - 24; }
            bool AtEnd() => L.AtBottomForTest && Math.Abs(L.MessageScrollableForTest - L.ScrollOffsetForTest) < 1.5;
            IEnumerable<TextBlock> Shown(FrameworkElement row) => row == null ? Enumerable.Empty<TextBlock>() : VisualsOf<TextBlock>(row).Where(t => t.IsVisible);

            // a picture and a video card above the view arrive late (c00300 = YouTube with a slow thumbnail, c00307 = a picture not loaded yet)
            await Feed(ChatWelcome(N, now, i => i == 300 ? "the late one https://www.youtube.com/watch?v=chatlistLAT" : null));
            Session.Select(Loot());
            await ChatSettle();
            Session.Select(Long());
            await ChatSettle();
            // (the video cards of the history come in on their own, a moment later: wait for them, except the one that is held back)
            await Until(() => Session.MessagesOf("long").Where(m => m.Video != null && m.Id != "c00300").All(m => m.VideoVisibility == Visibility.Visible), 8000);
            L.ScrollToEndForTest();
            await ChatSettle();

            // ---- opening
            check("open: a long channel opens at its newest message (scrolled to the end, the last message on screen) [rows built " + L.RowCountForTest + "]",
                await Until(() => AtEnd() && EndOnScreen("c00600")));
            Snapshot(Path.Combine(dir, "chatlist-1-bottom.png"));

            // ---- new messages
            await Feed(ChatLive("live1", "Bob", 2, "a new one while I'm at the bottom"));
            check("live: a new message while at the bottom scrolls it into view and is read", await Until(() => AtEnd() && EndOnScreen("live1")) && !Long().Unread);

            var s0 = L.MessageScrollableForTest;
            L.ScrollToVerticalForTest(Math.Max(0, L.MessageScrollableForTest - 2500));
            var oS = L.ScrollOffsetForTest;
            await ChatSettle();
            var a = Anchor(); var off = L.ScrollOffsetForTest;
            var diag = "S " + s0.ToString("0") + " to " + (s0 - 2500).ToString("0") + " got " + oS.ToString("0") + " settled " + off.ToString("0") + "/" + L.MessageScrollableForTest.ToString("0") + " bottom " + L.AtBottomForTest;
            await Feed(ChatLive("live2", "Tess", 4, "a new one while I read further up"));
            await ChatSettle();
            diag += " after " + L.ScrollOffsetForTest.ToString("0") + "/" + L.MessageScrollableForTest.ToString("0");
            check("live: a new message while scrolled up does not move the view (" + (Same(a, out var w1) ? "" : w1 + " " + diag) + ")", Same(a, out _) && Math.Abs(L.ScrollOffsetForTest - off) < 1.5);
            check("live: ... it is unread, the 'New' line goes above it", Long().Unread && Long().Divider?.Id == "live2" && Msg("live2").ShowDivider);
            check("live: ... and the pill offers Jump to present", L.JumpModeForTest == "present");

            for (int i = 3; i <= 62; i++) await Feed(ChatLive("live" + i, "Bob", 2, "more news " + i));
            await ChatSettle();
            L.ScrollToVerticalForTest(L.MessageScrollableForTest - 100);
            await ChatSettle();
            check("unread: with the 'New' line above the view the pill offers Jump to first unread [" + L.JumpModeForTest + "]", L.JumpModeForTest == "unread");
            check("unread: the line is drawn on its row ('New')", Shown(L.RowForTest("live2")).Any(t => t.Text == "New"));
            L.ClickJumpForTest();
            await ChatSettle();
            var yDiv = L.OffsetOfForTest("live2");
            check("unread: Jump to first unread brings the line near the top of the view (" + yDiv.ToString("0.0") + ")", Math.Abs(yDiv - 36) < 2);
            L.ClickJumpForTest();
            check("unread: then Jump to present goes to the end, where the channel is read", await Until(() => AtEnd() && !Long().Unread));

            // ---- sending
            L.ScrollToVerticalForTest(Math.Max(0, L.MessageScrollableForTest - 2500));
            await ChatSettle();
            await Feed(ChatLive("mine1", "Elan", 1, "my own message, sent while scrolled up"));
            check("send: my own message scrolls the list to the end, also when I was scrolled up", await Until(() => AtEnd() && EndOnScreen("mine1")));

            // ---- grouping
            check("grouping: a follow-up from the same person within minutes has no name or avatar; the first of the run has",
                Shown(L.RowForTest("live3")).Any(t => t.Text == "Bob") && !Shown(L.RowForTest("live4")).Any(t => t.Text == "Bob") && Msg("live4").Continuation);

            // ---- edits, reactions, deletes, mentions in view
            await Feed("{\"t\":\"edited\",\"channel\":\"long\",\"id\":\"live60\",\"text\":\"edited text here\"}");
            await Feed("{\"t\":\"react\",\"channel\":\"long\",\"id\":\"live60\",\"reaction\":\"ready\",\"users\":[\"Bob\"],\"by\":\"Bob\",\"on\":true}");
            await Feed("{\"t\":\"deleted\",\"channel\":\"long\",\"id\":\"live61\"}");
            await Feed(ChatLive("ment1", "Tess", 4, "@Elan ping, the raid is forming"));
            await ChatSettle();
            var row60 = L.RowForTest("live60");
            check("edit: the row shows the new text and (edited)", row60 != null && VisualsOf<TextBox>(row60).Any(t => t.Text == "edited text here") && Shown(row60).Any(t => t.Text == "(edited)"));
            check("react: the reaction pill is drawn on the row", row60 != null && VisualsOf<Border>(row60).Any(b => b.Tag is ReactionVM && b.IsVisible));
            check("delete: the deleted message has no row and is gone from the channel", L.RowForTest("live61") == null && Msg("live61") == null);
            var ment = Msg("ment1"); var rowM = L.RowForTest("ment1");
            check("mention: a message that mentions me has the accent bar", ment != null && ment.Mentioned && rowM != null && VisualsOf<Border>(rowM).Any(b => b.Width == 2 && b.IsVisible && b.Background != null && ReferenceEquals(b.Background, ment.MentionBar)));

            // ---- replies: the quote line and the jump to the original far up
            await Feed(ChatLive("rep1", "Tess", 4, "replying to something old", ",\"replyTo\":{\"id\":\"c00100\",\"by\":\"Bob\",\"snippet\":\"chatter number 100\"}"));
            await Until(() => AtEnd() && OnScreen("rep1"));
            var rowR = L.RowForTest("rep1");
            check("reply: the row shows the quoted line", rowR != null && VisualsOf<TextBlock>(rowR).Any(t => t.IsVisible && string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)).Contains("chatter number 100")));
            var found = await L.JumpToAsync("long", Msg("rep1").ReplyId);
            await ChatSettle();
            var y100 = L.OffsetOfForTest("c00100");
            check("reply: the quote jumps to the original 500 messages up: near the top of the view, flashed (" + y100.ToString("0.0") + ")", found && Msg("c00100").Flash && Math.Abs(y100 - 36) < 2);
            Snapshot(Path.Combine(dir, "chatlist-2-jump.png"));
            check("jump: the view is not at the end any more, the pill offers Jump to present", !L.AtBottomForTest && L.JumpModeForTest == "present");

            // ---- size changes above the view must not move what I'm reading
            found = await L.JumpToAsync("long", "c00320");
            await ChatSettle();
            check("jump: a jump to a message further down the history lands it near the top", found && Math.Abs(L.OffsetOfForTest("c00320") - 36) < 2);
            var a2 = Anchor();
            late.SetResult(FakeThumb(9));
            await Until(() => Msg("c00300").VideoVisibility == Visibility.Visible);
            await ChatSettle();
            check("late card: a video card that appears above the view does not move it (" + (Same(a2, out var w2) ? "" : w2) + ")", Msg("c00300").VideoVisibility == Visibility.Visible && Same(a2, out _));
            a2 = Anchor();
            Msg("c00307").File.Image = ChatPicture(360, 240, 30);
            await ChatSettle();
            check("late picture: a picture that loads above the view does not move it (" + (Same(a2, out var w3) ? "" : w3) + ")", Same(a2, out _));
            Snapshot(Path.Combine(dir, "chatlist-3-picture-above.png"));
            a2 = Anchor();
            await Feed("{\"t\":\"edited\",\"channel\":\"long\",\"id\":\"c00310\",\"text\":\"now a much longer message\\nover\\nfour\\nlines\"}");
            await Feed("{\"t\":\"react\",\"channel\":\"long\",\"id\":\"c00311\",\"reaction\":\"lol\",\"users\":[\"Bob\"],\"by\":\"Bob\",\"on\":true}");
            await ChatSettle();
            check("above the view: an edit and a new reaction above do not move the view (" + (Same(a2, out var w4) ? "" : w4) + ")", Same(a2, out _));
            a2 = Anchor();
            await Feed("{\"t\":\"deleted\",\"channel\":\"long\",\"id\":\"c00312\"}");
            await ChatSettle();
            check("above the view: a deleted message above does not move the view (" + (Same(a2, out var w5) ? "" : w5) + ")", Same(a2, out _));

            // ---- size changes at the bottom keep the end in view
            L.ClickJumpForTest();
            await Until(() => AtEnd());
            await Feed(ChatLive("pic1", "Bob", 2, "", ",\"file\":{\"id\":\"pic1x\",\"name\":\"pic1.png\",\"size\":20000,\"mime\":\"image/png\"}"));
            await Until(() => AtEnd() && OnScreen("pic1"));
            Session.MessagesOf("long").First(m => m.Id == "pic1").File.Image = ChatPicture(360, 240, 90);
            await ChatSettle();
            check("late picture: a picture that loads in the last message keeps the list at the end", AtEnd() && EndOnScreen("pic1"));
            await Feed(ChatLive("vid1", "Tess", 4, "https://www.youtube.com/watch?v=chatlistLA2"));
            await Until(() => AtEnd() && OnScreen("vid1"));
            late2.SetResult(FakeThumb(77));
            await Until(() => Msg("vid1").VideoVisibility == Visibility.Visible);
            await ChatSettle();
            check("late card: a video card that appears in the last message keeps the list at the end", AtEnd() && EndOnScreen("vid1"));
            L.ScrollToEndForTest();
            await Until(() => AtEnd() && !Long().Unread);

            // ---- the very first message, and older history on the way up
            found = await L.JumpToAsync("long", "c00001");
            await ChatSettle();
            check("jump: to the first message of the channel (top of the list)", found && Msg("c00001").Flash && L.ScrollOffsetForTest < 1 && L.OffsetOfForTest("c00001") >= 0);
            check("history: at the top the note says it is the start (the server had no more)", await Until(() => L.OlderNoteForTest == "This is the start of #Long"));
            L.ClickJumpForTest();
            await Until(() => AtEnd());
            // scrolling up step by step (a wheel turn each) never makes the view jump, all the way to the first message
            bool still = true; int steps = 0; string bad = "";
            while (steps < 400 && !(L.ScrollOffsetForTest < 0.5 && !double.IsNaN(L.OffsetOfForTest("c00001"))))
            {
                var b = Anchor(); var ob = L.ScrollOffsetForTest;
                L.ScrollToVerticalForTest(Math.Max(0, ob - 240));
                await Settle();
                var moved = Math.Min(240, ob);
                var y = b.id == null ? double.NaN : L.OffsetOfForTest(b.id);
                if (double.IsNaN(y) || Math.Abs(y - (b.y + moved)) > 1.5) { still = false; if (bad.Length == 0) bad = b.id + " " + b.y.ToString("0") + "+" + moved.ToString("0") + " -> " + y.ToString("0"); }
                steps++;
            }
            check("scroll up: a wheel turn at a time up to the first message, nothing jumps on the way (" + steps + " turns" + (bad.Length > 0 ? ", " + bad : "") + ")", still && steps < 400);
            Snapshot(Path.Combine(dir, "chatlist-4-top.png"));
            L.ScrollToVerticalForTest(L.MessageScrollableForTest / 2);
            await ChatSettle();
            Snapshot(Path.Combine(dir, "chatlist-5-middle.png"));

            // ---- channels: back at the newest; with unread messages at the line
            Session.Select(Loot());
            await ChatSettle();
            Session.Select(Long());
            check("switch: coming back to a channel opens it at the newest message again (the place is not kept)", await Until(() => AtEnd() && OnScreen("vid1")));
            Session.Select(Loot());
            await ChatSettle();
            await Feed(ChatLive("away1", "Bob", 2, "while you were in Loot"));
            await Feed(ChatLive("away2", "Bob", 2, "and another"));
            Session.Select(Long());
            await ChatSettle();
            var yAway = L.OffsetOfForTest("away1");
            check("switch: with unread messages it opens at the 'New' line (" + yAway.ToString("0.0") + ")", Long().Divider?.Id == "away1" && !double.IsNaN(yAway) && yAway > 0 && yAway < L.ViewportForTest);
            L.ClickJumpForTest();
            await Until(() => AtEnd() && !Long().Unread);

            // ---- typing line
            await Feed("{\"t\":\"typing\",\"channel\":\"long\",\"id\":2}");
            check("typing: someone typing shows under the list", await Until(() => L.TypingTextForTest.Contains("typing")));

            // ---- selection, menu, keyboard
            var rowText = L.RowForTest("live62");
            var tb = rowText == null ? null : VisualsOf<TextBox>(rowText).FirstOrDefault(t => t.IsVisible);
            bool sel = false;
            if (tb != null) { tb.SelectAll(); sel = tb.SelectedText == Msg("live62").ShownText; tb.Select(0, 0); }
            check("select: a message's text is read-only selectable text (no default text menu)", tb != null && tb.IsReadOnly && tb.ContextMenu == null && sel);
            L.RightClickRowForTest("live62");
            check("menu: a right click on a row opens the message menu, with Copy for a text message", L.MenuOpenForTest && L.MenuCopyVisibleForTest);
            L.ClosePopupsForTest();
            await ChatSettle();
            var o1 = L.ScrollOffsetForTest;
            L.ListKeyForTest(Key.PageUp);
            await ChatSettle();
            var o2 = L.ScrollOffsetForTest;
            check("keys: Page Up scrolls the list up by about a page (" + o1.ToString("0") + " -> " + o2.ToString("0") + ")", o2 < o1 - L.ViewportForTest * 0.5);
            L.ListKeyForTest(Key.PageDown);
            await ChatSettle();
            check("keys: Page Down scrolls back", Math.Abs(L.ScrollOffsetForTest - o1) < 2);
            var oh = L.ScrollOffsetForTest;
            L.ListKeyForTest(Key.Home);
            await ChatSettle();
            var oh1 = L.ScrollOffsetForTest;
            L.ListKeyForTest(Key.End);
            await ChatSettle();
            check("keys: Home and End without Ctrl leave the list where it is (they are the list's left / right end)", Math.Abs(oh1 - oh) < 1 && Math.Abs(L.ScrollOffsetForTest - oh) < 1);
            L.ScrollToEndForTest();
            await ChatSettle();
            L.ComposerTextForTest = "";
            L.ComposerKeyForTest(Key.Up);
            check("keys: Up in an empty box edits my last message", L.ModeTextForTest.StartsWith("Editing") && L.ComposerTextForTest == "my own message, sent while scrolled up");
            L.ComposerKeyForTest(Key.Escape);
            check("keys: Esc cancels the edit", L.ModeTextForTest == "" && L.ComposerTextForTest == "");

            // ---- accessibility
            int textPeers = 0;
            void Walk(AutomationPeer p) { foreach (var c in p?.GetChildren() ?? new List<AutomationPeer>()) { if (c is TextBoxAutomationPeer tp && ((TextBox)tp.Owner).IsVisible) textPeers++; Walk(c); } }
            Walk(UIElementAutomationPeer.CreatePeerForElement(L.ChatListForTest));
            int textBoxes = VisualsOf<TextBox>(L.MessagesForTest).Count(t => t.IsVisible);
            check("a11y: the text of every built row is in the automation tree [" + textPeers + " / " + textBoxes + " in " + L.RowCountForTest + " rows]", textPeers > 0 && textPeers == textBoxes);
            var tbPeer = tb == null ? null : UIElementAutomationPeer.CreatePeerForElement(tb);
            check("a11y: a message's text is readable (value = the text)", tbPeer?.GetPattern(PatternInterface.Value) is IValueProvider vp && vp.Value == Msg("live62").ShownText && vp.IsReadOnly);
        }

        async Task ChatPerf(string dir, Action<string, bool> check, List<string> perf)
        {
            const int N = 5000;
            var L = LodgePage;
            Session.Disconnect();
            LodgePage.ShowChatForTest();
            Session.SentForTest = ChatPager;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // every 50th message is a picture that is in the cache (it loads as the session builds the message)
            var cache = Directory.CreateDirectory(Path.Combine(Util.DataDir, "cache")).FullName;
            var png = ChatPng(360, 240);
            for (int i = 7; i <= N; i += 50) File.WriteAllBytes(Path.Combine(cache, "img" + i + ".png"), png);
            Session.FeedForTest(ChatWelcome(N, now, null, "p"));
            var general = Session.TextChannels.First(c => c.Id == "long");
            Session.Select(Session.TextChannels.First(c => c.Id == "loot"));
            await ChatSettle();
            await Task.Delay(300);
            await ChatSettle();
            double Mb(long b) => b / 1048576.0;
            long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            long Ws() { var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64; }
            long m0 = Managed(), w0 = Ws();

            var sw = Stopwatch.StartNew();
            Session.Select(general);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
            double openMs = sw.Elapsed.TotalMilliseconds;
            check("perf: a 5000-message channel opens at its newest message", await Until(() => L.AtBottomForTest && !double.IsNaN(L.OffsetOfForTest("p05000"))));
            int rows = L.RowCountForTest;
            long m1 = Managed(), w1 = Ws();
            Snapshot(Path.Combine(dir, "chatlist-6-5000-bottom.png"));

            var stepsUp = new List<double>();
            for (int k = 0; k < 120; k++)
            {
                var t = Stopwatch.StartNew();
                L.ScrollToVerticalForTest(Math.Max(0, L.ScrollOffsetForTest - 240));
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                UpdateLayout();
                stepsUp.Add(t.Elapsed.TotalMilliseconds);
            }
            var stepsDown = new List<double>();
            for (int k = 0; k < 120; k++)
            {
                var t = Stopwatch.StartNew();
                L.ScrollToVerticalForTest(Math.Min(L.MessageScrollableForTest, L.ScrollOffsetForTest + 240));
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                UpdateLayout();
                stepsDown.Add(t.Elapsed.TotalMilliseconds);
            }
            sw.Restart();
            var found = await L.JumpToAsync("long", "p00100");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
            double jumpMs = sw.Elapsed.TotalMilliseconds;
            check("perf: a jump 4900 messages up finds the message", found && Math.Abs(L.OffsetOfForTest("p00100") - 36) < 2);
            int rowsAfterJump = L.RowCountForTest;
            Snapshot(Path.Combine(dir, "chatlist-7-5000-jump.png"));
            sw.Restart();
            L.ClickJumpForTest();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
            double presentMs = sw.Elapsed.TotalMilliseconds;
            check("perf: Jump to present returns to the newest", await Until(() => L.AtBottomForTest && !double.IsNaN(L.OffsetOfForTest("p05000"))));

            Session.Select(Session.TextChannels.First(c => c.Id == "loot"));
            await ChatSettle();
            sw.Restart();
            Session.Select(general);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
            double reopenMs = sw.Elapsed.TotalMilliseconds;

            string Stat(List<double> l) { var s = l.OrderBy(x => x).ToList(); return "median " + s[s.Count / 2].ToString("0.0") + " ms, p95 " + s[(int)(s.Count * 0.95)].ToString("0.0") + " ms, max " + s.Last().ToString("0.0") + " ms"; }
            perf.Add("perf 5000 messages (100 pictures, 100 video cards, replies, mentions, reactions): open " + openMs.ToString("0") + " ms, re-open " + reopenMs.ToString("0") + " ms, rows built " + rows);
            perf.Add("perf memory for the open channel: managed +" + Mb(m1 - m0).ToString("0.0") + " MB, private bytes +" + Mb(w1 - w0).ToString("0.0") + " MB");
            perf.Add("perf scroll up, 120 steps of 240 px (layout per step): " + Stat(stepsUp));
            perf.Add("perf scroll down, 120 steps of 240 px: " + Stat(stepsDown));
            perf.Add("perf jump to a message 4900 up: " + jumpMs.ToString("0") + " ms (rows built then " + rowsAfterJump + "), Jump to present: " + presentMs.ToString("0") + " ms");
            for (int i = 7; i <= N; i += 50) try { File.Delete(Path.Combine(cache, "img" + i + ".png")); } catch { }
        }
    }
}
