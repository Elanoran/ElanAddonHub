using System.Text;
using System.Text.Json.Nodes;
using Elans.Shared;

namespace Lodge;

// One character a member has played (collected from their "game" presence messages).
public sealed class CharInfo
{
    public string Name = "", Class = "", ClassFile = "", Race = "", RaceFile = "";
    public int Level;
    public long Seen; // unix seconds
}

// A member's profile: personal avatar + accent, a short text, characters and the privacy choices.
// Personal codes: stored per identity in data/profiles.json. Guests: a session-only profile that lives on their
// connection and is never written to disk.
public sealed class Profile
{
    public string AvatarId, Accent, Main;
    public string About = "", PlayTimes = "";
    public string DisplayName = "";         // shown instead of the login name in the UI; "" = none (2.7)
    public string ShowChars = "all";        // all | main | none
    public string VisibleTo = "everyone";   // everyone | officers
    public List<string> Hidden = new();     // characters the member never shows
    public List<CharInfo> Chars = new();
    internal DateTime LastSaved;
}

// All input rules in one place: whitelists and bounds. Used for client messages AND for the file at load.
public static class ProfileRules
{
    public const int MaxAbout = 140, MaxPlayTimes = 40, MaxChars = 12, MaxCharName = 24;

    public static readonly string[] Avatars =
    {
        "av.wolf", "av.bear", "av.raptor", "av.owl", "av.boar", "av.lion", "av.serpent", "av.spider",
        "av.sword", "av.shield", "av.bow", "av.staff", "av.hammer", "av.axe", "av.skull", "av.gem",
        "av.potion", "av.campfire", "av.banner", "av.moon", "av.sun", "av.flame", "av.fish", "av.chicken",
    };
    public static readonly string[] Accents = { "gold", "crimson", "emerald", "teal", "azure", "violet", "rose", "slate" };
    public static readonly string[] ShowModes = { "all", "main", "none" };
    public static readonly string[] VisibleModes = { "everyone", "officers" };

    public static bool ValidAvatar(string s) => s != null && Array.IndexOf(Avatars, s) >= 0;
    public static bool ValidAccent(string s) => s != null && Array.IndexOf(Accents, s) >= 0;

    public static string Key(string name) => Names.Normalize(name).ToLowerInvariant();
    public static bool SameChar(string a, string b) => string.Equals(Names.Normalize(a), Names.Normalize(b), StringComparison.OrdinalIgnoreCase);

    public const int MinDisplay = 2, MaxDisplay = 24;

    // letters (any script), digits, space and - _ ' . !
    static bool DisplayChar(char c) => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '\'' || c == '.' || c == '!';

    // format check: the normalized name ("" = none), or null with the reason in `error`
    public static string CheckDisplayFormat(string raw, out string error)
    {
        error = null;
        var n = Names.Full(raw);
        if (n.Length == 0) return "";
        if (n.Length < MinDisplay) { error = "Display name: at least 2 characters"; return null; }
        if (n.Length > MaxDisplay) { error = "Display name: at most 24 characters"; return null; }
        foreach (var c in n)
            if (!DisplayChar(c)) { error = "Display name: only letters, digits, spaces and - _ ' . ! are allowed"; return null; }
        if (!n.Any(char.IsLetterOrDigit)) { error = "Display name: needs at least one letter or digit"; return null; }
        return n;
    }

    // the comparison key for display-name uniqueness (L5): the Unicode look-alike skeleton (UTS #39, Elans.Shared.Confusables) of the
    // normalized name, case folded, small capitals as letters, 0->o, 1/i/|/!->l, 3->e, 5->s, separators dropped - so "E1an", "El an",
    // "Eʟan" and "Elаn" (Cyrillic а) are all the name "Elan". Accents stay ("Élan" is its own name). Never shown, only compared.
    public static string NameKey(string name) => Names.Key(name);

