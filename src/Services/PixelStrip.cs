using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ElansAddonHub.Services
{
    // What the companion addon shows in the top-left corner of the WoW window: 32 solid squares (see ElansHub.lua).
    // Cell 0 is a magenta marker (its width tells us the cell size in pixels), cells 1-4 are grey calibration
    // levels (0/85/170/255) so colour management or HDR tinting can't hurt, then 27 data cells of 6 bits
    // (2 bits per channel). Payload: magic A7, flags, classId, raceId, level, nameLen, name[12], 2 checksum bytes.
    //
    // Version 2 (companion 1.5+) adds a second row of 32 data cells directly below the first (same cell size, decoded with
    // row 0's calibration): 24 bytes = magic B2, version 2, uiMapID (2), instanceID (2), flags1, flags2, xp %, group size,
    // 12 reserved, 2 checksum bytes. Version 3 (companion 1.15.3+) uses reserved bytes 10-14 for the OPT-IN map position: posFlags
    // (1 sharing on, 2 position valid), x (2 bytes), y (2 bytes) = 0..65535 of the uiMap in bytes 2-3. Older hubs ignore them.
    // Version 4 (companion 1.16.0+) uses bytes 15-21 for one trail event at a time (same opt-in): seq (1-255, 0 = none), kind, id (3 bytes),
    // extra, age in seconds; each event stays a few seconds, so a reader at 1 Hz sees it at least twice (StripEventFilter passes it once). Row 0 is unchanged except that flags bit 8 says "row 1 is present". An older hub reads
    // row 0 only and never looks below it, so v1 and v2 hubs/companions mix in every combination: a v2 decoder without row 1
    // (old companion, or a failed checksum there) simply returns the v1 fields.
    //   flags1: 1 combat, 2 dead/ghost, 4 AFK, 8 resting, 16 in instance, 32 raid instance, 64 party instance, 128 in group
    //   flags2: 1 combat flag valid, 2 xp valid, 4 rested, 8 group is a raid
    public static class StripCodec
    {
        public class Info
        {
            public int ClassId, RaceId, Level, Sex;
            public string Name;
            // ---- v2 (HasV2 = row 1 decoded and checksummed)
            public bool HasV2;
            public int MapId, InstanceId, Flags1, Flags2, XpPercent, GroupSize;
            public bool InCombat => HasV2 && (Flags2 & 1) != 0 && (Flags1 & 1) != 0;
            public bool CombatKnown => HasV2 && (Flags2 & 1) != 0;
            public bool Dead => HasV2 && (Flags1 & 2) != 0;
            public bool Afk => HasV2 && (Flags1 & 4) != 0;
            public bool Resting => HasV2 && (Flags1 & 8) != 0;
            public bool InInstance => HasV2 && (Flags1 & 16) != 0;
            public bool InGroup => HasV2 && (Flags1 & 128) != 0;
            public bool XpKnown => HasV2 && (Flags2 & 2) != 0;
            public bool Rested => HasV2 && (Flags2 & 4) != 0;
            public bool V2Equals(Info o) => o != null && HasV2 == o.HasV2 && MapId == o.MapId && InstanceId == o.InstanceId && Flags1 == o.Flags1
                && Flags2 == o.Flags2 && XpPercent == o.XpPercent && GroupSize == o.GroupSize;
            // ---- row 1 version 3 (companion 1.15.3+): the opt-in map position. RowVersion 2 = an older Hub that cannot send it.
            public int RowVersion, PosFlags, PosX, PosY;
            public bool PosSupported => HasV2 && RowVersion >= 3;
            public bool PosEnabled => PosSupported && (PosFlags & 1) != 0;          // "Share my map position" is on in the Hub
            public bool PosValid => PosEnabled && (PosFlags & 2) != 0;              // ... and the game gave a position (not in an instance, not secret)
            public double PosFx => PosX / 65535.0;
            public double PosFy => PosY / 65535.0;
            public bool PosEquals(Info o) => o != null && RowVersion == o.RowVersion && PosFlags == o.PosFlags && PosX == o.PosX && PosY == o.PosY;
            // ---- row 1 version 4 (companion 1.16.0+): one trail event at a time (bytes 15-21). EvSeq 0 = none; an older Hub never has one.
            public int EvSeq, EvKind, EvId, EvExtra, EvAge;
            public bool HasEvent => HasV2 && RowVersion >= 4 && EvSeq > 0 && EvKind > 0;
            public string ClassFile => ClassFiles.TryGetValue(ClassId, out var c) ? c : null;
            public string ClassName => ClassNames.TryGetValue(ClassId, out var c) ? c : null;
            public string RaceFile => RaceFiles.TryGetValue(RaceId, out var c) ? c : null;
            public string RaceName => RaceNames.TryGetValue(RaceId, out var c) ? c : null;
        }

        public const int Cells = 32, DataCells = 27, PayloadBytes = 20;

        public static readonly Dictionary<int, string> ClassFiles = new Dictionary<int, string>
        {
            [1] = "WARRIOR", [2] = "PALADIN", [3] = "HUNTER", [4] = "ROGUE", [5] = "PRIEST", [6] = "DEATHKNIGHT", [7] = "SHAMAN",
            [8] = "MAGE", [9] = "WARLOCK", [10] = "MONK", [11] = "DRUID", [12] = "DEMONHUNTER", [13] = "EVOKER",
        };
        public static readonly Dictionary<int, string> ClassNames = new Dictionary<int, string>
        {
            [1] = "Warrior", [2] = "Paladin", [3] = "Hunter", [4] = "Rogue", [5] = "Priest", [6] = "Death Knight", [7] = "Shaman",
            [8] = "Mage", [9] = "Warlock", [10] = "Monk", [11] = "Druid", [12] = "Demon Hunter", [13] = "Evoker",
        };
        public static readonly Dictionary<int, string> RaceFiles = new Dictionary<int, string>
        {
            [1] = "Human", [2] = "Orc", [3] = "Dwarf", [4] = "NightElf", [5] = "Scourge", [6] = "Tauren", [7] = "Gnome", [8] = "Troll",
            [9] = "Goblin", [10] = "BloodElf", [11] = "Draenei", [22] = "Worgen", [24] = "Pandaren", [25] = "Pandaren", [26] = "Pandaren",
        };
        public static readonly Dictionary<int, string> RaceNames = new Dictionary<int, string>
        {
            [1] = "Human", [2] = "Orc", [3] = "Dwarf", [4] = "Night Elf", [5] = "Undead", [6] = "Tauren", [7] = "Gnome", [8] = "Troll",
            [9] = "Goblin", [10] = "Blood Elf", [11] = "Draenei", [22] = "Worgen", [24] = "Pandaren", [25] = "Pandaren", [26] = "Pandaren",
        };

        // ---- encoder (the addon does this in Lua; the self-test uses it for synthetic strips)
        public static byte[] BuildPayload(int classId, int raceId, int sex, int level, string name, bool v2 = false)
        {
            var nb = Encoding.UTF8.GetBytes(name ?? "");
            int len = Math.Min(nb.Length, 12);
            while (len > 0 && len < nb.Length && (nb[len] & 0xC0) == 0x80) len--;
            var p = new byte[PayloadBytes];
            p[0] = 0xA7; p[1] = (byte)(1 + (sex % 4) * 2 + (v2 ? 8 : 0)); p[2] = (byte)classId; p[3] = (byte)raceId; p[4] = (byte)level; p[5] = (byte)len;
            Array.Copy(nb, 0, p, 6, len);
            Checksum(p, out p[18], out p[19]);
            return p;
        }

        static void Checksum(byte[] p, out byte c1, out byte c2, int n = 18)
        {
            int s1 = 1, s2 = 0;
            for (int i = 0; i < n; i++) { s1 = (s1 + p[i]) % 251; s2 = (s2 + s1) % 251; }
            c1 = (byte)s1; c2 = (byte)s2;
        }

        public const int V2Bytes = 24;

        public static byte[] BuildPayloadV2(int mapId, int instanceId, int flags1, int flags2, int xpPercent, int groupSize, int version = 2, int posFlags = 0, int posX = 0, int posY = 0,
            int evSeq = 0, int evKind = 0, int evId = 0, int evExtra = 0, int evAge = 0)
        {
            var q = new byte[V2Bytes];
            q[0] = 0xB2; q[1] = (byte)version;
            if (version >= 4 || evSeq != 0) { q[15] = (byte)evSeq; q[16] = (byte)evKind; q[17] = (byte)(evId >> 16); q[18] = (byte)(evId >> 8); q[19] = (byte)evId; q[20] = (byte)evExtra; q[21] = (byte)evAge; }
            if (version >= 3) { q[10] = (byte)posFlags; q[11] = (byte)(posX >> 8); q[12] = (byte)posX; q[13] = (byte)(posY >> 8); q[14] = (byte)posY; } q[2] = (byte)(mapId >> 8); q[3] = (byte)mapId; q[4] = (byte)(instanceId >> 8); q[5] = (byte)instanceId;
            q[6] = (byte)flags1; q[7] = (byte)flags2; q[8] = (byte)xpPercent; q[9] = (byte)groupSize;
            Checksum(q, out q[22], out q[23], 22);
            return q;
        }

        // the 32 colours of row 1 (data only; the calibration is row 0's)
        public static byte[][] CellV2(byte[] payload2)
        {
            byte[] lv = { 0, 85, 170, 255 };
            var cells = new byte[Cells][];
            var bits = new int[Cells * 6];
            for (int i = 0; i < payload2.Length; i++) for (int k = 0; k < 8; k++) bits[i * 8 + k] = (payload2[i] >> (7 - k)) & 1;
            for (int c = 0; c < Cells; c++)
            {
                int o = c * 6;
                cells[c] = new[] { lv[bits[o] * 2 + bits[o + 1]], lv[bits[o + 2] * 2 + bits[o + 3]], lv[bits[o + 4] * 2 + bits[o + 5]] };
            }
            return cells;
        }

        // the 32 cell colours (r,g,b per cell, 0..255)
        public static byte[][] Cell(byte[] payload)
        {
            var cells = new byte[Cells][];
            cells[0] = new byte[] { 255, 0, 255 };
            byte[] lv = { 0, 85, 170, 255 };
            for (int i = 1; i <= 4; i++) cells[i] = new[] { lv[i - 1], lv[i - 1], lv[i - 1] };
            var bits = new int[DataCells * 6];
            for (int i = 0; i < payload.Length; i++) for (int k = 0; k < 8; k++) bits[i * 8 + k] = (payload[i] >> (7 - k)) & 1;
            for (int c = 0; c < DataCells; c++)
            {
                int o = c * 6;
                cells[5 + c] = new[] { lv[bits[o] * 2 + bits[o + 1]], lv[bits[o + 2] * 2 + bits[o + 3]], lv[bits[o + 4] * 2 + bits[o + 5]] };
            }
            return cells;
        }

        // ---- decoder. bgra = top-down 32bpp pixels of the top-left corner of the window's client area.
        public static Info Decode(byte[] bgra, int width, int height, int stride, out string why)
        {
            why = null;
            int R(int x, int y, int ch) => bgra[y * stride + x * 4 + (2 - ch)]; // ch 0=r 1=g 2=b
            bool Magenta(int x, int y) => R(x, y, 0) > 170 && R(x, y, 2) > 170 && R(x, y, 1) < 90;

            int x0 = -1, y0 = -1;
            for (int y = 0; y < height && y0 < 0; y++)
                for (int x = 0; x < Math.Min(6, width); x++)
                    if (Magenta(x, y)) { x0 = x; y0 = y; break; }
            if (y0 < 0) { why = "no marker"; return null; }
            int w = 0;
            while (x0 + w < width && Magenta(x0 + w, y0)) w++;
            if (w < 2 || w > 16) { why = "marker width " + w; return null; }
            int ym = y0 + w / 2;
            if (ym >= height || x0 + Cells * w > width) { why = "region too small"; return null; }

            int[] Sample(int cell, int row = 0)
            {
                int cx = x0 + cell * w + w / 2, rad = w >= 4 ? 1 : 0, cy = ym + row * w;
                int[] sum = new int[3]; int n = 0;
                for (int yy = Math.Max(0, cy - rad); yy <= Math.Min(height - 1, cy + rad); yy++)
                    for (int xx = cx - rad; xx <= cx + rad; xx++)
                    { for (int c = 0; c < 3; c++) sum[c] += R(xx, yy, c); n++; }
                return new[] { sum[0] / n, sum[1] / n, sum[2] / n };
            }

            var refs = new int[3][];
            for (int c = 0; c < 3; c++) refs[c] = new int[4];
            for (int i = 1; i <= 4; i++) { var s = Sample(i); for (int c = 0; c < 3; c++) refs[c][i - 1] = s[c]; }
            for (int c = 0; c < 3; c++)
                for (int k = 1; k < 4; k++)
                    if (refs[c][k] - refs[c][k - 1] < 25) { why = "no calibration"; return null; }

            var bits = new int[DataCells * 6];
            for (int d = 0; d < DataCells; d++)
            {
                var s = Sample(5 + d);
                for (int c = 0; c < 3; c++)
                {
                    int best = 0, bd = int.MaxValue;
                    for (int k = 0; k < 4; k++) { int dd = Math.Abs(s[c] - refs[c][k]); if (dd < bd) { bd = dd; best = k; } }
                    bits[d * 6 + c * 2] = best >> 1; bits[d * 6 + c * 2 + 1] = best & 1;
                }
            }
            var p = new byte[PayloadBytes];
            for (int i = 0; i < PayloadBytes; i++) { int v = 0; for (int k = 0; k < 8; k++) v = v * 2 + bits[i * 8 + k]; p[i] = (byte)v; }
            if (p[0] != 0xA7) { why = "bad magic"; return null; }
            Checksum(p, out var c1, out var c2);
            if (c1 != p[18] || c2 != p[19]) { why = "bad checksum"; return null; }
            if (p[5] > 12) { why = "bad name length"; return null; }
            var info = new Info
            {
                Sex = (p[1] >> 1) & 3, ClassId = p[2], RaceId = p[3], Level = p[4],
                Name = Encoding.UTF8.GetString(p, 6, p[5]),
            };

            // v2: row 1, only when row 0 says it is there, the region is tall enough, and its own checksum holds
            if ((p[1] & 8) != 0 && y0 + 2 * w <= height)
            {
                var bits2 = new int[Cells * 6];
                for (int d = 0; d < Cells; d++)
                {
                    var s = Sample(d, 1);
                    for (int c = 0; c < 3; c++)
                    {
                        int best = 0, bd = int.MaxValue;
                        for (int k = 0; k < 4; k++) { int dd = Math.Abs(s[c] - refs[c][k]); if (dd < bd) { bd = dd; best = k; } }
                        bits2[d * 6 + c * 2] = best >> 1; bits2[d * 6 + c * 2 + 1] = best & 1;
                    }
                }
                var q = new byte[V2Bytes];
                for (int i = 0; i < V2Bytes; i++) { int v = 0; for (int k = 0; k < 8; k++) v = v * 2 + bits2[i * 8 + k]; q[i] = (byte)v; }
                Checksum(q, out var d1, out var d2, 22);
                if (q[0] == 0xB2 && q[1] >= 2 && d1 == q[22] && d2 == q[23])
                {
                    info.HasV2 = true;
                    info.MapId = q[2] * 256 + q[3]; info.InstanceId = q[4] * 256 + q[5];
                    info.Flags1 = q[6]; info.Flags2 = q[7]; info.XpPercent = Math.Min(100, (int)q[8]); info.GroupSize = q[9];
                    info.RowVersion = q[1];
                    if (q[1] >= 3)
                    {
                        // a "valid" bit without "on" is not trusted; x / y only count when valid
                        info.PosFlags = (q[10] & 1) != 0 ? q[10] & 3 : 0;
                        if ((info.PosFlags & 2) != 0) { info.PosX = q[11] * 256 + q[12]; info.PosY = q[13] * 256 + q[14]; }
                    }
                    if (q[1] >= 4 && q[15] != 0 && q[16] != 0)
                    {
                        // a trail event (only a version 4 Hub writes these bytes; before that they were reserved and zero)
                        info.EvSeq = q[15]; info.EvKind = q[16]; info.EvId = (q[17] << 16) | (q[18] << 8) | q[19]; info.EvExtra = q[20]; info.EvAge = q[21];
                    }
                }
            }
            return info;
        }
    }

    // One trail event from the strip (companion 1.16.0+). At = when it happened (the read time minus the age the Hub sent).
    public enum TrailKind { Kill = 1, Loot = 2, Herb = 3, Ore = 4, Skin = 5, Level = 6, Died = 7, Quest = 8, Area = 9, Flight = 10 }
    public sealed class StripEvent
    {
        public TrailKind Kind;
        public int Id, Extra, Seq;
        public DateTime At;
        public string Who = "";                                    // the character it came from (the strip's name)
        public static bool KnownKind(int k) => k >= 1 && k <= 10;
    }

    // The Hub shows each event for a few seconds and numbers them (seq 1-255): a reader that looks once a second sees it two or three times.
    // This passes each one once: a different seq (or kind / id) is a new event; the same one again is new only after a long gap (a /reload starts
    // the numbers again, so a repeat after StaleAfter is a new event, never the old one). Strips without events (older Hubs) pass nothing.
    public sealed class StripEventFilter
    {
        public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);
        int lastSeq = -1, lastKind, lastId; DateTime lastSeen = DateTime.MinValue; string lastWho = "";
        readonly object gate = new object();
        public StripEvent Take(StripCodec.Info info, DateTime nowUtc)
        {
            if (info == null || !info.HasEvent || !StripEvent.KnownKind(info.EvKind)) return null;
            lock (gate)
            {
                var who = info.Name ?? "";
                bool same = info.EvSeq == lastSeq && info.EvKind == lastKind && info.EvId == lastId && who == lastWho && nowUtc - lastSeen < StaleAfter;
                lastSeen = nowUtc;
                if (same) return null;
                lastSeq = info.EvSeq; lastKind = info.EvKind; lastId = info.EvId; lastWho = who;
                return new StripEvent { Kind = (TrailKind)info.EvKind, Id = info.EvId, Extra = info.EvExtra, Seq = info.EvSeq, At = nowUtc.AddSeconds(-info.EvAge), Who = who };
            }
        }
    }

    // Reads the strip from the screen. Only the few pixels in the window's top-left corner are copied (BitBlt from
    // the desktop when WoW is visible at that spot; if something covers it, PrintWindow on the window instead).
    // Nothing is sent to WoW and nothing is read from its memory - it is the same as looking at the screen.
    public class PixelStrip : IDisposable
    {
        readonly Func<bool> enabled;
        Timer timer;
        IntPtr hwnd;
        int pid;
        long nextPrint;
        public StripCodec.Info Latest { get; private set; }      // newest good read
        public DateTime LastSeen { get; private set; }             // when it last decoded
        public string Status { get; private set; } = "idle";
        public event Action Changed;
        public event Action PositionChanged;                       // only the opt-in map position changed: no presence change, no SavedVariables poll
        public event Action<StripEvent> TrailEvent;                // a new trail event (each one once; worker thread)
        public readonly StripEventFilter Events = new StripEventFilter();
        public IntPtr TestHwnd;                                  // self-test: read this window instead of WoW
        public StripCodec.Info Probe(out string why) => ReadOnce(out why);

        public PixelStrip(Func<bool> enabled) { this.enabled = enabled; }

        public bool Capturing => timer != null;                         // false while paused (WoW is not running)
        public void Start() { if (timer == null) { timer = new Timer(_ => Tick(), null, 1500, 1000); if (Status == "paused") Status = "idle"; } } // 1 s: combat and zone changes show up quickly
        // no WoW, no capture: the timer is gone until Start() is called again
        public void Pause() { timer?.Dispose(); timer = null; hwnd = IntPtr.Zero; Status = "paused"; }
        public void Dispose() { timer?.Dispose(); timer = null; }

        int busy;
        void Tick()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                if (!enabled()) { Status = "off"; return; }
                var info = ReadOnce(out var why);
                Status = info != null ? "ok" : why;
                var old = Latest;
                if (info != null)
                {
                    Latest = info; LastSeen = DateTime.UtcNow;
                    if (old == null || old.Name != info.Name || old.Level != info.Level || old.ClassId != info.ClassId || old.RaceId != info.RaceId || old.Sex != info.Sex
                        || !old.V2Equals(info))
                        Changed?.Invoke();
                    else if (!old.PosEquals(info)) PositionChanged?.Invoke();
                    var ev = Events.Take(info, DateTime.UtcNow);
                    if (ev != null) TrailEvent?.Invoke(ev);
                }
            }
            catch (Exception e) { Status = "error " + e.Message; }
            finally { busy = 0; }
        }

        // ---------------------------------------------------------------- window
        bool FindWindow()
        {
            if (TestHwnd != IntPtr.Zero) { hwnd = TestHwnd; return true; }
            if (hwnd != IntPtr.Zero && IsWindow(hwnd)) return true;
            hwnd = IntPtr.Zero;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName;
                    if (GamePresence.IgnoreWow || !n.StartsWith("Wow", StringComparison.OrdinalIgnoreCase) || n.IndexOf("Voice", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) h = FirstWindowOf(p.Id);
                    if (h != IntPtr.Zero) { hwnd = h; pid = p.Id; return true; }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return false;
        }

        static long NowMs() => DateTime.UtcNow.Ticks / 10000;

        static IntPtr FirstWindowOf(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out var wp);
                if (wp == pid && IsWindowVisible(h)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        StripCodec.Info ReadOnce(out string why)
        {
            why = null;
            if (!FindWindow()) { why = "WoW not running"; return null; }
            if (IsIconic(hwnd)) { why = "minimised"; return null; }
            IntPtr prev = IntPtr.Zero;
            try { prev = SetThreadDpiAwarenessContext((IntPtr)(-4)); } catch { } // per-monitor v2: real pixels, whatever the Windows scaling
            try
            {
                if (!GetClientRect(hwnd, out var rc)) { why = "no client rect"; return null; }
                int cw = rc.Right - rc.Left, ch = rc.Bottom - rc.Top;
                if (cw < 64 || ch < 16) { why = "tiny window"; return null; }
                int w = Math.Min(cw, 300), h = Math.Min(ch, 40); // two rows of cells (up to ~16 px each)
                var pt = new POINT { X = 0, Y = 0 };
                ClientToScreen(hwnd, ref pt);

                // is WoW the visible window at the strip's spot?
                var top = WindowFromPoint(new POINT { X = pt.X + 3, Y = pt.Y + 3 });
                var visible = top != IntPtr.Zero && GetAncestor(top, 2 /*GA_ROOT*/) == GetAncestor(hwnd, 2);
                byte[] px = null; string d1 = null;
                if (visible)
                {
                    px = CopyScreen(pt.X, pt.Y, w, h);
                    var info = px == null ? null : StripCodec.Decode(px, w, h, w * 4, out d1);
                    if (info != null) return info;
                    why = "screen: " + (d1 ?? "copy failed");
                }
                else why = "WoW window covered";
                // covered, or the desktop copy didn't show the strip: ask the window to paint itself (slower, so not too often)
                if (NowMs() >= nextPrint)
                {
                    nextPrint = NowMs() + (visible ? 10000 : 5000);
                    px = PrintCorner(cw, ch, w, h);
                    var info = px == null ? null : StripCodec.Decode(px, w, h, w * 4, out var d2);
                    if (info != null) return info;
                    why += "; window: " + (px == null ? "print failed" : "no strip");
                }
                return null;
            }
            finally { if (prev != IntPtr.Zero) try { SetThreadDpiAwarenessContext(prev); } catch { } }
        }

        static byte[] CopyScreen(int sx, int sy, int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr mem = CreateCompatibleDC(screen), bmp = IntPtr.Zero;
            try
            {
                bmp = CreateDib(screen, w, h, out var bits);
                if (bmp == IntPtr.Zero) return null;
                var old = SelectObject(mem, bmp);
                bool ok = BitBlt(mem, 0, 0, w, h, screen, sx, sy, 0x00CC0020 | 0x40000000 /*SRCCOPY|CAPTUREBLT*/);
                GdiFlush();
                SelectObject(mem, old);
                if (!ok) return null;
                var buf = new byte[w * h * 4];
                Marshal.Copy(bits, buf, 0, buf.Length);
                return buf;
            }
            finally { if (bmp != IntPtr.Zero) DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
        }

        byte[] PrintCorner(int cw, int ch, int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen), bmp = IntPtr.Zero;
            try
            {
                bmp = CreateDib(screen, cw, ch, out var bits);
                if (bmp == IntPtr.Zero) return null;
                var old = SelectObject(mem, bmp);
                bool ok = PrintWindow(hwnd, mem, 0x1 | 0x2 /*CLIENTONLY|RENDERFULLCONTENT*/);
                GdiFlush();
                SelectObject(mem, old);
                if (!ok) return null;
                var buf = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy(bits + y * cw * 4, buf, y * w * 4, w * 4);
                return buf;
            }
            finally { if (bmp != IntPtr.Zero) DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
        }

        static IntPtr CreateDib(IntPtr dc, int w, int h, out IntPtr bits)
        {
            var bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
            return CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
        }

        // ---------------------------------------------------------------- win32
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPels, biYPels, biClrUsed, biClrImportant; }
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool GdiFlush();
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }
}
