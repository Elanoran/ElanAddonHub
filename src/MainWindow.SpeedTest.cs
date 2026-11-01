using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;

namespace ElansAddonHub
{
    // --selftest "speed" (on demand only: Tools\selftest.ps1 -Speed; never part of -Full or a changed run): how the Outpost FEELS with a lot of data.
    // Everyday actions (page switches, every Settings section, What's new, a chat with a long history, a Library of 300 items, the Atlas and its
    // search) each run 3 times; per action:
    //   show   - from the click until the page is drawn (the dispatcher idle, layout done, the next frame)
    //   freeze - the longest stretch the window could not redraw (a 16 ms ticker on the UI thread that arrives late), the click itself and 400 ms
    //            after it included, so work a page hands back to the UI thread later counts too
    // The data is big on purpose (the normal tests are small, which is how a 27,000-file map cache froze Settings unnoticed): a world map cache of
    // 30,000 files (made once, kept in the self-test cache), 1,500 chat messages, 300 Library items. Budgets mark what is slow; nothing FAILs.
    // The numbers go to speed.txt (a table) and speed-history.txt in the self-test cache, and each run is compared with the previous one.
    public partial class MainWindow
    {
        sealed class SpeedRow { public string Name; public long Cold, Show, Freeze, Budget; }
        const int SpeedFreezeBudget = 250;

        async Task<(long show, long freeze)> SpeedOnce(Func<Task> act)
        {
            long maxGap = 0; var last = Stopwatch.StartNew();
            var tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
            tick.Tick += (s, e) => { maxGap = Math.Max(maxGap, last.ElapsedMilliseconds); last.Restart(); };
            tick.Start();
            await Task.Delay(50); maxGap = 0; last.Restart();
            var sw = Stopwatch.StartNew();
            await act();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
            await NextFrame();
            long show = sw.ElapsedMilliseconds;
            await Task.Delay(400);
            tick.Stop();
            maxGap = Math.Max(maxGap, last.ElapsedMilliseconds);
            return (show, Math.Max(0, maxGap - 16));
        }

        // the next drawn frame (a hidden desktop may not draw: then 300 ms at most)
        Task NextFrame()
        {
            var tcs = new TaskCompletionSource<bool>();
            EventHandler h = null;
            h = (s, e) => { CompositionTarget.Rendering -= h; tcs.TrySetResult(true); };
            CompositionTarget.Rendering += h;
            return Task.WhenAny(tcs.Task, Task.Delay(300));
        }

        async Task<SpeedRow> Speed(string name, long budget, Func<Task> prep, Func<Task> act)
        {
            var shows = new List<long>(); long freeze = 0, cold = 0;
            for (int i = 0; i < 3; i++)
            {
                if (prep != null) { await prep(); await Settle(); await Task.Delay(150); }
                var (show, fr) = await SpeedOnce(act);
                if (i == 0) cold = show;
                shows.Add(show); freeze = Math.Max(freeze, fr);
            }
            shows.Sort();
            return new SpeedRow { Name = name, Cold = cold, Show = shows[1], Freeze = freeze, Budget = budget };
        }

