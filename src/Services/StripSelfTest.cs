using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ElansAddonHub.Services
{
    // --selftest: the pixel strip codec on synthetic bitmaps (with colour noise, tint, other cell sizes), a strip
    // rendered by the real Lua addon (ELANSHUB_STRIP_PNG, optional), and the TGA avatar loader.
    public static class StripSelfTest
    {
        static byte[] Render(byte[] payload, int cell, int offX, int offY, int w, int h, int noise, double tint, Random rnd, byte[] payload2 = null)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { px[i * 4] = 40; px[i * 4 + 1] = 90; px[i * 4 + 2] = 60; px[i * 4 + 3] = 255; } // "game" background
            void Paint(byte[][] cells, int row)
            {
                for (int c = 0; c < cells.Length; c++)
                    for (int y = 0; y < cell; y++)
                        for (int x = 0; x < cell; x++)
                        {
                            int X = offX + c * cell + x, Y = offY + row * cell + y;
                            if (X >= w || Y >= h) continue;
                            for (int ch = 0; ch < 3; ch++)
                            {
                                int v = (int)(cells[c][ch] * tint) + rnd.Next(-noise, noise + 1);
                                px[(Y * w + X) * 4 + (2 - ch)] = (byte)Math.Max(0, Math.Min(255, v));
                            }
                        }
            }
            Paint(StripCodec.Cell(payload), 0);
            if (payload2 != null) Paint(StripCodec.CellV2(payload2), 1);
            return px;
        }

        static bool ReadCells(string file, out byte[] px, out int w, out int h)
        {
            // 64 lines "r g b": row 0 then row 1, 4x4 pixel cells (what the Lua test of the companion produced)
            var lines = File.ReadAllLines(file);
            w = 160; h = 20; px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { px[i * 4] = 40; px[i * 4 + 1] = 90; px[i * 4 + 2] = 60; px[i * 4 + 3] = 255; }
            for (int c = 0; c < 64 && c < lines.Length; c++)
            {
                var v = lines[c].Split(' ');
                int row = c / 32, col = c % 32;
                for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++)
                {
                    int X = col * 4 + x, Y = row * 4 + y;
                    for (int ch = 0; ch < 3; ch++) px[(Y * w + X) * 4 + (2 - ch)] = byte.Parse(v[ch]);
                }
            }
            return lines.Length >= 64;
        }

        public static string Run(string dir)
        {
            var sb = new StringBuilder();
            int fail = 0;
            void Check(string what, bool ok, string extra = "") { sb.AppendLine($"strip: {what}: {(ok ? "OK" : "FAIL")} {extra}"); if (!ok) fail++; }
            var rnd = new Random(7);
            var payload = StripCodec.BuildPayload(3, 4, 3, 42, "Elanøran-Longname");
            foreach (var (cell, ox, oy, noise, tint) in new[] { (4, 0, 0, 0, 1.0), (4, 0, 0, 14, 1.0), (4, 1, 0, 12, 0.85), (3, 0, 1, 10, 1.0), (6, 2, 0, 12, 0.9), (8, 0, 0, 14, 1.0) })
            {
                int w = 300, h = 12;
                var px = Render(payload, cell, ox, oy, w, h, noise, tint, rnd);
                var info = StripCodec.Decode(px, w, h, w * 4, out var why);
                bool ok = info != null && info.ClassFile == "HUNTER" && info.RaceFile == "NightElf" && info.Sex == 3 && info.Level == 42 && info.Name == "Elanøran-Lo";
                Check($"cell={cell} offset=({ox},{oy}) noise=+-{noise} tint={tint}", ok, info == null ? why : $"{info.Name} {info.ClassName} {info.RaceName} {info.Level}");
            }
            // ---- v2 (second row): zone/instance ids, flags, xp, group - with noise, tint and other cell sizes; v1 payloads still decode
            var v1only = StripCodec.BuildPayload(3, 4, 3, 42, "Elan");
            var v2p = StripCodec.BuildPayload(3, 4, 3, 42, "Elan", v2: true);
            var v2row = StripCodec.BuildPayloadV2(1413, 43, 1 + 16 + 64 + 128, 1 + 2 + 4, 62, 5);
            foreach (var (cell, ox, oy, noise, tint) in new[] { (4, 0, 0, 0, 1.0), (4, 0, 0, 14, 1.0), (4, 1, 1, 12, 0.85), (3, 0, 1, 10, 1.0), (6, 2, 0, 12, 0.9), (8, 0, 0, 14, 1.0) })
            {
                int w = 300, h = 40;
                var px = Render(v2p, cell, ox, oy, w, h, noise, tint, rnd, v2row);
                var info = StripCodec.Decode(px, w, h, w * 4, out var why);
                bool ok = info != null && info.Name == "Elan" && info.Level == 42 && info.HasV2 && info.MapId == 1413 && info.InstanceId == 43
                    && info.InCombat && info.InInstance && info.InGroup && info.GroupSize == 5 && info.XpKnown && info.XpPercent == 62 && info.Rested && !info.Dead && !info.Afk;
                Check($"v2 strip cell={cell} offset=({ox},{oy}) noise=+-{noise} tint={tint}", ok, info == null ? why : $"v2={info.HasV2} map={info.MapId} inst={info.InstanceId} f1={info.Flags1} xp={info.XpPercent} group={info.GroupSize}");
            }
            {
                var px = Render(v1only, 4, 0, 0, 300, 40, 10, 1.0, rnd);
                var info = StripCodec.Decode(px, 300, 40, 1200, out var why);
                Check("v1 strip (old companion) still decodes, no v2", info != null && info.Name == "Elan" && info.Level == 42 && !info.HasV2 && info.MapId == 0, why);
                // a v1-only reader (old hub) = only the first row of cells: a v2 strip cut to one row still gives the full v1 result
                px = Render(v2p, 4, 0, 0, 300, 4, 0, 1.0, rnd, v2row);
                info = StripCodec.Decode(px, 300, 4, 1200, out why);
                Check("old-hub view of a v2 strip (row 0 only): v1 fields intact", info != null && info.Name == "Elan" && info.ClassFile == "HUNTER" && !info.HasV2, why);
                var bad2 = Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, v2row);
                for (int y = 4; y < 8; y++) for (int x = 4 * 7; x < 4 * 8; x++) for (int ch = 0; ch < 3; ch++) bad2[(y * 300 + x) * 4 + ch] ^= 0x80; // flip a row-1 cell
                info = StripCodec.Decode(bad2, 300, 40, 1200, out why);
                Check("corrupted row 1: v2 dropped, v1 kept", info != null && info.Name == "Elan" && !info.HasV2, why);
                var flat2 = Render(v1only, 4, 0, 0, 300, 40, 0, 1.0, rnd, v2row); // v2 row present but row 0 doesn't announce it
                info = StripCodec.Decode(flat2, 300, 40, 1200, out why);
                Check("v2 row ignored when row 0 doesn't announce it", info != null && !info.HasV2, why);
            }
            // ---- v3 (companion 1.15.3+): the opt-in map position in the reserved bytes; on / valid flags; old Hubs; corrupt rows
            {
                int x = (int)Math.Round(0.25 * 65535), y = (int)Math.Round(0.75 * 65535);
                StripCodec.Info Dec(byte[] row, int cell = 4, int noise = 10, double tint = 1.0)
                {
                    var px = Render(v2p, cell, 0, 0, 300, 40, noise, tint, rnd, row);
                    return StripCodec.Decode(px, 300, 40, 1200, out _);
                }
                foreach (var (cell, noise, tint) in new[] { (4, 0, 1.0), (4, 14, 0.85), (3, 10, 1.0), (8, 12, 0.9) })
                {
                    var i3 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 3, 3, x, y), cell, noise, tint);
                    Check($"v3 strip, position valid: x/y decoded (cell={cell} noise=+-{noise} tint={tint})", i3 != null && i3.HasV2 && i3.RowVersion == 3 && i3.PosSupported && i3.PosEnabled && i3.PosValid && i3.PosX == x && i3.PosY == y
                        && Math.Abs(i3.PosFx - 0.25) < 1e-4 && Math.Abs(i3.PosFy - 0.75) < 1e-4 && i3.MapId == 1413, i3 == null ? "null" : $"v={i3.RowVersion} f={i3.PosFlags} x={i3.PosX} y={i3.PosY}");
                }
                var on = Dec(StripCodec.BuildPayloadV2(0, 43, 16 + 64, 1, 0, 0, 3, 1, 0, 0));
                Check("v3 strip, sharing on but no position (an instance): enabled, not valid, x/y 0", on != null && on.PosSupported && on.PosEnabled && !on.PosValid && on.PosX == 0 && on.InInstance);
                var off = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 3, 0, 0, 0));
                Check("v3 strip, sharing off in the game: supported, not enabled", off != null && off.PosSupported && !off.PosEnabled && !off.PosValid);
                var odd = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 3, 2, x, y));
                Check("v3 strip: a 'valid' bit without 'on' is not trusted (no position)", odd != null && odd.PosFlags == 0 && !odd.PosValid && odd.PosX == 0);
                var oldHub = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0));
                Check("v2 strip (an Elan's Hub before 1.15.3): decodes as before, position not supported (the Outpost can say 'too old')", oldHub != null && oldHub.HasV2 && oldHub.RowVersion == 2 && !oldHub.PosSupported && !oldHub.PosEnabled && oldHub.MapId == 1413);
                var oldv = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 2, 3, x, y));
                Check("v2 strip: whatever is in the reserved bytes is ignored", oldv != null && !oldv.PosSupported && !oldv.PosValid && oldv.PosX == 0);
                var v3row = StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 3, 3, x, y);
                var bad3 = Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, v3row);
                for (int yy = 4; yy < 8; yy++) for (int xx = 4 * 15; xx < 4 * 16; xx++) for (int ch = 0; ch < 3; ch++) bad3[(yy * 300 + xx) * 4 + ch] ^= 0x80;   // a cell of the x bytes
                var b3 = StripCodec.Decode(bad3, 300, 40, 1200, out var w3);
                Check("v3 strip with a corrupted position cell: row 1 fails its checksum, no position (v1 kept)", b3 != null && b3.Name == "Elan" && !b3.HasV2 && !b3.PosValid, w3);
                var one = Render(v2p, 4, 0, 0, 300, 4, 0, 1.0, rnd, v3row);
                var o1 = StripCodec.Decode(one, 300, 4, 1200, out var w4);
                Check("old-hub view of a v3 strip (row 0 only): v1 fields intact", o1 != null && o1.Name == "Elan" && !o1.HasV2, w4);
                var chp = new GamePresence.Character();
                GamePresence.ApplyLive(chp, Dec(v3row));
                var cho = new GamePresence.Character();
                GamePresence.ApplyLive(cho, oldHub);
                Check("presence: the position reaches the character (uiMap 1413, 0.25 / 0.75); an old Hub's says 'not supported'", chp.PosSupported && chp.PosEnabled && chp.PosValid && chp.PosMapId == 1413 && Math.Abs(chp.PosFx - 0.25) < 1e-4
                    && cho.HasLive && !cho.PosSupported && !cho.PosValid && cho.PosMapId == 0);
                var i1 = Dec(v3row); var i2 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 3, 3, x + 300, y));
                Check("a move alone is a position change, not a presence change (no Lodge update, no file poll)", i1.V2Equals(i2) && !i1.PosEquals(i2));
            }
            // ---- v4 (companion 1.16.0+): one trail event at a time in bytes 15-21; seq dedupe at a 1 Hz read; older rows never carry one
            {
                int x = (int)Math.Round(0.25 * 65535), y = (int)Math.Round(0.75 * 65535);
                StripCodec.Info Dec(byte[] row, int cell = 4, int noise = 10, double tint = 1.0)
                {
                    var px = Render(v2p, cell, 0, 0, 300, 40, noise, tint, rnd, row);
                    return StripCodec.Decode(px, 300, 40, 1200, out _);
                }
                foreach (var (cell, noise, tint) in new[] { (4, 0, 1.0), (4, 14, 0.85), (3, 10, 1.0), (8, 12, 0.9) })
                {
                    var e4 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 4, 3, x, y, evSeq: 200, evKind: 2, evId: 0xABCDEF, evExtra: 19, evAge: 7), cell, noise, tint);
                    Check($"v4 strip: the trail event decodes with the position (cell={cell} noise=+-{noise} tint={tint})", e4 != null && e4.RowVersion == 4 && e4.PosValid && e4.PosX == x && e4.HasEvent
                        && e4.EvSeq == 200 && e4.EvKind == 2 && e4.EvId == 0xABCDEF && e4.EvExtra == 19 && e4.EvAge == 7, e4 == null ? "null" : $"v={e4.RowVersion} seq={e4.EvSeq} k={e4.EvKind} id={e4.EvId} x={e4.EvExtra} age={e4.EvAge}");
                }
                var none4 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 4, 3, x, y));
                Check("v4 strip without an event: no event (seq 0), position as before", none4 != null && none4.RowVersion == 4 && !none4.HasEvent && none4.PosValid);
                var v3ev = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 3, 3, x, y, evSeq: 9, evKind: 1, evId: 6));
                Check("v3 strip (Hub 1.15.3): whatever is in bytes 15-21 is not an event", v3ev != null && v3ev.RowVersion == 3 && !v3ev.HasEvent && v3ev.EvSeq == 0 && v3ev.PosValid);
                var v2ev = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 2, 0, 0, 0, evSeq: 9, evKind: 1, evId: 6));
                Check("v2 strip: never an event", v2ev != null && v2ev.HasV2 && !v2ev.HasEvent);
                var ev4row = StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 4, 3, x, y, evSeq: 5, evKind: 1, evId: 6);
                var bad4 = Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, ev4row);
                for (int yy = 4; yy < 8; yy++) for (int xx = 4 * 22; xx < 4 * 23; xx++) for (int ch = 0; ch < 3; ch++) bad4[(yy * 300 + xx) * 4 + ch] ^= 0x80;   // a cell of the event id
                var b4 = StripCodec.Decode(bad4, 300, 40, 1200, out var w5);
                Check("v4 strip with a corrupted event cell: row 1 fails its checksum, no event", b4 != null && !b4.HasV2 && !b4.HasEvent, w5);
                var a4 = Dec(ev4row); var a4b = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 4, 3, x, y, evSeq: 6, evKind: 2, evId: 774, evExtra: 2));
                Check("an event alone is not a presence or position change (no Lodge update, no file poll)", a4.V2Equals(a4b) && a4.PosEquals(a4b));
                var one4 = Render(v2p, 4, 0, 0, 300, 4, 0, 1.0, rnd, ev4row);
                var o4 = StripCodec.Decode(one4, 300, 4, 1200, out var w6);
                Check("old-hub view of a v4 strip (row 0 only): v1 fields intact", o4 != null && o4.Name == "Elan" && !o4.HasV2, w6);

                // the filter: each event once at a 1 Hz read, a repeat long after (a /reload restarts the numbers) is new, older Hubs give nothing
                var f = new StripEventFilter(); var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
                StripCodec.Info Ev(int seq, int kind, int id, int extra = 0, int age = 0, string who = "Elan") => new StripCodec.Info { Name = who, HasV2 = true, RowVersion = 4, EvSeq = seq, EvKind = kind, EvId = id, EvExtra = extra, EvAge = age };
                var got = new System.Collections.Generic.List<StripEvent>();
                void Read(StripCodec.Info i, double s) { var e = f.Take(i, t0.AddSeconds(s)); if (e != null) got.Add(e); }
                Read(Ev(1, 1, 6, age: 2), 0); Read(Ev(1, 1, 6, age: 2), 1); Read(Ev(1, 1, 6, age: 2), 2);           // shown for 3 s: read three times
                Read(Ev(2, 2, 774, 2), 3); Read(Ev(2, 2, 774, 2), 4);
                Read(new StripCodec.Info { Name = "Elan", HasV2 = true, RowVersion = 4 }, 5);                         // nothing on the strip
                Read(Ev(3, 2, 774, 2), 6);                                                                          // the same item again: a new seq, a new event
                Check("filter: read at 1 Hz, each event passes once (3 events from 8 reads), with kind, id, extra and the time it happened (read time - age)",
                    got.Count == 3 && got[0].Kind == TrailKind.Kill && got[0].Id == 6 && got[0].At == t0.AddSeconds(-2) && got[1].Kind == TrailKind.Loot && got[1].Extra == 2 && got[2].Seq == 3 && got[2].Who == "Elan",
                    string.Join(",", got.Select(g => g.Seq + ":" + g.Kind + ":" + g.Id)));
                got.Clear();
                Read(Ev(3, 2, 774, 2), 6 + 30);                                                                     // the same seq much later: a /reload numbered it again
                Read(Ev(3, 2, 774, 2, who: "Other"), 37);                                                           // another character
                Read(Ev(4, 11, 5), 38);                                                                             // a kind this Outpost does not know
                Read(new StripCodec.Info { Name = "Elan", HasV2 = true, RowVersion = 3, EvSeq = 9, EvKind = 1, EvId = 6 }, 39);   // a v3 row never has one
                Check("filter: the same seq after 20 s counts again, another character's counts, unknown kinds and older rows never", got.Count == 2 && got[1].Who == "Other", got.Count + "");
            }
            // ---- v5 (companion 1.16.1+): how you move in posFlags bits 4 / 8 / 16 (flight, indoors, swimming), with a valid position only; older Outposts mask them off
            {
                int x = (int)Math.Round(0.25 * 65535), y = (int)Math.Round(0.75 * 65535);
                StripCodec.Info Dec(byte[] row, int cell = 4, int noise = 10, double tint = 1.0) => StripCodec.Decode(Render(v2p, cell, 0, 0, 300, 40, noise, tint, rnd, row), 300, 40, 1200, out _);
                foreach (var (cell, noise, tint) in new[] { (4, 0, 1.0), (4, 14, 0.85), (3, 10, 1.0) })
                {
                    var t5 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 5, 3 + 4 + 8, x, y), cell, noise, tint);
                    Check($"v5 strip: on a flight and indoors decode with the position (cell={cell} noise=+-{noise} tint={tint})", t5 != null && t5.RowVersion == 5 && t5.PosValid && t5.PosX == x && t5.PosMoveKnown && t5.OnTaxi && t5.Indoors && !t5.Swimming && t5.PosFlags == 3,
                        t5 == null ? "null" : $"v={t5.RowVersion} f={t5.PosFlags} m={t5.PosMove}");
                }
                var s5 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 5, 3 + 16, x, y));
                Check("v5 strip: swimming alone", s5 != null && s5.Swimming && !s5.OnTaxi && !s5.Indoors && s5.PosMoveKnown);
                var inv5 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 5, 1 + 4, 0, 0));
                Check("v5 strip: without a valid position the move bits are not trusted", inv5 != null && inv5.PosEnabled && !inv5.PosValid && inv5.PosMove == 0 && !inv5.PosMoveKnown);
                var v4m = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 4, 3 + 4, x, y));
                Check("v4 strip (Hub 1.16.0): whatever is in the high posFlags bits is not a move; the position reads as before (an older Outpost's view of a v5 strip)", v4m != null && v4m.PosValid && v4m.PosMove == 0 && !v4m.PosMoveKnown && v4m.PosFlags == 3 && v4m.PosX == x);
                var a5 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 5, 3, x, y)); var b5 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 5, 3 + 4, x, y));
                Check("taking off is a position change (the Outpost redraws), not a presence change", a5.V2Equals(b5) && !a5.PosEquals(b5));
                var cm = new GamePresence.Character(); GamePresence.ApplyLive(cm, b5);
                var co = new GamePresence.Character(); GamePresence.ApplyLive(co, v4m);
                Check("presence: the move bits reach the character (flight from a v5 Hub; nothing known from a v4 Hub)", cm.PosMove == 4 && cm.PosMoveKnown && co.PosMove == 0 && !co.PosMoveKnown);
                // ---- v6 (companion 1.17.0+): 32 mounted, 64 pet out (the trail's paw / hoof prints); a v5 strip's high bits are never read as those
                var t6 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 6, 3 + 32 + 64, x, y));
                Check("v6 strip: mounted with the pet out decode with the position", t6 != null && t6.RowVersion == 6 && t6.PosMountKnown && t6.Mounted && t6.PetOut && !t6.OnTaxi && t6.PosFlags == 3 && t6.PosX == x,
                    t6 == null ? "null" : $"v={t6.RowVersion} f={t6.PosFlags} m={t6.PosMove}");
                var v5hi = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 5, 3 + 4 + 32 + 64, x, y));
                Check("v5 strip (Hub 1.16.1): bits 32 / 64 are not mounted / pet (masked off), the flight bit still reads", v5hi != null && v5hi.OnTaxi && !v5hi.Mounted && !v5hi.PetOut && !v5hi.PosMountKnown && v5hi.PosMoveKnown && v5hi.PosMove == 4);
                var inv6 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 6, 1 + 32 + 64, 0, 0));
                Check("v6 strip: without a valid position the mounted / pet bits are not trusted", inv6 != null && !inv6.PosValid && inv6.PosMove == 0 && !inv6.PosMountKnown);
                var c6 = new GamePresence.Character(); GamePresence.ApplyLive(c6, t6);
                var c5 = new GamePresence.Character(); GamePresence.ApplyLive(c5, v5hi);
                Check("presence: mounted / pet out reach the character from a v6 Hub and the trail point's bits keep them (from a v5 Hub they are masked off)",
                    c6.PosMountKnown && (c6.PosMove & 96) == 96 && (WorldMap.TrailPrints.MoveOf(c6) & 96) == 96 && !c5.PosMountKnown && (WorldMap.TrailPrints.MoveOf(c5) & 96) == 0 && (WorldMap.TrailPrints.MoveOf(c5) & 4) == 4);
                var a6 = Dec(StripCodec.BuildPayloadV2(1413, 0, 0, 1, 50, 0, 6, 3, x, y));
                Check("mounting up is a position change (the Outpost redraws), not a presence change", a6.V2Equals(t6) && !a6.PosEquals(t6));
            }
            // names for the ids: open-world uiMapID, dungeon instance id, unknown -> nothing
            var ch1 = new GamePresence.Character();
            GamePresence.ApplyLive(ch1, StripCodec.Decode(Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, v2row), 300, 40, 1200, out _));
            Check("zone names: map 1413 -> The Barrens, instance 43 -> Wailing Caverns", ch1.Zone == "The Barrens" && ch1.InstanceName == "Wailing Caverns" && ch1.GroupSize == 5 && ch1.XpPercent == 62 && ch1.Rested && ch1.InCombat);
            var ch2 = new GamePresence.Character { Zone = "stale" };
            GamePresence.ApplyLive(ch2, StripCodec.Decode(Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, StripCodec.BuildPayloadV2(0, 43, 16 + 64, 1, 0, 0)), 300, 40, 1200, out _));
            Check("inside a dungeon (map unknown): the instance name is the zone", ch2.Zone == "Wailing Caverns" && ch2.InInstance && ch2.XpPercent == -1);
            var ch3 = new GamePresence.Character { Zone = "stale" };
            GamePresence.ApplyLive(ch3, StripCodec.Decode(Render(v2p, 4, 0, 0, 300, 40, 0, 1.0, rnd, StripCodec.BuildPayloadV2(9999, 0, 0, 1, 0, 0)), 300, 40, 1200, out _));
            Check("unknown map id: no zone text (not a stale or wrong one)", ch3.Zone == null);
            var cells = Environment.GetEnvironmentVariable("ELANSHUB_STRIP_CELLS");
            if (!string.IsNullOrEmpty(cells) && File.Exists(cells) && ReadCells(cells, out var cpx, out var cw, out var chh))
            {
                var li = StripCodec.Decode(cpx, cw, chh, cw * 4, out var lwhy);
                Check("strip drawn by the REAL Lua addon (companion 1.5 test): v1 + v2 fields", li != null && li.HasV2 && li.Name == "Elan" && li.Level == 60 && li.ClassFile == "HUNTER" && li.RaceFile == "Orc"
                    && li.MapId == 1426 && li.InstanceId == 43 && li.InCombat && li.Afk && li.InInstance && li.GroupSize == 5 && li.XpPercent == 50 && li.Rested,
                    li == null ? lwhy : $"map={li.MapId} inst={li.InstanceId} f1={li.Flags1} f2={li.Flags2} xp={li.XpPercent} group={li.GroupSize}");
            }

            // garbage / no strip must be rejected
            var bad = new byte[300 * 12 * 4];
            rnd.NextBytes(bad);
            Check("random noise rejected", StripCodec.Decode(bad, 300, 12, 1200, out _) == null);
            var flat = new byte[300 * 12 * 4];
            Check("black screen rejected", StripCodec.Decode(flat, 300, 12, 1200, out _) == null);
            var broken = Render(payload, 4, 0, 0, 300, 12, 0, 1, rnd);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) for (int ch = 0; ch < 3; ch++) broken[(y * 300 + 4 * 12 + x) * 4 + ch] ^= 0x80; // flip one data cell
            Check("corrupted cell rejected by checksum", StripCodec.Decode(broken, 300, 12, 1200, out var w2) == null, w2);

            // the real addon (run in a Lua mock by the dev tests) saved its cells as a PNG
            var png = Environment.GetEnvironmentVariable("ELANSHUB_STRIP_PNG");
            if (!string.IsNullOrEmpty(png) && File.Exists(png))
            {
                var dec = new FormatConvertedBitmap(BitmapFrame.Create(new Uri(png)), PixelFormats.Bgra32, null, 0);
                int w = dec.PixelWidth, h = dec.PixelHeight; var buf = new byte[w * h * 4];
                dec.CopyPixels(buf, w * 4, 0);
                var info = StripCodec.Decode(buf, w, h, w * 4, out var why);
                Check("strip drawn by the Lua addon", info != null, info == null ? why : $"{info.Name} {info.ClassName} {info.RaceName} L{info.Level} sex{info.Sex}");
            }

            // TGA avatar loader: RLE + raw, bottom-up + top-down, then the lookup in a fake WoW root
            var raw = Tga(2, 3, 2, false, bottomUp: true, rle: false);
            var rle = Tga(2, 3, 2, false, bottomUp: true, rle: true);
            var px1 = AvatarArt.DecodeTga(raw, out int tw, out int th);
            var px2 = AvatarArt.DecodeTga(rle, out _, out _);
            Check("TGA raw and RLE decode identically", px1 != null && px2 != null && tw == 3 && th == 2 && Convert.ToBase64String(px1) == Convert.ToBase64String(px2));
            Check("TGA bottom-up flipped (top-left pixel = row 1 data)", px1 != null && px1[0] == 13 && px1[3] == 255);
            var fake = Path.Combine(dir, "fakewow");
            var hb = Path.Combine(fake, "_classic_", "Interface", "AddOns", "HealBot", "Images", "class", "1");
            Directory.CreateDirectory(hb);
            File.WriteAllBytes(Path.Combine(hb, "Hunter.tga"), rle);
            var old = AvatarArt.Root;
            AvatarArt.Root = () => fake;
            Check("class icon found in a fake WoW root", AvatarArt.ClassBrush("HUNTER") != null);
            Check("missing class icon -> null (falls back to colour + race code)", AvatarArt.ClassBrush("MAGE") == null);
            AvatarArt.Root = old;
            sb.AppendLine(fail == 0 ? "strip selftest: ALL OK" : $"strip selftest: {fail} FAILED");
            return sb.ToString().TrimEnd();
        }

        // a real window in the top-left corner of the screen showing the strip, read back through the screen capture path
        public static async Task<string> Live()
        {
            // BitBlt/PrintWindow need the real, composited desktop: on the hidden one (Tools\selftest.ps1) this check cannot run
            if (Environment.GetEnvironmentVariable("ELANSHUB_HIDDEN_DESKTOP") == "1") return "strip live screen capture: skipped (hidden desktop)";
            var payload = StripCodec.BuildPayload(8, 1, 2, 60, "Livetest");
            var win = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false, ShowInTaskbar = false, Left = 0, Top = 0, Background = Brushes.DarkSlateGray };
            var canvas = new Canvas(); win.Content = canvas;
            win.Show();
            var dpi = VisualTreeHelper.GetDpi(win).DpiScaleX;
            double cell = 4 / dpi; // 4 physical pixels
            win.Width = 200 / dpi; win.Height = 60 / dpi; win.Left = 0; win.Top = 0;
            var cells = StripCodec.Cell(payload);
            for (int i = 0; i < cells.Length; i++)
            {
                var r = new Rectangle { Width = cell, Height = cell, Fill = new SolidColorBrush(Color.FromRgb(cells[i][0], cells[i][1], cells[i][2])) };
                Canvas.SetLeft(r, i * cell); canvas.Children.Add(r);
            }
            await Task.Delay(700);
            var strip = new PixelStrip(() => true) { TestHwnd = new WindowInteropHelper(win).Handle };
            var info = strip.Probe(out var why);
            win.Close();
            return "strip live screen capture (dpi " + dpi + "): " + (info != null && info.Name == "Livetest" && info.Level == 60 && info.ClassFile == "MAGE"
                ? "OK " + info.Name + " " + info.ClassName + " " + info.RaceName : "FAIL " + why);
        }

        // 3x2 test image: pixel i has B = 10 + i
        static byte[] Tga(int type, int w, int h, bool alpha, bool bottomUp, bool rle)
        {
            var ms = new MemoryStream();
            var hd = new byte[18];
            hd[2] = (byte)(rle ? 10 : 2); hd[12] = (byte)w; hd[14] = (byte)h; hd[16] = 32; hd[17] = (byte)(bottomUp ? 8 : 0x28);
            ms.Write(hd, 0, 18);
            var pix = new byte[w * h][];
            for (int i = 0; i < pix.Length; i++) pix[i] = new byte[] { (byte)(10 + i), 20, 30, 255 };
            if (!rle) foreach (var p in pix) ms.Write(p, 0, 4);
            else
            {
                // first row stored as a raw packet, the rest as a repeat packet when equal, else raw
                for (int row = 0; row < h; row++)
                {
                    ms.WriteByte((byte)(w - 1));
                    for (int x = 0; x < w; x++) ms.Write(pix[row * w + x], 0, 4);
                }
            }
            return ms.ToArray();
        }
    }
}