    // how many stored display names (identity key -> display name) share their key with another person's display name or login name.
    // Such names were allowed before the look-alike check (L5); they are kept as they are, and only a change has to pick a free name.
    public static int LookalikeDisplayNames(IEnumerable<(string Key, string Display)> displays, IEnumerable<string> logins)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // name key -> identity keys using it
        void Add(string nameKey, string who)
        {
            if (nameKey.Length == 0) return;
            if (!owners.TryGetValue(nameKey, out var set)) owners[nameKey] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(who);
        }
        var list = displays.ToList();
        foreach (var login in logins) Add(NameKey(login), Key(login));
        foreach (var (who, shown) in list) Add(NameKey(shown), who);
        return list.Count(d => owners.TryGetValue(NameKey(d.Display), out var set) && set.Any(o => o != d.Key));
    }

    // the same for login names: how many existing logins share their key with another login or with another person's display name.
    // Existing logins are never renamed or blocked (they sign in by code / device token, looked up exactly); only NEW names are checked.
    public static int LookalikeLogins(IEnumerable<(string Key, string Display)> displays, IEnumerable<string> logins)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string nameKey, string who)
        {
            if (nameKey.Length == 0) return;
            if (!owners.TryGetValue(nameKey, out var set)) owners[nameKey] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(who);
        }
        var list = logins.ToList();
        foreach (var login in list) Add(NameKey(login), Key(login));
        foreach (var (who, shown) in displays) Add(NameKey(shown), who);
        return list.Count(l => owners.TryGetValue(NameKey(l), out var set) && set.Any(o => o != Key(l)));
    }

    public static CharInfo CleanChar(string name, string cls, string classFile, string race, string raceFile, int level, long seen)
    {
        name = Names.Text(name, MaxCharName);
        if (name.Length == 0) return null;
        return new CharInfo
        {
            Name = name, Class = Names.Text(cls, 20), ClassFile = Names.Text(classFile, 20),
            Race = Names.Text(race, 24), RaceFile = Names.Text(raceFile, 20),
            Level = Math.Clamp(level, 0, 100), Seen = Math.Max(0, seen),
        };
    }

    // drop duplicates (case-insensitive), keep the most recent MaxChars; hidden entries must be known characters and the
    // main character is never hidden
    public static void Trim(Profile p)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        p.Chars = p.Chars.OrderByDescending(c => c.Seen).Where(c => seen.Add(c.Name)).Take(MaxChars).ToList();
        if (p.Main != null && !p.Chars.Any(c => SameChar(c.Name, p.Main))) p.Main = null;
        p.Hidden = p.Hidden.Where(h => p.Chars.Any(c => SameChar(c.Name, h)) && !(p.Main != null && SameChar(p.Main, h)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxChars).ToList();
    }
}

// data/profiles.json = { "<identity key>": profile }. Saved atomically (tmp file, flush, rename); a leftover tmp from a
// crash is used only if the main file is missing; the file is read with a size cap and every value is re-validated.
public sealed class ProfileStore
{
    public const int MaxProfiles = 2000;
    const long MaxFileBytes = 8 * 1024 * 1024;

    readonly string path, tmp;
    readonly ILogger log;
    readonly IClock clock;
    readonly object gate = new();
    readonly Dictionary<string, Profile> profiles = new();

    public ProfileStore(string dataDir, ILogger log, IClock clock)
    {
        this.log = log; this.clock = clock;
        path = Path.Combine(dataDir, "profiles.json");
        tmp = path + ".tmp";
        try
        {
            if (File.Exists(tmp)) { if (!File.Exists(path)) File.Move(tmp, path); else File.Delete(tmp); }
            if (File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root)
                foreach (var kv in root)
                {
                    if (profiles.Count >= MaxProfiles) break;
                    if (kv.Key.Length > 0 && ProfileRules.Key(kv.Key) == kv.Key && kv.Value is JsonObject o) profiles[kv.Key] = Read(o);
                }
        }
        catch (Exception e) { log?.LogWarning("Profiles: load failed: {Error}", e.GetType().Name); }
    }

    static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string ReadDisplay(string raw) { var d = ProfileRules.CheckDisplayFormat(raw, out var e); return e == null ? d : ""; }