        // a big world map cache, made once in the self-test cache (30,000 small files in the layout of the real one)
        static string SpeedMapCache()
        {
            var root = Path.Combine(SelfTestCache.CacheDir() ?? Path.GetTempPath(), "speed", "worldmap");
            var done = Path.Combine(root, "made-30000.txt");
            if (File.Exists(done)) return root;
            var payload = new byte[2048];
            int n = 0;
            for (int map = 0; map < 2 && n < 30000; map++)
                for (int a = 0; a < 64 && n < 30000; a++)
                {
                    var d = Path.Combine(root, "1.60.1.70374-speed", map.ToString(CultureInfo.InvariantCulture), "t", a.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(d);
                    for (int b = 0; b < 240 && n < 30000; b++, n++) File.WriteAllBytes(Path.Combine(d, b + ".bin"), payload);
                }
            File.WriteAllText(done, n + " files");
            return root;
        }

        async Task<string> SpeedTest(string dir)
        {
            var notes = new List<string>();
            var rows = new List<SpeedRow>();
            var savedMapRoot = ElansAddonHub.Services.WorldMap.WorldMapCache.RootOverride;
            FakeFilesServer fake = null;
            try
            {
                var prepClock = Stopwatch.StartNew();
                ElansAddonHub.Services.WorldMap.WorldMapCache.RootOverride = SpeedMapCache();
                notes.Add("note speed: the big map cache is ready in " + prepClock.ElapsedMilliseconds + " ms (" + ElansAddonHub.Services.WorldMap.WorldMapCache.RootOverride + ")");

                // ---- the pages
                foreach (var tab in new[] { "addons", "lodge", "inventory", "atlas", "settings" })
                    rows.Add(await Speed("Page: " + tab, 200, async () => { ShowTab(tab == "addons" ? "lodge" : "addons"); await Task.CompletedTask; }, async () => { ShowTab(tab); await Task.CompletedTask; }));

                // ---- every Settings section (General measures the map caches), then What's new from the note
                foreach (var sec in new[] { "general", "addons", "lodge", "profile", "privacy", "voice", "overlay", "about" })
                    rows.Add(await Speed("Settings: " + sec, 200, async () => { ShowTab("settings"); SettingsPage.Show(sec == "general" ? "about" : "general"); await Task.CompletedTask; },
                        async () => { SettingsPage.Show(sec); await Task.CompletedTask; }));
                rows.Add(await Speed("See what's new (note -> Settings > About)", 300, async () => { ShowTab("addons"); SettingsPage.ShowWhatsNewNote(); await Task.CompletedTask; },
                    async () => { SettingsPage.NoteForTest?.OpenForTest(); await Task.CompletedTask; }));

                // ---- the Lodge chat with a long history
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var hist = new StringBuilder();
                string[] who = { "Bob", "Mia", "Kor", "Dax" };
                for (int i = 0; i < 1500; i++)
                {
                    if (i > 0) hist.Append(',');
                    hist.Append("{\"t\":\"msg\",\"channel\":\"general\",\"id\":\"s" + i + "\",\"at\":" + (now - (1500 - i) * 60000L) + ",\"from\":" + Q(who[i % 4]) + ",\"fromId\":" + (2 + i % 4)
                        + ",\"text\":" + Q("Message " + i + ": Deadmines tonight? We need one more DPS, meet at the entrance at 20:00") + "}");
                }
                Session.Disconnect();
                ShowTab("lodge"); LodgePage.ShowChatForTest();
                Session.IsShownToUser = () => false;
                Session.FeedForTest(FunWelcome("owner", FunPeople(false), hist.ToString()));
                rows.Add(await Speed("Chat: open #general (1,500 messages)", 400, async () => { Session.Select(Session.TextChannels.First(c => c.Id == "loot")); await Task.CompletedTask; },
                    async () => { Session.Select(Session.TextChannels.First(c => c.Id == "general")); await Task.CompletedTask; }));

                // ---- the Library with 300 items (a third of them texts the page reads to learn their kind)
                fake = new FakeFilesServer();
                string[] cats = { "guide", "map", "ui", "build", null };
                for (int i = 0; i < 300; i++)
                {
                    string kind = i % 3 == 0 ? "addon" : i % 3 == 1 ? "text" : null;
                    var b = Encoding.UTF8.GetBytes(kind == "text" ? (i % 2 == 0 ? "#showtooltip\n/cast Lesser Heal " + i : "!WA:2!speed" + i) : "file " + i);
                    var r = new FakeFilesServer.SlotRec { Id = (1000 + i).ToString("x32"), Name = (kind == "addon" ? "Addon " : kind == "text" ? "Text " : "File ") + i.ToString("000", CultureInfo.InvariantCulture), CreatedBy = "Wing", Kind = kind, Cat = kind == null ? cats[i % 5] : null };
                    r.Versions.Add(new FakeFilesServer.Ver { V = 1, FileName = r.Name + (kind == "addon" ? ".zip" : ".txt"), Sha = LodgeFiles.Hex(System.Security.Cryptography.SHA256.Create().ComputeHash(b)), By = "Wing", Note = "", Bytes = b, At = 1_790_000_000_000L - i * 3_600_000L });
                    fake.Slots.Add(r);
                }
                Session.Disconnect();
                Session.FeedForTest(FilesWelcome("officer", true, "2.37.0", 1, addons: true, files2: true, text: true, info: true, kits: true, cats: true));
                Session.FilesClientForTest(new LodgeClient(fake.Url, FakeFilesServer.Code, "Elan"));
                Session.FeedForTest(fake.DataJson());
                rows.Add(await Speed("Library: open (300 items)", 400, async () => { LodgePage.ShowChatForTest(); await Task.CompletedTask; }, async () => { LodgePage.ShowFilesForTest(); await Task.CompletedTask; }));
                var fc = LodgePage.FilesForTest;
                rows.Add(await Speed("Library: sort by size", 250, null, async () => { fc.SortBy(nameof(SlotVM.SizeBytes)); await Task.CompletedTask; }));
                rows.Add(await Speed("Library: group on / off", 250, null, async () => { fc.GroupedForTest = !fc.GroupedForTest; await Task.CompletedTask; }));
                rows.Add(await Speed("Library: Tiles / Details", 400, null, async () => { fc.DetailsViewForTest = !fc.DetailsViewForTest; await Task.CompletedTask; }));
                fc.DetailsViewForTest = true; fc.GroupedForTest = true;

                // ---- the Atlas: Home and the Ctrl+K search
                ShowTab("atlas");
                rows.Add(await Speed("Atlas: Home", 300, async () => { await AtlasView.Open("browse:zones"); }, async () => { await AtlasView.GoStart(); }));
                rows.Add(await Speed("Atlas: Ctrl+K search", 250, async () => { AtlasView.PressPaletteForTest(System.Windows.Input.Key.Escape); await Task.CompletedTask; },
                    async () => { await AtlasView.OpenPalette("ro"); }));
                AtlasView.PressPaletteForTest(System.Windows.Input.Key.Escape);
            }
            catch (Exception e) { notes.Add("note speed: crashed - " + e); }
            finally
            {
                ElansAddonHub.Services.WorldMap.WorldMapCache.RootOverride = savedMapRoot;
                try { Session.Disconnect(); } catch { }
                fake?.Stop();
                ShowTab("addons");
            }

            // ---- the table, compared with the previous run
            var histFile = Path.Combine(SelfTestCache.CacheDir() ?? dir, "speed-history.txt");
            var prev = new Dictionary<string, (long show, long freeze)>();
            try
            {
                foreach (var l in File.Exists(histFile) ? File.ReadAllLines(histFile) : new string[0])
                {
                    var p = l.Split('|');
                    if (p.Length >= 5 && long.TryParse(p[3], out var s0) && long.TryParse(p[4], out var f0)) prev[p[2]] = (s0, f0);   // the last line per action wins
                }
            }
            catch { }
            var sb = new StringBuilder();
            sb.AppendLine("Speed " + App.Version + "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  (median of 3; cold = the first time; freeze = the longest the window could not redraw)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-46}{1,8}{2,8}{3,9}  {4,-22}{5}", "action", "show", "cold", "freeze", "previous", ""));
            int slow = 0;
            foreach (var r in rows)
            {
                var flags = new List<string>();
                if (r.Show > r.Budget) flags.Add("SLOW (budget " + r.Budget + ")");
                if (r.Freeze > SpeedFreezeBudget) flags.Add("FREEZE (budget " + SpeedFreezeBudget + ")");
                if (flags.Count > 0) slow++;
                var was = prev.TryGetValue(r.Name, out var pv) ? pv.show + " / " + pv.freeze + " ms" : "-";
                if (prev.ContainsKey(r.Name) && r.Show > Math.Max(pv.show * 1.5, pv.show + 50)) flags.Add("slower than before");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-46}{1,6} ms{2,6} ms{3,6} ms  {4,-22}{5}", r.Name, r.Show, r.Cold, r.Freeze, was, string.Join(", ", flags)));
            }
            sb.AppendLine(slow == 0 ? "  all within budget" : "  " + slow + " over budget");
            File.WriteAllText(Path.Combine(dir, "speed.txt"), sb.ToString());
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(histFile));
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                File.AppendAllLines(histFile, rows.Select(r => stamp + "|" + App.Version + "|" + r.Name + "|" + r.Show + "|" + r.Freeze));
            }
            catch { }
            notes.AddRange(sb.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(l => "note " + l.Trim()));
            return "speed: " + (slow == 0 ? "all within budget" : slow + " over budget") + "\r\n  " + string.Join("\r\n  ", notes);
        }
    }
}
