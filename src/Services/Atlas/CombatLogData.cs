using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub.Services.Atlas
{
    // 2.95: WoW's own combat log for the Atlas. Elan's Toolkit (0.5.12, Settings > Atlas) can switch the game's logging on; the game then writes
    // <client>\Logs\WoWCombatLog-<date>.txt. The Outpost reads those files from disk - never writes, renames or deletes them, opens them shared - and keeps
    // per creature (npc id): the spells it was seen using (cast start / success, aura applied, damage, heal) with how often, and, from advanced combat
    // logging, where it stood (world position, map, zone map), its level and its health at that level. No names, no player or pet GUIDs, no chat:
    // ids, counts, rounded positions. Kept in <data>\combatlog.txt; the files are read on from where the last pass stopped (each pass at most PassBytes).
    //
    // A line: "10/18/2026 20:15:33.123-4  SPELL_CAST_SUCCESS,<fields, CSV with quoted names>". Fields (0 = the event):
    //   1 source GUID, 2 name, 3 flags (hex), 4 raid flags, 5 dest GUID, 6 name, 7 flags, 8 raid flags; SPELL_* / RANGE_*: 9 spell id, 10 name, 11 school.
    //   With advanced logging (COMBAT_LOG_VERSION,<v>,ADVANCED_LOG_ENABLED,1,...) a block follows the prefix (at 12 for spells, 9 for swings):
    //   +0 info GUID (the unit described), +1 owner, +2 hp, +3 max hp, ..., +14 position x, +15 position y, +16 zone map (uiMapID), +17 facing, +18 level.
    //   The block is only used when it describes a creature (+0 is a Creature GUID) and its numbers parse; the creature's world map is the GUID's 4th part.
    //   Which of the two position numbers is the world's x is checked against the zone map the line names (MapIndex), not assumed.
    public static class CombatLogData
    {
        public const int MaxNpcs = 5000, MaxSpellsPerNpc = 60, MaxPositionsPerNpc = 30, MaxLevelsPerNpc = 20;
        public const double MergeYards = 10;
        public const long PassBytes = 64L << 20;
        const uint ControlPlayer = 0x100;               // COMBATLOG_OBJECT_CONTROL_PLAYER: a player's pet, totem or guardian - not the creature's own doing

        public sealed class Position { public int Map, Ui; public double X, Y; public long N; }
        public sealed class Npc
        {
            public Dictionary<int, long> Spells = new Dictionary<int, long>();
            public List<Position> Positions = new List<Position>();
            public Dictionary<int, long> Health = new Dictionary<int, long>();   // level -> the highest max health seen at it
            public long Last;                                                    // the order it was last seen in (evicts the oldest)
        }
        public sealed class FileMark { public long Length, Offset; public bool Advanced; }
        public sealed class Store
        {
            public Dictionary<string, FileMark> Files = new Dictionary<string, FileMark>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<int, Npc> Npcs = new Dictionary<int, Npc>();
            public long Seq, Lines, Used;
            public int NpcCount => Npcs.Count;
        }

        // ---- reading one line --------------------------------------------------------------------------------------------------------------------------
        public sealed class LineState { public bool Advanced; }

        static readonly HashSet<string> SpellEvents = new HashSet<string>(StringComparer.Ordinal)
            { "SPELL_CAST_START", "SPELL_CAST_SUCCESS", "SPELL_AURA_APPLIED", "SPELL_DAMAGE", "SPELL_PERIODIC_DAMAGE", "RANGE_DAMAGE", "SPELL_HEAL", "SPELL_SUMMON" };

        // the CSV fields after the two spaces that end the time stamp; null when the line is not a log line
        public static List<string> Fields(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int s = line.IndexOf("  ", StringComparison.Ordinal);
            if (s < 0) return null;
            var res = new List<string>(40);
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = s + 2; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; }
                    else sb.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
                else if (c != '\r') sb.Append(c);
            }
            res.Add(sb.ToString());
            return res;
        }

        // "Creature-0-4170-0-12-1234-0000ABCD" -> npc 1234 on world map 0 (Vehicle- too); false for players, pets and anything else
        public static bool CreatureOf(string guid, out int npcId, out int mapId)
        {
            npcId = 0; mapId = 0;
            if (string.IsNullOrEmpty(guid) || !(guid.StartsWith("Creature-", StringComparison.Ordinal) || guid.StartsWith("Vehicle-", StringComparison.Ordinal))) return false;
            var p = guid.Split('-');
            if (p.Length < 7) return false;
            if (!int.TryParse(p[5], NumberStyles.None, CultureInfo.InvariantCulture, out npcId) || npcId <= 0) return false;
            int.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out mapId);
            return true;
        }

        static bool Hex(string s, out uint v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        }

        static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
        static bool Int(string s, out int v) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

        static Npc Get(Store st, int id)
        {
            if (!st.Npcs.TryGetValue(id, out var n))
            {
                if (st.Npcs.Count >= MaxNpcs)
                {
                    var oldest = st.Npcs.OrderBy(k => k.Value.Last).First().Key;
                    st.Npcs.Remove(oldest);
                }
                st.Npcs[id] = n = new Npc();
            }
            n.Last = ++st.Seq;
            return n;
        }

        // one line into the store; true when it gave something (a spell, a position, a health)
        public static bool Read(string line, LineState ls, Store st)
        {
            var f = Fields(line);
            if (f == null || f.Count == 0) return false;
            st.Lines++;
            var ev = f[0];
            if (ev == "COMBAT_LOG_VERSION") { ls.Advanced = f.Count > 3 && f[3] == "1"; return false; }
            bool spell = SpellEvents.Contains(ev), swing = ev == "SWING_DAMAGE";
            if (!spell && !swing) return false;
            bool used = false;
            if (spell && f.Count > 9 && CreatureOf(f[1], out var src, out _) && Hex(f[3], out var flags) && (flags & ControlPlayer) == 0
                && Int(f[9], out var spellId) && spellId > 0)
            {
                var n = Get(st, src);
                if (n.Spells.TryGetValue(spellId, out var c)) n.Spells[spellId] = c + 1;
                else if (n.Spells.Count < MaxSpellsPerNpc) n.Spells[spellId] = 1;
                used = true;
            }
            if (ls.Advanced)
            {
                int a = spell ? 12 : 9;
                if (f.Count > a + 16 && CreatureOf(f[a], out var who, out var map) && Num(f[a + 14], out var px) && Num(f[a + 15], out var py)
                    && Int(f[a + 16], out var ui) && ui >= 0 && (px != 0 || py != 0) && Math.Abs(px) < 1e6 && Math.Abs(py) < 1e6)
                {
                    var n = Get(st, who);
                    AddPosition(n, map, ui, Math.Round(px * 2) / 2, Math.Round(py * 2) / 2);
                    if (f.Count > a + 18 && Int(f[a + 18], out var lv) && lv > 0 && lv <= 100 && long.TryParse(f[a + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp) && hp > 0)
                    {
                        if (n.Health.TryGetValue(lv, out var h)) { if (hp > h) n.Health[lv] = hp; }
                        else if (n.Health.Count < MaxLevelsPerNpc) n.Health[lv] = hp;
                    }
                    used = true;
                }
            }
            if (used) st.Used++;
            return used;
        }

        static void AddPosition(Npc n, int map, int ui, double x, double y)
        {
            foreach (var p in n.Positions)
                if (p.Map == map && p.Ui == ui && (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) <= MergeYards * MergeYards)
                {
                    p.X = (p.X * p.N + x) / (p.N + 1); p.Y = (p.Y * p.N + y) / (p.N + 1); p.N++;
                    return;
                }
            if (n.Positions.Count >= MaxPositionsPerNpc)
            {
                var least = n.Positions.OrderBy(p => p.N).First();
                if (least.N > 1) return;
                n.Positions.Remove(least);
            }
            n.Positions.Add(new Position { Map = map, Ui = ui, X = x, Y = y, N = 1 });
        }

        // ---- reading the files -----------------------------------------------------------------------------------------------------------------------
        public static string LogsDir(string root, string flavor)
        {
            if (string.IsNullOrEmpty(root)) return null;
            try { return Path.Combine(root, WowLocator.Resolve(root, flavor) ?? "_classic_beta_", "Logs"); } catch (Exception) { return null; }
        }

        // reads on in every WoWCombatLog*.txt of dir, at most budget bytes in all; returns the bytes read. A file that got shorter than where we stopped
        // (the game started it again) is read from the start. Only whole lines are taken: a line the game is still writing waits for the next pass.
        public static long Scan(string dir, Store st, long budget = PassBytes)
        {
            long total = 0;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            FileInfo[] files;
            try { files = new DirectoryInfo(dir).GetFiles("WoWCombatLog*.txt").Where(fi => (fi.Attributes & FileAttributes.ReparsePoint) == 0).OrderBy(fi => fi.LastWriteTimeUtc).ToArray(); }
            catch (Exception) { return 0; }
            foreach (var fi in files)
            {
                if (total >= budget) break;
                if (!st.Files.TryGetValue(fi.Name, out var mark)) st.Files[fi.Name] = mark = new FileMark();
                long len = fi.Length;
                if (len < mark.Offset) { mark.Offset = 0; mark.Advanced = false; }
                if (len == mark.Offset) { mark.Length = len; continue; }
                try
                {
                    using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                    {
                        fs.Seek(mark.Offset, SeekOrigin.Begin);
                        long end = Math.Min(len, mark.Offset + (budget - total));
                        var ls = new LineState { Advanced = mark.Advanced };
                        var buf = new byte[1 << 16];
                        var lineBytes = new List<byte>(512);
                        long pos = mark.Offset, lineStart = mark.Offset;
                        while (pos < end)
                        {
                            int want = (int)Math.Min(buf.Length, end - pos);
                            int got = fs.Read(buf, 0, want);
                            if (got <= 0) break;
                            for (int i = 0; i < got; i++)
                            {
                                byte b = buf[i];
                                if (b == (byte)'\n')
                                {
                                    if (lineBytes.Count < 8192) Read(Encoding.UTF8.GetString(lineBytes.ToArray()), ls, st);
                                    lineBytes.Clear();
                                    lineStart = pos + i + 1;
                                }
                                else if (lineBytes.Count < 8192) lineBytes.Add(b);
                            }
                            pos += got;
                        }
                        total += lineStart - mark.Offset;
                        mark.Offset = lineStart;
                        mark.Length = len;
                        mark.Advanced = ls.Advanced;
                    }
                }
                catch (Exception) { }
            }
            return total;
        }

        // ---- keeping it ------------------------------------------------------------------------------------------------------------------------------
        public static string FilePath = Path.Combine(Util.DataDir, "combatlog.txt");
        static string D(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static string L(long v) => v.ToString(CultureInfo.InvariantCulture);

        public static void Save(Store st, string path = null)
        {
            path = path ?? FilePath;
            var sb = new StringBuilder("combatlog 1\n");
            foreach (var f in st.Files) sb.Append("f\t").Append(f.Key).Append('\t').Append(L(f.Value.Length)).Append('\t').Append(L(f.Value.Offset)).Append('\t').Append(f.Value.Advanced ? "1" : "0").Append('\n');
            foreach (var kv in st.Npcs.OrderBy(k => k.Key))
            {
                var id = L(kv.Key);
                sb.Append("n\t").Append(id).Append('\t').Append(L(kv.Value.Last)).Append('\n');
                foreach (var s in kv.Value.Spells) sb.Append("s\t").Append(id).Append('\t').Append(L(s.Key)).Append('\t').Append(L(s.Value)).Append('\n');
                foreach (var p in kv.Value.Positions) sb.Append("p\t").Append(id).Append('\t').Append(L(p.Map)).Append('\t').Append(L(p.Ui)).Append('\t').Append(D(p.X)).Append('\t').Append(D(p.Y)).Append('\t').Append(L(p.N)).Append('\n');
                foreach (var h in kv.Value.Health) sb.Append("h\t").Append(id).Append('\t').Append(L(h.Key)).Append('\t').Append(L(h.Value)).Append('\n');
            }
            sb.Append("q\t").Append(L(st.Seq)).Append('\t').Append(L(st.Lines)).Append('\t').Append(L(st.Used)).Append('\n');
            var tmp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        public static Store Load(string path = null)
        {
            path = path ?? FilePath;
            var st = new Store();
            try
            {
                if (!File.Exists(path)) return st;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != "combatlog 1") return st;
                foreach (var line in lines.Skip(1))
                {
                    var p = line.Split('\t');
                    long a, b, c; double x, y;
                    switch (p[0])
                    {
                        case "f" when p.Length >= 5 && long.TryParse(p[2], out a) && long.TryParse(p[3], out b):
                            st.Files[p[1]] = new FileMark { Length = a, Offset = b, Advanced = p[4] == "1" }; break;
                        case "n" when p.Length >= 3 && int.TryParse(p[1], out var id) && long.TryParse(p[2], out a):
                            if (!st.Npcs.ContainsKey(id)) st.Npcs[id] = new Npc { Last = a }; break;
                        case "s" when p.Length >= 4 && int.TryParse(p[1], out var sid) && int.TryParse(p[2], out var spell) && long.TryParse(p[3], out c):
                            if (st.Npcs.TryGetValue(sid, out var sn)) sn.Spells[spell] = c; break;
                        case "p" when p.Length >= 7 && int.TryParse(p[1], out var pid) && int.TryParse(p[2], out var map) && int.TryParse(p[3], out var ui)
                                && Num(p[4], out x) && Num(p[5], out y) && long.TryParse(p[6], out c):
                            if (st.Npcs.TryGetValue(pid, out var pn)) pn.Positions.Add(new Position { Map = map, Ui = ui, X = x, Y = y, N = c }); break;
                        case "h" when p.Length >= 4 && int.TryParse(p[1], out var hid) && int.TryParse(p[2], out var lv) && long.TryParse(p[3], out c):
                            if (st.Npcs.TryGetValue(hid, out var hn)) hn.Health[lv] = c; break;
                        case "q" when p.Length >= 4 && long.TryParse(p[1], out a) && long.TryParse(p[2], out b) && long.TryParse(p[3], out c):
                            st.Seq = a; st.Lines = b; st.Used = c; break;
                    }
                }
            }
            catch (Exception) { return new Store(); }
            return st;
        }

        // ---- the one the Atlas reads -----------------------------------------------------------------------------------------------------------------
        static Store current;
        static int busy;
        static DateTime lastPass = DateTime.MinValue;
        public static TimeSpan MinInterval = TimeSpan.FromSeconds(60);
        public static bool Due => DateTime.UtcNow - lastPass >= MinInterval && busy == 0;
        public static Store Current { get { if (current == null) { var s = Load(); Interlocked.CompareExchange(ref current, s, null); } return current; } }
        public static void Use(Store s) { current = s; }
        public static event Action Changed;

        // a pass in the background (Atlas opened, every MinInterval at most); the new store replaces the old one when it is done
        public static void RefreshSoon(string root, string flavor)
        {
            if (DateTime.UtcNow - lastPass < MinInterval) return;
            var dir = LogsDir(root, flavor);
            if (dir == null || !Directory.Exists(dir)) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            lastPass = DateTime.UtcNow;
            Task.Run(() =>
            {
                try
                {
                    var st = Load();
                    long n = Scan(dir, st);
                    if (n > 0 || st.Files.Count != (current?.Files.Count ?? -1)) { Save(st); current = st; Changed?.Invoke(); }
                }
                catch (Exception e) { Util.Log("combat log: " + e.Message); }
                finally { Interlocked.Exchange(ref busy, 0); }
            });
        }

        // where a creature was fought, as zone map spots (kind "fought here"): the world position on the zone map the log named; if that map does not
        // show the point the two numbers are tried the other way round (the log's x / y order is checked, not assumed); a point no map shows is left out
        public static List<AtlasObs.NpcSpot> Spots(Npc n, MapIndex idx)
        {
            var res = new List<AtlasObs.NpcSpot>();
            if (n == null || idx == null) return res;
            foreach (var p in n.Positions.OrderByDescending(p => p.N))
            {
                if (p.Ui <= 0) continue;
                var mp = idx.WorldToMap(p.Map, p.X, p.Y, p.Ui) ?? idx.WorldToMap(p.Map, p.Y, p.X, p.Ui);
                if (mp == null) continue;
                double x = Math.Round(mp.Value.Fx, 4), y = Math.Round(mp.Value.Fy, 4);
                var near = res.FirstOrDefault(r => r.Z == p.Ui && (r.X - x) * (r.X - x) + (r.Y - y) * (r.Y - y) <= AtlasObs.NpcSpotMerge * AtlasObs.NpcSpotMerge);
                if (near != null) { near.N += p.N; continue; }
                res.Add(new AtlasObs.NpcSpot { Z = p.Ui, X = x, Y = y, K = AtlasObs.NpcSpotCombat, N = p.N });
            }
            return res;
        }
    }
}