    static Profile Read(JsonObject o)
    {
        var p = new Profile
        {
            AvatarId = ProfileRules.ValidAvatar(Str(o, "avatarId")) ? Str(o, "avatarId") : null,
            Accent = ProfileRules.ValidAccent(Str(o, "accent")) ? Str(o, "accent") : null,
            About = Names.Text(Str(o, "about"), ProfileRules.MaxAbout),
            PlayTimes = Names.Text(Str(o, "playTimes"), ProfileRules.MaxPlayTimes),
            DisplayName = ReadDisplay(Str(o, "displayName")),
            Main = Str(o, "main") is { Length: > 0 } m ? Names.Text(m, ProfileRules.MaxCharName) : null,
            ShowChars = Array.IndexOf(ProfileRules.ShowModes, Str(o, "showChars")) >= 0 ? Str(o, "showChars") : "all",
            VisibleTo = Array.IndexOf(ProfileRules.VisibleModes, Str(o, "visibleTo")) >= 0 ? Str(o, "visibleTo") : "everyone",
        };
        if (o["hidden"] is JsonArray ha)
            foreach (var h in ha.Take(64)) if (h is JsonValue hv && hv.TryGetValue<string>(out var hs)) p.Hidden.Add(Names.Text(hs, ProfileRules.MaxCharName));
        if (o["chars"] is JsonArray ca)
            foreach (var c in ca.Take(64))
                if (c is JsonObject co)
                {
                    long seen = 0; int lvl = 0;
                    try { if (co["seen"] is JsonValue sv && sv.TryGetValue<long>(out var l)) seen = l; } catch { }
                    try { if (co["level"] is JsonValue lv && lv.TryGetValue<int>(out var i)) lvl = i; } catch { }
                    var ci = ProfileRules.CleanChar(Str(co, "name"), Str(co, "class"), Str(co, "classFile"), Str(co, "race"), Str(co, "raceFile"), lvl, seen);
                    if (ci != null) p.Chars.Add(ci);
                }
        ProfileRules.Trim(p);
        return p;
    }

    static JsonObject CharJson(CharInfo c) => new()
    {
        ["name"] = c.Name, ["class"] = c.Class, ["classFile"] = c.ClassFile, ["race"] = c.Race, ["raceFile"] = c.RaceFile,
        ["level"] = c.Level, ["seen"] = c.Seen,
    };

    static JsonObject Write(Profile p) => new()
    {
        ["avatarId"] = p.AvatarId, ["accent"] = p.Accent, ["about"] = p.About, ["playTimes"] = p.PlayTimes, ["main"] = p.Main, ["displayName"] = p.DisplayName,
        ["showChars"] = p.ShowChars, ["visibleTo"] = p.VisibleTo,
        ["hidden"] = new JsonArray(p.Hidden.Select(h => (JsonNode)JsonValue.Create(h)).ToArray()),
        ["chars"] = new JsonArray(p.Chars.Select(c => (JsonNode)CharJson(c)).ToArray()),
    };

    // every change to a Profile (and every read that must be consistent) happens under this lock
    public object Gate => gate;

    // the stored profile of a personal identity (created empty on first use, up to MaxProfiles; past that a throwaway one)
    public Profile For(string name)
    {
        var key = ProfileRules.Key(name);
        lock (gate)
        {
            if (profiles.TryGetValue(key, out var p)) return p;
            p = new Profile();
            if (profiles.Count < MaxProfiles) profiles[key] = p;
            return p;
        }
    }

    // read-only lookup (never creates)
    public Profile Peek(string name) { lock (gate) return profiles.TryGetValue(ProfileRules.Key(name), out var p) ? p : null; }

    public int Count { get { lock (gate) return profiles.Count; } }

    // every stored (identity key, display name) pair that has a display name; callers hold Gate
    public List<(string Key, string Display)> DisplayNames() =>
        profiles.Where(kv => kv.Value.DisplayName.Length > 0).Select(kv => (kv.Key, kv.Value.DisplayName)).ToList();

    // callers hold Gate while changing a Profile, then call Save (stored profiles only)
    public bool Save(Profile p)
    {
        lock (gate)
        {
            p.LastSaved = clock.UtcNow;
            return SaveAll();
        }
    }

    // character sightings: a new character or a changed class/level saves at once; a bare "seen again" only every 10 minutes
    public void NoteCharacter(Profile p, bool stored, CharInfo seen)
    {
        lock (gate)
        {
            bool structural;
            var ex = p.Chars.FirstOrDefault(c => ProfileRules.SameChar(c.Name, seen.Name));
            if (ex == null) { p.Chars.Add(seen); structural = true; }
            else
            {
                structural = ex.Level != seen.Level || (seen.Class.Length > 0 && ex.Class != seen.Class) || (seen.Race.Length > 0 && ex.Race != seen.Race);
                ex.Level = seen.Level;
                if (seen.Class.Length > 0) { ex.Class = seen.Class; ex.ClassFile = seen.ClassFile; }
                if (seen.Race.Length > 0) { ex.Race = seen.Race; ex.RaceFile = seen.RaceFile; }
                ex.Seen = seen.Seen;
            }
            ProfileRules.Trim(p);
            if (stored && (structural || clock.UtcNow - p.LastSaved > TimeSpan.FromMinutes(10))) { p.LastSaved = clock.UtcNow; SaveAll(); }
        }
    }

