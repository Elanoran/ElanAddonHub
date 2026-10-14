using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Professions;

namespace ElansAddonHub
{
    // "Follow in game": the guide of one character and profession is written into Interface\AddOns\ElansHub\SharedData.lua (the Outpost's only write into the WoW
    // folder, see Services\SharedDataFile.cs) and Elan's Toolkit shows it in game after /reload or the next login. The followed guides are remembered in the settings.
    // They are made again (a) when the person follows or stops, (b) when the Lodge's or this PC's gathering nodes changed, (c) when a followed character's skill changed
    // (the Characters data was read again), always debounced and never on the UI thread.
    public partial class ProfessionsPage
    {
        Settings opts;
        Func<FlightShare.Merged> flightsNow = () => null;
        readonly DispatcherTimer followDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        readonly DispatcherTimer followWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        string followSig = "", followHash = "", followStatus = "", followNodes = "";
        long followAt;
        int followGen;
        Task followTask = Task.CompletedTask;
        bool followDirty;
        public TimeSpan FollowDebounce { get => followDebounce.Interval; set => followDebounce.Interval = value; }

        // settings: where the followed guides, the saved routes and the route choices live; flights: the Lodge's merged flight times (kept in the file next to the guides)
        public void UseSettings(Settings s, Func<FlightShare.Merged> flights = null)
        {
            opts = s;
            if (flights != null) flightsNow = flights;
            followDebounce.Tick -= FollowDebounceTick; followDebounce.Tick += FollowDebounceTick;
            followWatch.Tick -= FollowWatchTick; followWatch.Tick += FollowWatchTick;
            followWatch.Start();
        }

        void FollowDebounceTick(object sender, EventArgs e) { followDebounce.Stop(); followTask = RebuildFollowedAsync(); }

        void FollowWatchTick(object sender, EventArgs e)
        {
            if (opts?.FollowedGuides == null || opts.FollowedGuides.Count == 0) return;
            if (FollowSignature() != followSig) ScheduleFollowRebuild();
        }

        // a saved route was picked or dropped for a guide: the followed guides are made again
        public void RouteChoiceChanged() { if (opts?.FollowedGuides != null && opts.FollowedGuides.Count > 0) ScheduleFollowRebuild(); }

        void ScheduleFollowRebuild() { followDebounce.Stop(); followDebounce.Start(); }

        // what the followed guides were made from: the nodes (Lodge, own), the followed characters' skills
        string FollowSignature()
        {
            var sb = new StringBuilder();
            sb.Append(lodge()?.NodesChanged ?? 0).Append('|').Append(GatherNodes.OwnToken());
            foreach (var f in (opts?.FollowedGuides ?? new List<FollowedGuide>()).OrderBy(x => x.CharKey, StringComparer.Ordinal).ThenBy(x => x.SkillId))
            {
                var c = chars.FirstOrDefault(x => x.Key == f.CharKey);
                sb.Append('|').Append(f.CharKey).Append(':').Append(f.SkillId).Append(':').Append(c?.Skills.Values.Sum(v => v.Key) ?? -1);
            }
            return sb.ToString();
        }

        public bool IsFollowing(string charKey, int skillId) => opts?.FollowedGuides != null && opts.FollowedGuides.Any(f => f.CharKey == charKey && f.SkillId == skillId);

        public void SetFollow(ProfChar c, Profession p, bool on)
        {
            if (opts == null || c == null || p == null) return;
            if (opts.FollowedGuides == null) opts.FollowedGuides = new List<FollowedGuide>();
            opts.FollowedGuides.RemoveAll(f => f.CharKey == c.Key && f.SkillId == p.Id);
            if (on)
            {
                if (opts.FollowedGuides.Count(f => f.CharKey == c.Key) >= SharedDataFile.MaxGuidesPerChar) { followStatus = "At most " + SharedDataFile.MaxGuidesPerChar + " guides per character can be followed. Stop one first."; RenderBody(); return; }
                if (opts.FollowedGuides.Count >= FollowedGuide.MaxFollowed) { followStatus = "Too many followed guides. Stop one first."; RenderBody(); return; }
                opts.FollowedGuides.Add(new FollowedGuide { CharKey = c.Key, SkillId = p.Id, Camp = guideCamp });
            }
            try { SettingsStore.Save(opts); } catch (Exception e) { Util.Log("professions: settings: " + e.Message); }
            followStatus = on ? "Writing the guide..." : "Removing it from the game's file...";
            followTask = RebuildFollowedAsync();
            RenderBody();
        }

