using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ElansAddonHub.Lodge;
using ElansAddonHub.Services;
using ElansAddonHub.Services.Atlas;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The Atlas page's connections to the rest of the app (the Lodge pool, the browser for web links, item links from chat and the Characters page).
    public partial class MainWindow
    {
        // Talents and Professions are hosted categories of the Atlas (tiles on its start page, ids "talents:..." and "professions:..."): one page each, built once
        public TalentsPage TalentsView;
        public ProfessionsPage ProfessionsView;
        WorldMapView worldMapView;
        public WorldMapView WorldMapView => worldMapView ?? (worldMapView = MakeWorldMap());
        bool talentsWired, professionsWired;
        public ClassicLodgeSync ClassicSync;

        void InitAtlas()
        {
            RegisterAtlasHosts();
            AtlasView.Init(() => settings.WowRoot);
            InitGlobalSearch();            // Ctrl+K and the title bar box: the Atlas palette over any page
            AtlasView.UseRouteSettings(settings, () => ProfessionsView?.RouteChoiceChanged());
            AtlasView.UseNavSettings(settings, () => SettingsStore.Save(settings));       // recents and pins of the Atlas shell
            AtlasQuery.Context.Lodge = () => Session?.AtlasData;
            AtlasQuery.Context.OwnFishSpots = () => AtlasObs.Read(settings.WowRoot)?.FishSpots;
            AtlasQuery.Context.OwnEntrances = () => AtlasObs.Read(settings.WowRoot)?.Entrances;
            AtlasQuery.Context.OwnNpcSpots = () => AtlasObs.Read(settings.WowRoot)?.NpcSpots;   // where this PC's Toolkit met, killed or saw npcs (Forever)
            // 2.95: the game's combat log files (Toolkit 0.5.12 switches logging on): read in the background, at most once a minute, when the Atlas asks
            AtlasQuery.Context.CombatLog = () => { if (CombatLogData.Due && !LinkOpen.SelfTestRun) CombatLogData.RefreshSoon(settings.WowRoot, ClientFlavor()); return CombatLogData.Current; };   // a self-test never reads the real logs
            CombatLogData.Changed += () => Dispatcher.BeginInvoke(new Action(() => AtlasView.PoolChanged()));
            AtlasQuery.Context.OwnFieldNotes = () => AtlasObs.Read(settings.WowRoot)?.FieldNotes;   // Hunter Helper's field notes on this PC
            AtlasQuery.Context.OwnNodes = () => AtlasObs.Read(settings.WowRoot)?.Nodes;      // the gathering nodes this PC's Hub observed (Forever)
            AtlasView.LodgeState = LodgeAtlasState;
            // the Cairn's data (give to get, 2.87): only from the user's own Lodge (feature "cairn2"), only while Atlas sharing is on
            CairnAtlas.Changed += () => Dispatcher.BeginInvoke(new Action(() => AtlasView.PoolChanged()));
            CairnAtlas.Fetch = (path, etag) => Session != null ? Session.CairnFetchAsync(path, etag) : Task.FromResult(new CairnAtlas.Response { Reason = "disabled" });
            if (Session != null) Session.CairnStatusChanged += () => Dispatcher.BeginInvoke(new Action(OnCairnStatus));
            CairnAtlas.Start(CairnWanted, CairnReachable);
            InitSharingInvite();           // 2.87: the calm sharing card, the quiet line on item / creature pages
            // 2.93: the Data sources page's facts - this PC's own observations and flight times, the Lodge's flight times, where the user stands with the Cairn
            AtlasView.OwnObservations = () => AtlasObs.Read(settings.WowRoot);
            AtlasView.OwnFlightLegs = () => FlightShare.ReadCached(settings.WowRoot, out _)?.Legs.Keys.ToList();
            AtlasView.LodgeFlightLegs = () => Session?.FlightData?.Legs.Keys.ToList();
            AtlasView.CairnFactsSource = () =>
            {
                var j = Session?.CairnJoin;
                return new CairnFacts
                {
                    HasLodge = !string.IsNullOrEmpty(settings.LodgeUrl), Online = Session != null && Session.Online, Sharing = !settings.AtlasShareOff, Guest = Session != null && Session.IsGuestNow,
                    LodgeHandsOut = Session != null && Session.SupportsCairnData, Refused = CairnAtlas.RefusedWhy ?? "",
                    JoinKnown = j != null, JoinOn = j != null && (j.On || j.Env), JoinStanding = j?.Standing ?? "",
                };
            };
            // 2.91: "Where this comes from" on Atlas Home, dismissed once and remembered (it stays on the Data sources page)
            AtlasView.SourcesCardDismissed = () => settings.AtlasSourcesCardDismissed;
            AtlasView.DismissSourcesCard = () => { settings.AtlasSourcesCardDismissed = true; SettingsStore.Save(settings); };
            InitAtlasHome();
            // "Who in the Lodge can craft this?" (Armory, server 2.19): cached answers; an answer that arrives redraws the recipe / item page and the Professions rows
            AtlasQuery.Context.Crafters = spells => Session?.CraftersFor(spells);
            if (Session != null) Session.CraftersChanged += () => Dispatcher.BeginInvoke(new Action(() => { AtlasView.CraftersChanged(); ProfessionsView?.CraftersChanged(); }));
            if (Session != null) Session.Changed += () => Dispatcher.BeginInvoke(new Action(() => AtlasView.PoolChanged()));
            ProfessionsView.UseCrafters(spells => Session?.CraftersFor(spells));
            // "Where does the Lodge stand with this faction?" (Armory, feature reputations): an answer redraws the faction page
            AtlasQuery.Context.Standings = faction => Session?.StandingsFor(faction);
            if (Session != null) Session.StandingsChanged += () => Dispatcher.BeginInvoke(new Action(() => AtlasView.StandingsChanged()));
            AtlasView.OpenLinkOverride = url => OpenAtlasLink(url);
            TalentsPage.OpenWebLink = OpenAtlasLink;                        // Talents > Open in Seemeta: the Outpost browser
            // the Lodge can hand out the Classic data (server 2.17): fetched automatically for members, shareable by the owner and officers
            ClassicSync = new ClassicLodgeSync(Session, () => Dispatcher.CheckAccess() ? AtlasView.EnsureClassicSetup() : Dispatcher.Invoke(() => AtlasView.EnsureClassicSetup()), settings);
            AtlasView.UseLodgeSync(ClassicSync);
            SettingsPage.UseClassicSync(ClassicSync);
            LodgePage.OpenAtlas += OpenInAtlas;
            InventoryView.OpenAtlas += OpenInAtlas;
            // links on the Professions page (recipes, reagents, sources) open their Atlas page
            ElansAddonHub.Services.Professions.ProfessionLinks.OpenRequested += OnProfessionLink;
        }

        // The two pages move into the Atlas: a category each, a nav row each (2.71.1). Order puts Talents right after Spells and Professions before Recipes and Skills.
        void RegisterAtlasHosts()
        {
            AtlasCategories.Register(new AtlasCategory
            {
                Id = AtlasHosts.Talents, Title = "Talents", Glyph = "", Blurb = "Plan a build for any class, try recommended builds, share a build code or link", Order = 77,
                Group = AtlasNav.Character, Keywords = "class build tree calculator",
                Host = HostTalents, SubTitle = AtlasHosts.TalentsSubTitle, Search = AtlasHosts.TalentHits,
                Canonical = sub => sub != null && sub.StartsWith("build/", StringComparison.OrdinalIgnoreCase) ? sub : TalentsSub(TalentsView.ClassFileNow, sub),
            });
            // the world map: a zoomable continent (WebView2 + Leaflet) with the layers of the Atlas on it
            AtlasCategories.Register(new AtlasCategory
            {
                Id = ElansAddonHub.Services.WorldMap.WorldMapIds.Category, Title = "World map", Glyph = "", Blurb = "Kalimdor and the Eastern Kingdoms from your own game files: zone names, flight paths, nodes, vendors, trainers, quest givers and rares", Order = 45,
                Group = AtlasNav.World, Keywords = "continent kalimdor eastern kingdoms flight paths 3d",
                Host = HostWorldMap, Search = ElansAddonHub.Services.WorldMap.WorldMapIds.Hits, SubTitle = WorldMapSubTitle, Canonical = sub => { WorldMapView.Navigate(sub); return WorldMapView.CurrentSub; },
                Available = () => MapData.Status != MapData.State.Disabled,
                NavCount = () => MapData.Index == null ? -1 : ElansAddonHub.Services.WorldMap.WorldMapService.Continents(MapData.Index).Count,     // the side menu's number: the continents it shows
            });
            AtlasCategories.Register(new AtlasCategory
            {
                Id = AtlasHosts.Professions, Title = "Professions", Glyph = AtlasKinds.Glyph("skill"), Blurb = "Every profession, primary and secondary: its recipes by skill, a leveling guide, what you can craft now, camping", Order = 88,
                Listed = true,      // 2.85: the row opens the list of professions (browse:professions); a profession opens its page (professions:cooking/recipes)
                Group = AtlasNav.Character, Keywords = "crafting leveling guide gathering alchemy blacksmithing enchanting engineering herbalism leatherworking mining skinning tailoring cooking first aid fishing",
                Host = HostProfessions, SubTitle = AtlasHosts.ProfessionsSubTitle, Search = AtlasHosts.ProfessionHits, NavCount = AtlasHosts.ProfessionCount,
                Canonical = sub => ProfessionsSub(ProfessionsView.ProfessionNow, ProfessionsView.TabNow),
            });
        }

        static string TalentsSub(string classFile, string keep) => string.IsNullOrEmpty(classFile) ? keep : AtlasHosts.TalentsId(classFile).Substring(AtlasHosts.Talents.Length + 1);
        static string ProfessionsSub(string profession, string tab) => AtlasHosts.ProfessionsId(profession, tab).Substring(AtlasHosts.Professions.Length + 1);

        FrameworkElement HostTalents(string sub)
        {
            if (!talentsWired)
            {
                talentsWired = true;
                TalentsView.Embed();
                TalentsView.ClassChanged += cf => AtlasView.HostedChanged(AtlasHosts.Talents, TalentsSub(cf, ""));
            }
            AtlasHosts.ParseTalents(sub, out var classFile, out var code);
            if (classFile.Length > 0) TalentsView.ShowClass(classFile);
            TalentsView.Shown();
            if (code.Length > 0) TalentsView.Import(code);
            return TalentsView;
        }

        WorldMapView MakeWorldMap()
        {
            var v = new WorldMapView { Settings = settings, SaveSettings = () => SettingsStore.Save(settings) };
            v.OpenAtlas += id => Dispatcher.BeginInvoke(new Action(() => { _ = AtlasView.Open(id); }));
            v.SubChanged += sub => AtlasView.HostedChanged(ElansAddonHub.Services.WorldMap.WorldMapIds.Category, sub);
            ElansAddonHub.Services.WorldMap.WorldMapLayers.SavedRoutes = () => settings.SavedRoutes;
            ElansAddonHub.Services.WorldMap.WorldMapLayers.FlightTimes = FlightTimesForMap;
            WireLive();
            v.Live = LivePos;
            // 2.97: the Lodge members who share their position with me (Lodge feature "liveposition"); the session leaves out whoever I may not see
            v.MemberDots = () => Session.MemberDots();
            Session.LivePositionsChanged += () => Dispatcher.BeginInvoke(new Action(() => v.PushMembers(false)));
            // learned flight paths (2.88): a new learned route, the Lodge's, or Clear redraws the Flight routes layer
            ElansAddonHub.Services.WorldMap.LearnedFlights.AnyChanged += () => Dispatcher.BeginInvoke(new Action(() => v.LayerDataChanged("routes")));
            // the Cairn's data (2.92: its nodes, vendors, rares and flight times are on the world map) arrived, was replaced or deleted: those layers again
            CairnAtlas.Changed += () => Dispatcher.BeginInvoke(new Action(() => { foreach (var l in new[] { "routes", "nodes", "vendors", "rares" }) v.LayerDataChanged(l); }));
            return v;
        }

        // "Show my live position on the world map": the strip's position (opt-in in the Hub) -> the trail (memory only) -> the world map's live layer
        public readonly ElansAddonHub.Services.WorldMap.LivePosition LivePos = new ElansAddonHub.Services.WorldMap.LivePosition();
        bool liveWired;
        void WireLive()
        {
            if (liveWired) return;
            liveWired = true;
            Session.Presence.PositionChanged += () => Dispatcher.BeginInvoke(new Action(UpdateLive));
            Session.Presence.TrailEvent += ev => { try { LivePos.AddEvent(settings.WorldMapLivePos, ev, DateTime.UtcNow); } catch (Exception e) { Util.Log("live position event: " + e.Message); } };
            UpdateLive();
        }
        // also called by Settings when the switch changes (off clears the trail at once)
        public void UpdateLive()
        {
            try { LivePos.Update(settings.WorldMapLivePos, Session.Presence.Playing, Session.Presence.Current, DateTime.UtcNow); } catch (Exception e) { Util.Log("live position: " + e.Message); }
            worldMapView?.PushLive(true);
            worldMapView?.PushMembers(true);
        }

        // "Horde|11>22" -> seconds: what the Lodge's members measured, then what this PC's Hunter Helper learned
        IDictionary<string, double> FlightTimesForMap()
        {
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            try { var m = Session?.FlightData; if (m != null) foreach (var kv in m.Legs) d[kv.Key] = kv.Value.T; } catch (Exception) { }
            try { var l = FlightShare.ReadCached(settings.WowRoot, out _); if (l != null) foreach (var kv in l.Legs) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value.T; } catch (Exception) { }
            return d;
        }

        FrameworkElement HostWorldMap(string sub)
        {
            WorldMapView.Navigate(sub);
            return WorldMapView;
        }

        string WorldMapSubTitle(string sub)
        {
            var idx = MapData.Index;
            var t = ElansAddonHub.Services.WorldMap.WorldMapIds.Parse(sub, idx == null ? (Func<string, int?>)null : ElansAddonHub.Services.WorldMap.WorldMapIds.SlugsOf(idx));
            if (!t.Valid || t.MapId < 0 || idx == null) return "";
            return ElansAddonHub.Services.WorldMap.WorldMapService.Continent(idx, t.MapId)?.Name ?? "";
        }

        FrameworkElement HostProfessions(string sub)
        {
            if (!professionsWired)
            {
                professionsWired = true;
                ProfessionsView.Embed();
                ProfessionsView.ViewChanged += (p, t) => AtlasView.HostedChanged(AtlasHosts.Professions, ProfessionsSub(p, t));
            }
            AtlasHosts.ParseProfessions(sub, out var slug, out var tab);
            ProfessionsView.Navigate(slug, tab);
            return ProfessionsView;
        }

        // Ctrl+6, ShowTab("talents") and friends: the Atlas on that category. Already inside it (on any of its sub-pages) nothing changes, so the class or profession stays.
        public void OpenAtlasCategory(string id)
        {
            TabAtlas.IsChecked = true;
            if ((AtlasView.CurrentId ?? "").StartsWith(id + ":", StringComparison.OrdinalIgnoreCase)) return;
            _ = AtlasView.Open(id + ":");
        }

        void OnProfessionLink(string id) => Dispatcher.BeginInvoke(new Action(() => OpenInAtlas(id)));

        // "Open in Atlas": an Atlas id ("item:123") or a name to search for
        public void OpenInAtlas(string idOrText)
        {
            ShowTab("atlas");
            _ = AtlasView.Open(idOrText);
        }

        // ---- Atlas Home (2.74): the character on the status strip and the Lodge's day, and the events that keep them current while Home is open
        void InitAtlasHome()
        {
            if (Session == null) return;
            AtlasView.LiveSource = () => new HomeLive { Char = Session.Presence.Current, Strip = Session.Presence.Strip?.Latest, Playing = Session.Presence.Playing };
            AtlasView.HomeLodgeSource = HomeLodgeNow;
            Session.Presence.Changed += () => Dispatcher.BeginInvoke(new Action(() => { AtlasView.LiveChanged(); AtlasView.HomeLodgeChanged(); }));
            Session.Changed += () => Dispatcher.BeginInvoke(new Action(() => AtlasView.HomeLodgeChanged()));
            // a member who comes, goes or moves to another zone changes "near you"
            System.ComponentModel.PropertyChangedEventHandler moved = (s, e) => { if (e.PropertyName == nameof(MemberVM.ZoneLine) || e.PropertyName == nameof(MemberVM.Playing) || e.PropertyName == nameof(MemberVM.SecondLine)) AtlasView.HomeLodgeChanged(); };
            foreach (var m in Session.Members) m.PropertyChanged += moved;
            Session.Members.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null) foreach (MemberVM m in e.NewItems) m.PropertyChanged += moved;
                if (e.OldItems != null) foreach (MemberVM m in e.OldItems) m.PropertyChanged -= moved;
                AtlasView.HomeLodgeChanged();
            };
        }

        // the Lodge's day as Home shows it (null = not connected to a Lodge)
        HomeLodge HomeLodgeNow()
        {
            if (Session == null || !Session.Online) return null;
            var now = DateTime.UtcNow;
            var l = new HomeLodge { Online = Session.Members.Count(m => m.IsMe || !m.Invisible), Members = HomeMembersOf(Session.Members) };
            var ev = Session.NextEvent;
            if (ev != null) { l.NextEventTitle = ev.Title ?? ""; l.NextEventUtc = ev.StartUtc; }
            if (Session.WowEventsEnabled)
            {
                var reset = Session.WowEvents.Where(e => e.WowKind == "raidreset" && e.StartUtc > now).OrderBy(e => e.StartUtc).FirstOrDefault();
                if (reset != null) { l.NextResetTitle = reset.Title ?? ""; l.NextResetUtc = reset.StartUtc; }
            }
            return l;
        }

        // the Lodge's members for Home: what they play and where, as far as they share it (the server leaves the zone out when they keep it to themselves,
        // and only the owner is ever sent members who appear offline - Home leaves those out too)
        public static List<HomeMember> HomeMembersOf(IEnumerable<MemberVM> members)
        {
            var l = new List<HomeMember>();
            foreach (var m in members ?? Enumerable.Empty<MemberVM>())
            {
                int lv = 0;
                var gl = m.GameLine ?? "";
                if (gl.StartsWith("Lv ", StringComparison.Ordinal)) int.TryParse(gl.Substring(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out lv);
                l.Add(new HomeMember
                {
                    Name = m.Display ?? "", CharName = m.CharName ?? "", Place = m.Playing ? m.Place ?? "" : "", Level = lv, Playing = m.Playing, Invisible = m.Invisible, IsMe = m.IsMe,
                    Status = m.StateText ?? "", Color = m.Playing ? m.ClassBrush : null,
                });
            }
            return l;
        }

        string LodgeAtlasState()
        {
            if (Session == null || !Session.Online) return "Not connected to a Lodge";
            if (!Session.SupportsAtlas) return "Connected, but this Lodge's server has no Atlas pool yet (it needs server 2.15)";
            return settings.AtlasShareOff ? "Connected · receiving the Lodge's observations, not sharing yours (Settings > Lodge)" : "Connected · sharing your observations and receiving the Lodge's";
        }

        // ---- the Cairn's data (give to get): wanted = sharing on and a Lodge set (the copy on disk may stay); reachable = the Lodge hands it out now
        bool CairnWanted() => !settings.AtlasShareOff && !string.IsNullOrEmpty(settings.LodgeUrl);
        bool CairnReachable() => CairnWanted() && Session != null && Session.SupportsCairnData && !Session.IsGuestNow;

        // what the Lodge said: yes -> fetch (at once when it holds a newer dataset); not for you (sharing off, guest, the Lodge not entitled,
        // an older Lodge without "cairn2") -> the copy goes; "not yet" -> the copy stays
        void OnCairnStatus()
        {
            if (Session == null) return;
            if (!CairnWanted()) { CairnAtlas.Clear(settings.AtlasShareOff ? "share" : null); return; }
            if (!Session.SupportsCairnData) { CairnAtlas.Clear("disabled"); return; }
            if (Session.CairnEntitled)
            {
                bool newer = CairnAtlas.Current == null || Session.CairnSeq != CairnAtlas.Current.Seq;
                _ = CairnAtlas.Refresh(CairnReachable, force: newer);
            }
            else if (CairnAtlas.NotEntitled(Session.CairnWhy)) CairnAtlas.Clear(Session.CairnWhy);
        }
    }
}