    // 2.22: a member left / was removed: their profile (about, play times, characters, display name) is deleted. True when there was one.
    public bool Remove(string name)
    {
        lock (gate)
        {
            if (!profiles.Remove(ProfileRules.Key(name))) return false;
            SaveAll();
            return true;
        }
    }

    // flush everything (shutdown, tests)
    public bool Flush() { lock (gate) return SaveAll(); }

    bool SaveAll()
    {
        try
        {
            var root = new JsonObject();
            foreach (var kv in profiles) root[kv.Key] = Write(kv.Value);
            var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            log?.LogWarning("Profiles: save failed: {Error}", e.GetType().Name);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}

// profile:set / profile:get / profile:reset (typed in shared/Elans.Shared/Lodge/ProfileMessages.cs; profile:set is still validated from the raw
// JSON below: a wrong-typed field is refused, and a field left out differs from one sent as null)
public class ProfileModule : IModule
{
    readonly LodgeHub hub;
    public ProfileModule(LodgeHub hub) { this.hub = hub; }

    public async Task<bool> Handle(Member me, string type, JsonObject m)
    {
        switch (type)
        {
            case LodgeMsg.ProfileSet: await Set(me, m); return true;
            case LodgeMsg.ProfileGet: await Get(me, m); return true;
            case LodgeMsg.ProfileReset: await Reset(me, m); return true;
        }
        return false;
    }

    // a string field: Present = the key exists; Value null = explicit null (clear); Bad = some other JSON type
    readonly record struct Field(bool Present, string Value, bool Bad);

    static Field Read(JsonObject m, string key)
    {
        if (!m.ContainsKey(key)) return new Field(false, null, false);
        var n = m[key];
        if (n == null) return new Field(true, null, false);
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return new Field(true, s, false);
        return new Field(true, null, true);
    }

    // own profile; any field may be left out (unchanged). Everything is validated first, then applied as a whole.
    async Task Set(Member me, JsonObject m)
    {
        if (!me.Budget.ProfileSet.TryTake())
        {
            me.CountDrop(hub.Clock);
            await hub.Error(me, "Slow down - profile changes are limited to one every 2 seconds");
            return;
        }
        var avatar = Read(m, "avatarId"); var accent = Read(m, "accent"); var about = Read(m, "about"); var play = Read(m, "playTimes");
        var display = Read(m, "displayName"); var main = Read(m, "main"); var show = Read(m, "showChars"); var vis = Read(m, "visibleTo");
        string err = null;
        if (avatar.Bad || (avatar.Value is { Length: > 0 } && !ProfileRules.ValidAvatar(avatar.Value))) err = "Unknown avatar";
        else if (accent.Bad || (accent.Value is { Length: > 0 } && !ProfileRules.ValidAccent(accent.Value))) err = "Unknown accent colour";
        else if (about.Bad) err = "Invalid about text";
        else if (play.Bad) err = "Invalid play times";
        else if (display.Bad) err = "Invalid display name";
        else if (main.Bad) err = "Invalid main character";
        else if (show.Bad || (show.Present && Array.IndexOf(ProfileRules.ShowModes, show.Value) < 0)) err = "Invalid character visibility";
        else if (vis.Bad || (vis.Present && Array.IndexOf(ProfileRules.VisibleModes, vis.Value) < 0)) err = "Invalid profile visibility";
        List<string> hidden = null;
        if (err == null && m.ContainsKey("hidden"))
        {
            if (m["hidden"] is JsonArray ha && ha.Count <= 64)
            {
                hidden = new List<string>();
                foreach (var h in ha)
                {
                    if (h is JsonValue hv && hv.TryGetValue<string>(out var hs)) hidden.Add(hs);
                    else { err = "Invalid hidden characters"; break; }
                }
            }
            else err = "Invalid hidden characters";
        }
        string newDisplay = null;
        if (err == null && display.Present)
        {
            newDisplay = ProfileRules.CheckDisplayFormat(display.Value, out var fe);
            if (fe != null) err = fe;
            else if (newDisplay.Length > 0 && !me.Personal) err = "Guests can't set a display name";
        }
        if (err != null) { await hub.Error(me, err); return; }

        var p = me.Profile;
        bool saved = true, publicChanged = false;
        lock (hub.Profiles.Gate)
        {
            bool dnChange = newDisplay != null && newDisplay != p.DisplayName;
            string dnErr = dnChange && newDisplay.Length > 0 ? DisplayTaken(me, newDisplay) : null;
            if (dnErr == null && dnChange && !me.Budget.DisplayName.TryTake())
                dnErr = "Slow down - the display name can be changed once a minute";
            var mn = main.Present ? Names.Text(main.Value, ProfileRules.MaxCharName) : null;
            var known = string.IsNullOrEmpty(mn) ? null : p.Chars.FirstOrDefault(c => ProfileRules.SameChar(c.Name, mn));
            if (dnErr != null) err = dnErr;
            else if (!string.IsNullOrEmpty(mn) && known == null) err = "That isn't one of your characters yet - log in with it once";
            else
            {
                if (main.Present) p.Main = known?.Name;
                if (dnChange) { p.DisplayName = newDisplay; publicChanged = true; }
                if (avatar.Present) { var v = string.IsNullOrEmpty(avatar.Value) ? null : avatar.Value; publicChanged |= v != p.AvatarId; p.AvatarId = v; }
                if (accent.Present) { var v = string.IsNullOrEmpty(accent.Value) ? null : accent.Value; publicChanged |= v != p.Accent; p.Accent = v; }
                if (about.Present) p.About = Names.Text(about.Value, ProfileRules.MaxAbout);
                if (play.Present) p.PlayTimes = Names.Text(play.Value, ProfileRules.MaxPlayTimes);
                if (show.Present) p.ShowChars = show.Value;
                if (vis.Present) p.VisibleTo = vis.Value;
                if (hidden != null) p.Hidden = hidden.Select(h => Names.Text(h, ProfileRules.MaxCharName)).ToList();
                ProfileRules.Trim(p);
                if (me.Personal) saved = hub.Profiles.Save(p);
            }
        }
        if (err != null) { await hub.Error(me, err); return; }
        if (!saved) await hub.Error(me, "The server couldn't save your profile - it may be gone after a restart");
        await hub.Send(me, Data(OwnViewOf(me)));
        if (publicChanged) await hub.BroadcastProfile(me);
    }

    // null when `name` is free for `me`, else the reason. Called under Profiles.Gate. A display name may not look like (same
    // ProfileRules.NameKey) anyone else's login name or display name; one's own login name is fine. A stored name that already looks
    // like someone else's (allowed before the look-alike check) is kept while it is unchanged; a change is checked like any other.
    string DisplayTaken(Member me, string name)
    {
        var f = ProfileRules.NameKey(name);
        if (f.Length == 0) return "Display name: needs at least one letter or digit";
        bool Mine(string login) => me.Personal && LodgeHub.Same(login, me.Name);
        foreach (var login in hub.Auth.PersonalNames.Concat(hub.Members.Select(x => x.Name)))
            if (!Mine(login) && ProfileRules.NameKey(login) == f) return "That display name is taken";
        var myKey = ProfileRules.Key(me.Name);
        foreach (var (key, shown) in hub.Profiles.DisplayNames())
            if (key != myKey && ProfileRules.NameKey(shown) == f) return "That display name is taken";
        return null;
    }

    // profile:data (typed: shared/Elans.Shared/Lodge/ProfileMessages.cs)
    static JsonObject Data(ProfileView view) => LodgeJson.Build(new ProfileDataMsg { Profile = view }.Write);
    static ProfileView NotFound(string name) => new() { Name = name, Found = false };

    async Task Get(Member me, JsonObject m)
    {
        var name = ProfileGetMsg.Read(new LodgeJsonFields(m)).Name is string s ? Names.Normalize(s) : "";
        var view = name.Length == 0 ? NotFound("") : Lookup(me, name);
        await hub.Send(me, Data(view));
    }

    // owner only: back to the default avatar and accent, no display name (and optionally clear the texts)
    async Task Reset(Member me, JsonObject m)
    {
        if (!Roles.Can(me.Role, Perm.ResetProfiles)) { await hub.Error(me, "Your rank can't do that"); return; }
        var req = ProfileResetMsg.Read(new LodgeJsonFields(m));
        var name = req.Name is string s ? Names.Normalize(s) : "";
        bool clearText = req.ClearText;
        var online = hub.Members.FirstOrDefault(x => LodgeHub.Same(x.Name, name));
        bool personal = online?.Personal ?? hub.Auth.PersonalNames.Any(x => LodgeHub.Same(x, name));
        Profile p = online?.Profile ?? (personal ? hub.Profiles.For(name) : null);
        if (name.Length == 0 || p == null) { await hub.Error(me, "No such member"); return; }
        lock (hub.Profiles.Gate)
        {
            p.AvatarId = null; p.Accent = null; p.DisplayName = "";
            if (clearText) { p.About = ""; p.PlayTimes = ""; }
            if (personal) hub.Profiles.Save(p);
        }
        hub.Log.LogInformation("{By} reset the profile of {Name}", me.Name, name);
        if (online != null)
        {
            await hub.Send(online, Data(OwnViewOf(online)));
            await hub.BroadcastProfile(online);
        }
        await hub.Send(me, Data(Lookup(me, name)));
    }

    // what `viewer` gets for `name`. An invisible member looks exactly like an offline one to everyone who may not see
    // them (no online flag, no game); "profile visible to officers" leaves others only the avatar and accent.
    ProfileView Lookup(Member viewer, string name)
    {
        var online = hub.Members.FirstOrDefault(x => LodgeHub.Same(x.Name, name));
        if (online != null)
        {
            bool seeOnline = !online.Invisible || online.Id == viewer.Id || Roles.Can(viewer.Role, Perm.SeeInvisible);
            if (seeOnline) return Build(viewer, online.Name, online.Profile, online.Role, online.Personal, online);
            if (!online.Personal) return NotFound(name); // an invisible guest: nobody here
            return Build(viewer, online.Name, online.Profile, online.Role, true, null, hideSeen: true); // invisible personal: the stored profile, like offline - but without "seen", which would be "now"
        }
        var list = hub.Auth.ListPersonal();
        int at = list.FindIndex(x => LodgeHub.Same(x.Name, name));
        if (at < 0) return NotFound(name);
        var who = list[at];
        return Build(viewer, who.Name, hub.Profiles.Peek(who.Name) ?? new Profile(), who.Role, true, null);
    }

    // welcome.profile
    public JsonObject OwnView(Member me) => LodgeJson.Build(OwnViewOf(me).Write);
    public ProfileView OwnViewOf(Member me) => Build(me, me.Name, me.Profile, me.Role, me.Personal, me);

    ProfileView Build(Member viewer, string name, Profile p, string role, bool personal, Member online, bool hideSeen = false)
    {
        bool self = (online != null && online.Id == viewer.Id) || (personal && viewer.Personal && LodgeHub.Same(viewer.Name, name));
        bool allowed = self || p.VisibleTo != "officers" || Roles.AtLeast(viewer.Role, "officer");
        var j = new ProfileView { Name = name, Found = true, Guest = !personal, Role = role, Online = online != null, Restricted = !allowed };
        lock (hub.Profiles.Gate)
        {
            j.AvatarId = p.AvatarId; j.Accent = p.Accent; j.DisplayName = p.DisplayName;
            if (self) { j.ShowChars = p.ShowChars; j.VisibleTo = p.VisibleTo; }
            if (!allowed) return j;
            j.About = p.About; j.PlayTimes = p.PlayTimes;
            if (online?.Game != null) j.Game = online.Game.DeepClone();          // the presence object goes out as it came in
            foreach (var c in p.Chars.OrderByDescending(c => c.Seen))
            {
                bool isMain = p.Main != null && ProfileRules.SameChar(c.Name, p.Main);
                bool hid = p.Hidden.Any(h => ProfileRules.SameChar(h, c.Name));
                bool shown = self || (p.ShowChars == "all" && !hid) || (p.ShowChars == "main" && isMain) || (p.ShowChars == "all" && isMain);
                if (!shown) continue;
                j.Chars.Add(new ProfileChar
                {
                    Name = c.Name, Class = c.Class, ClassFile = c.ClassFile, Race = c.Race, RaceFile = c.RaceFile, Level = c.Level, Main = isMain,
                    Seen = hideSeen ? null : c.Seen, Hidden = self ? hid : null,
                });
            }
            if (p.Main != null && (self || p.ShowChars != "none")) j.Main = p.Main;
        }
        return j;
    }
}