        // Makes every followed guide again and writes SharedData.lua. Returns when the file is written (the UI is updated on the way).
        public async Task RebuildFollowedAsync()
        {
            if (opts == null) return;
            var gen = ++followGen;
            var b = EnsureBook();
            var list = (opts.FollowedGuides ?? new List<FollowedGuide>()).ToList();
            var charsNow = chars.ToList();
            var root = wowRoot();
            if (b == null && list.Count > 0) { followDirty = true; return; }       // the recipe tables are not read yet: the next reload tries again
            if (list.Count > 0 && charsNow.Count == 0 && !fixedChars) { followDirty = true; return; }
            var routes = (opts.SavedRoutes ?? new List<SavedRoute>()).ToList();
            var choice = opts.RouteForGuide == null ? new Dictionary<string, string>() : new Dictionary<string, string>(opts.RouteForGuide);
            var src = sources; var w = world; var nodes = nodeSourceForTest ?? new LiveNodeSource();
            followSig = FollowSignature();
            var nodeToken = (lodge()?.NodesChanged ?? 0) + "|" + GatherNodes.OwnToken();
            if (nodeToken != followNodes) { followNodes = nodeToken; (w as LiveGatherWorld)?.Invalidate(); }     // new nodes: the gathering places are worked out again
            var flights = flightsNow();
            var prev = followHash; long prevAt = followAt;
            var res = await Task.Run(() =>
            {
                var o = new ProfGuidesOut();
                var missing = new List<string>();
                foreach (var f in list)
                {
                    var c = charsNow.FirstOrDefault(x => x.Key == f.CharKey);
                    var p = b?.ById(f.SkillId);
                    if (c == null || p == null) { missing.Add(f.CharKey); continue; }
                    try
                    {
                        int cap = p.MaxCap, start = Math.Max(1, Math.Min(cap - 1, c.SkillOf(p.Name)));
                        GuideOut g;
                        if (p.Gathering)
                        {
                            var gg = GatherGuideBuilder.Build(b, p, start, cap, w);
                            if (gg.Unavailable != null) { missing.Add(p.Name + ": " + gg.Unavailable); continue; }
                            g = GuideHandoff.FromGatherGuide(p, gg, nodes, choice, routes);
                        }
                        else g = GuideHandoff.FromSkillGuide(p, SkillGuideBuilder.Build(b, p, start, cap, src, new Dictionary<int, int>(c.Items), c.Name, false, f.Camp));
                        if (!o.Chars.TryGetValue(f.CharKey, out var l)) o.Chars[f.CharKey] = l = new List<GuideOut>();
                        l.Add(g);
                    }
                    catch (Exception e) { Util.Log("professions: follow: " + e); missing.Add(f.CharKey); }
                }
                string hash = SharedDataFile.BuildProfGuides(o, 0);
                hash = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(hash)));
                o.At = hash == prev && prevAt > 0 ? prevAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SharedDataFile.Current = o.IsEmpty ? null : o;
                var wr = SharedDataFile.Write(root, flights, o, DateTime.UtcNow);
                return new { o, hash, wr, missing };
            });
            followBuilds++;
            if (gen != followGen) return;
            followHash = res.hash; followAt = res.o.At;
            if (list.Count == 0) followStatus = res.wr != null && res.wr.Written.Count > 0 ? "Removed from the game's file. The game shows the change after /reload or your next login." : "";
            else if (res.o.IsEmpty) followStatus = "The guide could not be made yet" + (res.missing.Count > 0 ? " (" + string.Join("; ", res.missing.Take(2)) + ")" : "") + ".";
            else if (res.wr == null || res.wr.Written.Count + res.wr.Unchanged.Count == 0) followStatus = "Ready, but no Elan's Hub addon folder was found to write it to. Install or update Elan's Hub from the Addons page.";
            else followStatus = "Shows in game after /reload or your next login (needs Elan's Toolkit 0.4.0).";
            followDirty = false;
            Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded && tab == "guide") RenderBody(); }));
        }

        // the button and the status line of the Guide tab
        FrameworkElement FollowRow()
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            if (opts == null) return sp;
            bool can = who != null && prof != null && who.FromHub;
            bool on = can && IsFollowing(who.Key, prof.Id);
            var btn = new Button { Style = Sty(on ? "Button.M" : "Button.Primary.M"), Content = on ? "Stop following" : "Follow in game", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = can, Tag = "follow" };
            btn.ToolTip = can ? (on ? "Remove this guide from the game's file" : "Show this guide in game with Elan's Toolkit: the current step, reagents, shopping list and gathering routes")
                : who == null ? "Pick a character above" : "Needs this character's Elan's Hub data (log in with Elan's Hub installed)";
            var pw = who; var pp = prof;
            btn.Click += (s, e) => SetFollow(pw, pp, !on);
            sp.Children.Add(btn);
            followButton = btn;
            string text = followStatus;
            if (on && string.IsNullOrEmpty(text)) text = "Following in game. Shows after /reload or your next login (needs Elan's Toolkit 0.4.0).";
            if (!can && who != null) text = "To follow this guide in game, the character needs Elan's Hub data.";
            followLine = Line(text, "Text.Caption", null, 6);
            followLine.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(followLine);
            return sp;
        }

        Button followButton; TextBlock followLine;

        // ---- test hooks
        public Button FollowButtonForTest => followButton;
        public string FollowStatusForTest => followLine?.Text ?? followStatus;
        INodeSource nodeSourceForTest; int followBuilds;
        public INodeSource NodeSourceForTest { get => nodeSourceForTest; set => nodeSourceForTest = value; }
        public int FollowBuildsForTest => followBuilds;
        public Task FollowTaskForTest => followTask;
        public void FollowRebuildNowForTest() { followTask = RebuildFollowedAsync(); }
        public bool FollowDirtyForTest => followDirty;
        public void FollowSignatureCheckForTest() { if (FollowSignature() != followSig) ScheduleFollowRebuild(); }
        public void FollowStopDebounceForTest() { followDebounce.Stop(); }
        public bool FollowDebounceRunningForTest => followDebounce.IsEnabled;
        public Services.Professions.ProfChar WhoForTest => who;
        public Settings SettingsForTest => opts;
    }
}
