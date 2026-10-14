using System.Text.Json.Nodes;

namespace Lodge;

// Badges and the ladder (feature "badges", see Badges.cs for the rules and the definitions table).
// Client to server: badges.mine {} (own badges with progress), badges.seen {} (looked at them: the quiet "new" dot goes), badges.hide { hide } (leave the ladder),
// badges.member { name } (another member's earned badges), ladder.get { season: "2026-10" | "all" }, admin.badges { on } (Guild Master: the whole feature on / off).
// Server to client: badges.mine, badges.member, ladder.data, badges.state { enabled, locked } (sent to everybody when the owner flips the switch).
// Computed on a timer (every 10 minutes) and when a member asks (at most every 30 seconds). Guests have no badges and are not on the ladder.
// Feature off (LODGE_BADGES_ENABLED=0 or the owner's switch): not advertised, every request answers an error "disabled"; stored data stays on disk.
public sealed class BadgesModule : IModule
{
    readonly LodgeHub hub;
    public readonly BadgesStore Store;
    DateTime lastRun = DateTime.MinValue;
    readonly object runGate = new();
    Dictionary<string, Dictionary<string, long>> metricsOf = new(StringComparer.Ordinal);   // identity -> metrics as of the last run (under Store.Gate)

    public BadgesModule(LodgeHub hub)
    {
        this.hub = hub;
        Store = new BadgesStore(hub.Cfg.DataDir, hub.Log);
        hub.AtlasPool.Store.Tallied += OnAtlasTally;
    }

    public bool Enabled => hub.Cfg.BadgesEnabled && Store.Enabled;
    public bool Locked => !hub.Cfg.BadgesEnabled;

