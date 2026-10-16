using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lodge;

// The Armory (2.16): members choose characters on their own PC and share a snapshot of each with the Lodge. Pure rules live here (validation and the summary);
// ArmoryStore keeps them in <data>/armory.json. A snapshot is the shape of the Hub modules the Characters page already reads (gear, talents, professions,
// hunter pets) plus a little identity - NEVER bags, bank, gold, mail, zone or anything else. The server rebuilds every snapshot field by field from what it
// accepts, so unknown fields are dropped, text is cleaned and every number is bounded.
public static partial class Armory
{
    public const int MaxCharsPerMember = 30;
    public const int MaxCharsTotal = 1500;
    public const int MaxSnapshotBytes = 48 * 1024;           // one character, serialized
    public const int MaxNodes = 400, MaxPets = 40, MaxAbilities = 40, MaxKnown = 100, MaxProfessions = 12, MaxSlots = 24, MaxLinks = 40;
    public const int MaxBuildLength = 600;
    public const int MaxKnownIds = 2000, MaxCrafterSpells = 16, MaxCrafterRows = 80;       // armory2: the learned recipes of one character (all professions together)
    public const int MaxReps = 300, MaxStandingRows = 80, MaxFactionId = 1_000_000;          // reputations (2.27): factions per character, rows of one "standing" answer
    public static readonly TimeSpan PutEvery = TimeSpan.FromSeconds(8);
    public const int PutBurst = 30;

