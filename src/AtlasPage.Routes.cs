using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Atlas.Classic;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The "Routes" panel under a zone map (like the classic Routes addon): pick the zone's gathering objects (herbs, ore, chests, fishing pools; several kinds in
    // one loop), build a closed loop through their nodes, see its length and time, save it under a name, and choose which saved route the in-game guide uses
    // for that zone. The loop comes from Services\Atlas\RouteBuilder.cs: Forever nodes (what players looted) first, the Classic baseline's positions only when
    // fewer than 8 Forever nodes are known.
    public partial class AtlasPage
    {
        Settings routeOpts;
        Action routeChoiceChanged;
        public INodeSource NodeSourceForTest;                       // tests hand in a hand-made source (null = the real layers)
        public readonly List<RoutesUi> routePanels = new List<RoutesUi>();
        public RoutesUi LastRoutesForTest => routePanels.LastOrDefault();

        // settings: saved routes and the choice per guide live there; changed: raised when the in-game guide has to be made again
        public void UseRouteSettings(Settings s, Action changed) { routeOpts = s; routeChoiceChanged = changed; }

        INodeSource Nodes => NodeSourceForTest ?? new LiveNodeSource();

        public sealed class RouteCandidate { public int Id; public string Name = "", Group = ""; public int Forever, Classic; }

        // the objects with nodes on a zone map (Forever or the Classic baseline's), grouped; builds the objects index the first time, so not on the UI thread
        public static List<RouteCandidate> CandidatesFor(INodeSource src, int zone, IEnumerable<int> extraIds)
        {
            var ids = new HashSet<int>(extraIds ?? Enumerable.Empty<int>());
            foreach (var id in GatherNodes.ObjectsWithNodes()) if (src.Forever(id, zone).Count > 0) ids.Add(id);
            try
            {
                var zc = AtlasDerived.For(ClassicAtlas.Current)?.Zone(MapData.Index, zone);
                if (zc != null) foreach (var id in zc.Objects.Keys) ids.Add(id);
            }
            catch (Exception) { }
            var res = new List<RouteCandidate>();
            foreach (var id in ids)
            {
                var c = new RouteCandidate { Id = id, Name = src.ObjectName(id), Group = src.GroupOf(id), Forever = src.Forever(id, zone).Count, Classic = src.Classic(id, zone).Count };
                if (string.IsNullOrEmpty(c.Group)) c.Group = GatherNodes.Other;
                if (c.Forever + c.Classic == 0) continue;
                if (c.Group == GatherNodes.Other && c.Forever == 0) continue;           // plain scenery of the Classic baseline is no gathering
                res.Add(c);
            }
            return res.OrderBy(c => Array.IndexOf(GatherNodes.Groups, c.Group)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).ToList();
        }

        // the profession a group of objects belongs to ("" = none: chests and the rest are not a profession)
        public static string ProfessionOfGroup(string group) => group == GatherNodes.Herbs ? "Herbalism" : group == GatherNodes.Ore ? "Mining" : group == GatherNodes.Fishing ? "Fishing" : "";

        // what the followed guides' gather steps need on a zone (the default ticks)
        public static List<int> GuideObjectsFor(int zone)
        {
            var res = new List<int>();
            var cur = SharedDataFile.Current;
            if (cur == null) return res;
            foreach (var g in cur.Chars.Values.SelectMany(l => l))
                foreach (var s in g.Steps.Where(s => s.K == "gather" && s.Zones != null && s.Zones.Contains(zone) && s.Objects != null))
                    foreach (var o in s.Objects) if (!res.Contains(o)) res.Add(o);
            return res;
        }

        FrameworkElement RoutesPanel(AtlasPageModel m, AtlasMap map, Func<int> zoneNow, out RoutesUi ui)
        {
            int pageObj = 0;
            if (m != null && m.Id != null && m.Id.StartsWith("obj:", StringComparison.Ordinal)) int.TryParse(m.Id.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out pageObj);
            ui = new RoutesUi(this, map, zoneNow, pageObj);
            routePanels.Add(ui);
            return ui.Root;
        }

        public sealed class RoutesUi
        {
            readonly AtlasPage page; readonly AtlasMap map; readonly Func<int> zoneNow; readonly int pageObject;
            public readonly Border Root = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
            readonly StackPanel body = new StackPanel();
            readonly StackPanel list = new StackPanel(), saved = new StackPanel();
            readonly TextBlock summary, status;
            readonly TextBox nameBox;
            readonly Button buildBtn, saveBtn;
            public List<RouteCandidate> Candidates = new List<RouteCandidate>();
            public readonly Dictionary<int, CheckBox> Checks = new Dictionary<int, CheckBox>();
            public RouteResult Last;
            int zone, gen;
            bool opened;
            TaskCompletionSource<bool> filled = new TaskCompletionSource<bool>();
            bool autoBuild;
            public List<int> Preselect;                              // "Show route" from the gathering guide: these objects are ticked and the loop is built at once
            public Task Loading => filled.Task;                      // completes when the objects of the zone are listed
            public int Zone => zone;
            public string SummaryText => summary.Text;
            public string StatusText => status.Text;
            public IReadOnlyList<Button> SavedButtons => savedButtons;
            readonly List<Button> savedButtons = new List<Button>();
            public Button BuildButton => buildBtn;
            public Button SaveButton => saveBtn;
            public TextBox NameBox => nameBox;

            public RoutesUi(AtlasPage p, AtlasMap map, Func<int> zoneNow, int pageObject)
            {
                page = p; this.map = map; this.zoneNow = zoneNow; this.pageObject = pageObject;
                Root.Child = body;
                body.Children.Add(p.Tb("Routes", "Text.Section"));
                var how = p.Tb("Tick what to gather and build a loop through the nodes players looted (Forever). Fewer than 8 Forever nodes known: the Classic baseline's positions fill it, and the line says so.", "Text.Caption", null, 2);
                how.TextWrapping = TextWrapping.Wrap; body.Children.Add(how);
                body.Children.Add(list);
                var bar = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                buildBtn = new Button { Style = (Style)p.FindResource("Button.Primary.M"), Content = "Build route", Margin = new Thickness(0, 0, 8, 8) };
                buildBtn.Click += (s, e) => Build();
                bar.Children.Add(buildBtn);
                nameBox = new TextBox { Style = (Style)p.FindResource("Input"), Width = 180, Margin = new Thickness(0, 0, 8, 8), Tag = "Name this route", MaxLength = SavedRoute.MaxNameLength, VerticalAlignment = VerticalAlignment.Center };
                bar.Children.Add(nameBox);
                saveBtn = new Button { Style = (Style)p.FindResource("Button.M"), Content = "Save route", Margin = new Thickness(0, 0, 8, 8), IsEnabled = false };
                saveBtn.Click += (s, e) => Save(nameBox.Text);
                bar.Children.Add(saveBtn);
                var clear = new Button { Style = (Style)p.FindResource("Button.M"), Content = "Clear route", Margin = new Thickness(0, 0, 8, 8) };
                clear.Click += (s, e) => { map.ClearRoute(); Last = null; summary.Text = ""; saveBtn.IsEnabled = false; };
                bar.Children.Add(clear);
                body.Children.Add(bar);
                summary = p.Tb("", "Text.Secondary", null, 0); summary.TextWrapping = TextWrapping.Wrap; body.Children.Add(summary);
                status = p.Tb("", "Text.Caption", Res("Brush.Text.Tertiary"), 2); status.TextWrapping = TextWrapping.Wrap; body.Children.Add(status);
                body.Children.Add(saved);
                map.MapChanged += id => { if (opened) Dispatch(Refresh); };
            }

            void Dispatch(Action a) { page.Dispatcher.BeginInvoke(a); }

            public bool IsOpen => Root.Visibility == Visibility.Visible;

            public void Toggle(bool autoBuild)
            {
                if (IsOpen) { Root.Visibility = Visibility.Collapsed; return; }
                Root.Visibility = Visibility.Visible;
                opened = true;
                this.autoBuild = (autoBuild && pageObject > 0) || Preselect != null;
                Refresh();
            }

            // the zone on the map changed (or the panel opened): the objects are looked up again
            public void Refresh()
            {
                int z = zoneNow();
                if (z <= 0) { status.Text = "The map is still loading."; return; }
                zone = z;
                int my = ++gen;
                status.Text = "Looking up the nodes...";
                var src = page.Nodes; int po = pageObject;
                if (filled.Task.IsCompleted) filled = new TaskCompletionSource<bool>();
                Task.Run(() =>
                {
                    var cands = CandidatesFor(src, z, po > 0 ? new[] { po } : null);
                    var guide = GuideObjectsFor(z);
                    return Tuple.Create(cands, guide);
                }).ContinueWith(t => page.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (my != gen) return;
                    if (t.IsFaulted) { status.Text = "The nodes could not be looked up."; Util.Log("atlas routes: " + t.Exception?.GetBaseException().Message); filled.TrySetResult(false); return; }
                    Fill(t.Result.Item1, t.Result.Item2);
                    if (autoBuild && Selected().Count > 0) { autoBuild = false; Build(); }
                    filled.TrySetResult(true);
                })));
            }

            void Fill(List<RouteCandidate> cands, List<int> guideObjs)
            {
                Candidates = cands;
                list.Children.Clear(); Checks.Clear();
                status.Text = "";
                if (cands.Count == 0) list.Children.Add(page.Tb("No gathering nodes are known on this map yet. They come from players' Elan's Toolkit (Forever) and the Classic baseline.", "Text.Secondary", null, 8));
                bool anyDefault = guideObjs.Any(g => cands.Any(c => c.Id == g));
                foreach (var grp in GatherNodes.Groups)
                {
                    var members = cands.Where(c => c.Group == grp).ToList();
                    if (members.Count == 0) continue;
                    list.Children.Add(page.Tb(GatherNodes.GroupTitle(grp), "Text.RowTitle", null, 10));
                    var wp = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                    foreach (var c in members)
                    {
                        var cell = new StackPanel { Orientation = Orientation.Horizontal, Width = 300, Margin = new Thickness(0, 0, 12, 8) };
                        var cb = new CheckBox { Style = (Style)page.FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center, Tag = c.Id };
                        cb.IsChecked = Preselect != null ? Preselect.Contains(c.Id) : anyDefault ? guideObjs.Contains(c.Id) : c.Id == pageObject;
                        cb.Click += (s, e) => { };
                        Checks[c.Id] = cb;
                        cell.Children.Add(cb);
                        var t = page.Tb("  " + c.Name, "Text.Body"); t.VerticalAlignment = VerticalAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxWidth = 170;
                        cell.Children.Add(t);
                        var cnt = page.Tb("  " + (c.Forever > 0 ? c.Forever + " " + AtlasLabel.Forever : "") + (c.Forever > 0 && c.Classic > 0 ? ", " : "") + (c.Classic > 0 ? c.Classic + " Classic" : ""), "Text.Caption");
                        cnt.VerticalAlignment = VerticalAlignment.Center; cell.Children.Add(cnt);
                        wp.Children.Add(cell);
                    }
                    list.Children.Add(wp);
                }
                if (anyDefault) status.Text = "Ticked: what your followed guide's gathering step needs here.";
                RefreshSaved();
            }

            public List<int> Selected() => Checks.Where(k => k.Value.IsChecked == true).Select(k => k.Key).OrderBy(i => i).ToList();

            public void Check(int objectId, bool on) { if (Checks.TryGetValue(objectId, out var cb)) cb.IsChecked = on; }

            public RouteResult Build()
            {
                var sel = Selected();
                if (sel.Count == 0) { summary.Text = "Tick at least one object."; Last = null; map.ClearRoute(); saveBtn.IsEnabled = false; return null; }
                var src = page.Nodes;
                var r = GatherRoutes.Build(src, sel, zone);
                Last = r;
                Show(r, src);
                summary.Text = r.Summary() + (r.Count > 0 ? " · " + (r.UsedClassic ? "includes the " + AtlasLabel.Classic + "'s positions" : "all " + AtlasLabel.Forever + " nodes") : "");
                status.Text = r.Note;
                saveBtn.IsEnabled = r.Count > 0;
                if (nameBox.Text.Length == 0 && r.Count > 0) nameBox.Text = Suggest(sel, src);
                return r;
            }

            string Suggest(List<int> sel, INodeSource src)
            {
                var n = string.Join(" + ", sel.Take(3).Select(src.ObjectName));
                var z = AtlasQuery.ZoneName(zone);
                return SavedRoute.CleanName(z + " " + n);
            }

            void Show(RouteResult r, INodeSource src)
            {
                map.SetRoute(r.Points.Select(p => new AtlasMap.RouteStop(p.X, p.Y, p.Object > 0 ? src.ObjectName(p.Object) : "", p.Forever)));
            }

            public SavedRoute Save(string name)
            {
                if (Last == null || Last.Count == 0 || page.routeOpts == null) return null;
                var s = page.routeOpts;
                if (s.SavedRoutes == null) s.SavedRoutes = new List<SavedRoute>();
                if (s.SavedRoutes.Count >= SavedRoute.MaxSaved) { status.Text = "You can keep " + SavedRoute.MaxSaved + " saved routes. Delete one first."; return null; }
                var sr = SavedRoute.From(name, zone, Selected(), Last, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                s.SavedRoutes.Add(sr);
                Persist();
                status.Text = "Saved as \"" + sr.Name + "\".";
                RefreshSaved();
                return sr;
            }

            void Persist() { try { SettingsStore.Save(page.routeOpts); } catch (Exception e) { Util.Log("atlas routes: settings: " + e.Message); } }

            public void Delete(string id)
            {
                var s = page.routeOpts; if (s?.SavedRoutes == null) return;
                bool used = s.RouteForGuide != null && s.RouteForGuide.ContainsValue(id);
                s.SavedRoutes.RemoveAll(x => x.Id == id);
                if (s.RouteForGuide != null) foreach (var k in s.RouteForGuide.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) s.RouteForGuide.Remove(k);
                Persist(); RefreshSaved();
                if (used) page.routeChoiceChanged?.Invoke();
            }

            // the saved route is the one the guide hands to the game for its profession and zone (again: back to the automatic loop)
            public void UseForGuide(string id)
            {
                var s = page.routeOpts; var sr = s?.SavedRoutes?.FirstOrDefault(x => x.Id == id);
                if (sr == null) return;
                var prof = ProfessionOfRoute(sr);
                if (prof.Length == 0) return;
                if (s.RouteForGuide == null) s.RouteForGuide = new Dictionary<string, string>();
                var key = ElansAddonHub.Services.Professions.GuideHandoff.RouteKey(prof, sr.Zone);
                if (s.RouteForGuide.TryGetValue(key, out var have) && have == id) s.RouteForGuide.Remove(key); else s.RouteForGuide[key] = id;
                Persist(); RefreshSaved();
                page.routeChoiceChanged?.Invoke();
            }

            string ProfessionOfRoute(SavedRoute sr)
            {
                var src = page.Nodes;
                foreach (var o in sr.Objects ?? new List<int>()) { var p = ProfessionOfGroup(src.GroupOf(o)); if (p.Length > 0) return p; }
                return "";
            }

            public void ShowSaved(string id)
            {
                var sr = page.routeOpts?.SavedRoutes?.FirstOrDefault(x => x.Id == id);
                if (sr == null) return;
                var nodes = sr.ToNodes();
                var y = page.Nodes.MapYards(sr.Zone);
                var len = RouteBuilder.LoopLength(nodes, y.W > 0 ? y.W : 1, y.H > 0 ? y.H : 1);
                var r = new RouteResult { Points = nodes, Length = len, Yards = y.W > 0, Minutes = y.W > 0 && nodes.Count >= 2 ? len / RouteBuilder.RunYardsPerSecond / 60.0 : 0, UsedClassic = sr.Src == "c" };
                Last = r;
                if (map.UiMapId != sr.Zone) map.ShowMap(sr.Zone);
                Show(r, page.Nodes);
                summary.Text = "\"" + sr.Name + "\": " + r.Summary();
                saveBtn.IsEnabled = false;
            }

            public void RefreshSaved()
            {
                saved.Children.Clear(); savedButtons.Clear();
                var s = page.routeOpts;
                var mine = (s?.SavedRoutes ?? new List<SavedRoute>()).Where(x => x.Zone == zone).ToList();
                if (mine.Count == 0) return;
                saved.Children.Add(page.Tb("Saved routes for " + AtlasQuery.ZoneName(zone), "Text.RowTitle", null, 14));
                foreach (var sr in mine)
                {
                    var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                    var nm = page.Tb(sr.Name + "  ", "Text.Body"); nm.VerticalAlignment = VerticalAlignment.Center; nm.Margin = new Thickness(0, 0, 4, 0); row.Children.Add(nm);
                    var cnt = page.Tb(AtlasFormat.Count((sr.Points?.Count ?? 0) / 2, "node", "nodes") + (sr.Src == "c" ? " · includes Classic" : " · Forever") + "   ", "Text.Caption"); cnt.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(cnt);
                    var id = sr.Id;
                    var show = new Button { Style = (Style)page.FindResource("Button.S"), Content = "Show", Margin = new Thickness(0, 0, 8, 0), Tag = id }; show.Click += (a, b) => ShowSaved(id); row.Children.Add(show);
                    string prof = ProfessionOfRoute(sr);
                    bool used = prof.Length > 0 && s.RouteForGuide != null && s.RouteForGuide.TryGetValue(ElansAddonHub.Services.Professions.GuideHandoff.RouteKey(prof, sr.Zone), out var cur) && cur == id;
                    var use = new Button { Style = (Style)page.FindResource(used ? "Button.Primary.S" : "Button.S"), Content = used ? "Used for the " + prof + " guide" : "Use for guide", Margin = new Thickness(0, 0, 8, 0), IsEnabled = prof.Length > 0, Tag = id };
                    use.ToolTip = prof.Length > 0 ? "The in-game " + prof + " guide takes this loop for this zone instead of the automatic one" : "Only herb, ore and fishing routes belong to a guide";
                    use.Click += (a, b) => UseForGuide(id);
                    row.Children.Add(use); savedButtons.Add(use);
                    var del = new Button { Style = (Style)page.FindResource("Button.S"), Content = "Delete", Tag = id }; del.Click += (a, b) => Delete(id); row.Children.Add(del);
                    saved.Children.Add(row);
                }
            }
        }
    }
}
