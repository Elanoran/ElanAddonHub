using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;
using ElansAddonHub.Services.Db2;

namespace ElansAddonHub.Services
{
    // The game's tooltip font (Friz Quadrata). It is Blizzard's, so it is never part of the Outpost: it is read from the user's own WoW install
    // (the same read-only CASC reader as the item data), kept in <data>\cache\fonts\ and loaded from there. Without the game files the tooltips use a
    // close system font. Nothing is guessed: a file only counts when its name table says Friz Quadrata.
    public static class GameFont
    {
        public const string FileName = "FRIZQT__.TTF";
        // FileDataIDs the font has had in the client's file list; each one is checked against the font's own name table before it is used
        public static readonly int[] Candidates = { 615960 };      // FRIZQT__.TTF ("Friz Quadrata TT"), checked on the 2.50 client against the font's own name table
        public static readonly string Fallback = "Palatino Linotype, Georgia, Segoe UI";

        static readonly object gate = new object();
        static FontFamily family;
        public static string Source = "system font";     // what the tooltips use now: "game font (cached)" | "game font (read from the game files)" | "system font"
        public static Action Changed;

        public static string CacheDir => Path.Combine(Util.DataDir, "cache", "fonts");

        public static FontFamily Family
        {
            get { lock (gate) { return family ?? (family = new FontFamily(Fallback)); } }
        }

        // the family name from a TrueType name table (name id 1), or null when the bytes are not a font
        public static string FamilyName(byte[] ttf)
        {
            try
            {
                if (ttf == null || ttf.Length < 12) return null;
                uint tag = U32(ttf, 0);
                if (tag != 0x00010000 && tag != 0x74727565) return null;        // 0x00010000 or 'true'
                int n = U16(ttf, 4);
                for (int i = 0; i < n; i++)
                {
                    int rec = 12 + i * 16;
                    if (rec + 16 > ttf.Length) return null;
                    if (U32(ttf, rec) != 0x6E616D65) continue;                  // 'name'
                    int off = (int)U32(ttf, rec + 8);
                    int cnt = U16(ttf, off + 2), str = off + U16(ttf, off + 4);
                    string best = null;
                    for (int k = 0; k < cnt; k++)
                    {
                        int r = off + 6 + k * 12;
                        int plat = U16(ttf, r), id = U16(ttf, r + 6), len = U16(ttf, r + 8), so = U16(ttf, r + 10);
                        if (id != 1) continue;
                        var raw = new byte[len]; Buffer.BlockCopy(ttf, str + so, raw, 0, len);
                        var s = plat == 3 || plat == 0 ? Encoding.BigEndianUnicode.GetString(raw) : Encoding.ASCII.GetString(raw);
                        if (best == null || plat == 3) best = s;
                    }
                    return best;
                }
            }
            catch { }
            return null;
        }
        static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        static int U16(byte[] b, int o) => b[o] << 8 | b[o + 1];

        public static bool IsFriz(byte[] ttf) { var n = FamilyName(ttf); return n != null && n.IndexOf("Friz Quadrata", StringComparison.OrdinalIgnoreCase) >= 0; }

        // load the font from a folder that holds FRIZQT__.TTF (verified); true = the tooltips use it from now on
        public static bool UseFolder(string dir, string how)
        {
            try
            {
                var f = Path.Combine(dir, FileName);
                if (!File.Exists(f) || !IsFriz(File.ReadAllBytes(f))) return false;
                var ff = new FontFamily(new Uri(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar), "./#" + FamilyName(File.ReadAllBytes(f)));
                lock (gate) { family = ff; Source = how; }
                try { Changed?.Invoke(); } catch { }
                return true;
            }
            catch (Exception e) { Util.Log("game font: " + e.Message); return false; }
        }

        public static void UseSystem() { lock (gate) { family = new FontFamily(Fallback); Source = "system font"; } }

        // the cached copy first; otherwise one pass over the game files (only when a candidate id is known)
        public static void Start()
        {
            try
            {
                if (UseFolder(CacheDir, "game font (cached)")) return;
                var root = ItemData.WowRoot();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || Candidates.Length == 0) return;
                using (var casc = CascStorage.Open(root, Maps.MapData.Flavor() ?? "_classic_beta_"))
                {
                    var keys = casc.ResolveFileIds(new HashSet<int>(Candidates));
                    foreach (var id in Candidates)
                    {
                        if (!keys.TryGetValue(id, out var ck)) continue;
                        var bytes = casc.ReadByContentKey(ck);
                        if (!IsFriz(bytes)) continue;
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllBytes(Path.Combine(CacheDir, FileName), bytes);
                        break;
                    }
                }
                UseFolder(CacheDir, "game font (read from the game files)");
            }
            catch (Exception e) { Util.Log("game font: " + e.Message); }
        }
    }
}