    static string Key(string name) => ProfileRules.Key(name);
    static bool CanHave(Member me) => me != null && me.Personal && me.Role != "guest";
    static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case "badges.mine": await Mine(me); return true;
            case "badges.seen": await Seen(me); return true;
            case "badges.hide": await Hide(me, m); return true;
            case "badges.member": await MemberView(me, m); return true;
            case "ladder.get": await LadderGet(me, m); return true;
            case "admin.badges": await Switch(me, m); return true;
        }
        return false;
    }

    public JsonObject StateJson() => new() { ["t"] = "badges.state", ["enabled"] = Enabled, ["locked"] = Locked };

    // ---------------------------------------------------------------- the requests

    async Task<bool> Gate(Member me, bool needMember)
    {
        if (!Enabled) { await hub.Error(me, "Badges are switched off on this lodge", "disabled"); return false; }
        if (needMember && !CanHave(me)) { await hub.Error(me, "Guests have no badges", "guest"); return false; }
        if (!me.Budget.Badges.TryTake()) { me.CountDrop(hub.Clock); await hub.Error(me, "Slow down a little", "rate"); return false; }
        return true;
    }

    async Task Mine(Member me)
    {
        if (!await Gate(me, true)) return;
        EnsureFresh();
        JsonObject o;
        lock (Store.Gate) o = MineJson(me);
        await hub.Send(me, o);
    }

    async Task Seen(Member me)
    {
        if (!Enabled || !CanHave(me)) return;
        lock (Store.Gate)
        {
            var r = Store.Get(Key(me.Name));
            if (r == null) return;
            r.SeenMs = new DateTimeOffset(hub.Clock.UtcNow).ToUnixTimeMilliseconds();
            Store.Touch();
        }
        await Task.CompletedTask;
    }

    async Task Hide(Member me, JsonObject m)
    {
        if (!await Gate(me, true)) return;
        EnsureFresh();
        JsonObject o;
        lock (Store.Gate)
        {
            var r = Store.Get(Key(me.Name));
            if (r == null) { o = null; }
            else { r.Hide = J.Bool(m, "hide"); Store.Touch(); o = MineJson(me); }
        }
        if (o == null) { await hub.Error(me, "Nothing to change yet - try again in a moment"); return; }
        hub.Log.LogInformation("{Name} {Action} the ladder", me.Name, o["hide"]!.GetValue<bool>() ? "left" : "joined");
        await hub.Send(me, o);
    }

    async Task MemberView(Member me, JsonObject m)
    {
        if (!await Gate(me, false)) return;
        var name = J.Str(m, "name") ?? "";
        EnsureFresh();
        var o = new JsonObject { ["t"] = "badges.member", ["ok"] = true, ["name"] = Names.Cut(name, Names.Max) };
        lock (Store.Gate)
        {
            var r = name.Length > 0 ? Store.Get(Key(name)) : null;
            var arr = new JsonArray();
            if (r != null) foreach (var id in Badges.TopIds(r.Earned, int.MaxValue)) arr.Add(EarnedJson(Badges.Find(id), r.Earned[id]));
            o["badges"] = arr;
            o["count"] = arr.Count;
        }
        await hub.Send(me, o);
    }

    async Task LadderGet(Member me, JsonObject m)
    {
        if (!await Gate(me, false)) return;
        EnsureFresh();
        var season = J.Str(m, "season");
        JsonObject o;
        lock (Store.Gate) o = LadderJson(me, Badges.ValidSeason(season) ? season : Store.Season);
        await hub.Send(me, o);
    }

    async Task Switch(Member me, JsonObject m)
    {
        if (!Roles.Can(me.Role, Perm.ManageMembers)) { await hub.Error(me, "Your rank can't do that"); return; }
        if (Locked) { await hub.Error(me, "Badges are switched off in the server settings", "locked"); return; }
        bool on = J.Bool(m, "on", true);
        lock (Store.Gate) { Store.SetEnabled(on); Store.Save(); }
        hub.Log.LogInformation("{By} switched badges and the ladder {State}", me.Name, on ? "on" : "off");
        if (on) EnsureFresh();
        await hub.Broadcast(StateJson());
    }

    // ---------------------------------------------------------------- views (callers hold Store.Gate)

    static JsonObject EarnedJson(Badges.Def d, long at) => new()
    {
        ["id"] = d.Id, ["family"] = d.Family, ["tier"] = d.Tier, ["name"] = d.Name, ["at"] = at,
    };

    JsonObject MineJson(Member me)
    {
        var key = Key(me.Name);
        var r = Store.Get(key);
        var metrics = metricsOf.GetValueOrDefault(key) ?? new Dictionary<string, long>();
        var badges = new JsonArray();
        foreach (var d in Badges.Table.OrderBy(x => x.Family == "secret").ThenBy(x => Array.IndexOf(Badges.FamilyNames.Keys.ToArray(), x.Family)).ThenBy(x => x.Tier))
        {
            bool earned = r != null && r.Earned.ContainsKey(d.Id);
            var b = new JsonObject { ["id"] = d.Id, ["family"] = d.Family, ["familyName"] = Badges.FamilyNames.GetValueOrDefault(d.Family, d.Family), ["tier"] = d.Tier, ["hidden"] = d.Hidden, ["earned"] = earned };
            if (d.Hidden && !earned)
            {
                b["name"] = "???"; b["flavour"] = ""; b["how"] = "???";
            }
            else
            {
                b["name"] = d.Name; b["flavour"] = d.Flavour; b["how"] = d.How;
                if (earned) b["at"] = r.Earned[d.Id];
                if (d.Special == null) { b["value"] = Math.Min(metrics.GetValueOrDefault(d.Metric), d.At); b["target"] = d.At; }
                else { b["value"] = r == null ? 0 : FamiliesEarned(r); b["target"] = d.At; }
            }
            badges.Add(b);
        }
        var seasonRank = RankOf(key, true, key, out var seasonPts);
        var allRank = RankOf(key, false, key, out var allPts);
        long seenMs = r?.SeenMs ?? 0;
        return new JsonObject
        {
            ["t"] = "badges.mine", ["ok"] = true, ["you"] = me.Name, ["hide"] = r?.Hide ?? false,
            ["new"] = r == null ? 0 : r.Earned.Count(kv => kv.Value * 1000 > seenMs),
            ["points"] = allPts, ["rank"] = allRank, ["season"] = Store.Season, ["seasonPoints"] = seasonPts, ["seasonRank"] = seasonRank,
            ["earned"] = r?.Earned.Count ?? 0, ["total"] = Badges.Table.Length,
            ["badges"] = badges,
        };
    }

    static int FamiliesEarned(BadgesStore.Rec r) => r.Earned.Keys.Select(id => Badges.Find(id)?.Family).Where(f => f != null && f != "secret").Distinct().Count();

    // rank of `key` among the visible members (the member themself always counts); 0 = no points yet
    int RankOf(string key, bool season, string viewer, out long points)
    {
        points = 0;
        var rows = Rows(season, viewer);
        var mine = rows.FirstOrDefault(x => x.Key == key);
        if (mine == null) return 0;
        points = mine.Points;
        return mine.Rank;
    }

    record Row(string Key, string Name, long Points, int Rank, bool Hidden, BadgesStore.Rec Rec);

    List<Row> Rows(bool season, string viewer)
    {
        var list = Store.All.Select(kv => (Key: kv.Key, Rec: kv.Value, Pts: season ? kv.Value.SeasonPoints : kv.Value.Points))
            .Where(x => x.Pts > 0 && (!x.Rec.Hide || x.Key == viewer))
            .OrderByDescending(x => x.Pts).ThenBy(x => x.Rec.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return Rank(list.Select(x => (x.Key, x.Rec.Name, x.Pts, x.Rec.Hide, x.Rec)));
    }

    static List<Row> Rank(IEnumerable<(string Key, string Name, long Pts, bool Hide, BadgesStore.Rec Rec)> sorted)
    {
        var rows = new List<Row>();
        foreach (var x in sorted)
        {
            int rank = rows.Count > 0 && rows[^1].Points == x.Pts ? rows[^1].Rank : rows.Count + 1;
            rows.Add(new Row(x.Key, x.Name, x.Pts, rank, x.Hide, x.Rec));
        }
        return rows;
    }

    JsonObject LadderJson(Member me, string season)
    {
        var viewer = CanHave(me) ? Key(me.Name) : "";
        List<Row> rows;
        if (season == "all" || season == Store.Season) rows = Rows(season != "all", viewer);
        else
        {
            var hist = Store.HistoryOf(season);
            var list = (hist ?? new List<BadgesStore.HistRow>())
                .Select(h => (Key: h.Key, Name: h.Name, Pts: h.Points, Rec: Store.Get(h.Key)))
                .Where(x => x.Pts > 0 && x.Rec != null && (!x.Rec.Hide || x.Key == viewer))
                .OrderByDescending(x => x.Pts).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => (x.Key, x.Rec.Name, x.Pts, x.Rec.Hide, x.Rec));
            rows = Rank(list);
        }
        var arr = new JsonArray();
        foreach (var row in rows.Take(100))
        {
            var top = new JsonArray();
            foreach (var id in Badges.TopIds(row.Rec.Earned, 3)) top.Add(EarnedJson(Badges.Find(id), row.Rec.Earned[id]));
            var o = new JsonObject { ["rank"] = row.Rank, ["name"] = row.Name, ["points"] = row.Points, ["top"] = top };
            if (row.Key == viewer) { o["you"] = true; if (row.Hidden) o["hidden"] = true; }
            arr.Add(o);
        }
        var seasons = new JsonArray();
        if (Store.Season.Length > 0) seasons.Add(Store.Season);
        foreach (var s in Store.Seasons) if (s != Store.Season) seasons.Add(s);
        var mine = rows.FirstOrDefault(x => x.Key == viewer);
        return new JsonObject
        {
            ["t"] = "ladder.data", ["ok"] = true, ["season"] = season, ["current"] = Store.Season, ["seasons"] = seasons, ["rows"] = arr,
            ["you"] = mine == null ? null : new JsonObject { ["rank"] = mine.Rank, ["points"] = mine.Points, ["hidden"] = mine.Hidden },
        };
    }

    // ---------------------------------------------------------------- computing

    void EnsureFresh()
    {
        if (hub.Clock.UtcNow - lastRun >= Badges.RunOnRequestAfter) RunAll();
    }

    // housekeeping (every 15 s): the full run every 10 minutes; tallies that were not written yet reach the disk
    public void Tick()
    {
        if (!Enabled) return;
        if (hub.Clock.UtcNow - lastRun >= Badges.RunEvery) RunAll();
        lock (Store.Gate) if (Store.Dirty) Store.Save();
    }

    public void Flush()
    {
        lock (Store.Gate) if (Store.Dirty) Store.Save();
    }

    // Everybody who can have badges: the members in the codes file (not guests) and whoever is connected as one.
    // An unreadable or empty codes file never wipes anybody.
    List<(string Key, string Name)> Eligible()
    {
        var list = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, role) in hub.Auth.ListPersonal()) if (role != "guest") list[Key(name)] = name;
        foreach (var m in hub.Members) if (CanHave(m)) list[Key(m.Name)] = m.Name;
        return list.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public void RunAll()
    {
        if (!Enabled) return;
        lock (runGate)
        {
            var now = hub.Clock.UtcNow;
            var nowSec = Unix(now);
            var people = Eligible();
            lastRun = now;
            if (people.Count == 0) return;
            var contrib = hub.AtlasPool.Store.Contributions();
            var flights = hub.Flight.Store.CountsByIdentity();
            var chars = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (key, _) in people) chars[key] = hub.Armory.Store.CountOf(key);
            var firstDevice = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var d in hub.Auth.ListDevices())
                if (!d.Guest && d.Created > DateTime.MinValue) { var k = Key(d.Name); var s = Unix(d.Created); if (!firstDevice.TryGetValue(k, out var old) || s < old) firstDevice[k] = s; }

            lock (Store.Gate)
            {
                var cur = Badges.SeasonKey(now);
                bool rollover = Store.Season != cur;
                if (rollover && Store.Season.Length > 0)
                    Store.Archive(Store.Season, Store.All.Where(kv => kv.Value.SeasonPoints > 0).Select(kv => new BadgesStore.HistRow(kv.Key, kv.Value.Name, kv.Value.SeasonPoints)).ToList());
                if (rollover) Store.SetSeason(cur);
                var seasonStart = Unix(Badges.SeasonStart(cur));

                foreach (var gone in Store.All.Select(kv => kv.Key).Where(k => people.All(p => p.Key != k)).ToList()) { Store.Remove(gone); metricsOf.Remove(gone); }

                foreach (var (key, name) in people)
                {
                    var rec = Store.Ensure(key, name, nowSec, firstDevice.GetValueOrDefault(key));
                    if (rec == null) continue;
                    var metrics = Gather(rec, contrib.GetValueOrDefault(key), flights.GetValueOrDefault(key), chars.GetValueOrDefault(key), nowSec);
                    metricsOf[key] = metrics;
                    if (rollover || rec.Base.Count == 0) { rec.Base = new Dictionary<string, long>(metrics, StringComparer.Ordinal); rec.SeasonPoints = 0; Store.Touch(); }

                    foreach (var d in Badges.Table)
                    {
                        if (rec.Earned.ContainsKey(d.Id)) continue;
                        long v = d.Special == null ? metrics.GetValueOrDefault(d.Metric) : FamiliesEarned(rec);
                        if (v >= d.At) { rec.Earned[d.Id] = nowSec; Store.Touch(); }
                    }

                    double all = 0, season = 0;
                    foreach (var mt in Badges.Metrics)
                    {
                        if (mt.Weight <= 0) continue;
                        long v = metrics.GetValueOrDefault(mt.Key);
                        all += Badges.CategoryPoints(mt.Key, v);
                        season += Badges.CategoryPoints(mt.Key, Math.Max(0, v - rec.Base.GetValueOrDefault(mt.Key)));
                    }
                    foreach (var (id, at) in rec.Earned)
                    {
                        var def = Badges.Find(id);
                        if (def == null) continue;
                        all += Badges.BadgePoints(def.Tier);
                        if (at >= seasonStart) season += Badges.BadgePoints(def.Tier);
                    }
                    long pts = (long)Math.Round(all), spts = (long)Math.Round(season);
                    if (rec.Points != pts || rec.SeasonPoints != spts) { rec.Points = pts; rec.SeasonPoints = spts; Store.Touch(); }
                }
                if (Store.Dirty) Store.Save();
            }
        }
    }

    // the numbers badges are earned from, as of now (the caller holds Store.Gate)
    Dictionary<string, long> Gather(BadgesStore.Rec rec, AtlasStore.MemberContribution c, int flights, int chars, long nowSec)
    {
        var m = new Dictionary<string, long>(StringComparer.Ordinal);
        c ??= new AtlasStore.MemberContribution();
        m["kills"] = c.Kills; m["objects"] = c.Objects; m["fishing"] = c.Fishing; m["vendors"] = c.Vendors; m["revealed"] = c.Revealed;
        m["finds"] = c.Finds; m["zones"] = c.Zones.Count; m["rare"] = c.Rare;
        m["flights"] = flights; m["chars"] = chars;
        foreach (var k in BadgesStore.TallyKeys) m[k] = rec.Of(k);
        m["days"] = Math.Max(0, (nowSec - rec.First) / 86_400);
        return m;
    }

    // ---------------------------------------------------------------- tallies (called by the other modules; all quiet, all cheap)

    void OnAtlasTally(string identity, int quests, int seen)
    {
        if (!Enabled) return;
        lock (Store.Gate)
        {
            if (Store.Get(identity) == null) return;     // not noticed yet: the next run starts the member with the share already counted in the Atlas
            Store.Add(identity, "quests", quests);
            Store.Add(identity, "seen", seen);
        }
    }

    static long DayNo(DateTime utc) => (long)(utc.Date - DateTime.UnixEpoch).TotalDays;

    // a chat message: counts a day active (not messages) and, between 00:00 and 05:00 UTC, a late night
    public void OnChat(Member me, bool lfgCard)
    {
        if (!Enabled || !CanHave(me)) return;
        var now = hub.Clock.UtcNow;
        lock (Store.Gate)
        {
            var key = Key(me.Name);
            var r = Store.Get(key) ?? Store.Ensure(key, me.Name, Unix(now), 0);
            if (r == null) return;
            long day = DayNo(now);
            if (r.ChatDay != day) { r.ChatDay = day; Store.Add(key, "chatdays", 1); }
            if (now.Hour < 5 && r.NightDay != day) { r.NightDay = day; Store.Add(key, "nightdays", 1); }
            if (lfgCard) Store.Add(key, "lfg", 1);
        }
    }

    // a tally of one kind for a member (events created, files shared, hosting the Classic data)
    public void Note(string name, string tally, long n = 1)
    {
        if (!Enabled || string.IsNullOrEmpty(name)) return;
        lock (Store.Gate)
        {
            var key = Key(name);
            var r = Store.Get(key) ?? Store.Ensure(key, name, Unix(hub.Clock.UtcNow), 0);
            if (r == null) return;
            if (tally == "host") { if (r.Of("host") == 0) Store.Add(key, "host", 1); }
            else Store.Add(key, tally, n);
        }
    }

    // a sign-up counts once per event, however often the answer is changed
    public void OnSignup(Member me, string eventId)
    {
        if (!Enabled || !CanHave(me) || string.IsNullOrEmpty(eventId)) return;
        lock (Store.Gate)
        {
            var key = Key(me.Name);
            var r = Store.Get(key) ?? Store.Ensure(key, me.Name, Unix(hub.Clock.UtcNow), 0);
            if (r == null || r.Signed.Contains(eventId)) return;
            r.Signed.Add(eventId);
            while (r.Signed.Count > BadgesStore.MaxSigned) r.Signed.RemoveAt(0);
            Store.Add(key, "signups", 1);
        }
    }

    // a member who left or was removed: badges, tallies and ladder rows go (also done by the run, for anyone no longer a member)
    public void RemoveMember(string name)
    {
        lock (Store.Gate)
        {
            var key = Key(name);
            metricsOf.Remove(key);
            if (Store.Remove(key)) Store.Save();
        }
    }
}