    [GeneratedRegex(@"^[A-Za-z]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RaceFileRx();
    [GeneratedRegex(@"^\|c[0-9a-fA-F]{8}\|Hitem:[0-9:\-]{1,120}\|h\[[^\[\]|]{1,80}\]\|h\|r\z", RegexOptions.CultureInvariant)]
    private static partial Regex LinkRx();
    [GeneratedRegex(@"^[A-Z][A-Z_]{0,19}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileRx();
    [GeneratedRegex(@"^ETB[0-9]{1,2}-[A-Za-z0-9_\-]{4,560}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BuildRx();
    [GeneratedRegex(@"^[a-z][a-z0-9_.]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AbilityRx();

    // ---- helpers: nothing here throws on odd input
    static bool Num(JsonNode n, out double d)
    {
        d = 0;
        if (n is not JsonValue v) return false;
        try
        {
            if (v.TryGetValue<double>(out d)) return !double.IsNaN(d) && !double.IsInfinity(d);
            if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        }
        catch { }
        return false;
    }

    static int I(JsonNode n, int lo, int hi, int fallback = 0) => Num(n, out var d) ? (int)Math.Clamp(Math.Floor(d), lo, hi) : fallback;
    static long L(JsonNode n, long lo, long hi, long fallback = 0) => Num(n, out var d) ? (long)Math.Clamp(Math.Floor(d), lo, hi) : fallback;
    static double? D(JsonNode n, double lo, double hi) => Num(n, out var d) ? Math.Round(Math.Clamp(d, lo, hi), 2) : null;

    static string S(JsonNode n, int max)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s) || s == null) return null;
        var t = Names.Text(s, max);
        return t.Length == 0 ? null : t;
    }

    static string FileName(JsonNode n)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s) || s == null) return null;
        return FileRx().IsMatch(s) ? s : null;
    }

    static string RaceFile(JsonNode n)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s) || s == null) return null;
        return RaceFileRx().IsMatch(s) ? s : null;
    }

    static void Set(JsonObject o, string k, JsonNode v) { if (v != null) o[k] = v; }
    static JsonNode Js(string s) => s == null ? null : JsonValue.Create(s);
    static JsonNode Jn(double? d) => d == null ? null : JsonValue.Create(d.Value);

    public static bool ValidKey(string key) => key != null && key.Length is >= 3 and <= 80 && Names.Text(key, 80) == key && key.IndexOf('-') > 0;

    // ---- the snapshot: returns the clean character, or null (and why) when it can't be used
    public static JsonObject Clean(JsonObject c, out string why) => Clean(c, out why, false);

    // `reps`: the lodge takes reputations (feature "reputations"); without it a snapshot's reputations are dropped
    public static JsonObject Clean(JsonObject c, out string why, bool reps)
    {
        why = null;
        if (c == null) { why = "empty"; return null; }
        var name = S(c["name"], 24);
        var cls = FileName(c["class"]);
        if (name == null || cls == null) { why = "invalid"; return null; }
        var o = new JsonObject { ["name"] = name, ["class"] = cls };
        Set(o, "realm", Js(S(c["realm"], 40)));
        Set(o, "classText", Js(S(c["classText"], 24)));
        Set(o, "race", Js(S(c["race"], 24)));
        Set(o, "raceFile", Js(RaceFile(c["raceFile"])));
        int sex = I(c["sex"], 0, 3);
        if (sex is 2 or 3) o["sex"] = sex;
        Set(o, "faction", Js(S(c["faction"], 16)));
        Set(o, "guild", Js(S(c["guild"], 40)));
        int level = I(c["level"], 0, 100);
        if (level < 1) { why = "invalid"; return null; }
        o["level"] = level;
        var ilvl = D(c["ilvl"], 0, 2000);
        if (ilvl is > 0) o["ilvl"] = ilvl;
        o["at"] = L(c["at"], 0, 4102444800L);

        var gear = Gear(c["gear"] as JsonObject);
        if (gear != null) o["gear"] = gear;
        var tal = Talents(c["talents"] as JsonObject);
        if (tal != null) o["talents"] = tal;
        var pro = Professions(c["professions"] as JsonObject);
        if (pro != null) o["professions"] = pro;
        var pets = Pets(c["pets"] as JsonObject);
        if (pets != null) o["pets"] = pets;
        if (gear == null && tal == null && pro == null) { why = "empty"; return null; }
        if (reps)
        {
            var rp = Reps(c["reps"] as JsonObject);
            if (rp != null) o["reps"] = rp;
        }
        if (o.ToJsonString().Length > MaxSnapshotBytes)
        {
            // the optional parts go first (reputations, then the learned recipes): a character that is too big with them is still shared without them
            o.Remove("reps");
            if (o.ToJsonString().Length > MaxSnapshotBytes && o["professions"] is JsonObject po && po["known"] != null) po.Remove("known");
            if (o.ToJsonString().Length > MaxSnapshotBytes) { why = "size"; return null; }
        }
        return o;
    }

    static JsonObject Gear(JsonObject g)
    {
        if (g == null) return null;
        var o = new JsonObject { ["at"] = L(g["at"], 0, 4102444800L) };
        var il = D(g["ilvl"], 0, 2000);
        if (il is > 0) o["ilvl"] = il;
        var slots = new JsonObject();
        if (g["slots"] is JsonObject so)
            foreach (var (k, v) in so)
            {
                if (slots.Count >= MaxSlots) break;
                if (!int.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot < 0 || slot > 30 || v is not JsonObject it) continue;
                var id = I(it["i"], 0, 10_000_000);
                if (id <= 0) continue;
                var e = new JsonObject { ["i"] = id, ["q"] = I(it["q"], 0, 7, 1) };
                int ic = I(it["ic"], 0, int.MaxValue), ilv = I(it["il"], 0, 10000);
                if (ic > 0) e["ic"] = ic;
                if (ilv > 0) e["il"] = ilv;
                slots[slot.ToString(CultureInfo.InvariantCulture)] = e;
            }
        o["slots"] = slots;
        // the item links carry the enchant and the suffix; only the exact link shape is kept, and only for items that are worn
        var links = new JsonObject();
        if (g["links"] is JsonObject lo)
        {
            var worn = new HashSet<string>(slots.Select(kv => ((int)kv.Value!["i"]!).ToString(CultureInfo.InvariantCulture)));
            foreach (var (k, v) in lo)
            {
                if (links.Count >= MaxLinks) break;
                if (!worn.Contains(k) || v is not JsonValue lv || !lv.TryGetValue<string>(out var s) || s == null || s.Length > 220 || !LinkRx().IsMatch(s)) continue;
                links[k] = s;
            }
        }
        if (links.Count > 0) o["links"] = links;
        if (g["stats"] is JsonObject st)
        {
            var so2 = new JsonObject();
            foreach (var k in new[] { "health", "power", "str", "agi", "sta", "int", "spi", "armor" }) Set(so2, k, Jn(D(st[k], 0, 1e9)));
            foreach (var k in new[] { "ap", "rap" }) Set(so2, k, Jn(D(st[k], -1e6, 1e6)));
            foreach (var k in new[] { "crit", "rcrit", "dodge", "parry", "block", "speed" }) Set(so2, k, Jn(D(st[k], 0, 1000)));
            var pt = S(st["powerType"], 24);
            if (pt != null && pt.All(ch => char.IsAsciiLetter(ch) || ch == '_')) so2["powerType"] = pt;
            foreach (var k in new[] { "melee", "ranged" })
                if (st[k] is JsonObject d)
                {
                    var dd = new JsonObject();
                    Set(dd, "min", Jn(D(d["min"], 0, 1e9))); Set(dd, "max", Jn(D(d["max"], 0, 1e9))); Set(dd, "speed", Jn(D(d["speed"], 0, 100)));
                    if (dd.Count > 0) so2[k] = dd;
                }
            if (so2.Count > 0) o["stats"] = so2;
        }
        return o;
    }

    static JsonObject Talents(JsonObject t)
    {
        if (t == null) return null;
        var o = new JsonObject { ["at"] = L(t["at"], 0, 4102444800L), ["spent"] = I(t["spent"], 0, 1000), ["unspent"] = I(t["unspent"], 0, 1000) };
        var api = S(t["api"], 16);
        if (api is "traits" or "classic") o["api"] = api;
        if (t["build"] is JsonValue bv && bv.TryGetValue<string>(out var build) && build != null && build.Length <= MaxBuildLength && BuildRx().IsMatch(build)) o["build"] = build;
        var nodes = new JsonArray();
        if (t["nodes"] is JsonArray na)
            foreach (var n in na)
            {
                if (nodes.Count >= MaxNodes) break;
                if (n is not JsonObject no) continue;
                int r = I(no["r"], 0, 1000);
                if (r <= 0) continue;            // only talents with points are shared
                var e = new JsonObject { ["id"] = I(no["id"], 0, int.MaxValue), ["r"] = r };
                int m = I(no["m"], 0, 1000), s = I(no["s"], 0, int.MaxValue), ic = I(no["ic"], 0, int.MaxValue);
                if (m > 0) e["m"] = m;
                if (s > 0) e["s"] = s;
                if (ic > 0) e["ic"] = ic;
                if (Num(no["x"], out var x) && Num(no["y"], out var y)) { e["x"] = Math.Round(Math.Clamp(x, -1e6, 1e6), 2); e["y"] = Math.Round(Math.Clamp(y, -1e6, 1e6), 2); }
                Set(e, "n", Js(S(no["n"], 48)));
                nodes.Add(e);
            }
        o["nodes"] = nodes;
        if (nodes.Count == 0 && o["build"] == null) return null;
        return o;
    }

    static JsonObject Professions(JsonObject p)
    {
        if (p == null) return null;
        var list = new JsonArray();
        if (p["list"] is JsonArray la)
            foreach (var n in la)
            {
                if (list.Count >= MaxProfessions) break;
                if (n is not JsonObject po) continue;
                var name = S(po["name"], 32);
                if (name == null) continue;
                var e = new JsonObject { ["name"] = name, ["skill"] = I(po["skill"], 0, 100000), ["max"] = I(po["max"], 0, 100000) };
                int ic = I(po["ic"], 0, int.MaxValue);
                if (ic > 0) e["ic"] = ic;
                int line = I(po["line"], 0, 1_000_000);
                if (line > 0) e["line"] = line;
                e["kind"] = S(po["kind"], 12) == "secondary" ? "secondary" : "primary";
                list.Add(e);
            }
        if (list.Count == 0) return null;
        var res = new JsonObject { ["at"] = L(p["at"], 0, 4102444800L), ["list"] = list };
        var known = Known(p["known"] as JsonObject);
        if (known != null) res["known"] = known;
        return res;
    }

    // armory2: { "<skillLineId>": { at, partial, n?, ids: [spellId, ...] } } - ids are positive integers, sorted, no duplicates, at most MaxKnownIds for the whole character
    static JsonObject Known(JsonObject k)
    {
        if (k == null) return null;
        var o = new JsonObject();
        int total = 0;
        foreach (var (key, v) in k.OrderBy(x => int.TryParse(x.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue))
        {
            if (o.Count >= MaxProfessions || total >= MaxKnownIds) break;
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1 || line > 1_000_000 || v is not JsonObject e) continue;
            var ids = new SortedSet<int>();
            if (e["ids"] is JsonArray ia)
                foreach (var n in ia)
                {
                    if (total + ids.Count >= MaxKnownIds) break;
                    if (!Num(n, out var d) || d < 1 || d > int.MaxValue || d != Math.Floor(d)) continue;     // a recipe id is a positive whole number
                    ids.Add((int)d);
                }
            if (ids.Count == 0) continue;
            total += ids.Count;
            var arr = new JsonArray();
            foreach (var id in ids) arr.Add(id);
            var entry = new JsonObject { ["at"] = L(e["at"], 0, 4102444800L), ["partial"] = !(e["partial"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && !pb), ["ids"] = arr };
            Set(entry, "n", Js(S(e["n"], 32)));
            o[line.ToString(CultureInfo.InvariantCulture)] = entry;
        }
        return o.Count == 0 ? null : o;
    }

    // reputations (2.27): { at, list: [{ id, s, v, min?, max? }] } - id a faction id 1..1000000, s the standing 1 Hated .. 8 Exalted, v / min / max whole numbers
    // within +-100,000,000; at most MaxReps, sorted by id, one per faction (the first wins). Names are never kept: every client names factions from its own game data.
    static JsonObject Reps(JsonObject r)
    {
        if (r == null || r["list"] is not JsonArray la) return null;
        var byId = new SortedDictionary<int, JsonObject>();
        foreach (var n in la)
        {
            if (byId.Count >= MaxReps) break;
            if (n is not JsonObject e || !Num(e["id"], out var id) || id < 1 || id > MaxFactionId || id != Math.Floor(id)) continue;
            if (!Num(e["s"], out var s) || s < 1 || s > 8 || s != Math.Floor(s) || !Num(e["v"], out _)) continue;
            if (byId.ContainsKey((int)id)) continue;
            var x = new JsonObject { ["id"] = (int)id, ["s"] = (int)s, ["v"] = I(e["v"], -100_000_000, 100_000_000) };
            if (Num(e["min"], out _)) x["min"] = I(e["min"], -100_000_000, 100_000_000);
            if (Num(e["max"], out _)) x["max"] = I(e["max"], -100_000_000, 100_000_000);
            byId[(int)id] = x;
        }
        if (byId.Count == 0) return null;
        var list = new JsonArray();
        foreach (var x in byId.Values) list.Add(x);
        return new JsonObject { ["at"] = L(r["at"], 0, 4102444800L), ["list"] = list };
    }

    // "standing with a faction": the row of one shared character, or null when it has no reputation with it
    public static JsonObject StandingOf(JsonObject c, int faction)
    {
        if (c["reps"] is not JsonObject r || r["list"] is not JsonArray list) return null;
        foreach (var n in list)
        {
            if (n is not JsonObject e || e["id"] is not JsonValue iv || !iv.TryGetValue<int>(out var id) || id != faction) continue;
            var row = new JsonObject
            {
                ["faction"] = faction, ["s"] = e["s"]!.DeepClone(), ["v"] = e["v"]!.DeepClone(),
                ["name"] = c["name"]!.DeepClone(), ["class"] = c["class"]!.DeepClone(), ["level"] = c["level"]!.DeepClone(), ["at"] = r["at"]?.DeepClone() ?? 0,
            };
            if (e["min"] != null) row["min"] = e["min"]!.DeepClone();
            if (e["max"] != null) row["max"] = e["max"]!.DeepClone();
            return row;
        }
        return null;
    }

    // a snapshot without its reputations (what is served while the lodge has the feature off); the stored one is not changed
    public static JsonObject WithoutReps(JsonObject c)
    {
        if (c["reps"] == null) return c;
        var o = c.DeepClone().AsObject();
        o.Remove("reps");
        return o;
    }

    // ---- "who can craft this": the shared characters of one member that have learned any of the spells. One row per (character, spell).
    public static IEnumerable<JsonObject> CraftersOf(JsonObject c, IReadOnlySet<int> spells)
    {
        if (c["professions"] is not JsonObject p || p["known"] is not JsonObject known) yield break;
        var list = (p["list"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        foreach (var (key, v) in known)
        {
            if (v is not JsonObject e || e["ids"] is not JsonArray ids || !int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var line)) continue;
            var prof = list.FirstOrDefault(x => x["line"] is JsonValue lv && lv.TryGetValue<int>(out var li) && li == line);
            string name = prof != null ? (string)prof["name"]! : (e["n"] as JsonValue)?.GetValue<string>();
            if (name == null) continue;
            foreach (var idn in ids)
            {
                int id = (int)idn!;
                if (!spells.Contains(id)) continue;
                yield return new JsonObject
                {
                    ["spell"] = id, ["profession"] = name, ["skill"] = prof != null ? prof["skill"]!.DeepClone() : 0, ["max"] = prof != null ? prof["max"]!.DeepClone() : 0,
                    ["name"] = c["name"]!.DeepClone(), ["class"] = c["class"]!.DeepClone(), ["level"] = c["level"]!.DeepClone(),
                    ["partial"] = e["partial"]?.DeepClone() ?? true,
                };
            }
        }
    }

    static JsonObject Pets(JsonObject p)
    {
        if (p == null) return null;
        var pets = new JsonArray();
        if (p["pets"] is JsonArray pa)
            foreach (var n in pa)
            {
                if (pets.Count >= MaxPets) break;
                if (n is not JsonObject po) continue;
                var name = S(po["name"], 48);
                if (name == null) continue;
                var e = new JsonObject { ["name"] = name, ["level"] = I(po["level"], 0, 1000), ["loyalty"] = I(po["loyalty"], 0, 6), ["stable"] = I(po["stable"], 0, 1000), ["happiness"] = I(po["happiness"], 0, 3) };
                Set(e, "key", Js(S(po["key"], 48)));
                Set(e, "family", Js(S(po["family"], 48)));
                Set(e, "familyId", Js(S(po["familyId"], 48)));
                Set(e, "loyaltyText", Js(S(po["loyaltyText"], 48)));
                int ic = I(po["ic"], 0, int.MaxValue);
                if (ic > 0) e["ic"] = ic;
                if (Num(po["tpTotal"], out var tt) && tt > 0) { e["tpTotal"] = I(po["tpTotal"], 0, 100000); e["tpSpent"] = I(po["tpSpent"], 0, 100000); }
                var abs = new JsonArray();
                if (po["abilities"] is JsonArray aa)
                    foreach (var an in aa)
                    {
                        if (abs.Count >= MaxAbilities) break;
                        if (an is not JsonObject ao) continue;
                        var id = S(ao["id"], 32);
                        if (id != null && !AbilityRx().IsMatch(id)) id = null;
                        var nm = S(ao["name"], 48);
                        if (id == null && nm == null) continue;
                        var ae = new JsonObject { ["rank"] = I(ao["rank"], 0, 100) };
                        Set(ae, "id", Js(id)); Set(ae, "name", Js(nm));
                        abs.Add(ae);
                    }
                e["abilities"] = abs;
                pets.Add(e);
            }
        if (pets.Count == 0) return null;
        var o = new JsonObject { ["at"] = L(p["at"], 0, 4102444800L), ["pets"] = pets };
        Set(o, "active", Js(S(p["active"], 48)));
        var known = new JsonObject();
        if (p["known"] is JsonObject ko)
            foreach (var (k, v) in ko)
            {
                if (known.Count >= MaxKnown) break;
                if (!AbilityRx().IsMatch(k) || !Num(v, out var r) || r < 1) continue;
                known[k] = I(v, 1, 100);
            }
        if (known.Count > 0) o["known"] = known;
        return o;
    }

    // ---- the one-line view of a character the list shows (the full snapshot is fetched when it is opened)
    public static JsonObject Summary(JsonObject c)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "name", "realm", "class", "classText", "race", "raceFile", "sex", "faction", "guild", "level", "ilvl", "at" })
            if (c[k] != null) o[k] = c[k]!.DeepClone();
        if (o["ilvl"] == null && c["gear"] is JsonObject g && g["ilvl"] != null) o["ilvl"] = g["ilvl"]!.DeepClone();
        var profs = new JsonArray();
        if (c["professions"] is JsonObject p && p["list"] is JsonArray l)
            foreach (var n in l.OfType<JsonObject>())
                profs.Add(new JsonObject { ["name"] = n["name"]!.DeepClone(), ["skill"] = n["skill"]!.DeepClone(), ["max"] = n["max"]!.DeepClone() });
        o["professions"] = profs;
        return o;
    }
}
